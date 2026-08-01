namespace Unitree.Net.Dds.Transports;

/// <summary>
/// Maps topic names to their registered handlers.
/// </summary>
/// <remarks>
/// Shared by every transport. Reads happen on the hot receive path, so the per-topic handler list is
/// replaced wholesale on mutation rather than locked on read — subscriptions change rarely, dispatch
/// happens hundreds of times a second.
/// </remarks>
internal sealed class SubscriptionRegistry
{
    private readonly Dictionary<string, DdsPayloadHandler[]> _handlersByTopic = new(StringComparer.Ordinal);
    private readonly Lock _mutationLock = new();

    /// <summary>Gets the topics that currently have at least one handler.</summary>
    internal IReadOnlyCollection<string> Topics
    {
        get
        {
            lock (_mutationLock)
            {
                return [.. _handlersByTopic.Keys];
            }
        }
    }

    /// <summary>Registers <paramref name="handler"/> for <paramref name="topic"/>.</summary>
    /// <returns>A token that removes the handler when disposed.</returns>
    internal IDisposable Add(string topic, DdsPayloadHandler handler)
    {
        lock (_mutationLock)
        {
            _handlersByTopic[topic] = _handlersByTopic.TryGetValue(topic, out DdsPayloadHandler[]? existing)
                ? [.. existing, handler]
                : [handler];
        }

        return new Registration(this, topic, handler);
    }

    /// <summary>Dispatches <paramref name="payload"/> to every handler registered for <paramref name="topic"/>.</summary>
    /// <returns>The number of handlers invoked.</returns>
    internal int Dispatch(string topic, ReadOnlySpan<byte> payload)
    {
        DdsPayloadHandler[]? handlers;

        lock (_mutationLock)
        {
            if (!_handlersByTopic.TryGetValue(topic, out handlers))
            {
                return 0;
            }
        }

        foreach (DdsPayloadHandler handler in handlers)
        {
            handler(payload);
        }

        return handlers.Length;
    }

    /// <summary>Whether any handler is registered for <paramref name="topic"/>.</summary>
    internal bool HasSubscribers(string topic)
    {
        lock (_mutationLock)
        {
            return _handlersByTopic.ContainsKey(topic);
        }
    }

    private void Remove(string topic, DdsPayloadHandler handler)
    {
        lock (_mutationLock)
        {
            if (!_handlersByTopic.TryGetValue(topic, out DdsPayloadHandler[]? existing))
            {
                return;
            }

            DdsPayloadHandler[] remaining = [.. existing.Where(h => h != handler)];

            if (remaining.Length == 0)
            {
                _handlersByTopic.Remove(topic);
            }
            else
            {
                _handlersByTopic[topic] = remaining;
            }
        }
    }

    private sealed class Registration(SubscriptionRegistry registry, string topic, DdsPayloadHandler handler)
        : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registry.Remove(topic, handler);
        }
    }
}
