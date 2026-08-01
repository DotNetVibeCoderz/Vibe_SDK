using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Unitree.Net.Core;

/// <summary>
/// The word-oriented CRC-32 used by Unitree low-level command and state messages.
/// </summary>
/// <remarks>
/// <para>
/// This is <em>not</em> a standard CRC-32. Unitree's <c>crc32_core</c> walks the message as an array of
/// 32-bit words (not bytes), seeds with <c>0xFFFFFFFF</c>, and applies the polynomial <c>0x04C11DB7</c>
/// twice per bit — once for the shift and once for the data bit. Reproducing it exactly matters: the robot
/// silently drops any <c>rt/lowcmd</c> whose CRC does not match, which presents as "commands do nothing".
/// </para>
/// <para>
/// Because the input is read as native-endian words, the caller must pass the message body exactly as it is
/// laid out in memory, with the CRC field itself excluded (it is the trailing word).
/// </para>
/// </remarks>
public static class UnitreeCrc32
{
    private const uint Polynomial = 0x04C11DB7u;

    /// <summary>
    /// Computes the Unitree CRC-32 over a span of 32-bit words.
    /// </summary>
    /// <param name="words">The message body reinterpreted as native-endian 32-bit words.</param>
    /// <returns>The CRC value to store in the message's trailing <c>crc</c> field.</returns>
    public static uint Compute(ReadOnlySpan<uint> words)
    {
        uint crc = 0xFFFFFFFFu;

        for (int i = 0; i < words.Length; i++)
        {
            uint data = words[i];
            uint xbit = 1u << 31;

            for (int bit = 0; bit < 32; bit++)
            {
                if ((crc & 0x80000000u) != 0)
                {
                    crc <<= 1;
                    crc ^= Polynomial;
                }
                else
                {
                    crc <<= 1;
                }

                if ((data & xbit) != 0)
                {
                    crc ^= Polynomial;
                }

                xbit >>= 1;
            }
        }

        return crc;
    }

    /// <summary>
    /// Computes the Unitree CRC-32 over a byte span whose length is a multiple of four.
    /// </summary>
    /// <param name="bytes">The message body, excluding the trailing CRC field.</param>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not word-aligned in length.</exception>
    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        if ((bytes.Length & 3) != 0)
        {
            throw new ArgumentException(
                $"Unitree CRC operates on 32-bit words; length {bytes.Length} is not a multiple of 4.",
                nameof(bytes));
        }

        return Compute(MemoryMarshal.Cast<byte, uint>(bytes));
    }

    /// <summary>
    /// Computes the CRC over a blittable struct, excluding its trailing <c>crc</c> field.
    /// </summary>
    /// <typeparam name="T">A blittable message struct whose last field is a <see cref="uint"/> CRC.</typeparam>
    /// <param name="message">The message to checksum.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ComputeForMessage<T>(in T message)
        where T : unmanaged
    {
        ReadOnlySpan<T> single = new(in message);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(single);

        // The CRC field is the final word and must not be included in its own checksum.
        return Compute(bytes[..^sizeof(uint)]);
    }
}
