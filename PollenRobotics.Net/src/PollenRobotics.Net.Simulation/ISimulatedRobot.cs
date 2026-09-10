using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.Simulation;

/// <summary>
/// Everything the 3D viewport needs to draw one frame.
/// </summary>
/// <remarks>
/// Deliberately a flat, allocation-light record rather than a robot-specific type: the viewport
/// renders whichever robot is loaded and should not have to switch on the family to find a joint
/// angle. <see cref="JointPositions"/> is in <see cref="RobotDescription"/> order, so the renderer
/// resolves a joint by name once at load and then indexes.
/// </remarks>
/// <param name="Kind">Which robot this is.</param>
/// <param name="JointPositions">Joint angles in radians, in wire order.</param>
/// <param name="BodyPose">Where the robot's base sits in the world.</param>
/// <param name="SimulationTime">Time since the simulation started.</param>
/// <param name="IsMoving">True while something is actually changing.</param>
public readonly record struct SimulationSnapshot(
    RobotKind Kind,
    IReadOnlyList<double> JointPositions,
    Pose BodyPose,
    TimeSpan SimulationTime,
    bool IsMoving);

/// <summary>
/// A robot model the simulation engine can tick.
/// </summary>
/// <remarks>
/// <para>
/// These are kinematic models, not physics. Joints track their targets through the same easing
/// curves the real daemons use, contact is not modelled, and nothing falls over unless the model
/// decides to. That is enough to develop an application against, and it is deliberately not enough
/// to validate that a motion is physically safe.
/// </para>
/// <para>
/// A simulated robot must start in the same state a real one does after power-on: motors off, at
/// rest. Starting it standing and ready would let an application skip its own bring-up and still
/// work here, then do nothing at all on hardware.
/// </para>
/// </remarks>
public interface ISimulatedRobot
{
    /// <summary>Which robot this models.</summary>
    RobotKind Kind { get; }

    /// <summary>The joint model.</summary>
    RobotDescription Description { get; }

    /// <summary>Advances the model by one timestep.</summary>
    void Tick(TimeSpan delta);

    /// <summary>The current frame.</summary>
    SimulationSnapshot Snapshot();

    /// <summary>Returns the model to its power-on state.</summary>
    void Reset();
}
