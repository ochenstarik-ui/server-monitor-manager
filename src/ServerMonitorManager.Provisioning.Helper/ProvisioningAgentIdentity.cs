namespace ServerMonitorManager.Provisioning.Helper;

public static class ProvisioningAgentIdentity
{
    private const string AgentUser = "ochenstarik-smm-agent";

    public static bool MatchesConfiguredUid(string passwdPath, uint configuredUserId)
    {
        try
        {
            foreach (var line in File.ReadLines(passwdPath))
            {
                var fields = line.Split(':');
                if (fields.Length >= 3
                    && string.Equals(fields[0], AgentUser, StringComparison.Ordinal)
                    && uint.TryParse(fields[2], out var actualUserId))
                {
                    return actualUserId == configuredUserId;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return false;
    }
}
