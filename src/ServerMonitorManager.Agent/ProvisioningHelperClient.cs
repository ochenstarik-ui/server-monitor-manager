using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Agent;

public sealed class ProvisioningHelperClient(string socketPath)
{
    private const int MaximumResponseBytes = 16 * 1024;

    public async Task<ProvisioningPreflightResult> RunPreflightAsync(
        ProvisioningJob job,
        CancellationToken cancellationToken)
    {
        var request = new ProvisioningHelperRequest(
            "1", job.Id, job.ActionType, job.SchemaVersion,
            ProvisioningActionCatalog.PreflightModuleHash, job.Parameters);
        var response = await SendAsync(request, cancellationToken);
        if (!response.Success || response.Preflight is null)
        {
            throw new InvalidOperationException($"Provisioning helper rejected the request: {response.Code}");
        }
        return response.Preflight;
    }

    public async Task<SystemBaseInstallPlan> CreateBaseInstallPlanAsync(
        ProvisioningJob job,
        CancellationToken cancellationToken)
    {
        var request = new ProvisioningHelperRequest(
            "1", job.Id, job.ActionType, job.SchemaVersion,
            ProvisioningActionCatalog.SystemBaseInstallModuleHash, job.Parameters);
        var response = await SendAsync(request, cancellationToken);
        if (!response.Success || response.BaseInstallPlan is null)
        {
            throw new InvalidOperationException($"Provisioning helper rejected the request: {response.Code}");
        }
        return response.BaseInstallPlan;
    }

    public async Task<ProvisioningBaseInstallExecutionResult> ExecuteBaseInstallAsync(
        ProvisioningJob job,
        ProvisioningBaseInstallExecutionAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            CreateBaseInstallExecutionRequest(job, authorization), cancellationToken);
        return ValidateBaseInstallExecutionResponse(response, authorization);
    }

    internal static ProvisioningBaseInstallExecutionResult ValidateBaseInstallExecutionResponse(
        ProvisioningHelperResponse response,
        ProvisioningBaseInstallExecutionAuthorization authorization)
    {
        if (response.BaseInstallExecution is null)
        {
            throw new InvalidOperationException(
                $"Provisioning helper returned no execution result: {response.Code}");
        }
        var result = response.BaseInstallExecution;
        if (response.Success != result.Success
            || (result.Success && (!result.Verified
                || result.RollbackAttempted
                || result.RollbackSucceeded
                || !string.Equals(
                    result.ObservedTimezone,
                    authorization.Plan.Timezone,
                    StringComparison.Ordinal)))
            || (result.RollbackSucceeded && !result.RollbackAttempted))
        {
            throw new InvalidDataException("Provisioning helper returned an inconsistent execution result.");
        }
        return result;
    }

    internal static ProvisioningHelperRequest CreateBaseInstallExecutionRequest(
        ProvisioningJob job,
        ProvisioningBaseInstallExecutionAuthorization authorization)
        => new(
            ProvisioningExecutionGrantCodec.ProtocolVersion,
            job.Id,
            job.ActionType,
            job.SchemaVersion,
            ProvisioningActionCatalog.SystemBaseInstallModuleHash,
            job.Parameters,
            authorization);

    private async Task<ProvisioningHelperResponse> SendAsync(
        ProvisioningHelperRequest request,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var json = JsonSerializer.Serialize(request, SmmJsonContext.Default.ProvisioningHelperRequest) + "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
        var payload = await ReadResponseAsync(stream, cancellationToken);
        var response = JsonSerializer.Deserialize(payload, SmmJsonContext.Default.ProvisioningHelperResponse)
            ?? throw new InvalidDataException("Provisioning helper returned an empty response.");
        return response;
    }

    private static async Task<byte[]> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var singleByte = new byte[1];
        while (buffer.Length <= MaximumResponseBytes)
        {
            var count = await stream.ReadAsync(singleByte, cancellationToken);
            if (count == 0 || singleByte[0] == (byte)'\n')
            {
                break;
            }
            buffer.WriteByte(singleByte[0]);
        }
        if (buffer.Length == 0 || buffer.Length > MaximumResponseBytes)
        {
            throw new InvalidDataException("Provisioning helper response size is invalid.");
        }
        return buffer.ToArray();
    }
}
