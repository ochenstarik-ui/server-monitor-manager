using Microsoft.Extensions.Configuration;

namespace ServerMonitorManager.Agent;

internal static class AgentConfiguration
{
    internal const string BindingError =
        "Agent configuration binding failed; check SMM_ControlUrl and SMM_NodeId.";
    internal const string ControlUrlError =
        "Agent ControlUrl must be a secure HTTPS origin.";

    internal static bool TryBind(
        IConfiguration configuration,
        string? environmentControlUrl,
        string? environmentNodeId,
        out AgentOptions options,
        out string? error)
    {
        try
        {
            options = configuration.Get<AgentOptions>() ?? new AgentOptions();
        }
        catch (Exception)
        {
            options = new AgentOptions();
            error = BindingError;
            return false;
        }

        if (environmentControlUrl is not null
            && (!Uri.TryCreate(environmentControlUrl, UriKind.Absolute, out var expectedControlUrl)
                || options.ControlUrl != expectedControlUrl))
        {
            error = "Agent configuration binding failed for SMM_ControlUrl.";
            return false;
        }

        if (environmentNodeId is not null
            && !string.Equals(options.NodeId, environmentNodeId, StringComparison.Ordinal))
        {
            error = "Agent configuration binding failed for SMM_NodeId.";
            return false;
        }

        if (!IsSecureControlOrigin(options.ControlUrl))
        {
            error = ControlUrlError;
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsSecureControlOrigin(Uri? controlUrl)
    {
        return controlUrl is not null
            && controlUrl.IsAbsoluteUri
            && string.Equals(controlUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(controlUrl.Host)
            && Uri.CheckHostName(controlUrl.IdnHost) != UriHostNameType.Unknown
            && string.IsNullOrEmpty(controlUrl.UserInfo)
            && string.IsNullOrEmpty(controlUrl.Query)
            && string.IsNullOrEmpty(controlUrl.Fragment)
            && controlUrl.AbsolutePath == "/"
            && controlUrl.Port is >= 1 and <= 65535;
    }
}
