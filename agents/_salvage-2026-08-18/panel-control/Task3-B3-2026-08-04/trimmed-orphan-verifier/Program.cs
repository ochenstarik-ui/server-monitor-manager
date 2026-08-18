using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;

var root = Path.Combine(Path.GetTempPath(), $"smm-trimmed-orphan-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var databasePath = Path.Combine(root, "control.db");
try
{
    var loadedControlAssembly = typeof(ControlStore).Assembly.Location;
    var loadedControlSha256 = Convert.ToHexString(
        SHA256.HashData(await File.ReadAllBytesAsync(loadedControlAssembly))).ToLowerInvariant();
    var store = new ControlStore(Options.Create(new ControlOptions
    {
        DatabasePath = databasePath,
        CertificateAuthorityPath = Path.Combine(root, "unused.pfx")
    }));
    await store.InitializeAsync();

    var orphan = new LinkRule("source", "target", "tcp", 2222);
    var applier = new OrphanApplier(orphan);
    var service = new LinkService(store, applier, new ControlEventBroker());
    var result = await service.ReconcileAllAsync();

    Require(result.Examined == 1, $"Examined={result.Examined}");
    Require(result.Converged == 1, $"Converged={result.Converged}");
    Require(result.Failed == 0, $"Failed={result.Failed}");
    Require(result.Deferred == 0, $"Deferred={result.Deferred}");
    Require(applier.ListCalls == 2, $"ListCalls={applier.ListCalls}");
    Require(applier.RawDisconnectCalls == 1, $"RawDisconnectCalls={applier.RawDisconnectCalls}");

    await using var connection = new SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync();
    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT actor, action, subject, details_json
        FROM audit
        WHERE action = 'link.orphan-removed'
        ORDER BY sequence;
        """;
    await using var reader = await command.ExecuteReaderAsync();
    Require(await reader.ReadAsync(), "orphan audit row missing");
    var actor = reader.GetString(0);
    var action = reader.GetString(1);
    var subject = reader.GetString(2);
    var detailsJson = reader.GetString(3);
    Require(!await reader.ReadAsync(), "duplicate orphan audit rows");
    Require(actor == "system:reconcile", $"actor={actor}");
    Require(action == "link.orphan-removed", $"action={action}");
    Require(subject == "source:target:tcp:2222", $"subject={subject}");

    using var details = JsonDocument.Parse(detailsJson);
    var rootElement = details.RootElement;
    Require(rootElement.GetProperty("sourceNodeId").GetString() == "source", detailsJson);
    Require(rootElement.GetProperty("targetNodeId").GetString() == "target", detailsJson);
    Require(rootElement.GetProperty("protocol").GetString() == "tcp", detailsJson);
    Require(rootElement.GetProperty("port").GetInt32() == 2222, detailsJson);
    Require(rootElement.EnumerateObject().Count() == 4, detailsJson);

    Console.WriteLine("TRIMMED_ORPHAN_AUDIT=PASS");
    Console.WriteLine($"LOADED_CONTROL_ASSEMBLY={loadedControlAssembly}");
    Console.WriteLine($"LOADED_CONTROL_SHA256={loadedControlSha256}");
    Console.WriteLine($"RESULT={result.Examined}/{result.Converged}/{result.Failed}/{result.Deferred}");
    Console.WriteLine($"HELPER_CALLS=list:{applier.ListCalls},raw-disconnect:{applier.RawDisconnectCalls}");
    Console.WriteLine($"AUDIT={actor}|{action}|{subject}|{detailsJson}");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class OrphanApplier(LinkRule orphan) : ILinkPolicyApplier
{
    private bool _connected = true;
    public int ListCalls { get; private set; }
    public int RawDisconnectCalls { get; private set; }

    public Task<IReadOnlyList<LinkRule>> ListRulesAsync(CancellationToken cancellationToken)
    {
        ListCalls++;
        return Task.FromResult<IReadOnlyList<LinkRule>>(_connected ? [orphan] : []);
    }

    public Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Unexpected connect.");

    public Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Unexpected persisted disconnect.");

    public Task ApplyDisconnectAsync(LinkRule rule, CancellationToken cancellationToken)
    {
        if (rule != orphan)
        {
            throw new InvalidOperationException($"Unexpected raw rule: {rule}");
        }
        RawDisconnectCalls++;
        _connected = false;
        return Task.CompletedTask;
    }

    public Task<bool> IsConnectedAsync(LinkPolicy link, CancellationToken cancellationToken)
        => throw new InvalidOperationException("link-status must not be used.");
}
