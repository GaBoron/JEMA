using System.Buffers.Binary;

namespace Jema;

internal enum Predictor : byte
{
    Word,
    Xor,
    Magnitude1,
    Magnitude2,
    ChannelSign,
    TrackSign
}

internal enum Shuffle : byte
{
    Byte,
    Bit
}

internal sealed class SampleTransform(PcmLayout layout, int blockBytes)
{
    private const uint Sign = 0x80000000;
    private const uint Magnitude = Sign - 1;
    private readonly uint[] words = new uint[blockBytes / sizeof(uint)];
    private readonly int[] channelParents = MakeParents(layout, false);
    private readonly int[] trackParents = MakeParents(layout, true);

    public Span<uint> PredictedWords(int samples) => words.AsSpan(0, samples);

    private static int[] MakeParents(PcmLayout layout, bool acrossTracks)
    {
        var parents = new int[layout.Channels];
        int start = 0;
        int previousTrack = -1;
        foreach (int channels in layout.ChannelsPerTrack)
        {
            for (int i = 0; i < channels; i++)
            {
                parents[start + i] = i == 0
                    ? (acrossTracks ? previousTrack : -1)
                    : (acrossTracks && previousTrack >= 0 && previousTrack + i < start
                        ? previousTrack + i
                        : start);
            }
            previousTrack = start;
            start += channels;
        }
        return parents;
    }

    public void Predict(ReadOnlySpan<byte> pcm, Predictor mode)
    {
        int frames = pcm.Length / layout.BytesPerFrame;
        var parents = mode == Predictor.TrackSign ? trackParents : channelParents;
        for (int channel = 0; channel < layout.Channels; channel++)
        {
            int parent = mode is Predictor.ChannelSign or Predictor.TrackSign ? parents[channel] : -1;
            uint previousWord = 0;
            uint previousSign = 0;
            uint previousDelta = 0;
            var lane = words.AsSpan(channel * frames, frames);
            for (int frame = 0; frame < frames; frame++)
            {
                int start = frame * layout.BytesPerFrame;
                uint current = BinaryPrimitives.ReadUInt32LittleEndian(pcm[(start + channel * 4)..]);
                uint sign = current & Sign;
                if (parent >= 0)
                    sign ^= BinaryPrimitives.ReadUInt32LittleEndian(pcm[(start + parent * 4)..]) & Sign;
                lane[frame] = mode switch
                {
                    Predictor.Word => current,
                    Predictor.Xor => current ^ previousWord,
                    _ => PredictMagnitude(current, previousWord, sign ^ previousSign, mode, ref previousDelta)
                };
                previousWord = current;
                previousSign = sign;
            }
        }
    }

    private static uint PredictMagnitude(uint word, uint previousWord, uint sign, Predictor mode, ref uint previousDelta)
    {
        uint delta = (word & Magnitude) - (previousWord & Magnitude);
        uint residual = (mode == Predictor.Magnitude2 ? delta - previousDelta : delta) & Magnitude;
        previousDelta = delta;
        // Zigzag in the 31-bit ring, with bit 30 as the sign of the centered residual.
        return sign | (((residual << 1) ^ (0u - (residual >> 30))) & Magnitude);
    }

