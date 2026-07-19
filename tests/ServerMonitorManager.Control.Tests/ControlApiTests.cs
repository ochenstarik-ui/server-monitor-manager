extern alias controlapp;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class ControlApiTests : IAsyncDisposable
{
    private readonly ControlApiFactory _factory = new();

    [Fact]
    public async Task HealthEndpointIsAnonymousAndControlEndpointsRequireOperator()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await anonymous.GetAsync("/healthz", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(
                "/api/v1/control/agents", TestContext.Current.CancellationToken)).StatusCode);

        using var authenticated = _factory.CreateClient();
        authenticated.DefaultRequestHeaders.Add("X-Test-Identity", "windows-pc");
        authenticated.DefaultRequestHeaders.Add("X-Test-Role", "Operator");
        Assert.Equal(HttpStatusCode.OK,
            (await authenticated.GetAsync(
                "/api/v1/control/agents", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task AutomationIdentityCannotUseOperatorSurface()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "coding-agent");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Automation");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync(
                "/api/v1/control/links", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(
                "/api/v1/automation/links", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task InvalidEnrollmentUsesProblemDetailsResponse()
    {
        using var client = _factory.CreateClient();
        using var content = JsonContent.Create(new
        {
            nodeId = "INVALID NODE",
            token = "short",
            certificateSigningRequestPem = "bad",
            idempotencyKey = "bad"
        });
        using var response = await client.PostAsync(
            "/api/v1/enroll", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task OperatorCanCreateAndReadProvisioningJob()
    {
        var store = _factory.Services.GetRequiredService<ServerMonitorManager.Control.ControlStore>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var token = await store.CreateEnrollmentTokenAsync("home", TimeSpan.FromMinutes(10), cancellationToken);
        await store.EnrollAsync(
            new ServerMonitorManager.Core.EnrollmentRequest(
                "home", token, "csr", Guid.NewGuid().ToString()),
            () => new ServerMonitorManager.Control.IssuedCertificate(
                "certificate", "ca", "F1E2", DateTimeOffset.UtcNow.AddDays(1)),
            cancellationToken);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(
            $"/api/v1/control/provisioning/jobs/{Guid.NewGuid():N}", cancellationToken)).StatusCode);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "windows-pc");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");
        using var response = await client.PostAsJsonAsync(
            "/api/v1/control/agents/home/provisioning/jobs",
            new
            {
                actionType = "system.base-install",
                schemaVersion = 1,
                parameters = new { },
                ttlMinutes = 60,
                auditReason = "API integration test",
                idempotencyKey = Guid.NewGuid().ToString()
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<ServerMonitorManager.Core.ProvisioningJob>(
            cancellationToken);
        Assert.NotNull(job);
        Assert.Equal(ServerMonitorManager.Core.ProvisioningJobStates.AwaitingConfirmation, job.State);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(
            $"/api/v1/control/provisioning/jobs/{job.Id}", cancellationToken)).StatusCode);
        using var eventsResponse = await client.GetAsync(
            $"/api/v1/control/provisioning/jobs/{job.Id}/events?limit=10", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
        var events = await eventsResponse.Content.ReadFromJsonAsync<ServerMonitorManager.Core.ProvisioningEvent[]>(
            cancellationToken);
        Assert.NotNull(events);
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task AgentReceivesOnlyItsOwnProvisioningJob()
    {
        var nodeId = $"node-{Guid.NewGuid():N}"[..13];
        var store = _factory.Services.GetRequiredService<ServerMonitorManager.Control.ControlStore>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var token = await store.CreateEnrollmentTokenAsync(nodeId, TimeSpan.FromMinutes(10), cancellationToken);
        await store.EnrollAsync(
            new ServerMonitorManager.Core.EnrollmentRequest(
                nodeId, token, "csr", Guid.NewGuid().ToString()),
            () => new ServerMonitorManager.Control.IssuedCertificate(
                "certificate", "ca", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddDays(1)),
            cancellationToken);
        using var parameters = System.Text.Json.JsonDocument.Parse("{}");
        var created = await store.CreateProvisioningJobAsync(
            nodeId,
            new ServerMonitorManager.Core.ProvisioningJobCreateRequest(
                "preflight", 1, parameters.RootElement.Clone(), 60,
                "API Agent isolation test", Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);

        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Test-Identity", "other-node");
        other.DefaultRequestHeaders.Add("X-Test-Role", "Agent");
        Assert.Equal(HttpStatusCode.NoContent, (await other.GetAsync(
            "/api/v1/agents/provisioning/jobs/next", cancellationToken)).StatusCode);

        using var assigned = _factory.CreateClient();
        assigned.DefaultRequestHeaders.Add("X-Test-Identity", nodeId);
        assigned.DefaultRequestHeaders.Add("X-Test-Role", "Agent");
        var response = await assigned.GetAsync(
            "/api/v1/agents/provisioning/jobs/next", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claimed = await response.Content.ReadFromJsonAsync<ServerMonitorManager.Core.ProvisioningJob>(
            cancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(created.Id, claimed.Id);
        Assert.Equal(nodeId, claimed.NodeId);
        Assert.Equal(ServerMonitorManager.Core.ProvisioningJobStates.Preflight, claimed.State);
        Assert.Equal(HttpStatusCode.NoContent, (await assigned.GetAsync(
            "/api/v1/agents/provisioning/jobs/next", cancellationToken)).StatusCode);

        var progress = new
        {
            state = "Running",
            progressPercent = 25,
            step = "apply",
            eventCode = "apply.started",
            message = "Applying the approved plan.",
            idempotencyKey = Guid.NewGuid().ToString()
        };
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(
            $"/api/v1/agents/provisioning/jobs/{created.Id}/progress",
            progress,
            cancellationToken)).StatusCode);
        using var progressResponse = await assigned.PostAsJsonAsync(
            $"/api/v1/agents/provisioning/jobs/{created.Id}/progress",
            progress,
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, progressResponse.StatusCode);
        var updated = await progressResponse.Content.ReadFromJsonAsync<ServerMonitorManager.Core.ProvisioningJob>(
            cancellationToken);
        Assert.Equal(25, updated!.ProgressPercent);
        Assert.Equal("apply", updated.CurrentStep);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private sealed class ControlApiFactory : WebApplicationFactory<controlapp::Program>
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"smm-api-tests-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directory);
            var authorityPath = Path.Combine(_directory, "control-ca.pfx");
            if (!File.Exists(authorityPath))
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var request = new CertificateRequest("CN=SMM API Test CA", key, HashAlgorithmName.SHA256);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                using var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                File.WriteAllBytes(authorityPath, certificate.Export(X509ContentType.Pfx));
            }
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Control:DatabasePath"] = Path.Combine(_directory, "control.db"),
                    ["Control:CertificateAuthorityPath"] = authorityPath,
                    ["Control:BackupDirectory"] = Path.Combine(_directory, "backups")
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Identity", out var identity)
                || !Request.Headers.TryGetValue("X-Test-Role", out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, identity.ToString()),
                new(ClaimTypes.Role, role.ToString())
            };
            if (role.ToString() == "Automation")
            {
                claims.Add(new Claim("smm:source_node_id", "ai-agent"));
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
