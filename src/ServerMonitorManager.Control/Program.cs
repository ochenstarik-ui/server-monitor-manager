using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;

const string AutomationSourceClaim = "smm:source_node_id";

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
            && !string.IsNullOrWhiteSpace(options.HubHelperPath)
            && !string.IsNullOrWhiteSpace(options.PrivilegeEscalationPath)
            && !string.IsNullOrWhiteSpace(options.BackupDirectory)
            && options.HeartbeatSeconds is >= 10 and <= 300
            && options.MaxBufferedMetricAgeHours is >= 1 and <= 168
            && options.MetricRetentionHours is >= 24 and <= 8760
            && options.IdempotencyRetentionHours is >= 1 and <= 720
            && options.AuditRetentionDays is >= 1 and <= 3650
            && options.MaintenanceIntervalMinutes is >= 1 and <= 1440
            && options.LinkExpirationPollSeconds is >= 1 and <= 300
            && options.BackupIntervalHours is >= 1 and <= 720
            && options.BackupRetentionCount is >= 1 and <= 100,
        "Invalid Control paths, heartbeat, retention, maintenance, expiration, or backup settings.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ControlStore>();
builder.Services.AddSingleton<CertificateAuthority>();
builder.Services.AddSingleton<ControlEventBroker>();
builder.Services.AddSingleton<ILinkPolicyApplier, LinkPolicyApplier>();
builder.Services.AddSingleton<LinkService>();
builder.Services.AddSingleton<CertificateLifecycleService>();
builder.Services.AddSingleton<ControlBackupService>();
builder.Services.AddHostedService<LinkExpirationBackgroundService>();
builder.Services.AddHostedService<ControlMaintenanceBackgroundService>();
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
                var identity = await store.ResolveIdentityAsync(context.ClientCertificate.Thumbprint);
                if (identity is null)
                {
                    context.Fail("The agent certificate is unknown, expired, or revoked.");
                    return;
                }

                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, identity.Id),
                    new(ClaimTypes.Role, identity.Role)
                };
                if (identity.SourceNodeId is not null)
                {
                    claims.Add(new Claim(AutomationSourceClaim, identity.SourceNodeId));
                }
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name));
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
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Agent", policy => policy.RequireRole("Agent"));
    options.AddPolicy("Operator", policy => policy.RequireRole("Operator"));
    options.AddPolicy("Automation", policy => policy.RequireRole("Automation"));
});
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
var backupService = app.Services.GetRequiredService<ControlBackupService>();
if (args is ["backup-restore", var backupPath])
{
    await backupService.RestoreAsync(backupPath);
    Console.WriteLine("Control backup restored. Start the service and verify /healthz.");
    return 0;
}
await store.InitializeAsync();

if (args is ["backup-create"])
{
    var createdBackupPath = await backupService.CreateAsync(DateTimeOffset.UtcNow);
    Console.WriteLine(createdBackupPath);
    return 0;
}

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

if (args is ["device-token-create", var deviceId])
{
    if (!NodeIdValidator.IsValid(deviceId))
    {
        Console.Error.WriteLine("Device id must contain 1-63 lowercase letters, digits, or hyphens.");
        return 2;
    }

    Console.WriteLine(await store.CreateDeviceEnrollmentTokenAsync(deviceId, TimeSpan.FromMinutes(10)));
    return 0;
}

