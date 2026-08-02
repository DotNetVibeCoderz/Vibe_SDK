using Microsoft.Extensions.Logging;

namespace DepthAI.Streaming;

/// <summary>
/// Implementasi <see cref="IFrameStream{T}"/> berbasis multicast. Sengaja tidak
/// bergantung pada System.Reactive supaya Core tetap ringan; konsumen tetap bebas
/// memakai operator Rx karena tipenya <see cref="IObservable{T}"/> biasa.
/// </summary>
internal sealed class FrameStream<T> : IFrameStream<T>, IDisposable
{
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private IObserver<T>[] _observers = [];
    private bool _completed;
    private Exception? _error;

    public FrameStream(string name, ILogger? logger = null)
    {
        Name = name;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public string Name { get; }

    public bool HasObservers
    {
        get
        {
            lock (_gate)
            {
                return _observers.Length > 0;
            }
        }
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            if (_completed)
            {
                // Stream sudah selesai: laporkan langsung, jangan gantungkan observer.
                if (_error is not null)
                {
                    observer.OnError(_error);
                }
                else
                {
                    observer.OnCompleted();
                }

                return NullSubscription.Instance;
            }

            _observers = [.. _observers, observer];
        }

        return new Subscription(this, observer);
    }

    /// <summary>
    /// Menyiarkan satu item. Untuk payload <see cref="Frame"/>, item dibuang setelah
    /// semua observer kembali — lihat kontrak pada <see cref="IFrameStream{T}"/>.
    /// </summary>
    public void Publish(T item)
    {
        IObserver<T>[] observers;
        lock (_gate)
        {
            if (_completed)
            {
                (item as Frame)?.Dispose();
                return;
            }

            observers = _observers;
        }

        try
        {
            foreach (var observer in observers)
            {
                try
                {
                    observer.OnNext(item);
                }
                catch (Exception ex)
                {
                    // Satu observer yang rewel tidak boleh menjatuhkan stream untuk yang lain.
                    _logger.LogError(ex, "Observer melempar exception saat memproses stream {Stream}.", Name);
                }
            }
        }
        finally
        {
            (item as Frame)?.Dispose();
        }
    }

    public void Complete(Exception? error = null)
    {
        IObserver<T>[] observers;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _error = error;
            observers = _observers;
            _observers = [];
        }

        foreach (var observer in observers)
        {
            try
            {
                if (error is not null)
                {
                    observer.OnError(error);
                }
                else
                {
                    observer.OnCompleted();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Observer melempar exception saat penutupan stream {Stream}.", Name);
            }
        }
    }

    public void Dispose() => Complete();

    private void Unsubscribe(IObserver<T> observer)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_observers, observer);
            if (index < 0)
            {
                return;
            }

            var next = new IObserver<T>[_observers.Length - 1];
            Array.Copy(_observers, next, index);
            Array.Copy(_observers, index + 1, next, index, _observers.Length - index - 1);
            _observers = next;
        }
    }

    private sealed class Subscription(FrameStream<T> stream, IObserver<T> observer) : IDisposable
    {
        private IObserver<T>? _observer = observer;

        public void Dispose()
        {
            var target = Interlocked.Exchange(ref _observer, null);
            if (target is not null)
            {
                stream.Unsubscribe(target);
            }
        }
    }

    private sealed class NullSubscription : IDisposable
    {
        public static NullSubscription Instance { get; } = new();

        public void Dispose() { }
    }
}
