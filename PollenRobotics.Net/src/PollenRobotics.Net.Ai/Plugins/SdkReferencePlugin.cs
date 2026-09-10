using System.ComponentModel;
using System.Reflection;
using System.Text;
using Microsoft.SemanticKernel;
using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.Ai.Plugins;

/// <summary>
/// Lets the model look the SDK up instead of remembering it.
/// </summary>
/// <remarks>
/// <para>
/// This reflects over the actually-loaded SDK assemblies, so what it reports is what will compile.
/// That is the entire point. Telling an assistant to "check the API before using it" does not work
/// on its own - it skips the check precisely when it feels confident, which is when it is most
/// likely to be quoting a signature from some other robotics SDK. A tool that answers with the real
/// signature does work, because the answer arrives in the conversation whether or not the model
/// thought it needed one.
/// </para>
/// <para>
/// Output is deliberately terse. A model given three screens of XML documentation per type spends
/// its context on prose; a signature list is what it needs to write a call correctly.
/// </para>
/// </remarks>
public sealed class SdkReferencePlugin
{
    private static readonly Assembly[] SdkAssemblies =
    [
        typeof(RobotCatalog).Assembly,
        typeof(ReachyMini.ReachyMiniClient).Assembly,
        typeof(MicroDuck.MicroDuckClient).Assembly,
        typeof(Reachy2.Reachy2Client).Assembly,
        typeof(Kinematics.SerialChain).Assembly,
    ];

    /// <summary>Lists the SDK's public types, optionally filtered.</summary>
    [KernelFunction("list_sdk_types")]
    [Description("Lists public types in the PollenRobotics.Net SDK. Pass a filter such as 'ReachyMini', 'Duck' or 'Pose' to narrow it. Call this first when you are not sure what a type is called.")]
    public string ListTypes(
        [Description("Case-insensitive substring to match against the type name. Empty lists everything.")] string filter = "")
    {
        IEnumerable<Type> types = SdkAssemblies
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => !t.IsNested && (t.IsClass || t.IsInterface || t.IsEnum || t.IsValueType))
            .Where(t => string.IsNullOrWhiteSpace(filter) || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Namespace, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal);

        var builder = new StringBuilder();
        string? currentNamespace = null;
        int count = 0;

        foreach (Type type in types)
        {
            if (type.Namespace != currentNamespace)
            {
                currentNamespace = type.Namespace;
                builder.AppendLine();
                builder.AppendLine($"namespace {currentNamespace}");
            }

            builder.AppendLine($"  {Kind(type)} {FriendlyName(type)}");
            count++;
        }

