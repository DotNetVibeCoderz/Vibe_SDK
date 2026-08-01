using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;

namespace Unitree.Net.Sensors;

/// <summary>
/// Receives LiDAR point clouds.
/// </summary>
/// <remarks>
/// <para>
/// Point clouds are large — a Unitree L1 frame runs to several hundred kilobytes — and arrive at around
/// 10 Hz. Only the most recent frame is retained; a queue of stale clouds is worse than useless for
/// obstacle checks and costs a great deal of memory.
/// </para>
/// <para>
/// The managed multicast transport cannot carry a full frame, because a cloud exceeds the 64 KB UDP
/// datagram limit. LiDAR needs the native transport, which fragments.
/// </para>
/// </remarks>
public sealed class LidarClient : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly ILogger _logger;
    private readonly Lock _latestLock = new();

    private PointCloud2? _latest;
    private long _frameCount;
    private long _malformedCount;
    private DateTimeOffset? _lastFrameAt;
    private bool _disposed;

    /// <summary>Creates a client subscribed to the LiDAR cloud topic.</summary>
    public LidarClient(IDdsParticipant participant, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        _logger = logger ?? NullLogger.Instance;
        _subscription = participant.Transport.Subscribe(Topics.LidarCloud, OnPayload);
    }

    /// <summary>Total frames decoded.</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>Frames that failed to decode.</summary>
    public long MalformedCount => Interlocked.Read(ref _malformedCount);

    /// <summary>When the most recent frame arrived.</summary>
    public DateTimeOffset? LastFrameAt
    {
        get
        {
            lock (_latestLock)
            {
                return _lastFrameAt;
            }
        }
    }

    /// <summary>Raised for each decoded frame.</summary>
    /// <remarks>Handlers run on the transport receive path and must return promptly.</remarks>
    public event Action<PointCloud2>? FrameReceived;

    /// <summary>Gets the most recent frame, or <see langword="null"/> if none has arrived.</summary>
    public PointCloud2? GetLatest()
    {
        lock (_latestLock)
        {
            return _latest;
        }
    }

    /// <summary>
    /// Finds the nearest obstacle directly ahead.
    /// </summary>
    /// <param name="sectorHalfWidthDegrees">Half-width of the forward sector to search.</param>
    /// <returns>Distance in metres, or <see langword="null"/> if no frame or no returns.</returns>
    public float? GetForwardClearance(float sectorHalfWidthDegrees = 15f)
    {
        PointCloud2? cloud = GetLatest();

        return cloud?.FindNearestInSector(0f, float.DegreesToRadians(sectorHalfWidthDegrees));
    }

    private void OnPayload(ReadOnlySpan<byte> payload)
    {
        PointCloud2 cloud;

        try
        {
            cloud = PointCloud2.Deserialize(payload);
        }
        catch (Exception ex) when (ex is CdrFormatException or ArgumentException)
        {
            Interlocked.Increment(ref _malformedCount);
            _logger.LogDebug(ex, "Discarded a malformed LiDAR frame.");
            return;
        }

        lock (_latestLock)
        {
            _latest = cloud;
            _lastFrameAt = DateTimeOffset.UtcNow;
        }

        Interlocked.Increment(ref _frameCount);
        FrameReceived?.Invoke(cloud);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
    }
}