if (args is ["automation-token-create", var automationId, var sourceNodeId])
{
    if (!NodeIdValidator.IsValid(automationId) || !NodeIdValidator.IsValid(sourceNodeId))
    {
        Console.Error.WriteLine("Automation and source Node ids must contain 1-63 lowercase letters, digits, or hyphens.");
        return 2;
    }

    var response = await store.CreateAutomationTokenAsync(
        new AutomationTokenCreateRequest(
            automationId, sourceNodeId, Guid.NewGuid().ToString()),
        "hub-cli",
        TimeSpan.FromMinutes(10));
    Console.WriteLine(JsonSerializer.Serialize(response, SmmJsonContext.Default.AutomationTokenResponse));
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

app.MapPost("/api/v1/device-enroll", async (
    DeviceEnrollmentRequest request,
    ControlStore controlStore,
    CertificateAuthority authority,
    CancellationToken cancellationToken) =>
{
    if (!NodeIdValidator.IsValid(request.DeviceId)
        || string.IsNullOrWhiteSpace(request.Token)
        || string.IsNullOrWhiteSpace(request.CertificateSigningRequestPem)
        || !IdempotencyKeyValidator.IsValid(request.IdempotencyKey))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = ["Invalid device enrollment request."]
        });
    }

    try
    {
        var response = await controlStore.EnrollDeviceAsync(
            request,
            () => authority.IssueClientCertificate(request.DeviceId, request.CertificateSigningRequestPem),
            cancellationToken);
        return response is null ? Results.Unauthorized() : Results.Ok(response);
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
}).AllowAnonymous().RequireRateLimiting("enrollment");

app.MapPost("/api/v1/automation-enroll", async (
    AutomationEnrollmentRequest request,
    ControlStore controlStore,
    CertificateAuthority authority,
    CancellationToken cancellationToken) =>
{
    if (!NodeIdValidator.IsValid(request.AutomationId)
        || string.IsNullOrWhiteSpace(request.Token)
        || string.IsNullOrWhiteSpace(request.CertificateSigningRequestPem)
        || !IdempotencyKeyValidator.IsValid(request.IdempotencyKey))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = ["Invalid automation enrollment request."]
        });
    }

    try
    {
        var response = await controlStore.EnrollAutomationAsync(
            request,
            () => authority.IssueClientCertificate(
                request.AutomationId, request.CertificateSigningRequestPem),
            cancellationToken);
        return response is null ? Results.Unauthorized() : Results.Ok(response);
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
}).AllowAnonymous().RequireRateLimiting("enrollment");

var agents = app.MapGroup("/api/v1/agents").RequireAuthorization("Agent");
agents.MapPost("/heartbeat", async (
    AgentHeartbeat heartbeat,
    HttpContext context,
    ControlStore controlStore,
    LinkService linkService,
    IOptions<ControlOptions> options,
    CancellationToken cancellationToken) =>
{
    if (!NodeIdValidator.IsValid(heartbeat.NodeId)
        || !IdempotencyKeyValidator.IsValid(heartbeat.IdempotencyKey)
        || heartbeat.SentAt < DateTimeOffset.UtcNow.AddHours(-options.Value.MaxBufferedMetricAgeHours)
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
        var mutation = await controlStore.RecordHeartbeatAsync(
            heartbeat,
            options.Value.HeartbeatSeconds,
            cancellationToken);
        if (mutation.RequiresReconciliation)
        {
            var reconciliation = await linkService.ReconcileDisabledLinksForNodeAsync(
                heartbeat.NodeId, cancellationToken);
            if (reconciliation.Failed == 0)
            {
                await controlStore.CompleteAgentReconciliationAsync(
                    heartbeat.NodeId, cancellationToken);
            }
        }
        var broker = context.RequestServices.GetRequiredService<ControlEventBroker>();
        broker.Publish(
            "agent.heartbeat",
            heartbeat.NodeId,
            JsonSerializer.Serialize(heartbeat, SmmJsonContext.Default.AgentHeartbeat));
        return Results.Ok(mutation.Response);
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
agents.MapGet("/provisioning/jobs/next", async (
    HttpContext context,
    ControlStore controlStore,
    CancellationToken cancellationToken) =>
{
    var nodeId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(nodeId) || !NodeIdValidator.IsValid(nodeId))
    {
        return Results.Forbid();
    }

    var job = await controlStore.ClaimNextProvisioningJobAsync(nodeId, cancellationToken);
    return job is null ? Results.NoContent() : Results.Ok(job);
});
agents.MapPost("/provisioning/jobs/{id}/progress", async (
    string id,
    ProvisioningJobProgressRequest request,
    HttpContext context,
    ControlStore controlStore,
    CancellationToken cancellationToken) =>
{
    var nodeId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(nodeId)
        || !NodeIdValidator.IsValid(nodeId)
        || !ProvisioningJobValidator.IsValidId(id)
        || !ProvisioningJobValidator.IsValid(request))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["provisioningProgress"] =
                ["Invalid job id, state, progress, step, event code, message, or idempotency key."]
        });
    }

    try
    {
        var job = await controlStore.ReportProvisioningProgressAsync(
            nodeId, id, request, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails { Title = "Idempotency key conflict" });
    }
    catch (ProvisioningTransitionException exception)
    {
        return Results.Conflict(new ProblemDetails { Title = exception.Message });
    }
});