        return count == 0
            ? $"No public SDK type matches '{filter}'. Try a shorter filter, or an empty one to list everything."
            : $"{count} type(s) matching '{filter}':{builder}";
    }

    /// <summary>Describes one type's public members.</summary>
    [KernelFunction("describe_sdk_type")]
    [Description("Lists the public constructors, properties and methods of one SDK type, with full signatures. Call this before writing code that uses the type.")]
    public string DescribeType(
        [Description("Type name, with or without its namespace. For example 'ReachyMiniClient' or 'PollenRobotics.Net.ReachyMini.ReachyMiniClient'.")] string typeName)
    {
        Type? type = Resolve(typeName);

        if (type is null)
        {
            return $"No public SDK type named '{typeName}'. Use list_sdk_types to find the right name.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{Kind(type)} {FriendlyName(type)}");
        builder.AppendLine($"  namespace {type.Namespace}");

        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType))
        {
            builder.AppendLine($"  inherits {FriendlyName(baseType)}");
        }

        Type[] interfaces = type.GetInterfaces().Where(i => i.IsPublic).ToArray();
        if (interfaces.Length > 0)
        {
            builder.AppendLine($"  implements {string.Join(", ", interfaces.Select(FriendlyName))}");
        }

        if (type.IsEnum)
        {
            builder.AppendLine();
            builder.AppendLine("  values:");
            foreach (string name in Enum.GetNames(type))
            {
                builder.AppendLine($"    {name}");
            }

            return builder.ToString();
        }

        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        ConstructorInfo[] constructors = type.GetConstructors(Public);
        if (constructors.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  constructors:");
            foreach (ConstructorInfo constructor in constructors)
            {
                builder.AppendLine($"    new {type.Name}({Parameters(constructor.GetParameters())})");
            }
        }

        PropertyInfo[] properties = type.GetProperties(Public);
        if (properties.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  properties:");
            foreach (PropertyInfo property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                string accessors = property.CanRead && property.CanWrite ? "{ get; set; }" : property.CanRead ? "{ get; }" : "{ set; }";
                builder.AppendLine($"    {FriendlyName(property.PropertyType)} {property.Name} {accessors}");
            }
        }

        // Property accessors and event add/remove show up as methods; they are already reported
        // above, and listing them again doubles the output for no information.
        MethodInfo[] methods = [.. type.GetMethods(Public).Where(m => !m.IsSpecialName)];

        if (methods.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  methods:");
            foreach (MethodInfo method in methods.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                string modifier = method.IsStatic ? "static " : string.Empty;
                builder.AppendLine($"    {modifier}{FriendlyName(method.ReturnType)} {method.Name}({Parameters(method.GetParameters())})");
            }
        }

        EventInfo[] events = type.GetEvents(Public);
        if (events.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  events:");
            foreach (EventInfo declared in events)
            {
                builder.AppendLine($"    event {FriendlyName(declared.EventHandlerType!)} {declared.Name}");
            }
        }

        return builder.ToString();
    }

    /// <summary>Lists a robot's joints and their limits.</summary>
    [KernelFunction("get_robot_joints")]
    [Description("Lists a robot's joints in wire order with their limits in degrees. Use it before writing anything that commands joint angles.")]
    public string GetRobotJoints(
        [Description("Robot name: ReachyMini, MicroDuck or Reachy2.")] string robot)
    {
        if (!Enum.TryParse(robot.Replace(" ", string.Empty), ignoreCase: true, out RobotKind kind))
        {
            return $"Unknown robot '{robot}'. Valid values: ReachyMini, MicroDuck, Reachy2.";
        }

        RobotDescription description = RobotCatalog.For(kind);
        var builder = new StringBuilder();
        builder.AppendLine($"{description.DisplayName}: {description.JointCount} actuated joints, in wire order.");

        foreach (JointDescriptor joint in description.Joints)
        {
            builder.AppendLine($"  [{joint.Index,2}] {joint.Name,-26} {joint.Lower.Degrees,7:0.#} .. {joint.Upper.Degrees,7:0.#} deg");
        }

        if (kind == RobotKind.ReachyMini)
        {
            builder.AppendLine();
            builder.AppendLine("Pose limits, which bind before the per-joint ones: head pitch and roll +/-40 deg, "
                + "body yaw +/-160 deg, and head yaw must stay within 65 deg of body yaw.");
        }

        return builder.ToString();
    }

    /// <summary>Finds SDK members by name.</summary>
    [KernelFunction("search_sdk")]
    [Description("Searches the SDK for public members whose name contains a term - for example 'Goto', 'Antenna' or 'Velocity'. Use it when you know what you want to do but not what it is called.")]
    public string Search(
        [Description("Substring to look for in member names.")] string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return "Error: the search term is empty.";
        }

        var builder = new StringBuilder();
        int found = 0;

        foreach (Type type in SdkAssemblies.SelectMany(a => a.GetExportedTypes()).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is MethodInfo { IsSpecialName: true } ||
                    !member.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                builder.AppendLine($"  {type.Name}.{Signature(member)}");

                if (++found >= 60)
                {
                    builder.AppendLine("  [more results omitted; narrow the search term]");
                    return $"Members matching '{term}':{Environment.NewLine}{builder}";
                }
            }
        }

        return found == 0
            ? $"Nothing in the SDK matches '{term}'."
            : $"{found} member(s) matching '{term}':{Environment.NewLine}{builder}";
    }

    private static Type? Resolve(string typeName)
    {
        string wanted = typeName.Trim();

        return SdkAssemblies
            .SelectMany(a => a.GetExportedTypes())
            .FirstOrDefault(t =>
                string.Equals(t.FullName, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase) ||
                // Generic arity is spelled `Name`1` in metadata and nobody types that.
                string.Equals(t.Name.Split('`')[0], wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string Signature(MemberInfo member) => member switch
    {
        MethodInfo method => $"{method.Name}({Parameters(method.GetParameters())}) -> {FriendlyName(method.ReturnType)}",
        PropertyInfo property => $"{property.Name} : {FriendlyName(property.PropertyType)}",
        FieldInfo field => $"{field.Name} : {FriendlyName(field.FieldType)}",
        EventInfo declared => $"event {declared.Name}",
        ConstructorInfo constructor => $"ctor({Parameters(constructor.GetParameters())})",
        _ => member.Name,
    };

    private static string Parameters(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(p =>
        {
            string optional = p.HasDefaultValue ? " = ..." : string.Empty;
            return $"{FriendlyName(p.ParameterType)} {p.Name}{optional}";
        }));

    private static string Kind(Type type) => type switch
    {
        { IsInterface: true } => "interface",
        { IsEnum: true } => "enum",
        { IsValueType: true } => "struct",
        { IsAbstract: true, IsSealed: true } => "static class",
        _ => "class",
    };

    /// <summary>
    /// Renders a type the way it would be written in C#.
    /// </summary>
    /// <remarks>
    /// <c>Task`1[Double[]]</c> is not something anyone can copy into a source file;
    /// <c>Task&lt;double[]&gt;</c> is. The model is going to reproduce whatever shape it sees.
    /// </remarks>
    private static string FriendlyName(Type type)
    {
        if (type.IsArray)
        {
            return $"{FriendlyName(type.GetElementType()!)}[]";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return $"{FriendlyName(underlying)}?";
        }

        if (type.IsGenericType)
        {
            string name = type.Name.Split('`')[0];
            string arguments = string.Join(", ", type.GetGenericArguments().Select(FriendlyName));
            return $"{name}<{arguments}>";
        }

        return type.Name switch
        {
            "Void" => "void",
            "Boolean" => "bool",
            "Int32" => "int",
            "Int64" => "long",
            "Double" => "double",
            "Single" => "float",
            "String" => "string",
            "Object" => "object",
            "Byte" => "byte",
            "UInt32" => "uint",
            _ => type.Name,
        };
    }
}
