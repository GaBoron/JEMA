using System.Buffers.Binary;
using System.Numerics;

namespace Jema;

internal sealed class FixedPointTransform(PcmLayout layout, int blockBytes)
{
    private const int Precision = 19;
    private const uint Sign = 0x80000000;
    private const uint Fraction = 0x7fffff;
    private readonly byte[] side = new byte[blockBytes];
    public byte[] Exponents { get; } = new byte[layout.Channels];
    public Span<byte> Side => side;

    public int Decompose(ReadOnlySpan<byte> pcm, Span<uint> words)
    {
        int frames = pcm.Length / layout.BytesPerFrame;
        int remainderBits = 0;
        int exceptionCount = 0;
        for (int lane = 0; lane < layout.Channels; lane++)
        {
            var integers = words.Slice(lane * frames, frames);
            int highest = 0;
            for (int t = 0, offset = lane * 4; t < frames; t++, offset += layout.BytesPerFrame)
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(pcm[offset..]);
                int exponent = (int)(word >> 23) & 255;
                if (exponent < 255 && exponent > highest)
                    highest = exponent;
            }
            Exponents[lane] = (byte)highest;
            for (int t = 0, offset = lane * 4; t < frames; t++, offset += layout.BytesPerFrame)
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(pcm[offset..]);
                int exponent = (int)(word >> 23) & 255;
                int shift = highest - exponent + (23 - Precision);
                if (exponent is 0 or 255 || shift >= 24)
                {
                    integers[t] = 0;
                    exceptionCount++;
                    continue;
                }
                uint integer = (word & Fraction | 1u << 23) >> shift;
                integers[t] = (word & Sign) == 0 ? integer : 0u - integer;
                remainderBits += shift;
            }
        }

        int remainderBytes = (remainderBits + 7) / 8;
        var writer = new RemainderWriter(side.AsSpan(0, remainderBytes));
        int exception = remainderBytes;
        for (int lane = 0; lane < layout.Channels; lane++)
        {
            var integers = words.Slice(lane * frames, frames);
            for (int t = 0, offset = lane * 4; t < frames; t++, offset += layout.BytesPerFrame)
            {
                uint sample = BinaryPrimitives.ReadUInt32LittleEndian(pcm[offset..]);
                uint integer = integers[t];
                if (integer == 0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(side.AsSpan(exception), sample);
                    exception += 4;
                }
                else
                {
                    int shift = Exponents[lane] - ((int)(sample >> 23) & 255) + (23 - Precision);
                    writer.Write(sample & ((1u << shift) - 1), shift);
                }
            }
        }
        writer.Finish();
        return remainderBytes + 4 * exceptionCount;
    }

    public static void PredictSecondDifference(Span<uint> words, int frames)
    {
        for (int start = 0; start < words.Length; start += frames)
        {
            uint previous = 0;
            uint previousDelta = 0;
            var integers = words.Slice(start, frames);
            for (int t = 0; t < frames; t++)
            {
                uint integer = integers[t];
                uint delta = integer - previous;
                uint residual = delta - previousDelta;
                integers[t] = (residual << 1) ^ (0u - (residual >> 31));
                previous = integer;
                previousDelta = delta;
            }
        }
    }

    public static void RestoreSecondDifference(Span<uint> words, int frames)
    {
        for (int start = 0; start < words.Length; start += frames)
        {
            uint previous = 0;
            uint delta = 0;
            var integers = words.Slice(start, frames);
            for (int t = 0; t < frames; t++)
            {
                uint folded = integers[t];
                delta += (folded >> 1) ^ (0u - (folded & 1));
                previous += delta;
                integers[t] = previous;
            }
        }
    }

    public (int RemainderBits, int SideBytes) MeasureSide(ReadOnlySpan<uint> words, int frames)
    {
        int remainderBits = 0;
        int exceptions = 0;
        for (int lane = 0; lane < layout.Channels; lane++)
        {
            int scale = Exponents[lane] - Precision;
            if (Exponents[lane] == 255)
                throw new InvalidDataException("Invalid fixed-point scale exponent.");
            foreach (uint integer in words.Slice(lane * frames, frames))
            {
                if (integer == 0)
                {
                    exceptions++;
                    continue;
                }

                int highest = BitOperations.Log2(Absolute(integer));
                if (highest > Precision || scale + highest <= 0)
                    throw new InvalidDataException("Fixed-point integer exceeds its scale.");
                remainderBits += 23 - highest;
            }
        }

        return (remainderBits, (remainderBits + 7) / 8 + 4 * exceptions);
    }

    public void Restore(Span<byte> pcm, ReadOnlySpan<uint> words, int remainderBits)
    {
        int remainderBytes = (remainderBits + 7) / 8;
        if ((remainderBits & 7) != 0 && side[remainderBytes - 1] >> (remainderBits & 7) != 0)
            throw new InvalidDataException("Nonzero padding in fixed-point remainder.");

        var reader = new RemainderReader(side.AsSpan(0, remainderBytes));
        int exception = remainderBytes;
        int frames = pcm.Length / layout.BytesPerFrame;
        for (int lane = 0; lane < layout.Channels; lane++)
        {
            var integers = words.Slice(lane * frames, frames);
            int scale = Exponents[lane] - Precision;
            for (int t = 0, offset = lane * 4; t < frames; t++, offset += layout.BytesPerFrame)
            {
                uint integer = integers[t];
                uint word;
                if (integer == 0)
                {
                    word = BinaryPrimitives.ReadUInt32LittleEndian(side.AsSpan(exception));
                    exception += 4;
                }
                else
                {
                    uint absolute = Absolute(integer);
                    int highest = BitOperations.Log2(absolute);
                    int shift = 23 - highest;
                    uint fraction = ((absolute << shift) & Fraction) | reader.Read(shift);
                    word = (integer & Sign) | ((uint)(scale + highest) << 23) | fraction;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(pcm[offset..], word);
            }
        }
    }

    private static uint Absolute(uint integer) => (integer & Sign) == 0 ? integer : 0u - integer;

    private ref struct RemainderWriter(Span<byte> output)
    {
        private readonly Span<byte> destination = output;
        private int position;
        private uint current;
        private int bits;

        public void Write(uint value, int count)
        {
            current |= value << bits;
            bits += count;
            while (bits >= 8)
            {
                destination[position++] = (byte)current;
                current >>= 8;
                bits -= 8;
            }
        }

        public void Finish()
        {
            if (bits > 0)
                destination[position] = (byte)current;
        }
    }

    private ref struct RemainderReader(ReadOnlySpan<byte> input)
    {
        private readonly ReadOnlySpan<byte> source = input;
        private int position;
        private uint current;
        private int bits;

        public uint Read(int count)
        {
            while (bits < count)
            {
                current |= (uint)source[position++] << bits;
                bits += 8;
            }
            uint value = current & ((1u << count) - 1);
            current >>= count;
            bits -= count;
            return value;
        }
    }
}
