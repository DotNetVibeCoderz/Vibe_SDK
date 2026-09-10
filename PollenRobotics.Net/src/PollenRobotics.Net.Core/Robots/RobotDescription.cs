using System.Collections.Frozen;

namespace PollenRobotics.Net.Core.Robots;

/// <summary>
/// The static description of a robot: which joints it has, in which order, and what they are called.
/// </summary>
/// <remarks>
/// Every layer that needs to agree on joint ordering - the transports, the kinematics, the
/// simulator's 3D rig, the wizard's generated code - resolves it through here rather than
/// hard-coding indices. A hard-coded index is correct until a variant with a different joint count
/// appears, and then it is silently wrong.
/// </remarks>
public sealed class RobotDescription
{
    private readonly FrozenDictionary<string, JointDescriptor> _byName;

    /// <summary>The robot family.</summary>
    public RobotKind Kind { get; }

    /// <summary>Human-readable model name.</summary>
    public string DisplayName { get; }

    /// <summary>Joints in wire order.</summary>
    public IReadOnlyList<JointDescriptor> Joints { get; }

    /// <summary>Number of actuated degrees of freedom.</summary>
    public int JointCount => Joints.Count;

    /// <summary>Creates a description.</summary>
    public RobotDescription(RobotKind kind, string displayName, IReadOnlyList<JointDescriptor> joints)
    {
        ArgumentNullException.ThrowIfNull(joints);

        for (int i = 0; i < joints.Count; i++)
        {
            if (joints[i].Index != i)
            {
                throw new ArgumentException(
                    $"Joint '{joints[i].Name}' declares index {joints[i].Index} but sits at position {i}. " +
                    "The list order is the wire order and the two must agree.", nameof(joints));
            }
        }

        Kind = kind;
        DisplayName = displayName;
        Joints = joints;
        _byName = joints.ToFrozenDictionary(j => j.Name, StringComparer.Ordinal);
    }

    /// <summary>Looks a joint up by its dotted name.</summary>
    public JointDescriptor this[string name] => _byName.TryGetValue(name, out JointDescriptor j)
        ? j
        : throw new KeyNotFoundException($"{DisplayName} has no joint named '{name}'. Known joints: {string.Join(", ", _byName.Keys)}.");

    /// <summary>Looks a joint up by name without throwing.</summary>
    public bool TryGetJoint(string name, out JointDescriptor joint) => _byName.TryGetValue(name, out joint);

    /// <summary>True when the robot has a joint with this name.</summary>
    public bool HasJoint(string name) => _byName.ContainsKey(name);

    /// <summary>A joint vector holding each joint's neutral position.</summary>
    public double[] NeutralPositions()
    {
        double[] q = new double[JointCount];
        for (int i = 0; i < q.Length; i++)
        {
            q[i] = Joints[i].Neutral.Radians;
        }

        return q;
    }
}