var control = app.MapGroup("/api/v1/control").RequireAuthorization("Operator");
control.MapGet("/agents", async (ControlStore controlStore, CancellationToken cancellationToken) =>
    Results.Ok((await controlStore.ListAgentsAsync(cancellationToken)).ToArray()));
control.MapPost("/agents/{nodeId}/provisioning/jobs", async (
    string nodeId,
    ProvisioningJobCreateRequest request,
    HttpContext context,
    ControlStore controlStore,
    CancellationToken cancellationToken) =>
{
    if (!NodeIdValidator.IsValid(nodeId) || !ProvisioningJobValidator.IsValid(request))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["provisioningJob"] =
                ["Invalid node id, action schema, parameters, TTL, audit reason, or idempotency key."]
        });
    }

    try
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var job = await controlStore.CreateProvisioningJobAsync(
            nodeId, request, actor, cancellationToken);
        return Results.Created($"/api/v1/control/provisioning/jobs/{job.Id}", job);
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails { Title = "Idempotency key conflict" });
    }
    catch (ProvisioningNodeNotFoundException)
    {
        return Results.NotFound();
    }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
    {
        return Results.Conflict(new ProblemDetails
        {
            Title = "The node already has an incompatible active provisioning job."
        });
    }
});
control.MapGet("/provisioning/jobs/{id}", async (
    string id,
    ControlStore controlStore,
    CancellationToken cancellationToken) =>
{
    if (!ProvisioningJobValidator.IsValidId(id))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["provisioningJob"] = ["Invalid provisioning job id."]
        });
    }
    var job = await controlStore.GetProvisioningJobAsync(id, cancellationToken);
    return job is null ? Results.NotFound() : Results.Ok(job);
});
control.MapPost("/provisioning/jobs/{id}/confirm", async (
    string id,
    ProvisioningJobCommandRequest request,
    HttpContext context,
    ControlStore controlStore,
    CancellationToken cancellationToken) =>
    await ChangeProvisioningJobAsync(
        id, request, context, controlStore, confirm: true, cancellationToken));
control.MapPost("/provisioning/jobs/{id}/cancel", async (
    string id,
    ProvisioningJobCommandRequest request,
    HttpContext context,
    ControlStore controlStore,
    CancellationToken cancellationToken) =>
    await ChangeProvisioningJobAsync(
        id, request, context, controlStore, confirm: false, cancellationToken));
