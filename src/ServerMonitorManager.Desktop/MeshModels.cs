namespace ServerMonitorManager_Desktop;

public sealed class MeshNodeViewModel
{
    public MeshNodeViewModel(string name, string address, string state, int handshakeAgeSeconds)
    {
        Name = name;
        Address = address;
        State = state;
        HandshakeAgeSeconds = handshakeAgeSeconds;
    }

    public string Name { get; set; }
    public string Address { get; set; }
    public string State { get; set; }
    public int HandshakeAgeSeconds { get; set; }
    public string Label => $"{Name} · {Address} · {(State == "online" ? "онлайн" : "не в сети")}";
}

public sealed class MeshLinkViewModel
{
    public MeshLinkViewModel(
        string source,
        string target,
        string cidr,
        string protocol,
        int port,
        long expiresUnix)
    {
        Source = source;
        Target = target;
        Cidr = cidr;
        Protocol = protocol;
        Port = port;
        ExpiresUnix = expiresUnix;
    }

    public string Source { get; set; }
    public string Target { get; set; }
    public string Cidr { get; set; }
    public string Protocol { get; set; }
    public int Port { get; set; }
    public long ExpiresUnix { get; set; }
    public string ExpirationText => ExpiresUnix == 0
        ? "вручную"
        : $"до {DateTimeOffset.FromUnixTimeSeconds(ExpiresUnix).ToLocalTime():dd.MM HH:mm}";
    public string Label => $"{Source} → {Target} · {Protocol.ToUpperInvariant()}/{Port} · {Cidr} · {ExpirationText}";
}
