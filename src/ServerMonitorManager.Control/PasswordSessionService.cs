using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed record PasswordSessionInfo(
    string Token,
    string Username,
    string Role,
    DateTimeOffset ExpiresAt);

public sealed class PasswordSessionService(
    IOptionsMonitor<PasswordLoginOptions> options,
    TimeProvider? timeProvider = null)
{
    private readonly IOptionsMonitor<PasswordLoginOptions> _options = options;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, PasswordSessionInfo> _sessions = new(StringComparer.Ordinal);

    public bool IsEnabled => _options.CurrentValue.EnabledForTesting;

    public PasswordLoginResponse? AuthenticateAndCreateSession(string? username, string? password)
    {
        var config = _options.CurrentValue;
        if (!config.EnabledForTesting)
        {
            return null;
        }

        var isUsernameMatch = !string.IsNullOrWhiteSpace(username)
            && !string.IsNullOrWhiteSpace(config.Username)
            && string.Equals(username, config.Username, StringComparison.Ordinal);

        bool isPasswordValid;
        if (isUsernameMatch)
        {
            isPasswordValid = PasswordHasher.VerifyPassword(password ?? string.Empty, config.PasswordHash);
        }
        else
        {
            PasswordHasher.PerformDummyVerification(password);
            isPasswordValid = false;
        }

        if (!isPasswordValid)
        {
            return null;
        }

        CleanupExpiredSessions();

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(tokenBytes).ToLowerInvariant();
        var ttl = config.SessionTtlMinutes > 0 ? config.SessionTtlMinutes : 60;
        var expiresAt = _timeProvider.GetUtcNow().AddMinutes(ttl);

        var session = new PasswordSessionInfo(token, username!, "Operator", expiresAt);
        _sessions[token] = session;

        return new PasswordLoginResponse(token, "Operator", expiresAt);
    }

    public PasswordSessionInfo? ValidateSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_options.CurrentValue.EnabledForTesting)
        {
            return null;
        }

        if (_sessions.TryGetValue(token, out var session))
        {
            if (_timeProvider.GetUtcNow() < session.ExpiresAt)
            {
                return session;
            }

            _sessions.TryRemove(token, out _);
        }

        return null;
    }

    public void RevokeSession(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            _sessions.TryRemove(token, out _);
        }
    }

    private void CleanupExpiredSessions()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (token, session) in _sessions)
        {
            if (now >= session.ExpiresAt)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }
}
