using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("enrollment", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, SmmJsonContext.Default));
builder.Services.AddOptions<ControlOptions>()
    .Bind(builder.Configuration.GetSection(ControlOptions.SectionName))
    .Validate(options =>
            !string.IsNullOrWhiteSpace(options.DatabasePath)
            && !string.IsNullOrWhiteSpace(options.CertificateAuthorityPath)
            && options.HeartbeatSeconds is >= 10 and <= 300,
        "Control paths are required and HeartbeatSeconds must be between 10 and 300.")
    .ValidateOnStart();
builder.Services.AddSingleton<ControlStore>();
builder.Services.AddSingleton<CertificateAuthority>();
builder.Services.AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
    .AddCertificate(options =>
    {
        options.AllowedCertificateTypes = CertificateTypes.All;
        options.RevocationMode = X509RevocationMode.NoCheck;
        options.ValidateCertificateUse = true;
        options.ValidateValidityPeriod = true;
        options.Events = new CertificateAuthenticationEvents
        {
            OnCertificateValidated = async context =>
            {
                var store = context.HttpContext.RequestServices.GetRequiredService<ControlStore>();
                if (!await store.IsCertificateActiveAsync(context.ClientCertificate.Thumbprint))
                {
                    context.Fail("The agent certificate is unknown, expired, or revoked.");
                    return;
                }

                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, context.ClientCertificate.Thumbprint)],
                    context.Scheme.Name));
                context.Success();
            }
        };
    });
builder.Services.AddOptions<CertificateAuthenticationOptions>(
        CertificateAuthenticationDefaults.AuthenticationScheme)
    .Configure<CertificateAuthority>((options, authority) =>
    {
        options.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
        options.CustomTrustStore.Add(authority.PublicCertificate);
    });
builder.Services.AddAuthorization();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.ConfigureHttpsDefaults(https =>
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate);
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseHsts();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var store = app.Services.GetRequiredService<ControlStore>();
await store.InitializeAsync();

if (args is ["token-create", var nodeId])
{
    if (!NodeIdValidator.IsValid(nodeId))
    {
        Console.Error.WriteLine("Node id must contain 1-63 lowercase letters, digits, or hyphens.");
        return 2;
    }

    Console.WriteLine(await store.CreateEnrollmentTokenAsync(nodeId, TimeSpan.FromMinutes(10)));
    return 0;
}

app.MapHealthChecks("/healthz").AllowAnonymous();

app.MapPost("/api/v1/enroll", async (
    EnrollmentRequest request,
    ControlStore controlStore,
    CertificateAuthority authority,
    CancellationToken cancellationToken) =>
{
    if (!NodeIdValidator.IsValid(request.NodeId)
        || string.IsNullOrWhiteSpace(request.Token)
        || string.IsNullOrWhiteSpace(request.CertificateSigningRequestPem)
        || !IdempotencyKeyValidator.IsValid(request.IdempotencyKey))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = ["Invalid enrollment request."]
        });
    }

    EnrollmentResponse? response;
    try
    {
        response = await controlStore.EnrollAsync(
            request,
            () => authority.IssueClientCertificate(request.NodeId, request.CertificateSigningRequestPem),
            cancellationToken);
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails
        {
            Title = "Idempotency key conflict",
            Status = StatusCodes.Status409Conflict
        });
    }
    catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["certificateSigningRequestPem"] = ["Invalid certificate signing request."]
        });
    }

    return response is null
        ? Results.Unauthorized()
        : Results.Ok(response);
}).AllowAnonymous().RequireRateLimiting("enrollment");

var agents = app.MapGroup("/api/v1/agents").RequireAuthorization();
agents.MapPost("/heartbeat", async (
    AgentHeartbeat heartbeat,
    HttpContext context,
    ControlStore controlStore,
    IOptions<ControlOptions> options,
    CancellationToken cancellationToken) =>
{
    if (!NodeIdValidator.IsValid(heartbeat.NodeId)
        || !IdempotencyKeyValidator.IsValid(heartbeat.IdempotencyKey)
        || heartbeat.SentAt < DateTimeOffset.UtcNow.AddMinutes(-5)
        || heartbeat.SentAt > DateTimeOffset.UtcNow.AddMinutes(1))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["heartbeat"] = ["Invalid or stale heartbeat."]
        });
    }

    var certificate = await context.Connection.GetClientCertificateAsync(cancellationToken);
    if (certificate is null)
    {
        return Results.Unauthorized();
    }

    if (!await controlStore.IsCertificateForNodeAsync(
            certificate.Thumbprint,
            heartbeat.NodeId,
            cancellationToken))
    {
        return Results.Forbid();
    }

    try
    {
        var response = await controlStore.RecordHeartbeatAsync(
            heartbeat,
            options.Value.HeartbeatSeconds,
            cancellationToken);
        return Results.Ok(response);
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails
        {
            Title = "Idempotency key conflict",
            Status = StatusCodes.Status409Conflict
        });
    }
});

await app.RunAsync();
return 0;

public partial class Program;

internal static class NodeIdValidator
{
    public static bool IsValid(string value)
        => value.Length is >= 1 and <= 63
           && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

internal static class IdempotencyKeyValidator
{
    public static bool IsValid(string value)
        => Guid.TryParse(value, out _);
}