    public void Restore(Span<byte> pcm, Predictor mode)
    {
        int frames = pcm.Length / layout.BytesPerFrame;
        var parents = mode == Predictor.TrackSign ? trackParents : channelParents;
        for (int channel = 0; channel < layout.Channels; channel++)
        {
            int parent = mode is Predictor.ChannelSign or Predictor.TrackSign ? parents[channel] : -1;
            uint previousWord = 0;
            uint previousSign = 0;
            uint previousDelta = 0;
            var lane = words.AsSpan(channel * frames, frames);
            for (int frame = 0; frame < frames; frame++)
            {
                int start = frame * layout.BytesPerFrame;
                uint residual = lane[frame];
                uint current;
                if (mode is Predictor.Word or Predictor.Xor)
                {
                    current = mode == Predictor.Word ? residual : residual ^ previousWord;
                }
                else
                {
                    uint signedDelta = ((residual & Magnitude) >> 1) ^ (0u - (residual & 1));
                    uint delta = mode == Predictor.Magnitude2 ? signedDelta + previousDelta : signedDelta;
                    uint magnitude = (previousWord + delta) & Magnitude;
                    uint sign = (residual & Sign) ^ previousSign;
                    previousSign = sign;
                    if (parent >= 0)
                        sign ^= BinaryPrimitives.ReadUInt32LittleEndian(pcm[(start + parent * 4)..]) & Sign;
                    current = sign | magnitude;
                    previousDelta = delta;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(pcm[(start + channel * 4)..], current);
                previousWord = current;
            }
        }
    }

    public void ShuffleTo(Span<byte> result, Shuffle shuffle)
    {
        int frames = result.Length / layout.BytesPerFrame;
        if (shuffle == Shuffle.Byte)
        {
            int count = frames * layout.Channels;
            for (int i = 0; i < count; i++)
            {
                uint word = words[i];
                result[i] = (byte)word;
                result[count + i] = (byte)(word >> 8);
                result[2 * count + i] = (byte)(word >> 16);
                result[3 * count + i] = (byte)(word >> 24);
            }
            return;
        }

        int groups = frames / 8;
        int plane = groups * layout.Channels;
        for (int lane = 0; lane < layout.Channels; lane++)
        {
            for (int group = 0; group < groups; group++)
            {
                int wordIndex = lane * frames + group * 8;
                int planeIndex = lane * groups + group;
                ulong a = 0, b = 0, c = 0, d = 0;
                for (int i = 0; i < 8; i++)
                {
                    uint word = words[wordIndex + i];
                    a |= (ulong)(byte)word << (i * 8);
                    b |= (ulong)(byte)(word >> 8) << (i * 8);
                    c |= (ulong)(byte)(word >> 16) << (i * 8);
                    d |= (ulong)(word >> 24) << (i * 8);
                }
                WritePlanes(result, plane, planeIndex, Transpose(a));
                WritePlanes(result, plane, planeIndex + 8 * plane, Transpose(b));
                WritePlanes(result, plane, planeIndex + 16 * plane, Transpose(c));
                WritePlanes(result, plane, planeIndex + 24 * plane, Transpose(d));
            }
            int tail = frames % 8;
            for (int i = 0; i < tail; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(result[(32 * plane + (lane * tail + i) * 4)..],
                    words[lane * frames + groups * 8 + i]);
        }
    }

    public void Unshuffle(ReadOnlySpan<byte> input, Shuffle shuffle)
    {
        int frames = input.Length / layout.BytesPerFrame;
        if (shuffle == Shuffle.Byte)
        {
            int count = frames * layout.Channels;
            for (int i = 0; i < count; i++)
                words[i] = (uint)(input[i] | input[count + i] << 8
                    | input[2 * count + i] << 16 | input[3 * count + i] << 24);
            return;
        }

        int groups = frames / 8;
        int plane = groups * layout.Channels;
        for (int lane = 0; lane < layout.Channels; lane++)
        {
            for (int group = 0; group < groups; group++)
            {
                int wordIndex = lane * frames + group * 8;
                int planeIndex = lane * groups + group;
                ulong a = Transpose(ReadPlanes(input, plane, planeIndex));
                ulong b = Transpose(ReadPlanes(input, plane, planeIndex + 8 * plane));
                ulong c = Transpose(ReadPlanes(input, plane, planeIndex + 16 * plane));
                ulong d = Transpose(ReadPlanes(input, plane, planeIndex + 24 * plane));
                for (int i = 0; i < 8; i++)
                    words[wordIndex + i] = (uint)((byte)(a >> (i * 8)) | (byte)(b >> (i * 8)) << 8
                        | (byte)(c >> (i * 8)) << 16 | (byte)(d >> (i * 8)) << 24);
            }
            int tail = frames % 8;
            for (int i = 0; i < tail; i++)
                words[lane * frames + groups * 8 + i] =
                    BinaryPrimitives.ReadUInt32LittleEndian(input[(32 * plane + (lane * tail + i) * 4)..]);
        }
    }

    private static void WritePlanes(Span<byte> result, int stride, int start, ulong value)
    {
        for (int i = 0; i < 8; i++)
            result[start + i * stride] = (byte)(value >> (8 * i));
    }

    private static ulong ReadPlanes(ReadOnlySpan<byte> source, int stride, int start)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
            value |= (ulong)source[start + i * stride] << (8 * i);
        return value;
    }

    private static ulong Transpose(ulong x)
    {
        ulong exchange = (x ^ (x >> 7)) & 0x00aa00aa00aa00aaUL;
        x ^= exchange ^ (exchange << 7);
        exchange = (x ^ (x >> 14)) & 0x0000cccc0000ccccUL;
        x ^= exchange ^ (exchange << 14);
        exchange = (x ^ (x >> 28)) & 0x00000000f0f0f0f0UL;
        return x ^ exchange ^ (exchange << 28);
    }
}
