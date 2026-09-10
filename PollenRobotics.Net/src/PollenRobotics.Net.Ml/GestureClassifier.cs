using Microsoft.ML;
using Microsoft.ML.Data;
using PollenRobotics.Net.Core;

namespace PollenRobotics.Net.Ml;

/// <summary>One labelled window of motion, used to train or query the classifier.</summary>
public sealed class GestureSample
{
    /// <summary>
    /// A flattened window of sensor readings.
    /// </summary>
    /// <remarks>
    /// Every sample fed to one classifier must be the same length - ML.NET fixes the feature vector
    /// size when the model is trained, and a shorter window fails at prediction time with a shape
    /// error rather than a useful message. <see cref="GestureWindow"/> exists to make that hard to
    /// get wrong.
    /// </remarks>
    [VectorType]
    public float[] Features { get; set; } = [];

    /// <summary>The gesture name.</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>The classifier's answer.</summary>
public sealed class GesturePrediction
{
    /// <summary>The winning label.</summary>
    [ColumnName("PredictedLabel")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Per-class scores, in the order the model learned them.</summary>
    public float[] Score { get; set; } = [];

    /// <summary>The winning score, which is the one to threshold on.</summary>
    public float Confidence => Score.Length == 0 ? 0 : Score.Max();
}

/// <summary>
/// A sliding window over a sensor stream, flattened into a feature vector.
/// </summary>
/// <remarks>
/// Gesture recognition on a robot is nearly always "classify the last second of motion", and the
/// awkward part is keeping the window a fixed length while samples arrive at an uneven rate. This
/// keeps a ring of fixed capacity and reports whether it is full; classifying a partly-filled
/// window trains the model on padding zeros that will never appear at runtime.
/// </remarks>
public sealed class GestureWindow
{
    private readonly float[] _buffer;
    private readonly int _channels;
    private int _count;
    private int _head;

    /// <summary>Number of samples the window holds.</summary>
    public int Length { get; }

    /// <summary>Values per sample.</summary>
    public int Channels => _channels;

    /// <summary>Total feature vector length.</summary>
    public int FeatureCount => Length * _channels;

    /// <summary>True once enough samples have arrived to classify.</summary>
    public bool IsFull => _count >= Length;

    /// <summary>Creates a window.</summary>
    /// <param name="length">How many samples to keep.</param>
    /// <param name="channels">How many values each sample carries, e.g. 3 for one accelerometer.</param>
    public GestureWindow(int length, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        Length = length;
        _channels = channels;
        _buffer = new float[length * channels];
    }

    /// <summary>Appends one sample, evicting the oldest once full.</summary>
    public void Append(ReadOnlySpan<double> sample)
    {
        if (sample.Length != _channels)
        {
            throw new ArgumentException($"This window takes {_channels} values per sample, got {sample.Length}.", nameof(sample));
        }

        int offset = _head * _channels;
        for (int i = 0; i < _channels; i++)
        {
            _buffer[offset + i] = (float)sample[i];
        }

        _head = (_head + 1) % Length;
        _count = Math.Min(_count + 1, Length);
    }

    /// <summary>
    /// The window as a feature vector, oldest sample first.
    /// </summary>
    /// <remarks>
    /// Unrolled from the ring into chronological order. Handing the raw ring to the model instead
    /// would rotate the features by an amount that depends on how many samples have gone past,
    /// which trains beautifully and predicts at chance.
    /// </remarks>
    public float[] ToFeatures()
    {
        float[] features = new float[FeatureCount];
        int start = _count < Length ? 0 : _head;

        for (int i = 0; i < Length; i++)
        {
            int source = (start + i) % Length;
            Array.Copy(_buffer, source * _channels, features, i * _channels, _channels);
        }

        return features;
    }

    /// <summary>Empties the window.</summary>
    public void Clear()
    {
        Array.Clear(_buffer);
        _count = 0;
        _head = 0;
    }
}

/// <summary>
/// A small multi-class classifier over windows of motion, built on ML.NET.
/// </summary>
/// <remarks>
/// Suited to the scale of problem a robot application actually has: a few gestures, a few hundred
/// examples each, trained on the spot rather than in a separate pipeline. SDCA maximum entropy is
/// the right default there - fast, deterministic, and it does not need a GPU. For anything larger,
/// train elsewhere, export ONNX and use <see cref="OnnxPolicyRunner"/>.
/// </remarks>
public sealed class GestureClassifier
{
    private readonly MLContext _context;
    private ITransformer? _model;
    private PredictionEngine<GestureSample, GesturePrediction>? _engine;
    private DataViewSchema? _schema;

