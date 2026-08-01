using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TorchSharp;
using Unitree.Net.Core;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Ml;

/// <summary>
/// How an observation vector is assembled for a learned policy.
/// </summary>
/// <remarks>
/// The layout must match what the policy was trained on, element for element. A mismatch does not throw —
/// the network happily consumes any correctly-sized vector — it just produces confident nonsense, which
/// on a robot means a fall. Keep this in step with the training environment's observation spec.
/// </remarks>
public sealed class ObservationSpec
{
    /// <summary>Scale applied to joint positions.</summary>
    public float PositionScale { get; init; } = 1.0f;

    /// <summary>Scale applied to joint velocities.</summary>
    public float VelocityScale { get; init; } = 0.05f;

    /// <summary>Scale applied to body angular rates.</summary>
    public float AngularVelocityScale { get; init; } = 0.25f;

    /// <summary>Joint positions the policy treats as its neutral pose, radians.</summary>
    public required float[] DefaultJointPositions { get; init; }

    /// <summary>Number of joints included in the observation.</summary>
    public int JointCount => DefaultJointPositions.Length;

    /// <summary>
    /// Total observation length: gravity (3) + angular rate (3) + command (3) + positions + velocities + last action.
    /// </summary>
    public int ObservationLength => 9 + (JointCount * 3);

    /// <summary>The standard 12-joint quadruped layout used by most Isaac Lab locomotion tasks.</summary>
    public static ObservationSpec Go2Default => new()
    {
        DefaultJointPositions =
        [
            0.0f, 0.8f, -1.5f,
            0.0f, 0.8f, -1.5f,
            0.0f, 1.0f, -1.5f,
            0.0f, 1.0f, -1.5f,
        ],
    };
}

/// <summary>
/// Runs a TorchSharp locomotion policy against live robot state.
/// </summary>
/// <remarks>
/// <para>
/// Loads a TorchScript module exported from a training run — Isaac Lab, Legged Gym, or any PyTorch
/// pipeline that can call <c>torch.jit.save</c> — and evaluates it at control rate.
/// </para>
/// <para>
/// TorchSharp needs a libtorch backend at runtime. The <c>TorchSharp</c> package alone does not include
/// one; add <c>TorchSharp-cpu</c> (or a CUDA variant) to the application project. Without it, the first
/// call throws rather than the constructor, so check <see cref="IsBackendAvailable"/> at startup rather
/// than discovering it mid-gait.
/// </para>
/// </remarks>
public sealed class PolicyRunner : IDisposable
{
    private readonly torch.jit.ScriptModule<torch.Tensor, torch.Tensor> _module;
    private readonly ObservationSpec _spec;
    private readonly ILogger _logger;
    private readonly float[] _observation;
    private readonly float[] _lastAction;
    private readonly torch.Device _device;
    private bool _disposed;

    private PolicyRunner(
        torch.jit.ScriptModule<torch.Tensor, torch.Tensor> module,
        ObservationSpec spec,
        torch.Device device,
        ILogger logger)
    {
        _module = module;
        _spec = spec;
        _device = device;
        _logger = logger;
        _observation = new float[spec.ObservationLength];
        _lastAction = new float[spec.JointCount];
    }

    /// <summary>Length of the observation vector this policy consumes.</summary>
    public int ObservationLength => _spec.ObservationLength;

    /// <summary>Number of actions the policy produces.</summary>
    public int ActionLength => _spec.JointCount;

