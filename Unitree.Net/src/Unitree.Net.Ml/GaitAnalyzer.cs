using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;
using Unitree.Net.Messages.Go;
using Unitree.Net.Sensors;

namespace Unitree.Net.Ml;

/// <summary>
/// One sample of gait state, as consumed by the ML pipeline.
/// </summary>
public sealed class GaitSample
{
    /// <summary>Seconds since the analysis window opened.</summary>
    public float TimeSeconds { get; set; }

    /// <summary>Number of feet currently loaded.</summary>
    public float ContactCount { get; set; }

    /// <summary>Total measured foot force across all feet.</summary>
    public float TotalFootForce { get; set; }

    /// <summary>Body pitch, radians.</summary>
    public float Pitch { get; set; }

    /// <summary>Body roll, radians.</summary>
    public float Roll { get; set; }

    /// <summary>Magnitude of the body angular rate, rad/s.</summary>
    public float AngularRate { get; set; }

    /// <summary>Mean absolute joint velocity across the actuated joints, rad/s.</summary>
    public float MeanJointSpeed { get; set; }

    /// <summary>Hottest motor at this instant, °C.</summary>
    public float MaxMotorTemperature { get; set; }
}

/// <summary>
/// Anomaly detection output.
/// </summary>
public sealed class GaitAnomalyPrediction
{
    /// <summary>
    /// Alert flag, raw score and p-value, in that order.
    /// </summary>
    /// <remarks>ML.NET's spike detector emits a fixed three-element vector; the layout is its contract.</remarks>
    [VectorType(3)]
    public double[] Prediction { get; set; } = new double[3];

    /// <summary>Whether this sample was flagged.</summary>
    public bool IsAnomaly => Prediction[0] == 1;

    /// <summary>The raw anomaly score.</summary>
    public double Score => Prediction[1];

    /// <summary>The p-value; smaller means more surprising.</summary>
    public double PValue => Prediction[2];
}

/// <summary>
/// Descriptive gait statistics over an analysis window.
/// </summary>
/// <param name="SampleCount">Number of samples analysed.</param>
/// <param name="DurationSeconds">Window length.</param>
/// <param name="StepFrequencyHz">Estimated steps per second.</param>
/// <param name="DutyFactor">Fraction of the cycle a foot spends loaded.</param>
/// <param name="MeanContactCount">Average number of loaded feet.</param>
/// <param name="SymmetryIndex">
/// Left/right loading balance: 0 is perfectly symmetric, 1 is entirely one-sided.
/// </param>
/// <param name="PitchStandardDeviation">Body pitch variability, radians.</param>
/// <param name="RollStandardDeviation">Body roll variability, radians.</param>
public readonly record struct GaitStatistics(
    int SampleCount,
    double DurationSeconds,
    double StepFrequencyHz,
    double DutyFactor,
    double MeanContactCount,
    double SymmetryIndex,
    double PitchStandardDeviation,
    double RollStandardDeviation)
{
    /// <summary>
    /// Whether the gait looks asymmetric enough to suggest a mechanical problem.
    /// </summary>
    /// <remarks>
    /// A limp shows up as sustained asymmetry in foot loading well before it becomes visible to an
    /// operator. The 0.15 threshold is a starting point — calibrate it against a healthy robot, because
    /// baseline asymmetry varies between units.
    /// </remarks>
    public bool SuggestsAsymmetry => SymmetryIndex > 0.15;
}

/// <summary>
/// Analyses gait from telemetry and flags anomalies.
/// </summary>
/// <remarks>
/// <para>
/// Two complementary things happen here. <see cref="Analyze"/> computes descriptive statistics —
/// step frequency, duty factor, left/right symmetry — which are directly interpretable and need no
/// training data. <see cref="DetectAnomalies"/> runs ML.NET's SSA spike detector over the same window
/// to catch deviations that no fixed rule anticipates.
/// </para>
/// <para>
/// Both operate on a captured window rather than streaming, because gait metrics are only meaningful
/// over several complete cycles.
/// </para>
/// </remarks>
public sealed class GaitAnalyzer(ILogger? logger = null)
{
    private readonly MLContext _mlContext = new(seed: 0);
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Converts raw telemetry into a gait sample.
    /// </summary>
    /// <param name="state">The low-level state to convert.</param>
    /// <param name="timeSeconds">Time offset within the analysis window.</param>
    public static GaitSample ToSample(in LowState state, float timeSeconds)
    {
        float totalForce = 0;
        int contactCount = 0;

        for (int i = 0; i < 4; i++)
        {
            short force = state.FootForce[i];
            totalForce += force;

            if (force > FootContactState.ContactThreshold)
            {
                contactCount++;
            }
        }

        float jointSpeedSum = 0;

        for (int i = 0; i < Core.GoJoint.Count; i++)
        {
            jointSpeedSum += MathF.Abs(state.MotorState[i].Dq);
        }

        Core.EulerAngles rpy = state.ImuState.ToEuler();
        float gx = state.ImuState.Gyroscope[0];
        float gy = state.ImuState.Gyroscope[1];
        float gz = state.ImuState.Gyroscope[2];

        return new GaitSample
        {
            TimeSeconds = timeSeconds,
            ContactCount = contactCount,
            TotalFootForce = totalForce,
            Pitch = rpy.Pitch,
            Roll = rpy.Roll,
            AngularRate = MathF.Sqrt((gx * gx) + (gy * gy) + (gz * gz)),
            MeanJointSpeed = jointSpeedSum / Core.GoJoint.Count,
            MaxMotorTemperature = state.GetMaxMotorTemperature(),
        };
    }

