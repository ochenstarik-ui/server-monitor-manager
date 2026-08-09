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
    public const string FirewallUnavailableErrorCode = "mesh.firewall-unavailable";
    public const string NodeNotActivatedErrorCode = "mesh.node-not-activated";

    public MeshLinkViewModel(
        string source,
        string target,
        string cidr,
        string protocol,
        int port,
        long expiresUnix,
        string state,
        long version,
        string? id = null,
        string? desiredState = null,
        string? actualState = null,
        string? lastError = null)
    {
        Source = source;
        Target = target;
        Cidr = cidr;
        Protocol = protocol;
        Port = port;
        ExpiresUnix = expiresUnix;
        State = state;
        Version = version;
        Id = id;
        DesiredState = desiredState ?? state;
        ActualState = actualState ?? state;
        LastError = lastError;
    }

    public string Source { get; set; }
    public string Target { get; set; }
    public string Cidr { get; set; }
    public string Protocol { get; set; }
    public int Port { get; set; }
    public long ExpiresUnix { get; set; }
    public string State { get; set; }
    public long Version { get; set; }
    public string? Id { get; set; }
    public string DesiredState { get; set; }
    public string ActualState { get; set; }
    public string? LastError { get; set; }
    public bool HasDrift => !string.Equals(DesiredState, ActualState, StringComparison.Ordinal);
    public string DesiredStatusText => $"Желаемое состояние: {DesiredState}";
    public string ActualStatusText => LastError == NodeNotActivatedErrorCode
        ? "Фактическое состояние: ожидает активации Node в Mesh"
        : $"Фактическое состояние: {ActualState}";
    public string DriftText => LastError == NodeNotActivatedErrorCode
        ? "Ожидание: активируйте Node в Mesh — это не ошибка политики"
        : HasDrift ? "Расхождение: требуется сверка политики" : "Расхождение: нет";
    public string ErrorText => LastError is FirewallUnavailableErrorCode or NodeNotActivatedErrorCode ? string.Empty
        : string.IsNullOrWhiteSpace(LastError) ? "Ошибка: нет" : $"Ошибка: {LastError}";
    public string VersionText => $"Версия политики: {Version}";
    public string ExpirationText => ExpiresUnix == 0
        ? "вручную"
        : $"до {DateTimeOffset.FromUnixTimeSeconds(ExpiresUnix).ToLocalTime():dd.MM HH:mm}";
    public string Label => $"{Source} → {Target} · {Protocol.ToUpperInvariant()}/{Port} · {Cidr} · {ExpirationText}";
}
