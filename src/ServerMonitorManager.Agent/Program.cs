using Microsoft.Extensions.Configuration;
using ServerMonitorManager.Agent;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SMM_")
    .AddCommandLine(args)
    .Build();
var options = configuration.Get<AgentOptions>() ?? new AgentOptions();
if (string.IsNullOrWhiteSpace(options.NodeId)
    || !options.NodeId.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
{
    Console.Error.WriteLine("NodeId must contain lowercase letters, digits, or hyphens.");
    return 2;
}
if (options.HeartbeatSeconds is < 10 or > 300
    || options.BufferMaxSamples is < 10 or > 10_000
    || options.BufferRecentSamples is < 1
    || options.BufferRecentSamples >= options.BufferMaxSamples
    || options.BufferDownsampleFactor is < 2 or > 100
    || options.UploadBatchSize is < 1 or > 100
    || options.MaxRetrySeconds is < 10 or > 3600
    || !Path.IsPathFullyQualified(options.ProvisioningSocketPath))
{
    Console.Error.WriteLine(
        "Invalid buffer settings: heartbeat 10-300s, max samples 10-10000, recent samples below max, "
        + "downsample factor 2-100, batch size 1-100, retry 10-3600s.");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
var client = new AgentClient(options);
var enrollmentToken = configuration["EnrollToken"];
if (!string.IsNullOrWhiteSpace(enrollmentToken))
{
    await client.EnrollAsync(enrollmentToken, shutdown.Token);
    Console.WriteLine("Agent enrollment completed.");
    return 0;
}

await client.RunAsync(shutdown.Token);
return 0;
