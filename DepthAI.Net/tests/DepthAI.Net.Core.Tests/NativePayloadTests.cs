using System.Buffers.Binary;
using System.Text;
using DepthAI.Interop;

namespace DepthAI.Tests;

/// <summary>
/// Format payload yang ditulis shim depthai-c. Test ini menyusun byte persis seperti
/// <c>serializeTensors</c> dan paket IMU di <c>native/depthai-c/src/depthai_c.cpp</c>,
/// sehingga perubahan format di salah satu sisi langsung ketahuan tanpa hardware.
/// </summary>
public class NativePayloadTests
{
    private static byte[] Tensors(params (string Name, int[] Shape, float[] Data)[] tensors)
    {
        var bytes = new List<byte>();
        void Int(int v)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, v);
            bytes.AddRange(buffer);
        }

        Int(tensors.Length);
        foreach (var (name, shape, data) in tensors)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            Int(nameBytes.Length);
            bytes.AddRange(nameBytes);
            Int(shape.Length);
            foreach (var d in shape)
            {
                Int(d);
            }

            foreach (var f in data)
            {
                var buffer = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(buffer, f);
                bytes.AddRange(buffer);
            }
        }

        return [.. bytes];
    }

    [Fact]
    public void ParseTensors_ReadsNamesShapesAndValuesInOrder()
    {
        var payload = Tensors(
            ("output1_yolov6r2", [1, 2, 1, 2], [1f, 2f, 3f, 4f]),
            ("scores", [3], [0.1f, 0.2f, 0.7f]));

        var tensors = NativePayload.ParseTensors(payload);

        Assert.Equal(["output1_yolov6r2", "scores"], tensors.Keys);
        Assert.Equal([1, 2, 1, 2], tensors["output1_yolov6r2"].Shape);
        Assert.Equal([1f, 2f, 3f, 4f], tensors["output1_yolov6r2"].Span.ToArray());
        Assert.Equal(0.7f, tensors["scores"].Span[2]);
    }

    [Fact]
    public void ParseTensors_RejectsTruncatedPayload()
    {
        var payload = Tensors(("out", [4], [1f, 2f, 3f, 4f]));

        Assert.Throws<InvalidDataException>(() => NativePayload.ParseTensors(payload.AsSpan(0, payload.Length - 4)));
    }

    [Fact]
    public void ParseImu_MapsAccelGyroAndOptionalRotation()
    {
        var withRotation = new float[] { 0.1f, 9.8f, 0.2f, 0.01f, 0.02f, 0.03f, 0f, 0f, 0.7071f, 0.7071f, 1f };
        var withoutRotation = (float[])withRotation.Clone();
        withoutRotation[10] = 0f;

        var reading = NativePayload.ParseImu(ToBytes(withRotation), TimeSpan.FromSeconds(2));
        var plain = NativePayload.ParseImu(ToBytes(withoutRotation), TimeSpan.Zero);

        Assert.Equal((0.1f, 9.8f, 0.2f), reading.Accelerometer);
        Assert.Equal((0.01f, 0.02f, 0.03f), reading.Gyroscope);
        Assert.Equal((0f, 0f, 0.7071f, 0.7071f), reading.Rotation);
        Assert.Equal(TimeSpan.FromSeconds(2), reading.Timestamp);
        Assert.Null(plain.Rotation);

        static byte[] ToBytes(float[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
            }

            return bytes;
        }
    }
}

public class NativeLibraryResolverTests
{
    [Fact]
    public void CurrentRid_MatchesTheRuntimesFolderLayout()
    {
        var rid = DepthAI.Interop.NativeLibraryResolver.CurrentRid;

        // Folder runtimes/<rid>/native memakai RID portabel: os-arch, huruf kecil.
        Assert.Matches("^(win|linux|osx)-(x64|x86|arm64|arm)$", rid);
        Assert.EndsWith(
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            rid,
            StringComparison.Ordinal);
    }
}
