using System.Collections.Concurrent;
using System.Threading.Channels;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class ControlEventBroker
{
    private readonly ConcurrentDictionary<Guid, Channel<ControlEvent>> _subscribers = new();
    private long _sequence;

    public ControlEvent Publish(string type, string subject, string payloadJson)
    {
        var controlEvent = new ControlEvent(
            Interlocked.Increment(ref _sequence),
            type,
            subject,
            DateTimeOffset.UtcNow,
            payloadJson);
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(controlEvent);
        }
        return controlEvent;
    }

    public ControlEventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ControlEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[id] = channel;
        return new ControlEventSubscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }
}

public sealed class ControlEventSubscription(ChannelReader<ControlEvent> reader, Action unsubscribe) : IDisposable
{
    public ChannelReader<ControlEvent> Reader { get; } = reader;

    public void Dispose() => unsubscribe();
}
