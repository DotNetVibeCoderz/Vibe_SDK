using System.Text.Json;
using System.Text.Json.Serialization;
using PollenRobotics.Net.Core.Geometry;

namespace PollenRobotics.Net.ReachyMini.Moves;

/// <summary>
/// One sample of a recorded move: a timestamp and the targets that were commanded.
/// </summary>
/// <param name="TimeSeconds">Offset from the start of the move.</param>
/// <param name="Head">Head pose, or null if it was not commanded in that frame.</param>
/// <param name="Antennas">Antenna angles, or null.</param>
/// <param name="BodyYaw">Body yaw, or null.</param>
public readonly record struct MoveFrame(
    double TimeSeconds,
    Pose? Head,
    (Angle Right, Angle Left)? Antennas,
    Angle? BodyYaw)
{
    /// <summary>The frame as a target that can be sent straight to a transport.</summary>
    public ReachyMiniTarget ToTarget() => new() { Head = Head, Antennas = Antennas, BodyYaw = BodyYaw };
}

/// <summary>
/// A recorded motion, optionally with audio.
/// </summary>
/// <remarks>
/// <para>
/// The JSON form matches what the Python <c>RecordedMove</c> parser and the JavaScript
/// <c>playMove</c> expect: a <c>time</c> array beside a <c>set_target_data</c> array of the same
/// length. Keeping the two parallel rather than nesting a timestamp in each frame is what makes the
/// format cheap to resample.
/// </para>
/// <para>
/// Long moves, and any move with audio, belong on the daemon's clock rather than streamed frame by
/// frame from here. Streaming works on a wired Lite and falls apart over Wi-Fi, where each frame
/// pays a round trip; the daemon ticks its own inner loop and keeps motion and audio on one clock.
/// </para>
/// </remarks>
public sealed class RecordedMove
{
    /// <summary>A name for the move, used when it is published or replayed by name.</summary>
    public string Name { get; init; } = "untitled";

    /// <summary>Optional description, carried through to the daemon.</summary>
    public string? Description { get; init; }

    /// <summary>The frames, ordered by time.</summary>
    public IReadOnlyList<MoveFrame> Frames { get; init; } = [];

    /// <summary>
    /// Canonical 16 kHz mono 16-bit PCM WAV to play alongside, or null.
    /// </summary>
    /// <remarks>
    /// The daemon does not transcode. A 44.1 kHz file plays at the wrong speed and a stereo one
    /// plays as noise, and neither reports an error - which is the usual cause of "the audio on
    /// this move is broken" when a dataset is inherited from elsewhere.
    /// </remarks>
    public byte[]? Audio { get; init; }

    /// <summary>
    /// Milliseconds the audio leads the motion by. Negative means motion first.
    /// </summary>
    /// <remarks>
    /// The default of -100 ms is the measured system-wide constant: it covers motor pickup and
    /// GStreamer playbin warm-up together. Change it only after measuring your own setup.
    /// </remarks>
    public int AudioLeadMilliseconds { get; init; } = -100;