    /// <summary>Whether a libtorch backend is loadable in this process.</summary>
    public static bool IsBackendAvailable
    {
        get
        {
            try
            {
                using torch.Tensor probe = torch.zeros(1);
                return true;
            }
            catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Loads a TorchScript policy from disk.
    /// </summary>
    /// <param name="modelPath">Path to a <c>torch.jit.save</c> archive.</param>
    /// <param name="spec">Observation layout, which must match the training environment.</param>
    /// <param name="useCuda">Whether to place the model on CUDA when available.</param>
    /// <param name="logger">Logger.</param>
    public static PolicyRunner Load(
        string modelPath,
        ObservationSpec spec,
        bool useCuda = false,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(spec);

        ILogger effectiveLogger = logger ?? NullLogger.Instance;

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Policy model not found at '{modelPath}'.", modelPath);
        }

        if (!IsBackendAvailable)
        {
            throw new UnitreeException(
                "No libtorch backend is loadable. Add the TorchSharp-cpu package (or a CUDA variant) " +
                "to the application project — the TorchSharp package alone contains only the managed bindings.");
        }

        torch.Device device = useCuda && torch.cuda.is_available()
            ? torch.CUDA
            : torch.CPU;

        if (useCuda && !torch.cuda.is_available())
        {
            effectiveLogger.LogWarning("CUDA was requested but is unavailable; running the policy on CPU.");
        }

        var module = torch.jit.load<torch.Tensor, torch.Tensor>(modelPath, device);
        module.eval();

        effectiveLogger.LogInformation(
            "Loaded policy from {Path} on {Device}; observation length {ObservationLength}, {ActionLength} actions.",
            modelPath,
            device.type,
            spec.ObservationLength,
            spec.JointCount);

        return new PolicyRunner(module, spec, device, effectiveLogger);
    }

    /// <summary>
    /// Builds an observation vector from robot state and a velocity command.
    /// </summary>
    /// <param name="state">Current low-level state.</param>
    /// <param name="command">The commanded body velocity.</param>
    /// <returns>The observation, valid until the next call.</returns>
    /// <remarks>
    /// The returned span aliases an internal buffer that is overwritten on every call. That is deliberate:
    /// at 500 Hz, allocating a fresh array per tick would put steady pressure on the GC in the one place
    /// a pause is least acceptable.
    /// </remarks>
    public ReadOnlySpan<float> BuildObservation(in LowState state, VelocityCommand command)
    {
        int offset = 0;
        int jointCount = _spec.JointCount;

        // Projected gravity in the body frame is the standard orientation input for locomotion policies:
        // unlike raw roll and pitch it has no discontinuity and no gimbal degeneracy.
        System.Numerics.Quaternion orientation = state.ImuState.ToQuaternion();
        System.Numerics.Vector3 gravity = System.Numerics.Vector3.Transform(
            new System.Numerics.Vector3(0f, 0f, -1f),
            System.Numerics.Quaternion.Conjugate(orientation));

        _observation[offset++] = gravity.X;
        _observation[offset++] = gravity.Y;
        _observation[offset++] = gravity.Z;

        _observation[offset++] = state.ImuState.Gyroscope[0] * _spec.AngularVelocityScale;
        _observation[offset++] = state.ImuState.Gyroscope[1] * _spec.AngularVelocityScale;
        _observation[offset++] = state.ImuState.Gyroscope[2] * _spec.AngularVelocityScale;

        _observation[offset++] = command.Forward;
        _observation[offset++] = command.Lateral;
        _observation[offset++] = command.YawRate;

        for (int i = 0; i < jointCount; i++)
        {
            _observation[offset++] = (state.MotorState[i].Q - _spec.DefaultJointPositions[i]) * _spec.PositionScale;
        }

        for (int i = 0; i < jointCount; i++)
        {
            _observation[offset++] = state.MotorState[i].Dq * _spec.VelocityScale;
        }

        for (int i = 0; i < jointCount; i++)
        {
            _observation[offset++] = _lastAction[i];
        }

        return _observation;
    }

    /// <summary>
    /// Evaluates the policy and returns target joint positions.
    /// </summary>
    /// <param name="observation">An observation of length <see cref="ObservationLength"/>.</param>
    /// <param name="actionScale">Scale from network output to radians of offset from the neutral pose.</param>
    /// <returns>Target joint positions, radians.</returns>
    public float[] Evaluate(ReadOnlySpan<float> observation, float actionScale = 0.25f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (observation.Length != _spec.ObservationLength)
        {
            throw new ArgumentException(
                $"Expected an observation of length {_spec.ObservationLength} but received {observation.Length}. " +
                "This usually means the ObservationSpec does not match the trained policy.",
                nameof(observation));
        }

        // Every intermediate tensor created inside this scope is freed on exit. Without it, native
        // allocations accumulate until the GC happens to run finalizers, which at control rate means
        // unbounded growth in unmanaged memory.
        using var scope = torch.NewDisposeScope();

        torch.Tensor input = torch.tensor(observation.ToArray(), [1, _spec.ObservationLength], device: _device);
        torch.Tensor output = _module.forward(input);

        float[] actions = output.reshape(-1).cpu().data<float>().ToArray();

        if (actions.Length != _spec.JointCount)
        {
            throw new UnitreeException(
                $"Policy produced {actions.Length} actions but the robot has {_spec.JointCount} joints.");
        }

        var targets = new float[_spec.JointCount];

        for (int i = 0; i < actions.Length; i++)
        {
            _lastAction[i] = actions[i];
            targets[i] = _spec.DefaultJointPositions[i] + (actions[i] * actionScale);
        }

        return targets;
    }

    /// <summary>Clears the remembered previous action, for use when restarting a policy.</summary>
    public void Reset() => Array.Clear(_lastAction);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _module.Dispose();
    }
}
