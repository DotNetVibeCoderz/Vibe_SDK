using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Unitree.Net.Simulation;

namespace Unitree.Net.Simulator;

/// <summary>
/// A vector in the shape the viewport expects.
/// </summary>
/// <param name="X">X component.</param>
/// <param name="Y">Y component.</param>
/// <param name="Z">Z component.</param>
public readonly record struct ViewportVector(float X, float Y, float Z)
{
    /// <summary>Converts from a numeric vector.</summary>
    /// <param name="value">The vector to convert.</param>
    public static ViewportVector From(Vector3 value) => new(value.X, value.Y, value.Z);
}

/// <summary>A drawable primitive, as the viewport sees it.</summary>
/// <param name="Kind">Primitive index matching <see cref="RigShapeKind"/>.</param>
/// <param name="Size">Dimensions, interpreted per kind.</param>
/// <param name="Center">Offset within the link frame.</param>
/// <param name="Surface">Surface role index matching <see cref="RigSurface"/>.</param>
/// <param name="AxisAlong">Which local axis a capsule or cylinder runs along.</param>
public readonly record struct ViewportShape(
    int Kind,
    ViewportVector Size,
    ViewportVector Center,
    int Surface,
    int AxisAlong);

/// <summary>One rigid body, as the viewport sees it.</summary>
/// <param name="Name">Link name.</param>
/// <param name="Parent">Parent link name, or <see langword="null"/> for the root.</param>
/// <param name="Offset">Translation from the parent.</param>
/// <param name="Axis">Rotation axis in the link frame.</param>
/// <param name="JointIndex">Index into the pose array, or -1 when fixed.</param>
/// <param name="Sign">Maps joint angle sign to rotation direction.</param>
/// <param name="Shapes">Primitives drawn in this link's frame.</param>
public sealed record ViewportLink(
    string Name,
    string? Parent,
    ViewportVector Offset,
    ViewportVector Axis,
    int JointIndex,
    float Sign,
    IReadOnlyList<ViewportShape> Shapes);

/// <summary>
/// A rig flattened into the shape <c>viewport.js</c> consumes.
/// </summary>
/// <param name="Model">Platform name.</param>
/// <param name="JointCount">Number of actuated joints.</param>
/// <param name="StandingHeight">Body height above the ground when standing, in metres.</param>
/// <param name="Links">Links, parents ahead of children.</param>
/// <param name="ContactLinks">Names of the ground-contact links, in contact order.</param>
/// <remarks>
/// Enums cross as integers rather than names. The viewport switches on them in a hot path, and an
/// integer comparison there is both faster and immune to a casing mismatch between the two languages.
/// </remarks>
public sealed record ViewportRig(
    string Model,
    int JointCount,
    float StandingHeight,
    IReadOnlyList<ViewportLink> Links,
    IReadOnlyList<string> ContactLinks)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Flattens <paramref name="rig"/> for the viewport.</summary>
    /// <param name="rig">The rig to convert.</param>
    public static ViewportRig From(RobotRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);

        var links = new List<ViewportLink>(rig.Links.Count);

        foreach (RigLink link in rig.Links)
        {
            var shapes = new List<ViewportShape>(link.Shapes.Count);

            foreach (RigShape shape in link.Shapes)
            {
                shapes.Add(new ViewportShape(
                    (int)shape.Kind,
                    ViewportVector.From(shape.Size),
                    ViewportVector.From(shape.Center),
                    (int)shape.Surface,
                    shape.AxisAlong));
            }

            links.Add(new ViewportLink(
                link.Name,
                link.Parent,
                ViewportVector.From(link.Offset),
                ViewportVector.From(link.Axis),
                link.JointIndex,
                link.Sign,
                shapes));
        }

        return new ViewportRig(
            rig.DisplayName,
            rig.JointCount,
            rig.StandingHeight,
            links,
            rig.ContactLinks);
    }

    /// <summary>Serialises to the JSON shape the viewport module expects.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