    /// <summary>True once a model has been trained or loaded.</summary>
    public bool IsTrained => _model is not null;

    /// <summary>Creates a classifier.</summary>
    /// <param name="seed">Fixes the random seed so training is reproducible.</param>
    public GestureClassifier(int? seed = 42) => _context = new MLContext(seed);

    /// <summary>
    /// Trains on labelled samples.
    /// </summary>
    /// <param name="samples">At least two labels, with several examples each.</param>
    /// <returns>Macro accuracy on the training set.</returns>
    /// <remarks>
    /// The returned accuracy is measured on the data the model just learned, so it is an upper
    /// bound and not an estimate of real performance. Hold data back and use
    /// <see cref="Evaluate"/> for that.
    /// </remarks>
    public double Train(IReadOnlyCollection<GestureSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count < 2)
        {
            throw new ArgumentException("Training needs at least two samples.", nameof(samples));
        }

        int distinctLabels = samples.Select(s => s.Label).Distinct(StringComparer.Ordinal).Count();
        if (distinctLabels < 2)
        {
            throw new ArgumentException(
                $"Training needs at least two distinct labels, got {distinctLabels}.", nameof(samples));
        }

        int featureCount = samples.First().Features.Length;
        if (samples.Any(s => s.Features.Length != featureCount))
        {
            throw new ArgumentException(
                "Every sample must have the same feature count. Use one GestureWindow to build them all.", nameof(samples));
        }

        IDataView data = _context.Data.LoadFromEnumerable(samples);

        EstimatorChain<Microsoft.ML.Transforms.KeyToValueMappingTransformer> pipeline = _context.Transforms.Conversion
            .MapValueToKey(nameof(GestureSample.Label))
            .Append(_context.Transforms.NormalizeMinMax(nameof(GestureSample.Features)))
            .Append(_context.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                labelColumnName: nameof(GestureSample.Label),
                featureColumnName: nameof(GestureSample.Features)))
            .Append(_context.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        _model = pipeline.Fit(data);
        _schema = data.Schema;
        _engine = _context.Model.CreatePredictionEngine<GestureSample, GesturePrediction>(_model);

        return Evaluate(samples);
    }

    /// <summary>Measures macro accuracy on a set of labelled samples.</summary>
    public double Evaluate(IReadOnlyCollection<GestureSample> samples)
    {
        if (_model is null)
        {
            throw new PollenRoboticsException("Train or load a model first.");
        }

        IDataView predictions = _model.Transform(_context.Data.LoadFromEnumerable(samples));

        return _context.MulticlassClassification
            .Evaluate(predictions, labelColumnName: nameof(GestureSample.Label))
            .MacroAccuracy;
    }

    /// <summary>Classifies one feature vector.</summary>
    public GesturePrediction Predict(float[] features)
    {
        if (_engine is null)
        {
            throw new PollenRoboticsException("Train or load a model first.");
        }

        return _engine.Predict(new GestureSample { Features = features });
    }

    /// <summary>Classifies a full window.</summary>
    /// <exception cref="PollenRoboticsException">The window is not yet full.</exception>
    public GesturePrediction Predict(GestureWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!window.IsFull)
        {
            throw new PollenRoboticsException(
                "The window is not full yet. Classifying a partial window matches it against padding that " +
                "never occurs at runtime; wait for GestureWindow.IsFull.");
        }

        return Predict(window.ToFeatures());
    }

    /// <summary>Saves the trained model.</summary>
    public void Save(string path)
    {
        if (_model is null || _schema is null)
        {
            throw new PollenRoboticsException("Train a model before saving it.");
        }

        _context.Model.Save(_model, _schema, path);
    }

    /// <summary>Loads a model saved by <see cref="Save"/>.</summary>
    public void Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No model at {path}.", path);
        }

        _model = _context.Model.Load(path, out DataViewSchema schema);
        _schema = schema;
        _engine = _context.Model.CreatePredictionEngine<GestureSample, GesturePrediction>(_model);
    }
}
