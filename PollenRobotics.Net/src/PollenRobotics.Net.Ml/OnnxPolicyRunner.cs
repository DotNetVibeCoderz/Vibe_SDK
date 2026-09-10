using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PollenRobotics.Net.Core;

namespace PollenRobotics.Net.Ml;

/// <summary>
/// Runs an exported reinforcement-learning policy.
/// </summary>
/// <remarks>
/// <para>
/// This is the same shape MicroDuck uses on the robot: a policy trained with PPO in MuJoCo,
/// exported to ONNX, evaluated once per control tick. Running one here lets a policy be tested
/// against the simulator, or a learned behaviour be driven from a desktop application, without
/// putting Python on the robot.
/// </para>
/// <para>
/// The tensor buffers are allocated once and reused. At 50 Hz a fresh allocation per tick is 3,000
/// arrays a minute for no benefit, and on an RK3566 the resulting GC pressure is measurable against
/// the control loop.
/// </para>
/// <para>
/// <see cref="Evaluate(ReadOnlySpan{double}, Span{double})"/> is not thread-safe. One runner per control loop.
/// </para>
/// </remarks>
public sealed class OnnxPolicyRunner : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly float[] _inputBuffer;
    private readonly DenseTensor<float> _inputTensor;
    private readonly List<NamedOnnxValue> _inputs;
    private int _disposed;

    /// <summary>Length of the observation vector the policy expects.</summary>
    public int ObservationSize { get; }

    /// <summary>Length of the action vector the policy produces.</summary>
    public int ActionSize { get; }

    /// <summary>The model file this runner loaded.</summary>
    public string ModelPath { get; }

    /// <summary>Loads a policy from a file.</summary>
    /// <param name="modelPath">Path to the .onnx file.</param>
    /// <param name="observationSize">
    /// Observation length. Inferred from the model when omitted, which works unless the model
    /// declares a dynamic first dimension.
    /// </param>
    /// <param name="actionSize">Action length. Inferred when omitted.</param>
    public OnnxPolicyRunner(string modelPath, int? observationSize = null, int? actionSize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"No ONNX policy at {modelPath}.", modelPath);
        }

        ModelPath = modelPath;

        var options = new SessionOptions
        {
            // One thread. A policy this small does not parallelise usefully, and taking a second
            // core from a 50 Hz control loop costs more than the inference saves.
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        try
        {
            _session = new InferenceSession(modelPath, options);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new PollenRoboticsException($"Could not load the ONNX policy at {modelPath}: {ex.Message}", ex);
        }

        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();

        ObservationSize = observationSize ?? InferLength(_session.InputMetadata[_inputName].Dimensions, nameof(observationSize));
        ActionSize = actionSize ?? InferLength(_session.OutputMetadata[_outputName].Dimensions, nameof(actionSize));

        _inputBuffer = new float[ObservationSize];
        _inputTensor = new DenseTensor<float>(_inputBuffer, [1, ObservationSize]);
        _inputs = [NamedOnnxValue.CreateFromTensor(_inputName, _inputTensor)];
    }

    /// <summary>
    /// Evaluates the policy for one observation.
    /// </summary>
    /// <param name="observation">Observation vector, <see cref="ObservationSize"/> long.</param>
    /// <param name="action">Receives the action, <see cref="ActionSize"/> long.</param>
    public void Evaluate(ReadOnlySpan<double> observation, Span<double> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (observation.Length != ObservationSize)
        {
            throw new ArgumentException(
                $"This policy takes a {ObservationSize}-element observation, got {observation.Length}.", nameof(observation));
        }

        if (action.Length != ActionSize)
        {
            throw new ArgumentException(
                $"This policy produces a {ActionSize}-element action, got room for {action.Length}.", nameof(action));
        }

        for (int i = 0; i < observation.Length; i++)
        {
            _inputBuffer[i] = (float)observation[i];
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(_inputs);
        ReadOnlySpan<float> output = results.First(r => r.Name == _outputName).AsTensor<float>().ToArray();

        for (int i = 0; i < action.Length; i++)
        {
            action[i] = output[i];
        }
    }

    /// <summary>Evaluates the policy, returning a fresh array.</summary>
    public double[] Evaluate(ReadOnlySpan<double> observation)
    {
        double[] action = new double[ActionSize];
        Evaluate(observation, action);
        return action;
    }

    /// <summary>
    /// A one-line description of the model's inputs and outputs, for a diagnostics panel.
    /// </summary>
    public string Describe()
    {
        string inputs = string.Join(", ", _session.InputMetadata.Select(kv => $"{kv.Key}[{string.Join('x', kv.Value.Dimensions)}]"));
        string outputs = string.Join(", ", _session.OutputMetadata.Select(kv => $"{kv.Key}[{string.Join('x', kv.Value.Dimensions)}]"));
        return $"{Path.GetFileName(ModelPath)}: in {inputs} -> out {outputs}";
    }

    /// <summary>
    /// Reads the meaningful length out of a tensor shape.
    /// </summary>
    /// <remarks>
    /// The batch dimension is exported as -1 by most training frameworks, so the length is the last
    /// positive dimension rather than the first. A model with no positive dimension at all cannot
    /// be sized automatically and the caller has to say.
    /// </remarks>
    private static int InferLength(int[] dimensions, string parameterName)
    {
        for (int i = dimensions.Length - 1; i >= 0; i--)
        {
            if (dimensions[i] > 0)
            {
                return dimensions[i];
            }
        }

        throw new PollenRoboticsException(
            $"This model declares a fully dynamic shape [{string.Join(", ", dimensions)}], so its size cannot be " +
            $"inferred. Pass {parameterName} explicitly.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.Dispose();
    }
}
