using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Provisioning.Helper;

public sealed class ProvisioningHelperServer
{
    private const int MaximumRequestBytes = 16 * 1024;
    private const int SocketOptionLevel = 1;
    private const int SocketPeerCredentials = 17;
    private readonly string _socketPath;
    private readonly uint _expectedPeerUserId;
    private readonly TimezoneProvisioningExecutor? _timezoneExecutor;
    private readonly TimeSpan _connectionTimeout;
    private readonly int _maximumConcurrentConnections;
    private readonly int _requestsPerMinute;
    private readonly int _unauthorizedAttemptsPerMinute;
    private readonly int _globalUnauthorizedAttemptsPerMinute;
    private readonly TimeProvider _timeProvider;
    private readonly object _rateLimitLock = new();
    private readonly Queue<DateTimeOffset> _recentRequests = new();
    private readonly Queue<DateTimeOffset> _recentUnauthorizedAttempts = new();
    private readonly Dictionary<uint, Queue<DateTimeOffset>> _recentUnauthorizedAttemptsByUser = [];
    private DateTimeOffset? _lastRequestRateLimitLog;

    public ProvisioningHelperServer(
        string socketPath,
        uint expectedPeerUserId,
        TimezoneProvisioningExecutor? timezoneExecutor = null,
        TimeSpan? connectionTimeout = null,
        int maximumConcurrentConnections = 4,
        int requestsPerMinute = 120,
        int unauthorizedAttemptsPerMinute = 30,
        int globalUnauthorizedAttemptsPerMinute = 120,
        TimeProvider? timeProvider = null)
    {
        if (maximumConcurrentConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentConnections),
                "Connection limit must be positive.");
        }
        if (requestsPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestsPerMinute),
                "Request limit must be positive.");
        }
        if (connectionTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectionTimeout),
                "Connection timeout must be positive.");
        }
        if (unauthorizedAttemptsPerMinute <= 0 || globalUnauthorizedAttemptsPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unauthorizedAttemptsPerMinute),
                "Unauthorized connection limits must be positive.");
        }

        _socketPath = socketPath;
        _expectedPeerUserId = expectedPeerUserId;
        _timezoneExecutor = timezoneExecutor;
        _connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(30);
        _maximumConcurrentConnections = maximumConcurrentConnections;
        _requestsPerMinute = requestsPerMinute;
        _unauthorizedAttemptsPerMinute = unauthorizedAttemptsPerMinute;
        _globalUnauthorizedAttemptsPerMinute = globalUnauthorizedAttemptsPerMinute;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    [SupportedOSPlatform("linux")]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);
        File.Delete(_socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        File.SetUnixFileMode(_socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        listener.Listen(8);
        using var connectionSlots = new SemaphoreSlim(_maximumConcurrentConnections);
        var handlers = new List<Task>(_maximumConcurrentConnections);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await connectionSlots.WaitAsync(cancellationToken);
                Socket? connection = null;
                try
                {
                    connection = await listener.AcceptAsync(cancellationToken);
                    handlers.RemoveAll(static task => task.IsCompleted);
                    handlers.Add(Task.Run(
                        () => HandleConnectionAsync(
                            connection,
                            connectionSlots,
                            cancellationToken),
                        CancellationToken.None));
                }
                catch
                {
                    connection?.Dispose();
                    connectionSlots.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(handlers);
            File.Delete(_socketPath);
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task HandleConnectionAsync(
        Socket connection,
        SemaphoreSlim connectionSlots,
        CancellationToken serverCancellationToken)
    {
        try
        {
            var credentials = GetPeerCredentials(connection);
            if (credentials.UserId != _expectedPeerUserId)
            {
                if (TryConsumeUnauthorizedAttempt(credentials.UserId))
                {
                    Console.Error.WriteLine(
                        $"Provisioning helper rejected peer uid {credentials.UserId}.");
                }
                connection.Dispose();
                return;
            }
            if (!TryConsumeRequest())
            {
                if (ShouldLogRequestRateLimit())
                {
                    Console.Error.WriteLine(
                        $"Provisioning helper rate limit exceeded for uid {credentials.UserId}.");
                }
                connection.Dispose();
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                serverCancellationToken);
            timeout.CancelAfter(_connectionTimeout);
            try
            {
                await HandleAsync(connection, timeout.Token);
            }
            catch (OperationCanceledException) when (!serverCancellationToken.IsCancellationRequested)
            {
                connection.Dispose();
                Console.Error.WriteLine(
                    $"Provisioning helper connection timed out for uid {credentials.UserId}.");
            }
        }
        catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
        {
            connection.Dispose();
        }
        catch (Exception exception)
        {
            connection.Dispose();
            Console.Error.WriteLine(
                $"Provisioning helper rejected a local connection: {exception.GetType().Name}.");
        }
        finally
        {
            connectionSlots.Release();
        }
    }

    private bool TryConsumeRequest()
    {
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - TimeSpan.FromMinutes(1);
        lock (_rateLimitLock)
        {
            while (_recentRequests.TryPeek(out var timestamp) && timestamp <= cutoff)
            {
                _recentRequests.Dequeue();
            }
            if (_recentRequests.Count >= _requestsPerMinute)
            {
                return false;
            }
            _recentRequests.Enqueue(now);
            return true;
        }
    }

    private bool TryConsumeUnauthorizedAttempt(uint userId)
    {
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - TimeSpan.FromMinutes(1);
        lock (_rateLimitLock)
        {
            TrimExpired(_recentUnauthorizedAttempts, cutoff);
            if (!_recentUnauthorizedAttemptsByUser.TryGetValue(userId, out var userAttempts))
            {
                userAttempts = new Queue<DateTimeOffset>();
                _recentUnauthorizedAttemptsByUser[userId] = userAttempts;
            }
            TrimExpired(userAttempts, cutoff);
            if (_recentUnauthorizedAttempts.Count >= _globalUnauthorizedAttemptsPerMinute
                || userAttempts.Count >= _unauthorizedAttemptsPerMinute)
            {
                return false;
            }
            _recentUnauthorizedAttempts.Enqueue(now);
            userAttempts.Enqueue(now);
            return true;
        }
    }

    private bool ShouldLogRequestRateLimit()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_rateLimitLock)
        {
            if (_lastRequestRateLimitLog is { } lastLog
                && lastLog > now - TimeSpan.FromMinutes(1))
            {
                return false;
            }
            _lastRequestRateLimitLog = now;
            return true;
        }
    }

    private static void TrimExpired(Queue<DateTimeOffset> attempts, DateTimeOffset cutoff)
    {
        while (attempts.TryPeek(out var timestamp) && timestamp <= cutoff)
        {
            attempts.Dequeue();
        }
    }

    [SupportedOSPlatform("linux")]
    private static UnixPeerCredentials GetPeerCredentials(Socket socket)
    {
        Span<byte> rawCredentials = stackalloc byte[12];
        if (socket.GetRawSocketOption(
                SocketOptionLevel,
                SocketPeerCredentials,
                rawCredentials) != rawCredentials.Length)
        {
            throw new InvalidDataException("SO_PEERCRED returned an invalid credential length.");
        }
        return new UnixPeerCredentials(
            BitConverter.ToInt32(rawCredentials),
            BitConverter.ToUInt32(rawCredentials[4..]),
            BitConverter.ToUInt32(rawCredentials[8..]));
    }

    public static ProvisioningHelperResponse Execute(ProvisioningHelperRequest request)
    {
        var validationFailure = ValidateEnvelope(request);
        return validationFailure ?? ExecuteValidated(request);
    }

    private static ProvisioningHelperResponse ExecuteValidated(ProvisioningHelperRequest request)
        => request.ActionType switch
        {
            "preflight" => ExecutePreflight(request),
            "system.base-install" => request.Execution is null
                ? CreateBaseInstallPlan(request)
                : Failure("execution.unavailable", "Provisioning execution is unavailable."),
            _ => Failure("action.denied", "The requested action is not allowed.")
        };

    private async Task<ProvisioningHelperResponse> ExecuteRequestAsync(
        ProvisioningHelperRequest request,
        CancellationToken cancellationToken)
    {
        var validationFailure = ValidateEnvelope(request);
        if (validationFailure is not null)
        {
            return validationFailure;
        }
        if (request.Execution is null
            || !string.Equals(request.ActionType, "system.base-install", StringComparison.Ordinal)
            || _timezoneExecutor is null)
        {
            return ExecuteValidated(request);
        }
        var result = await _timezoneExecutor.ExecuteAsync(request, cancellationToken);
        return new ProvisioningHelperResponse(
            result.Success, result.Code, result.Message, null, null, result);
    }

    private static ProvisioningHelperResponse? ValidateEnvelope(ProvisioningHelperRequest request)
    {
        if (request.ProtocolVersion != "1")
        {
            return Failure("protocol.unsupported", "Unsupported helper protocol version.");
        }
        if (request.JobId is not { Length: 32 } || !request.JobId.All(Uri.IsHexDigit))
        {
            return Failure("request.invalid-job", "Invalid provisioning job identifier.");
        }
        return request.SchemaVersion == 1 && request.Parameters.ValueKind == JsonValueKind.Object
            ? null
            : Failure("action.denied", "The requested action is not allowed.");
    }

    private static ProvisioningHelperResponse ExecutePreflight(ProvisioningHelperRequest request)
    {
        if (request.ModuleHash != ProvisioningActionCatalog.PreflightModuleHash
            || request.Parameters.EnumerateObject().Any())
        {
            return Failure("action.denied", "The requested action is not allowed.");
        }

        var release = ReadOperatingSystemRelease();
        var result = new ProvisioningPreflightResult(
            release.GetValueOrDefault("ID", "linux"),
            release.GetValueOrDefault("VERSION_ID", "unknown"),
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Directory.Exists("/run/systemd/system"),
            Exists("/usr/sbin/sshd", "/usr/bin/sshd", "/sbin/sshd"),
            Exists("/usr/sbin/nft", "/usr/bin/nft", "/sbin/nft"),
            Exists("/usr/bin/wg", "/usr/sbin/wg", "/bin/wg"),
            Exists("/usr/bin/apt-get", "/bin/apt-get"));
        return new ProvisioningHelperResponse(
            true, "preflight.completed", "Preflight completed.", result, null);
    }

    private static ProvisioningHelperResponse CreateBaseInstallPlan(ProvisioningHelperRequest request)
    {
        if (request.ModuleHash != ProvisioningActionCatalog.SystemBaseInstallModuleHash
            || !SystemBaseInstallSchema.TryParse(request.Parameters, out var parameters))
        {
            return Failure("action.denied", "The requested action is not allowed.");
        }

        var warnings = new List<string>();
        if (!Exists("/usr/bin/apt-get", "/bin/apt-get"))
        {
            warnings.Add("apt.missing");
        }
        if (!File.Exists(Path.Combine("/usr/share/zoneinfo", parameters!.Timezone)))
        {
            warnings.Add("timezone.missing");
        }
        var plan = new SystemBaseInstallPlan(
            parameters.Timezone,
            parameters.Locale,
            parameters.AptUpdate,
            parameters.AptUpgrade,
            SystemBaseInstallCatalogDefinition.ExpandGroups(parameters.PackageGroupIds),
            parameters.SwapMode,
            parameters.SwapSizeMiB,
            parameters.VmSwappiness,
            parameters.EnableUnattendedUpgrades,
            parameters.RebootPolicy,
            [.. warnings]);
        return new ProvisioningHelperResponse(
            true, "system.base-install.plan-ready", "Base install plan is ready.", null, plan);
    }

    private async Task HandleAsync(Socket socket, CancellationToken cancellationToken)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            ProvisioningHelperResponse response;
            try
            {
                var payload = await ReadRequestAsync(stream, cancellationToken);
                var request = JsonSerializer.Deserialize(payload, SmmJsonContext.Default.ProvisioningHelperRequest)
                    ?? throw new JsonException("Empty request.");
                response = await ExecuteRequestAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidRequestSizeException)
            {
                response = Failure("request.invalid-size", "Invalid helper request size.");
            }
            catch (Exception)
            {
                response = Failure("request.invalid", "Invalid helper request.");
            }
            var json = JsonSerializer.Serialize(response, SmmJsonContext.Default.ProvisioningHelperResponse) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
        }
    }

    private static async Task<byte[]> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (buffer.Length <= MaximumRequestBytes)
            {
                var remaining = MaximumRequestBytes + 1 - checked((int)buffer.Length);
                var count = await stream.ReadAsync(
                    rented.AsMemory(0, Math.Min(rented.Length, remaining)),
                    cancellationToken);
                if (count == 0)
                {
                    break;
                }
                var newline = rented.AsSpan(0, count).IndexOf((byte)'\n');
                var payloadCount = newline >= 0 ? newline : count;
                buffer.Write(rented, 0, payloadCount);
                if (newline >= 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
        if (buffer.Length == 0 || buffer.Length > MaximumRequestBytes)
        {
            throw new InvalidRequestSizeException();
        }
        return buffer.ToArray();
    }

    private static Dictionary<string, string> ReadOperatingSystemRelease()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists("/etc/os-release"))
        {
            return result;
        }
        foreach (var line in File.ReadLines("/etc/os-release"))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator];
            if (key is "ID" or "VERSION_ID")
            {
                result[key] = line[(separator + 1)..].Trim().Trim('"');
            }
        }
        return result;
    }

    private static bool Exists(params string[] paths) => paths.Any(File.Exists);

    private static ProvisioningHelperResponse Failure(string code, string message)
        => new(false, code, message, null, null);

    private sealed class InvalidRequestSizeException : Exception
    {
    }

    private readonly record struct UnixPeerCredentials(int ProcessId, uint UserId, uint GroupId);
}
