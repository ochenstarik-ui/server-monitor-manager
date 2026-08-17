namespace ServerMonitorManager.Core;

public sealed record PasswordLoginRequest(string Username, string Password);

public sealed record PasswordLoginResponse(string Token, string Role, DateTimeOffset ExpiresAt);

public sealed record PasswordLoginStatusResponse(bool EnabledForTesting);
