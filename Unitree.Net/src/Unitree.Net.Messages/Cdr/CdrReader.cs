using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Unitree.Net.Core;

namespace Unitree.Net.Messages.Cdr;

/// <summary>
/// Reads DDS Common Data Representation (CDR) payloads from a span.
/// </summary>
/// <remarks>
/// Mirrors <see cref="CdrWriter"/>. Only little-endian plain CDR is accepted, which is what Unitree
/// firmware emits on every platform the SDK targets; a big-endian payload is rejected loudly rather
/// than silently misread.
/// </remarks>
public ref struct CdrReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    /// <summary>
    /// Creates a reader over <paramref name="payload"/>, validating and skipping the encapsulation header.
    /// </summary>
    /// <exception cref="CdrFormatException">The payload is truncated or is not little-endian plain CDR.</exception>
    public CdrReader(ReadOnlySpan<byte> payload)
    {
        ushort scheme = CdrConstants.ReadEncapsulationScheme(payload);

        if (scheme != CdrConstants.PlainCdrLittleEndian)
        {
            throw new CdrFormatException(
                $"Unsupported CDR encapsulation scheme 0x{scheme:X4}; only little-endian plain CDR (0x0001) is supported.");
        }

        _buffer = payload;
        _position = CdrConstants.EncapsulationHeaderSize;
    }

    /// <summary>Number of bytes consumed so far, including the encapsulation header.</summary>
    public readonly int BytesRead => _position;

    /// <summary>Number of bytes left in the payload.</summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>Reads an unsigned byte.</summary>
    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    /// <summary>Reads a signed byte.</summary>
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    /// <summary>Reads a boolean from a single byte.</summary>
    public bool ReadBool() => ReadByte() != 0;

    /// <summary>Reads a 16-bit signed integer, aligned to two bytes.</summary>
    public short ReadInt16()
    {
        Align(2);
        EnsureAvailable(2);
        short value = BinaryPrimitives.ReadInt16LittleEndian(_buffer[_position..]);
        _position += 2;
        return value;
    }

    /// <summary>Reads a 16-bit unsigned integer, aligned to two bytes.</summary>
    public ushort ReadUInt16()
    {
        Align(2);
        EnsureAvailable(2);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_position..]);
        _position += 2;
        return value;
    }

    /// <summary>Reads a 32-bit signed integer, aligned to four bytes.</summary>
    public int ReadInt32()
    {
        Align(4);
        EnsureAvailable(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer[_position..]);
        _position += 4;
        return value;
    }

    /// <summary>Reads a 32-bit unsigned integer, aligned to four bytes.</summary>
    public uint ReadUInt32()
    {
        Align(4);
        EnsureAvailable(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_position..]);
        _position += 4;
        return value;
    }

    /// <summary>Reads a 64-bit signed integer, aligned to eight bytes.</summary>
    public long ReadInt64()
    {
        Align(8);
        EnsureAvailable(8);
        long value = BinaryPrimitives.ReadInt64LittleEndian(_buffer[_position..]);
        _position += 8;
        return value;
    }

    /// <summary>Reads a 64-bit unsigned integer, aligned to eight bytes.</summary>
    public ulong ReadUInt64()
    {
        Align(8);
        EnsureAvailable(8);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer[_position..]);
        _position += 8;
        return value;
    }

    /// <summary>Reads a 32-bit float, aligned to four bytes.</summary>
    public float ReadSingle()
    {
        Align(4);
        EnsureAvailable(4);
        float value = BinaryPrimitives.ReadSingleLittleEndian(_buffer[_position..]);
        _position += 4;
        return value;
    }

    /// <summary>Reads a 64-bit float, aligned to eight bytes.</summary>
    public double ReadDouble()
    {
        Align(8);
        EnsureAvailable(8);
        double value = BinaryPrimitives.ReadDoubleLittleEndian(_buffer[_position..]);
        _position += 8;
        return value;
    }

    /// <summary>Reads a fixed-length byte array into <paramref name="destination"/>.</summary>
    public void ReadByteArray(scoped Span<byte> destination)
    {
        EnsureAvailable(destination.Length);
        _buffer.Slice(_position, destination.Length).CopyTo(destination);
        _position += destination.Length;
    }

    /// <summary>Reads a fixed-length float array into <paramref name="destination"/>.</summary>
    public void ReadSingleArray(scoped Span<float> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = ReadSingle();
        }
    }

    /// <summary>Reads a fixed-length 32-bit unsigned integer array into <paramref name="destination"/>.</summary>
    public void ReadUInt32Array(scoped Span<uint> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = ReadUInt32();
        }
    }

    /// <summary>Reads a fixed-length 16-bit signed integer array into <paramref name="destination"/>.</summary>
    public void ReadInt16Array(scoped Span<short> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = ReadInt16();
        }
    }

    /// <summary>Reads a fixed-length 16-bit unsigned integer array into <paramref name="destination"/>.</summary>
    public void ReadUInt16Array(scoped Span<ushort> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = ReadUInt16();
        }
    }

    /// <summary>Reads a variable-length byte sequence.</summary>
    /// <returns>A slice of the underlying payload; no copy is made.</returns>
    public ReadOnlySpan<byte> ReadByteSequence()
    {
        uint length = ReadUInt32();
        EnsureAvailable((int)length);
        ReadOnlySpan<byte> slice = _buffer.Slice(_position, (int)length);
        _position += (int)length;
        return slice;
    }

    /// <summary>
    /// Reads a CDR string whose length prefix includes the null terminator.
    /// </summary>
    public string ReadString()
    {
        uint lengthWithTerminator = ReadUInt32();

        if (lengthWithTerminator == 0)
        {
            return string.Empty;
        }

        EnsureAvailable((int)lengthWithTerminator);
        int contentLength = (int)lengthWithTerminator - 1;
        string value = Encoding.UTF8.GetString(_buffer.Slice(_position, contentLength));
        _position += (int)lengthWithTerminator;
        return value;
    }

    /// <summary>Advances the cursor to the next multiple of <paramref name="alignment"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Align(int alignment)
    {
        int offset = _position - CdrConstants.EncapsulationHeaderSize;
        int padding = (alignment - (offset % alignment)) % alignment;
        _position += padding;
    }

    /// <summary>Skips <paramref name="byteCount"/> bytes without interpreting them.</summary>
    public void Skip(int byteCount)
    {
        EnsureAvailable(byteCount);
        _position += byteCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void EnsureAvailable(int required)
    {
        if (_position + required > _buffer.Length)
        {
            throw new CdrFormatException(
                $"CDR payload truncated: need {required} bytes at offset {_position} but only {_buffer.Length - _position} remain.");
        }
    }
}