control.MapPost("/automations/token", async (
    AutomationTokenCreateRequest request,
    HttpContext context,
    ControlStore controlStore,
    CancellationToken cancellationToken) =>
{
    if (!NodeIdValidator.IsValid(request.AutomationId)
        || !NodeIdValidator.IsValid(request.SourceNodeId)
        || !IdempotencyKeyValidator.IsValid(request.IdempotencyKey))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["automation"] = ["Invalid automation id, source Node id, or idempotency key."]
        });
    }

    try
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        return Results.Ok(await controlStore.CreateAutomationTokenAsync(
            request, actor, TimeSpan.FromMinutes(10), cancellationToken));
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails { Title = "Idempotency key conflict" });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ProblemDetails { Title = exception.Message });
    }
});
control.MapPost("/agents/{nodeId}/reenroll", async (
    string nodeId,
    CertificateReenrollmentRequest request,
    HttpContext context,
    CertificateLifecycleService lifecycle,
    CancellationToken cancellationToken) =>
{
    if (!NodeIdValidator.IsValid(nodeId) || !CertificateReenrollmentValidator.IsValid(request))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["certificate"] = ["Invalid node id, reason, or idempotency key."]
        });
    }

    try
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var ticket = await lifecycle.ReenrollAgentAsync(nodeId, request, actor, cancellationToken);
        return ticket is null ? Results.NotFound() : Results.Ok(ticket);
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails { Title = "Idempotency key conflict" });
    }
});
control.MapPost("/devices/{deviceId}/reenroll", async (
    string deviceId,
    CertificateReenrollmentRequest request,
    HttpContext context,
    CertificateLifecycleService lifecycle,
    CancellationToken cancellationToken) =>
{
    if (!NodeIdValidator.IsValid(deviceId) || !CertificateReenrollmentValidator.IsValid(request))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["certificate"] = ["Invalid device id, reason, or idempotency key."]
        });
    }

    try
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (string.Equals(actor, deviceId, StringComparison.Ordinal))
        {
            return Results.BadRequest(new ProblemDetails
            {
                Title = "An Operator cannot revoke its own certificate. Use another Operator or the local Hub CLI."
            });
        }
        var ticket = await lifecycle.ReenrollDeviceAsync(deviceId, request, actor, cancellationToken);
        return ticket is null ? Results.NotFound() : Results.Ok(ticket);
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails { Title = "Idempotency key conflict" });
    }
});
control.MapGet("/links", async (ControlStore controlStore, CancellationToken cancellationToken) =>
    Results.Ok((await controlStore.ListLinksAsync(cancellationToken)).ToArray()));
control.MapPost("/links", async (
    LinkPolicyCreateRequest request,
    HttpContext context,
    LinkService linkService,
    CancellationToken cancellationToken) =>
{
    if (!LinkPolicyValidator.IsValid(request))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["link"] = ["Invalid source, target, protocol, port, TTL, reason, or idempotency key."]
        });
    }
    var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    try
    {
        var link = await linkService.CreateAsync(request, actor, cancellationToken);
        return Results.Created($"/api/v1/control/links/{link.Id}", link);
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails { Title = "Idempotency key conflict" });
    }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
    {
        return Results.Conflict(new ProblemDetails { Title = "An active Link already exists." });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ProblemDetails { Title = exception.Message });
    }
});
control.MapPost("/links/{id}/disable", async (
    string id,
    LinkPolicyDisableRequest request,
    HttpContext context,
    LinkService linkService,
    CancellationToken cancellationToken) =>
{
    if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _)
        || !IdempotencyKeyValidator.IsValid(request.IdempotencyKey))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["link"] = ["Invalid Link id or idempotency key."]
        });
    }
    var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    try
    {
        var link = await linkService.DisableAsync(id, request, actor, cancellationToken);
        return link is null ? Results.NotFound() : Results.Ok(link);
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails { Title = "Idempotency key conflict" });
    }
});
control.MapGet("/events", async (HttpContext context, ControlEventBroker broker) =>
{
    context.Response.ContentType = "application/x-ndjson";
    context.Response.Headers.CacheControl = "no-store";
    using var subscription = broker.Subscribe();
    await foreach (var controlEvent in subscription.Reader.ReadAllAsync(context.RequestAborted))
    {
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            controlEvent,
            SmmJsonContext.Default.ControlEvent,
            context.RequestAborted);
        await context.Response.WriteAsync("\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});

var automation = app.MapGroup("/api/v1/automation").RequireAuthorization("Automation");
automation.MapGet("/links", async (
    HttpContext context,
    ControlStore controlStore,
    CancellationToken cancellationToken) =>
{
    var sourceNodeId = context.User.FindFirstValue(AutomationSourceClaim);
    if (string.IsNullOrWhiteSpace(sourceNodeId))
    {
        return Results.Forbid();
    }

    var grants = (await controlStore.ListEffectiveLinksForNodeAsync(sourceNodeId, cancellationToken))
        .Where(link => string.Equals(link.SourceNodeId, sourceNodeId, StringComparison.Ordinal))
        .Select(link => new AutomationLinkGrant(
            link.TargetNodeId,
            link.Protocol,
            link.Port,
            link.DesiredState,
            link.ActualState,
            link.Version,
            link.ExpiresAt))
        .ToArray();
    return Results.Ok(grants);
});

await app.RunAsync();
return 0;

static async Task<IResult> ChangeProvisioningJobAsync(
    string id,
    ProvisioningJobCommandRequest request,
    HttpContext context,
    ControlStore controlStore,
    bool confirm,
    CancellationToken cancellationToken)
{
    if (!ProvisioningJobValidator.IsValidId(id)
        || !ProvisioningJobValidator.IsValid(request))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["provisioningJob"] = ["Invalid job id, reason, or idempotency key."]
        });
    }

    try
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var job = confirm
            ? await controlStore.ConfirmProvisioningJobAsync(id, request, actor, cancellationToken)
            : await controlStore.CancelProvisioningJobAsync(id, request, actor, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }
    catch (IdempotencyConflictException)
    {
        return Results.Conflict(new ProblemDetails { Title = "Idempotency key conflict" });
    }
    catch (ProvisioningTransitionException exception)
    {
        return Results.Conflict(new ProblemDetails { Title = exception.Message });
    }
}

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

