using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using DepthAI.Backends;
using DepthAI.Inference;

namespace DepthAI.Interop;

/// <summary>
/// Mengurai payload paket depthai-c yang bukan piksel. Format biner-nya didefinisikan
/// di <c>native/depthai-c/include/depthai_c.h</c>; keduanya harus diubah bersamaan.
/// </summary>
internal static class NativePayload
{
    /// <summary>
    /// <c>int32 count</c>, lalu per tensor: <c>int32 nameLen, name, int32 rank,
    /// int32 dims[rank], float32 data[prod(dims)]</c>, semuanya little-endian.
    /// </summary>
    public static IReadOnlyDictionary<string, Tensor> ParseTensors(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var count = ReadInt32(payload, ref offset);
        if (count < 0)
        {
            throw new InvalidDataException($"Payload tensor native rusak: jumlah tensor {count}.");
        }

        // Urutan dipertahankan: parser YOLO memakai tensor pertama untuk model satu keluaran.
        var tensors = new Dictionary<string, Tensor>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var nameLength = ReadInt32(payload, ref offset);
            var name = Encoding.UTF8.GetString(Take(payload, ref offset, nameLength));

            var rank = ReadInt32(payload, ref offset);
            if (rank is < 0 or > 8)
            {
                throw new InvalidDataException($"Tensor '{name}' punya rank {rank} yang tidak masuk akal.");
            }

            var shape = new int[rank];
            long elements = 1;
            for (var d = 0; d < rank; d++)
            {
                shape[d] = ReadInt32(payload, ref offset);
                elements *= shape[d];
            }

            var bytes = Take(payload, ref offset, checked((int)(elements * sizeof(float))));
            var data = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            tensors[name] = new Tensor(name, data, shape);
        }

        return tensors;
    }

    /// <summary>
    /// <c>float32[11]</c>: accel xyz, gyro xyz, rotasi i j k real, penanda rotasi.
    /// </summary>
    public static ImuReading ParseImu(ReadOnlySpan<byte> payload, TimeSpan timestamp)
    {
        if (payload.Length < 11 * sizeof(float))
        {
            throw new InvalidDataException($"Payload IMU native hanya {payload.Length} byte, butuh 44.");
        }

        var v = MemoryMarshal.Cast<byte, float>(payload[..(11 * sizeof(float))]);
        return new ImuReading
        {
            Accelerometer = (v[0], v[1], v[2]),
            Gyroscope = (v[3], v[4], v[5]),
            Rotation = v[10] != 0 ? (v[6], v[7], v[8], v[9]) : null,
            Timestamp = timestamp,
        };
    }

    private static int ReadInt32(ReadOnlySpan<byte> payload, ref int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(Take(payload, ref offset, sizeof(int)));

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> payload, ref int offset, int length)
    {
        if (length < 0 || offset + length > payload.Length)
        {
            throw new InvalidDataException("Payload native terpotong: panjang field melebihi data.");
        }

        var slice = payload.Slice(offset, length);
        offset += length;
        return slice;
    }
}
