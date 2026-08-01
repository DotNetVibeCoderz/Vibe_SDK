using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Unitree.Net.Core;

namespace Unitree.Net.Messages.Cdr;

/// <summary>
/// Writes DDS Common Data Representation (CDR) payloads into a caller-supplied span.
/// </summary>
/// <remarks>
/// <para>
/// CDR aligns each primitive to its own width, measured from the start of the encapsulated stream —
/// that is, <em>after</em> the four-byte encapsulation header, not from the start of the buffer.
/// Getting that origin wrong shifts every field past the first padding boundary, which is why alignment
/// here is always computed against <see cref="CdrConstants.EncapsulationHeaderSize"/>.
/// </para>
/// <para>
/// This is a <see langword="ref"/> struct over a span: it allocates nothing and is intended to be used
/// with a stack buffer on the publish path.
/// </para>
/// </remarks>
public ref struct CdrWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    /// <summary>
    /// Creates a writer over <paramref name="buffer"/> and emits the little-endian encapsulation header.
    /// </summary>
    /// <param name="buffer">Destination buffer. Must hold the header plus the encoded body.</param>
    public CdrWriter(Span<byte> buffer)
    {
        if (buffer.Length < CdrConstants.EncapsulationHeaderSize)
        {
            throw new CdrFormatException(
                $"Buffer of {buffer.Length} bytes is too small for the CDR encapsulation header.");
        }

        _buffer = buffer;
        CdrConstants.LittleEndianHeader.CopyTo(buffer);
        _position = CdrConstants.EncapsulationHeaderSize;
    }

    /// <summary>Number of bytes written so far, including the encapsulation header.</summary>
    public readonly int BytesWritten => _position;

    /// <summary>Writes an unsigned byte.</summary>
    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    /// <summary>Writes a signed byte.</summary>
    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));

    /// <summary>Writes a boolean as a single byte.</summary>
    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <summary>Writes a 16-bit signed integer, aligned to two bytes.</summary>
    public void WriteInt16(short value)
    {
        Align(2);
        EnsureCapacity(2);
        BinaryPrimitives.WriteInt16LittleEndian(_buffer[_position..], value);
        _position += 2;
    }

    /// <summary>Writes a 16-bit unsigned integer, aligned to two bytes.</summary>
    public void WriteUInt16(ushort value)
    {
        Align(2);
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer[_position..], value);
        _position += 2;
    }

    /// <summary>Writes a 32-bit signed integer, aligned to four bytes.</summary>
    public void WriteInt32(int value)
    {
        Align(4);
        EnsureCapacity(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer[_position..], value);
        _position += 4;
    }

    /// <summary>Writes a 32-bit unsigned integer, aligned to four bytes.</summary>
    public void WriteUInt32(uint value)
    {
        Align(4);
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer[_position..], value);
        _position += 4;
    }

    /// <summary>Writes a 64-bit signed integer, aligned to eight bytes.</summary>
    public void WriteInt64(long value)
    {
        Align(8);
        EnsureCapacity(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer[_position..], value);
        _position += 8;
    }

    /// <summary>Writes a 64-bit unsigned integer, aligned to eight bytes.</summary>
    public void WriteUInt64(ulong value)
    {
        Align(8);
        EnsureCapacity(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer[_position..], value);
        _position += 8;
    }

    /// <summary>Writes a 32-bit float, aligned to four bytes.</summary>
    public void WriteSingle(float value)
    {
        Align(4);
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer[_position..], value);
        _position += 4;
    }

    /// <summary>Writes a 64-bit float, aligned to eight bytes.</summary>
    public void WriteDouble(double value)
    {
        Align(8);
        EnsureCapacity(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer[_position..], value);
        _position += 8;
    }

    /// <summary>
    /// Writes a fixed-length array of bytes with no length prefix.
    /// </summary>
    public void WriteByteArray(scoped ReadOnlySpan<byte> values)
    {
        EnsureCapacity(values.Length);
        values.CopyTo(_buffer[_position..]);
        _position += values.Length;
    }

    /// <summary>Writes a fixed-length array of floats with no length prefix.</summary>
    public void WriteSingleArray(scoped ReadOnlySpan<float> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            WriteSingle(values[i]);
        }
    }

    /// <summary>Writes a fixed-length array of 32-bit unsigned integers with no length prefix.</summary>
    public void WriteUInt32Array(scoped ReadOnlySpan<uint> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            WriteUInt32(values[i]);
        }
    }

    /// <summary>Writes a fixed-length array of 16-bit signed integers with no length prefix.</summary>
    public void WriteInt16Array(scoped ReadOnlySpan<short> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            WriteInt16(values[i]);
        }
    }

    /// <summary>Writes a fixed-length array of 16-bit unsigned integers with no length prefix.</summary>
    public void WriteUInt16Array(scoped ReadOnlySpan<ushort> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            WriteUInt16(values[i]);
        }
    }

    /// <summary>
    /// Writes a variable-length byte sequence: a 32-bit count followed by the elements.
    /// </summary>
    public void WriteByteSequence(scoped ReadOnlySpan<byte> values)
    {
        WriteUInt32((uint)values.Length);
        WriteByteArray(values);
    }

    /// <summary>
    /// Writes a CDR string: a 32-bit length that <em>includes</em> the terminator, the UTF-8 bytes, then a null byte.
    /// </summary>
    /// <remarks>
    /// The length including the terminator trips people up constantly — a reader that assumes the length
    /// excludes it will truncate the final character of every string.
    /// </remarks>
    public void WriteString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteUInt32(1);
            WriteByte(0);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteUInt32((uint)(byteCount + 1));
        EnsureCapacity(byteCount + 1);
        Encoding.UTF8.GetBytes(value, _buffer[_position..]);
        _position += byteCount;
        _buffer[_position++] = 0;
    }

    /// <summary>
    /// Advances the cursor to the next multiple of <paramref name="alignment"/>, zero-filling the padding.
    /// </summary>
    /// <remarks>
    /// Padding bytes are explicitly zeroed rather than left as whatever the caller's buffer held. Leaving
    /// them uninitialised would make otherwise identical messages serialise to different bytes, breaking
    /// checksums and any byte-level comparison in tests.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Align(int alignment)
    {
        int offset = _position - CdrConstants.EncapsulationHeaderSize;
        int padding = (alignment - (offset % alignment)) % alignment;

        if (padding == 0)
        {
            return;
        }

        EnsureCapacity(padding);
        _buffer.Slice(_position, padding).Clear();
        _position += padding;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void EnsureCapacity(int required)
    {
        if (_position + required > _buffer.Length)
        {
            throw new CdrFormatException(
                $"CDR buffer overflow: need {required} more bytes at offset {_position} but capacity is {_buffer.Length}.");
        }
    }
}

/// <summary>
/// Constants shared by the CDR reader and writer.
/// </summary>
public static class CdrConstants
{
    /// <summary>Size in bytes of the CDR encapsulation header that precedes every payload.</summary>
    public const int EncapsulationHeaderSize = 4;

    /// <summary>
    /// The little-endian plain-CDR encapsulation header: scheme <c>0x0001</c> then two options bytes.
    /// </summary>
    public static ReadOnlySpan<byte> LittleEndianHeader => [0x00, 0x01, 0x00, 0x00];

    /// <summary>Encapsulation identifier for big-endian plain CDR.</summary>
    public const ushort PlainCdrBigEndian = 0x0000;

    /// <summary>Encapsulation identifier for little-endian plain CDR.</summary>
    public const ushort PlainCdrLittleEndian = 0x0001;

    /// <summary>
    /// Reads the encapsulation scheme from a payload.
    /// </summary>
    /// <exception cref="CdrFormatException">The payload is too short to contain a header.</exception>
    public static ushort ReadEncapsulationScheme(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < EncapsulationHeaderSize)
        {
            throw new CdrFormatException($"Payload of {payload.Length} bytes is shorter than the CDR header.");
        }

        return BinaryPrimitives.ReadUInt16BigEndian(payload);
    }
}