    /// <summary>
    /// Computes descriptive gait statistics over a window.
    /// </summary>
    /// <param name="samples">Samples in time order.</param>
    /// <param name="leftForceSelector">Total left-side foot force per sample index.</param>
    /// <param name="rightForceSelector">Total right-side foot force per sample index.</param>
    public GaitStatistics Analyze(
        IReadOnlyList<GaitSample> samples,
        Func<int, float>? leftForceSelector = null,
        Func<int, float>? rightForceSelector = null)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count < 2)
        {
            return new GaitStatistics(samples.Count, 0, 0, 0, 0, 0, 0, 0);
        }

        double duration = samples[^1].TimeSeconds - samples[0].TimeSeconds;

        // Step frequency is estimated by counting transitions into full stance. Counting contacts
        // directly would double-count the two feet of a trot pair landing microseconds apart.
        int stanceEntries = 0;
        bool wasFullStance = samples[0].ContactCount >= 4;
        double loadedSampleCount = 0;
        double contactSum = 0;

        foreach (GaitSample sample in samples)
        {
            bool isFullStance = sample.ContactCount >= 4;

            if (isFullStance && !wasFullStance)
            {
                stanceEntries++;
            }

            wasFullStance = isFullStance;
            contactSum += sample.ContactCount;
            loadedSampleCount += sample.ContactCount / 4.0;
        }

        double stepFrequency = duration > 0 ? stanceEntries / duration : 0;
        double dutyFactor = loadedSampleCount / samples.Count;

        double symmetry = 0;

        if (leftForceSelector is not null && rightForceSelector is not null)
        {
            double leftTotal = 0;
            double rightTotal = 0;

            for (int i = 0; i < samples.Count; i++)
            {
                leftTotal += leftForceSelector(i);
                rightTotal += rightForceSelector(i);
            }

            double combined = leftTotal + rightTotal;
            symmetry = combined > 0 ? Math.Abs(leftTotal - rightTotal) / combined : 0;
        }

        return new GaitStatistics(
            samples.Count,
            duration,
            stepFrequency,
            dutyFactor,
            contactSum / samples.Count,
            symmetry,
            StandardDeviation(samples.Select(s => (double)s.Pitch)),
            StandardDeviation(samples.Select(s => (double)s.Roll)));
    }

    /// <summary>
    /// Flags anomalous samples using singular spectrum analysis.
    /// </summary>
    /// <param name="samples">Samples in time order. At least 24 are needed for a usable model.</param>
    /// <param name="confidence">Detection confidence, 0–100. Higher means fewer, stronger alerts.</param>
    /// <param name="selector">Which value to monitor. Defaults to total foot force.</param>
    /// <remarks>
    /// SSA models the signal's own periodic structure, which suits gait well: a trot is strongly periodic,
    /// so a stumble or a dragging leg breaks the pattern in a way a fixed threshold would miss entirely.
    /// </remarks>
    public IReadOnlyList<GaitAnomalyPrediction> DetectAnomalies(
        IReadOnlyList<GaitSample> samples,
        double confidence = 95.0,
        Func<GaitSample, float>? selector = null)
    {
        ArgumentNullException.ThrowIfNull(samples);

        const int MinimumSamples = 24;

        if (samples.Count < MinimumSamples)
        {
            _logger.LogDebug(
                "Skipping anomaly detection: {Count} samples is below the {Minimum} needed to fit a model.",
                samples.Count,
                MinimumSamples);
            return [];
        }

        selector ??= sample => sample.TotalFootForce;

        var series = samples.Select(sample => new MonitoredValue { Value = selector(sample) }).ToList();
        IDataView data = _mlContext.Data.LoadFromEnumerable(series);

        // The training window covers half the data and the seasonality window a quarter of that, which
        // keeps the model responsive without letting a single stride dominate the fit.
        int trainingWindow = Math.Max(MinimumSamples / 2, samples.Count / 2);
        int seasonalityWindow = Math.Max(4, trainingWindow / 4);

        SsaSpikeEstimator estimator = _mlContext.Transforms.DetectSpikeBySsa(
            outputColumnName: nameof(GaitAnomalyPrediction.Prediction),
            inputColumnName: nameof(MonitoredValue.Value),
            confidence: confidence,
            pvalueHistoryLength: seasonalityWindow,
            trainingWindowSize: trainingWindow,
            seasonalityWindowSize: seasonalityWindow);

        try
        {
            ITransformer model = estimator.Fit(data);
            IDataView transformed = model.Transform(data);

            return [.. _mlContext.Data.CreateEnumerable<GaitAnomalyPrediction>(transformed, reuseRowObject: false)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A degenerate window — a robot standing perfectly still, for instance — has no structure to
            // model. That is not an error worth propagating to a caller that is just watching for limps.
            _logger.LogDebug(ex, "SSA spike detection could not fit a model to this window.");
            return [];
        }
    }

    private static double StandardDeviation(IEnumerable<double> values)
    {
        double[] materialised = [.. values];

        if (materialised.Length < 2)
        {
            return 0;
        }

        double mean = materialised.Average();
        double sumOfSquares = materialised.Sum(value => (value - mean) * (value - mean));
        return Math.Sqrt(sumOfSquares / (materialised.Length - 1));
    }

    private sealed class MonitoredValue
    {
        public float Value { get; set; }
    }
}
