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
    public MeshLinkViewModel(string source, string target)
    {
        Source = source;
        Target = target;
    }

    public string Source { get; set; }
    public string Target { get; set; }
    public string Label => $"{Source} → {Target}";
}