internal static class LinkPolicyValidator
{
    public static bool IsValid(LinkPolicyCreateRequest request)
        => NodeIdValidator.IsValid(request.SourceNodeId)
           && NodeIdValidator.IsValid(request.TargetNodeId)
           && request.SourceNodeId != request.TargetNodeId
           && request.Protocol is "tcp" or "udp"
           && request.Port is >= 1 and <= 65535
           && request.TtlMinutes is >= 0 and <= 525600
           && request.Reason.Length <= 256
           && IdempotencyKeyValidator.IsValid(request.IdempotencyKey);
}

internal static class CertificateReenrollmentValidator
{
    public static bool IsValid(CertificateReenrollmentRequest request)
        => request.Reason.Length is >= 1 and <= 200
           && IdempotencyKeyValidator.IsValid(request.IdempotencyKey);
}

internal static class ProvisioningJobValidator
{
    private const int MaximumParametersBytes = 16 * 1024;

    public static bool IsValid(ProvisioningJobCreateRequest request)
        => request.SchemaVersion == 1
           && request.ActionType is "preflight" or "system.base-install"
           && request.Parameters.ValueKind == JsonValueKind.Object
           && !request.Parameters.EnumerateObject().Any()
           && request.Parameters.GetRawText().Length <= MaximumParametersBytes
           && request.TtlMinutes is >= 5 and <= 1440
           && request.AuditReason.Length is >= 1 and <= 256
           && IdempotencyKeyValidator.IsValid(request.IdempotencyKey);

    public static bool IsValid(ProvisioningJobCommandRequest request)
        => request.Reason.Length is >= 1 and <= 256
           && IdempotencyKeyValidator.IsValid(request.IdempotencyKey);

    public static bool IsValid(ProvisioningJobProgressRequest request)
        => request.State is ProvisioningJobStates.Preflight
            or ProvisioningJobStates.Running
            or ProvisioningJobStates.Verifying
            or ProvisioningJobStates.Completed
            or ProvisioningJobStates.Failed
            or ProvisioningJobStates.NeedsReconciliation
           && request.ProgressPercent is >= 0 and <= 100
           && IsSafeCode(request.Step, 64)
           && IsSafeCode(request.EventCode, 64)
           && request.Message.Length <= 512
           && IdempotencyKeyValidator.IsValid(request.IdempotencyKey);

    public static bool IsValidId(string id)
        => id.Length == 32 && Guid.TryParseExact(id, "N", out _);

    public static bool RequiresConfirmation(string actionType)
        => actionType == "system.base-install";

    private static bool IsSafeCode(string value, int maximumLength)
        => value.Length is >= 1 && value.Length <= maximumLength
           && value.All(character => character is >= 'a' and <= 'z'
               or >= '0' and <= '9'
               or '.' or '-' or '_');
}
