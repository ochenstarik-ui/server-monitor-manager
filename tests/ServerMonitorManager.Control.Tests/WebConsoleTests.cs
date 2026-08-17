extern alias controlapp;

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
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

public sealed class WebConsoleTests : IAsyncDisposable
{
    private readonly WebConsoleTestFactory _factory = new();

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/style.css")]
    [InlineData("/app.js")]
    [InlineData("/console")]
    public async Task AnonymousAccessToWebConsoleRoutesIsRejectedWithUnauthorized(string path)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/style.css")]
    [InlineData("/app.js")]
    public async Task AgentRoleIsForbiddenFromWebConsole(string path)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "agent-node-01");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Agent");

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/style.css")]
    [InlineData("/app.js")]
    public async Task AutomationRoleIsForbiddenFromWebConsole(string path)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "automation-worker-01");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Automation");

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OperatorCanAccessWebConsoleHtmlAndAssets()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "operator-admin");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

        var indexResponse = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        Assert.Contains("text/html", indexResponse.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);

        var cssResponse = await client.GetAsync("/style.css", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
        Assert.Contains("text/css", cssResponse.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);

        var jsResponse = await client.GetAsync("/app.js", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, jsResponse.StatusCode);
        Assert.Contains("javascript", jsResponse.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebConsoleHtmlContainsRequiredUiElementsAndWarning()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "operator-admin");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

        var response = await client.GetAsync("/index.html", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Header & Actions
        Assert.Contains("id=\"add-node-btn\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"refresh-btn\"", html, StringComparison.Ordinal);

        // Tables & Counts
        Assert.Contains("id=\"nodes-table\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"links-table\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"nodes-count\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"links-count\"", html, StringComparison.Ordinal);

        // Modal & Form
        Assert.Contains("id=\"add-node-modal\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"node-id-input\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"generate-code-btn\"", html, StringComparison.Ordinal);

        // Result displays
        Assert.Contains("id=\"ca-fingerprint-display\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"enrollment-code-display\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"copy-code-btn\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"copy-fingerprint-btn\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"countdown-timer\"", html, StringComparison.Ordinal);

        // Security verification requirement for CA fingerprint
        Assert.Contains("ОБЯЗАТЕЛЬНО К ПРОВЕРКЕ", html, StringComparison.Ordinal);
        Assert.Contains("отпечаток", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbeddedAssetsAreServedWhenDiskWebRootIsMissing()
    {
        await using var embeddedFactory = new WebConsoleEmbeddedOnlyTestFactory();
        using var client = embeddedFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "operator-admin");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

        // 1. Root /
        var rootResponse = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        Assert.Contains("text/html", rootResponse.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        var rootHtml = await rootResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(rootHtml);
        Assert.Contains("id=\"nodes-table\"", rootHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"add-node-btn\"", rootHtml, StringComparison.Ordinal);

        // 2. /index.html
        var indexResponse = await client.GetAsync("/index.html", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        Assert.Contains("text/html", indexResponse.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        var indexHtml = await indexResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(indexHtml);
        Assert.Contains("id=\"links-table\"", indexHtml, StringComparison.Ordinal);

        // 3. /style.css
        var cssResponse = await client.GetAsync("/style.css", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
        Assert.Contains("text/css", cssResponse.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        var cssContent = await cssResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(cssContent);
        Assert.Contains(".app-container", cssContent, StringComparison.Ordinal);

        // 4. /app.js
        var jsResponse = await client.GetAsync("/app.js", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, jsResponse.StatusCode);
        Assert.Contains("javascript", jsResponse.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        var jsContent = await jsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(jsContent);
        Assert.Contains("loadDashboardData", jsContent, StringComparison.Ordinal);

        // 5. /console alias
        var consoleResponse = await client.GetAsync("/console", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, consoleResponse.StatusCode);
        Assert.Contains("text/html", consoleResponse.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private sealed class WebConsoleEmbeddedOnlyTestFactory : WebApplicationFactory<controlapp::Program>
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"smm-web-console-embedded-{Guid.NewGuid():N}");

        public string DatabasePath => Path.Combine(_directory, "control.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directory);
            var emptyWebRoot = Path.Combine(_directory, "empty-webroot-no-files");
            Directory.CreateDirectory(emptyWebRoot);
            builder.UseWebRoot(emptyWebRoot);

            var authorityPath = Path.Combine(_directory, "control-ca.pfx");
            if (!File.Exists(authorityPath))
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var request = new CertificateRequest("CN=SMM Web Console Embedded CA", key, HashAlgorithmName.SHA256);
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

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Control:DatabasePath"] = DatabasePath,
                    ["Control:CertificateAuthorityPath"] = authorityPath,
                    ["Control:BackupDirectory"] = Path.Combine(_directory, "backups"),
                    ["Control:PublicUrlPath"] = publicUrlPath,
                    ["Control:MeshEnvironmentPath"] = meshEnvPath,
                    ["Control:MeshNodesPath"] = meshNodesPath
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
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch
            {
                // Ignored in test cleanup
            }
        }
    }

    private sealed class WebConsoleTestFactory : WebApplicationFactory<controlapp::Program>
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"smm-web-console-tests-{Guid.NewGuid():N}");

        public string DatabasePath => Path.Combine(_directory, "control.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directory);
            var authorityPath = Path.Combine(_directory, "control-ca.pfx");
            if (!File.Exists(authorityPath))
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var request = new CertificateRequest("CN=SMM Web Console Test CA", key, HashAlgorithmName.SHA256);
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

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Control:DatabasePath"] = DatabasePath,
                    ["Control:CertificateAuthorityPath"] = authorityPath,
                    ["Control:BackupDirectory"] = Path.Combine(_directory, "backups"),
                    ["Control:PublicUrlPath"] = publicUrlPath,
                    ["Control:MeshEnvironmentPath"] = meshEnvPath,
                    ["Control:MeshNodesPath"] = meshNodesPath
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
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch
            {
                // Ignored in test cleanup
            }
        }
    }

    private sealed class TestAuthenticationHandler(
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
                : "test-user";

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, identityName),
                new(ClaimTypes.Role, roleValue.ToString())
            };

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
