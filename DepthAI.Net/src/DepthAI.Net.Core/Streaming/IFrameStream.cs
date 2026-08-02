namespace DepthAI.Streaming;

/// <summary>
/// Aliran paket bernama dari perangkat, dipaparkan sebagai <see cref="IObservable{T}"/>
/// supaya bisa dikomposisi dengan System.Reactive bila diinginkan.
/// </summary>
/// <remarks>
/// Kontrak siklus hidup: untuk payload turunan <see cref="Frame"/>, stream memiliki
/// frame dan membuangnya setelah semua observer kembali dari <c>OnNext</c>. Observer
/// yang menyimpan frame melewati batas panggilan itu harus memanggil <c>Clone()</c>.
/// </remarks>
public interface IFrameStream<out T> : IObservable<T>
{
    /// <summary>Nama output stream sebagaimana didefinisikan di pipeline.</summary>
    string Name { get; }
}
