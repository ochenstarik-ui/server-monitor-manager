namespace ServerMonitorManager.Control;

public sealed class PasswordLoginOptions
{
    public const string SectionName = "Authentication:PasswordLogin";

    public bool EnabledForTesting { get; set; }

    public string? Username { get; set; }

    public string? PasswordHash { get; set; }

    public int SessionTtlMinutes { get; set; } = 60;
}