    /// <summary>Total duration.</summary>
    public TimeSpan Duration => Frames.Count == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Frames[^1].TimeSeconds);

    /// <summary>
    /// The frame that should be applied at <paramref name="elapsed"/>, interpolated between samples.
    /// </summary>
    public ReachyMiniTarget Sample(TimeSpan elapsed)
    {
        if (Frames.Count == 0)
        {
            return default;
        }

        double t = elapsed.TotalSeconds;
        if (t <= Frames[0].TimeSeconds)
        {
            return Frames[0].ToTarget();
        }

        if (t >= Frames[^1].TimeSeconds)
        {
            return Frames[^1].ToTarget();
        }

        // Binary search for the bracketing pair. A recorded move at 100 Hz for three minutes is
        // 18,000 frames, and a linear scan per playback tick is 18,000 comparisons at 100 Hz.
        int low = 0;
        int high = Frames.Count - 1;
        while (high - low > 1)
        {
            int mid = (low + high) / 2;
            if (Frames[mid].TimeSeconds <= t)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        MoveFrame a = Frames[low];
        MoveFrame b = Frames[high];
        double span = b.TimeSeconds - a.TimeSeconds;
        double alpha = span <= 0 ? 0 : (t - a.TimeSeconds) / span;

        return new ReachyMiniTarget
        {
            Head = a.Head is { } ha && b.Head is { } hb ? Pose.Lerp(ha, hb, alpha) : b.Head ?? a.Head,
            Antennas = a.Antennas is { } aa && b.Antennas is { } ab
                ? (Angle.FromRadians(Lerp(aa.Right.Radians, ab.Right.Radians, alpha)),
                   Angle.FromRadians(Lerp(aa.Left.Radians, ab.Left.Radians, alpha)))
                : b.Antennas ?? a.Antennas,
            BodyYaw = a.BodyYaw is { } ya && b.BodyYaw is { } yb
                ? Angle.FromRadians(Lerp(ya.Radians, yb.Radians, alpha))
                : b.BodyYaw ?? a.BodyYaw,
        };

        static double Lerp(double from, double to, double alpha) => from + ((to - from) * alpha);
    }

    /// <summary>Serialises to the JSON shape the daemon and the Python parser share.</summary>
    public string ToJson()
    {
        var payload = new MoveJson
        {
            Time = [.. Frames.Select(f => f.TimeSeconds)],
            SetTargetData = [.. Frames.Select(f => new MoveFrameJson
            {
                Head = f.Head is { } head ? [.. head.ToRowMajor()] : null,
                Antennas = f.Antennas is { } a ? [a.Right.Radians, a.Left.Radians] : null,
                BodyYaw = f.BodyYaw?.Radians,
            })],
        };

        return JsonSerializer.Serialize(payload, MoveJsonContext.Default.MoveJson);
    }

    /// <summary>Reads the JSON shape produced by <see cref="ToJson"/> or by the Python SDK.</summary>
    public static RecordedMove FromJson(string json, string name = "untitled")
    {
        MoveJson payload = JsonSerializer.Deserialize(json, MoveJsonContext.Default.MoveJson)
            ?? throw new ArgumentException("The move JSON was empty.", nameof(json));

        if (payload.Time.Count != payload.SetTargetData.Count)
        {
            throw new ArgumentException(
                $"A move has one timestamp per frame, but this one has {payload.Time.Count} timestamps " +
                $"and {payload.SetTargetData.Count} frames.", nameof(json));
        }

        var frames = new MoveFrame[payload.Time.Count];
        for (int i = 0; i < frames.Length; i++)
        {
            MoveFrameJson frame = payload.SetTargetData[i];
            frames[i] = new MoveFrame(
                payload.Time[i],
                frame.Head is { Count: 16 } head ? Pose.FromRowMajor([.. head]) : null,
                frame.Antennas is { Count: 2 } antennas
                    ? (Angle.FromRadians(antennas[0]), Angle.FromRadians(antennas[1]))
                    : null,
                frame.BodyYaw is { } yaw ? Angle.FromRadians(yaw) : null);
        }

        return new RecordedMove { Name = name, Frames = frames };
    }

    /// <summary>Resamples the move onto a uniform grid, which is what daemon playback wants.</summary>
    public RecordedMove Resample(double frequencyHz)
    {
        if (frequencyHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "Frequency must be positive.");
        }

        if (Frames.Count == 0)
        {
            return this;
        }

        double step = 1.0 / frequencyHz;
        int count = (int)Math.Ceiling(Duration.TotalSeconds / step) + 1;
        var frames = new MoveFrame[count];

        for (int i = 0; i < count; i++)
        {
            double t = Math.Min(i * step, Duration.TotalSeconds);
            ReachyMiniTarget target = Sample(TimeSpan.FromSeconds(t));
            frames[i] = new MoveFrame(t, target.Head, target.Antennas, target.BodyYaw);
        }

        return new RecordedMove
        {
            Name = Name,
            Description = Description,
            Frames = frames,
            Audio = Audio,
            AudioLeadMilliseconds = AudioLeadMilliseconds,
        };
    }
}

internal sealed class MoveJson
{
    [JsonPropertyName("time")]
    public List<double> Time { get; set; } = [];

    [JsonPropertyName("set_target_data")]
    public List<MoveFrameJson> SetTargetData { get; set; } = [];
}

internal sealed class MoveFrameJson
{
    [JsonPropertyName("head")]
    public List<double>? Head { get; set; }

    [JsonPropertyName("antennas")]
    public List<double>? Antennas { get; set; }

    [JsonPropertyName("body_yaw")]
    public double? BodyYaw { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MoveJson))]
internal sealed partial class MoveJsonContext : JsonSerializerContext;
