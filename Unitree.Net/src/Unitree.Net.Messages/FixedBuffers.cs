using System.Runtime.CompilerServices;

namespace Unitree.Net.Messages;

// Unitree's IDL uses fixed-length arrays throughout. [InlineArray] gives us those as value types
// embedded directly in the message struct: no heap allocation, no pointer chase, and an implicit
// conversion to Span<T> so the CDR codec can read and write them without copying.
//
// Each of these is deliberately a distinct named type rather than a generic, because the CLR requires
// the length to be baked into the type.

/// <summary>A fixed two-element byte buffer.</summary>
[InlineArray(2)]
public struct Byte2
{
    private byte _element0;
}

/// <summary>A fixed three-element byte buffer.</summary>
[InlineArray(3)]
public struct Byte3
{
    private byte _element0;
}

/// <summary>A fixed 12-element byte buffer.</summary>
[InlineArray(12)]
public struct Byte12
{
    private byte _element0;
}

/// <summary>A fixed 40-element byte buffer, used for the wireless remote payload.</summary>
[InlineArray(40)]
public struct Byte40
{
    private byte _element0;
}

/// <summary>A fixed two-element signed byte buffer.</summary>
[InlineArray(2)]
public struct SByte2
{
    private sbyte _element0;
}

/// <summary>A fixed two-element 32-bit unsigned integer buffer.</summary>
[InlineArray(2)]
public struct UInt32x2
{
    private uint _element0;
}

/// <summary>A fixed three-element 32-bit unsigned integer buffer.</summary>
[InlineArray(3)]
public struct UInt32x3
{
    private uint _element0;
}

/// <summary>A fixed four-element 16-bit signed integer buffer.</summary>
[InlineArray(4)]
public struct Int16x4
{
    private short _element0;
}

/// <summary>A fixed four-element 16-bit unsigned integer buffer.</summary>
[InlineArray(4)]
public struct UInt16x4
{
    private ushort _element0;
}

/// <summary>A fixed 15-element 16-bit unsigned integer buffer, one per battery cell.</summary>
[InlineArray(15)]
public struct UInt16x15
{
    private ushort _element0;
}

/// <summary>A fixed three-element float buffer.</summary>
[InlineArray(3)]
public struct Float3
{
    private float _element0;
}

/// <summary>A fixed four-element float buffer.</summary>
[InlineArray(4)]
public struct Float4
{
    private float _element0;
}

/// <summary>A fixed 12-element float buffer, typically four feet by three axes.</summary>
[InlineArray(12)]
public struct Float12
{
    private float _element0;
}
