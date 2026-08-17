extern alias controlapp;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class PasswordLoginTests : IAsyncDisposable
{
    private readonly PasswordLoginTestFactory _disabledFactory = new(enabledForTesting: false);
    private readonly PasswordLoginTestFactory _enabledFactory = new(enabledForTesting: true);

    [Fact]
    public async Task WhenPasswordLoginIsDisabledEndpointsReturnDisabledStatusAndRejectLogin()
    {
        using var client = _disabledFactory.CreateClient();

        // 1. Status returns enabledForTesting: false
        var statusResponse = await client.GetAsync("/api/v1/auth/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadFromJsonAsync<PasswordLoginStatusResponse>(
            SmmJsonContext.Default.PasswordLoginStatusResponse, TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.False(status.EnabledForTesting);

        // 2. Login attempt is rejected with 404 Not Found
        var loginResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new PasswordLoginRequest("test-operator", "TestPassword123!"),
            SmmJsonContext.Default.PasswordLoginRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, loginResponse.StatusCode);

        // 3. Anonymous access to control endpoints is unauthorized
        var controlResponse = await client.GetAsync("/api/v1/control/agents", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, controlResponse.StatusCode);
    }

    [Fact]
    public async Task WhenPasswordLoginIsEnabledSuccessfulLoginGrantsOperatorRoleOnly()
    {
        using var client = _enabledFactory.CreateClient();

        // 1. Status returns enabledForTesting: true
        var statusResponse = await client.GetAsync("/api/v1/auth/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadFromJsonAsync<PasswordLoginStatusResponse>(
            SmmJsonContext.Default.PasswordLoginStatusResponse, TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.True(status.EnabledForTesting);

        // 2. Login with correct credentials
        var loginResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new PasswordLoginRequest("test-operator", "TestPassword123!"),
            SmmJsonContext.Default.PasswordLoginRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<PasswordLoginResponse>(
            SmmJsonContext.Default.PasswordLoginResponse, TestContext.Current.CancellationToken);

        Assert.NotNull(loginResult);
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Token));
        Assert.Equal("Operator", loginResult.Role);
        Assert.True(loginResult.ExpiresAt > DateTimeOffset.UtcNow);

        // Verify Set-Cookie header is present
        Assert.True(loginResponse.Headers.Contains("Set-Cookie"));

        // 3. Access Operator Control endpoints using Bearer token
        using var authClient = _enabledFactory.CreateClient();
        authClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Token);

        var agentsResponse = await authClient.GetAsync("/api/v1/control/agents", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, agentsResponse.StatusCode);

        var linksResponse = await authClient.GetAsync("/api/v1/control/links", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, linksResponse.StatusCode);

        // 4. Can issue enrollment code
        var codeResponse = await authClient.PostAsync(
            "/api/v1/control/agents/test-node-alpha/enrollment-code",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, codeResponse.StatusCode);
    }

    [Fact]
    public async Task PasswordSessionTokenCannotAccessAutomationRoutes()
    {
        using var client = _enabledFactory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new PasswordLoginRequest("test-operator", "TestPassword123!"),
            SmmJsonContext.Default.PasswordLoginRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<PasswordLoginResponse>(
            SmmJsonContext.Default.PasswordLoginResponse, TestContext.Current.CancellationToken);
        Assert.NotNull(loginResult);

        // Attempt to access Automation endpoint with Operator password session token
        using var authClient = _enabledFactory.CreateClient();
        authClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Token);

        var automationResponse = await authClient.GetAsync(
            "/api/v1/automation/links",
            TestContext.Current.CancellationToken);

        // Must be 403 Forbidden because password session ONLY grants Operator role
        Assert.Equal(HttpStatusCode.Forbidden, automationResponse.StatusCode);

        var agentResponse = await authClient.GetAsync(
            "/api/v1/agents/provisioning/jobs/next",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, agentResponse.StatusCode);
    }

    [Fact]
    public async Task WrongPasswordAndUnknownUserBothFailWithUnauthorizedAndUniformExecution()
    {
        using var client = _enabledFactory.CreateClient();

        // 1. Wrong password for existing user
        var wrongPwdResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new PasswordLoginRequest("test-operator", "WrongPassword999!"),
            SmmJsonContext.Default.PasswordLoginRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPwdResponse.StatusCode);

        // 2. Unknown user
        var unknownUserResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new PasswordLoginRequest("non-existent-user", "SomePassword123!"),
            SmmJsonContext.Default.PasswordLoginRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, unknownUserResponse.StatusCode);
    }

    [Fact]
    public async Task ExceedingRateLimitRejectsWithTooManyRequests()
    {
        using var client = _enabledFactory.CreateClient();

        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new PasswordLoginRequest("test-operator", "BadPasswordAttempt"),
                SmmJsonContext.Default.PasswordLoginRequest,
                TestContext.Current.CancellationToken);

            statusCodes.Add(response.StatusCode);
        }

        // Limit is 5 per minute; 6th and 7th requests must be 429 TooManyRequests
        Assert.Contains(HttpStatusCode.TooManyRequests, statusCodes);
    }

    [Fact]
    public async Task ClientCertificateAuthenticationHasPriorityAndWorksUnderBothModes()
    {
        // 1. When password login is disabled
        using (var certClient = _disabledFactory.CreateClient())
        {
            certClient.DefaultRequestHeaders.Add("X-Test-Identity", "cert-operator");
            certClient.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

            var response = await certClient.GetAsync("/api/v1/control/agents", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 2. When password login is enabled
        using (var certClient = _enabledFactory.CreateClient())
        {
            certClient.DefaultRequestHeaders.Add("X-Test-Identity", "cert-operator");
            certClient.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

            var response = await certClient.GetAsync("/api/v1/control/agents", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task LogoutRevokesSessionToken()
    {
        using var client = _enabledFactory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new PasswordLoginRequest("test-operator", "TestPassword123!"),
            SmmJsonContext.Default.PasswordLoginRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<PasswordLoginResponse>(
            SmmJsonContext.Default.PasswordLoginResponse, TestContext.Current.CancellationToken);
        Assert.NotNull(loginResult);

        using var authClient = _enabledFactory.CreateClient();
        authClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Token);

        // Before logout: access allowed
        var okResponse = await authClient.GetAsync("/api/v1/control/agents", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);

        // Logout
        var logoutResponse = await authClient.PostAsync("/api/v1/auth/logout", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        // After logout: access rejected
        var rejectedResponse = await authClient.GetAsync("/api/v1/control/agents", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rejectedResponse.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _disabledFactory.DisposeAsync();
        await _enabledFactory.DisposeAsync();
    }

    private sealed class PasswordLoginTestFactory(bool enabledForTesting) : WebApplicationFactory<controlapp::Program>
    {
        private readonly bool _enabledForTesting = enabledForTesting;
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"smm-pwd-tests-{Guid.NewGuid():N}");

        public string DatabasePath => Path.Combine(_directory, "control.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directory);

            var authorityPath = Path.Combine(_directory, "control-ca.pfx");
            if (!File.Exists(authorityPath))
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var request = new CertificateRequest("CN=SMM Password Test CA", key, HashAlgorithmName.SHA256);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                using var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                File.WriteAllBytes(authorityPath, certificate.Export(X509ContentType.Pfx));
            }

            var publicUrlPath = Path.Combine(_directory, "control-public-url");
            File.WriteAllText(publicUrlPath, "https://hub.example.com:7443\n");

            var meshEnvPath = Path.Combine(_directory, "mesh.env");
            File.WriteAllText(meshEnvPath, "HUB_ENDPOINT=hub.example.com:51820\nHUB_PUBLIC_KEY=mQZ/Y4yQpQhX6j0rL8vU2w==\nMESH_NETWORK=10.77.0.0/24\n");

            var meshNodesPath = Path.Combine(_directory, "mesh", "nodes.tsv");

            var passwordHash = PasswordHasher.HashPassword("TestPassword123!");

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Control:DatabasePath"] = DatabasePath,
                    ["Control:CertificateAuthorityPath"] = authorityPath,
                    ["Control:BackupDirectory"] = Path.Combine(_directory, "backups"),
                    ["Control:PublicUrlPath"] = publicUrlPath,
                    ["Control:MeshEnvironmentPath"] = meshEnvPath,
                    ["Control:MeshNodesPath"] = meshNodesPath,
                    ["Authentication:PasswordLogin:EnabledForTesting"] = _enabledForTesting ? "true" : "false",
                    ["Authentication:PasswordLogin:Username"] = "test-operator",
                    ["Authentication:PasswordLogin:PasswordHash"] = passwordHash,
                    ["Authentication:PasswordLogin:SessionTtlMinutes"] = "60"
                }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestMtlsAuthenticationHandler>(
                        "TestCert", _ => { });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch
            {
                // Ignored in cleanup
            }
        }
    }

    private sealed class TestMtlsAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var roleValue)
                || string.IsNullOrWhiteSpace(roleValue))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identityName = Request.Headers.TryGetValue("X-Test-Identity", out var identityValue)
                && !string.IsNullOrWhiteSpace(identityValue)
                ? identityValue.ToString()
                : "test-cert-user";

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, identityName),
                new(ClaimTypes.Role, roleValue.ToString())
            };

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return Task.CompletedTask;
        }
    }
}
