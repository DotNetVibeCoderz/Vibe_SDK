namespace PollenRobotics.Net.Core.Robots;

/// <summary>The robot families this SDK speaks to.</summary>
public enum RobotKind
{
    /// <summary>Reachy Mini - desk robot, 9 actuated degrees of freedom.</summary>
    ReachyMini,

    /// <summary>MicroDuck - 25 cm biped, 15 servos, driven by reinforcement-learning policies.</summary>
    MicroDuck,

    /// <summary>Reachy 2 - two 7-DOF arms, Orbita neck, optional mobile base.</summary>
    Reachy2,
}
