using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace DepthAI.Streaming;

/// <summary>
/// Operator praktis di atas <see cref="IObservable{T}"/> supaya pemakaian umum tidak
/// memaksa developer menarik System.Reactive.
/// </summary>
public static class FrameStreamExtensions
{
    /// <summary>Berlangganan dengan delegate, tanpa perlu menulis kelas <see cref="IObserver{T}"/>.</summary>
    public static IDisposable Subscribe<T>(
        this IObservable<T> source,
        Action<T> onNext,
        Action<Exception>? onError = null,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onNext);
        return source.Subscribe(new DelegateObserver<T>(onNext, onError, onCompleted));
    }

    /// <summary>
    /// Menjembatani stream ke <c>await foreach</c>.
    /// </summary>
    /// <remarks>
    /// Konsumen async hampir pasti lebih lambat dari perangkat, jadi channel-nya bounded
    /// dan <b>membuang frame terlama</b> saat penuh: lebih baik menampilkan frame terkini
    /// daripada menumpuk antrean yang makin tertinggal. Frame di-clone saat masuk channel
    /// karena frame milik stream sudah dibuang begitu <c>OnNext</c> selesai.
    /// </remarks>
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        this IObservable<T> source,
        int capacity = 2,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        void OnDropped(T dropped) => (dropped as Frame)?.Dispose();

        using var subscription = source.Subscribe(
            item =>
            {
                var payload = item is Frame frame ? (T)(object)CloneFrame(frame) : item;
                if (!channel.Writer.TryWrite(payload))
                {
                    OnDropped(payload);
                }
            },
            error => channel.Writer.TryComplete(error),
            () => channel.Writer.TryComplete());

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>Menunggu satu item berikutnya — berguna untuk capture sekali jalan.</summary>
    public static async Task<T> FirstAsync<T>(
        this IObservable<T> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state =>
            ((TaskCompletionSource<T>)state!).TrySetCanceled(), tcs);

        using var subscription = source.Subscribe(
            item => tcs.TrySetResult(item is Frame frame ? (T)(object)CloneFrame(frame) : item),
            error => tcs.TrySetException(error),
            () => tcs.TrySetException(new InvalidOperationException("Stream selesai sebelum mengirim item apa pun.")));

        return await tcs.Task;
    }

    /// <summary>Menyaring stream — padanan ringan <c>Observable.Where</c>.</summary>
    public static IObservable<T> Where<T>(this IObservable<T> source, Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);
        return new AnonymousObservable<T>(observer => source.Subscribe(
            item =>
            {
                if (predicate(item))
                {
                    observer.OnNext(item);
                }
            },
            observer.OnError,
            observer.OnCompleted));
    }

    /// <summary>
    /// Memproyeksikan tiap item. Perhatikan siklus hidup frame: bila hasil proyeksi
    /// perlu hidup lebih lama dari callback, salin isinya di dalam <paramref name="selector"/>.
    /// </summary>
    public static IObservable<TResult> Select<T, TResult>(this IObservable<T> source, Func<T, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);
        return new AnonymousObservable<TResult>(observer => source.Subscribe(
            item => observer.OnNext(selector(item)),
            observer.OnError,
            observer.OnCompleted));
    }

    /// <summary>
    /// Membatasi laju agar tidak lebih rapat dari <paramref name="interval"/>.
    /// Cocok untuk UI yang tidak perlu 60 fps penuh.
    /// </summary>
    public static IObservable<T> Throttle<T>(this IObservable<T> source, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AnonymousObservable<T>(observer =>
        {
            var last = long.MinValue;
            var ticks = interval.Ticks;
            return source.Subscribe(
                item =>
                {
                    var now = DateTime.UtcNow.Ticks;
                    if (now - Interlocked.Read(ref last) < ticks)
                    {
                        return;
                    }

                    Interlocked.Exchange(ref last, now);
                    observer.OnNext(item);
                },
                observer.OnError,
                observer.OnCompleted);
        });
    }

    private static Frame CloneFrame(Frame frame) => frame switch
    {
        ImageFrame image => image.Clone(),
        DepthFrame depth => depth.Clone(),
        _ => frame,
    };

    private sealed class DelegateObserver<T>(
        Action<T> onNext,
        Action<Exception>? onError,
        Action? onCompleted) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);

        public void OnError(Exception error) => onError?.Invoke(error);

        public void OnCompleted() => onCompleted?.Invoke();
    }

    private sealed class AnonymousObservable<T>(Func<IObserver<T>, IDisposable> subscribe) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => subscribe(observer);
    }
}
