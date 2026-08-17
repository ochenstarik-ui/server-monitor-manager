extern alias controlapp;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class NodeEnrollmentCodeTests : IAsyncDisposable
{
    private readonly NodeEnrollmentTestFactory _factory = new();

    [Fact]
    public async Task AnonymousRequestIsRejectedWithUnauthorized()
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsync(
            "/api/v1/control/agents/test-node/enrollment-code",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AgentRoleIsRejectedWithForbidden()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "agent-node");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Agent");

        var response = await client.PostAsync(
            "/api/v1/control/agents/test-node/enrollment-code",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AutomationRoleIsRejectedWithForbidden()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "automation-worker");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Automation");

        var response = await client.PostAsync(
            "/api/v1/control/agents/test-node/enrollment-code",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("NODE_UPPERCASE")]
    [InlineData("node_with_underscore")]
    [InlineData("node with spaces")]
    [InlineData("this-node-name-is-way-too-long-and-exceeds-the-maximum-sixty-three-characters-limit")]
    public async Task InvalidNodeIdIsRejectedWithBadRequestBeforeTokenCreation(string invalidNodeId)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "operator-1");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

        var response = await client.PostAsync(
            $"/api/v1/control/agents/{Uri.EscapeDataString(invalidNodeId)}/enrollment-code",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify that no token was generated in database
        var store = _factory.Services.GetRequiredService<ControlStore>();
        var dbPath = _factory.DatabasePath;
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM enrollment_tokens;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task EmptyNodeIdThrowsArgumentExceptionInService()
    {
        var service = _factory.Services.GetRequiredService<NodeEnrollmentService>();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateEnrollmentCodeAsync(string.Empty, "operator-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OperatorCanRequestNodeEnrollmentCodeWithCorrectStructure()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "operator-admin");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

        var response = await client.PostAsync(
            "/api/v1/control/agents/node-alpha/enrollment-code",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<NodeEnrollmentCodeResponse>(
            SmmJsonContext.Default.NodeEnrollmentCodeResponse,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("node-alpha", result.NodeId);
        Assert.False(string.IsNullOrWhiteSpace(result.CaFingerprint));
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(9));
        Assert.True(result.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(11));

        // Format check: SMMNODE2.<control_url>.<ca_pem>.<node_id>.<token>.<hub_endpoint>.<hub_public_key>.<node_address>.<mesh_network>
        var segments = result.Code.Split('.');
        Assert.Equal(9, segments.Length);
        Assert.Equal("SMMNODE2", segments[0]);

        var controlUrl = DecodeBase64UrlString(segments[1]);
        var caPem = DecodeBase64UrlString(segments[2]);
        var nodeId = DecodeBase64UrlString(segments[3]);
        var token = DecodeBase64UrlString(segments[4]);
        var hubEndpoint = DecodeBase64UrlString(segments[5]);
        var hubPublicKey = DecodeBase64UrlString(segments[6]);
        var nodeAddress = DecodeBase64UrlString(segments[7]);
        var meshNetwork = DecodeBase64UrlString(segments[8]);

        Assert.Equal("https://hub.example.com:7443", controlUrl);
        Assert.Contains("-----BEGIN CERTIFICATE-----", caPem);
        Assert.Equal("node-alpha", nodeId);
        Assert.Equal(43, token.Length); // 32 bytes base64url unpadded is 43 chars
        Assert.Equal("hub.example.com:51820", hubEndpoint);
        Assert.Equal("mQZ/Y4yQpQhX6j0rL8vU2w==", hubPublicKey);
        Assert.Equal("10.77.0.2", nodeAddress);
        Assert.Equal("10.77.0.0/24", meshNetwork);
    }

    [Fact]
    public async Task TwoDifferentNodeIdsGetDistinctMeshAddresses()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "operator-admin");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

        var response1 = await client.PostAsync(
            "/api/v1/control/agents/node-first/enrollment-code",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        var result1 = await response1.Content.ReadFromJsonAsync<NodeEnrollmentCodeResponse>(
            SmmJsonContext.Default.NodeEnrollmentCodeResponse,
            TestContext.Current.CancellationToken);

        var response2 = await client.PostAsync(
            "/api/v1/control/agents/node-second/enrollment-code",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var result2 = await response2.Content.ReadFromJsonAsync<NodeEnrollmentCodeResponse>(
            SmmJsonContext.Default.NodeEnrollmentCodeResponse,
            TestContext.Current.CancellationToken);

        var address1 = DecodeBase64UrlString(result1!.Code.Split('.')[7]);
        var address2 = DecodeBase64UrlString(result2!.Code.Split('.')[7]);

        Assert.Equal("10.77.0.2", address1);
        Assert.Equal("10.77.0.3", address2);
        Assert.NotEqual(address1, address2);
    }

    [Fact]
    public async Task RepeatedRequestForSameNodeIdReusesReservedAddress()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "operator-admin");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

        var response1 = await client.PostAsync(
            "/api/v1/control/agents/node-repeat/enrollment-code",
            null,
            TestContext.Current.CancellationToken);
        var result1 = await response1.Content.ReadFromJsonAsync<NodeEnrollmentCodeResponse>(
            SmmJsonContext.Default.NodeEnrollmentCodeResponse,
            TestContext.Current.CancellationToken);

        var response2 = await client.PostAsync(
            "/api/v1/control/agents/node-repeat/enrollment-code",
            null,
            TestContext.Current.CancellationToken);
        var result2 = await response2.Content.ReadFromJsonAsync<NodeEnrollmentCodeResponse>(
            SmmJsonContext.Default.NodeEnrollmentCodeResponse,
            TestContext.Current.CancellationToken);

        var address1 = DecodeBase64UrlString(result1!.Code.Split('.')[7]);
        var address2 = DecodeBase64UrlString(result2!.Code.Split('.')[7]);

        Assert.Equal("10.77.0.2", address1);
        Assert.Equal("10.77.0.2", address2);

        // Tokens should be distinct per request
        var token1 = DecodeBase64UrlString(result1.Code.Split('.')[4]);
        var token2 = DecodeBase64UrlString(result2.Code.Split('.')[4]);
        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public async Task AuditLogRecordsEnrollmentCodeIssuance()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "operator-carol");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

        var response = await client.PostAsync(
            "/api/v1/control/agents/audited-node/enrollment-code",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var conn = new SqliteConnection($"Data Source={_factory.DatabasePath}");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actor, action, subject, details_json FROM audit WHERE action = 'agent.enrollment_code.issued';";
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));

        var actor = reader.GetString(0);
        var action = reader.GetString(1);
        var subject = reader.GetString(2);
        var detailsJson = reader.GetString(3);

        Assert.Equal("operator-carol", actor);
        Assert.Equal("agent.enrollment_code.issued", action);
        Assert.Equal("audited-node", subject);
        Assert.Contains("audited-node", detailsJson);
        Assert.Contains("10.77.0.2", detailsJson);
    }

    [Fact]
    public async Task CodeStructureMatchesBashReferenceFormatFixture()
    {
        // Fixture matching the reference implementation in create_node_code:
        // SMMNODE2.<control_url>.<ca_pem>.<node_id>.<token>.<hub_endpoint>.<hub_public_key>.<node_address>.<mesh_network>
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "operator-admin");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");

        var response = await client.PostAsync(
            "/api/v1/control/agents/reference-node/enrollment-code",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<NodeEnrollmentCodeResponse>(
            SmmJsonContext.Default.NodeEnrollmentCodeResponse,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var parts = result.Code.Split('.');
        Assert.Equal(9, parts.Length);
        Assert.Equal("SMMNODE2", parts[0]);

        // Validate each segment character set (base64url characters only, no =)
        for (int i = 1; i < parts.Length; i++)
        {
            Assert.Matches("^[A-Za-z0-9_-]+$", parts[i]);
            Assert.DoesNotContain("=", parts[i]);
        }
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static string DecodeBase64UrlString(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private sealed class NodeEnrollmentTestFactory : WebApplicationFactory<controlapp::Program>
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"smm-node-enrollment-tests-{Guid.NewGuid():N}");

        public string DatabasePath => Path.Combine(_directory, "control.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directory);
            var authorityPath = Path.Combine(_directory, "control-ca.pfx");
            if (!File.Exists(authorityPath))
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var request = new CertificateRequest("CN=SMM Node Code Test CA", key, HashAlgorithmName.SHA256);
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
                // Best effort cleanup in tests
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
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
