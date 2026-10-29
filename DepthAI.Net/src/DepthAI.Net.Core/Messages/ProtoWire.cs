using System.Buffers.Binary;
using System.Text;

namespace DepthAI.Messages;

/// <summary>Tipe wire protobuf yang dipakai skema depthai.</summary>
internal enum WireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    Fixed32 = 5,
}

/// <summary>
/// Penulis protobuf minimal untuk skema pesan depthai.
/// </summary>
/// <remarks>
/// Ditulis tangan alih-alih memakai Google.Protobuf karena Core sengaja bebas dependensi
/// dan skemanya kecil serta stabil. Aturan proto3 diikuti: nilai bawaan (0, string kosong)
/// tidak ditulis, sehingga keluarannya bisa dibaca protobuf mana pun.
/// </remarks>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _buffer = new();

    public void WriteVarint(int field, ulong value)
    {
        if (value == 0)
        {
            return;
        }

        WriteTag(field, WireType.Varint);
        WriteRawVarint(value);
    }

    /// <summary>Bilangan bertanda proto3 (<c>int32</c>/<c>int64</c>) ditulis sebagai varint 64-bit.</summary>
    public void WriteInt64(int field, long value) => WriteVarint(field, unchecked((ulong)value));

    public void WriteBool(int field, bool value) => WriteVarint(field, value ? 1UL : 0UL);

    public void WriteFloat(int field, float value)
    {
        if (BitConverter.SingleToInt32Bits(value) == 0)
        {
            return;
        }

        WriteTag(field, WireType.Fixed32);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        _buffer.Write(bytes);
    }

    public void WriteString(int field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        WriteBytes(field, Encoding.UTF8.GetBytes(value));
    }

    public void WriteBytes(int field, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        WriteLengthDelimited(field, value);
    }

    /// <summary>
    /// Menulis sub-pesan. Sub-pesan kosong tetap ditulis, karena di proto3 kehadiran field
    /// pesan bermakna (misalnya <c>has_ts()</c> di depthai).
    /// </summary>
    public void WriteMessage(int field, Action<ProtoWriter> write)
    {
        var nested = new ProtoWriter();
        write(nested);
        WriteLengthDelimited(field, nested.ToSpan());
    }

    public ReadOnlySpan<byte> ToSpan() => _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);

    public byte[] ToArray() => _buffer.ToArray();

    private void WriteLengthDelimited(int field, ReadOnlySpan<byte> value)
    {
        WriteTag(field, WireType.LengthDelimited);
        WriteRawVarint((ulong)value.Length);
        _buffer.Write(value);
    }

    private void WriteTag(int field, WireType type) => WriteRawVarint(((ulong)field << 3) | (ulong)type);

    private void WriteRawVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _buffer.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        _buffer.WriteByte((byte)value);
    }
}

/// <summary>Pembaca protobuf minimal; field yang tidak dikenal dilewati.</summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public bool TryReadTag(out int field, out WireType type)
    {
        if (_position >= _data.Length)
        {
            field = 0;
            type = default;
            return false;
        }

        var tag = ReadVarint();
        field = (int)(tag >> 3);
        type = (WireType)(tag & 0x7);

        if (field == 0)
        {
            throw new InvalidDataException("Pesan protobuf rusak: nomor field 0.");
        }

        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_position >= _data.Length)
            {
                throw new InvalidDataException("Pesan protobuf terpotong di tengah varint.");
            }

            var b = _data[_position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("Varint protobuf lebih panjang dari 64 bit.");
    }

    public long ReadInt64() => unchecked((long)ReadVarint());

    public float ReadFloat() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public ReadOnlySpan<byte> ReadBytes() => Take(checked((int)ReadVarint()));

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    public void Skip(WireType type)
    {
        switch (type)
        {
            case WireType.Varint:
                ReadVarint();
                break;
            case WireType.Fixed64:
                Take(8);
                break;
            case WireType.LengthDelimited:
                ReadBytes();
                break;
            case WireType.Fixed32:
                Take(4);
                break;
            default:
                throw new InvalidDataException($"Tipe wire protobuf {(int)type} tidak didukung.");
        }
    }

    private ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || _position + length > _data.Length)
        {
            throw new InvalidDataException("Pesan protobuf terpotong: panjang field melebihi data.");
        }

        var slice = _data.Slice(_position, length);
        _position += length;
        return slice;
    }
}
