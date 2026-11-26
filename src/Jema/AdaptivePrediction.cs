using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Jema;

internal sealed class AdaptivePrediction(PcmLayout layout)
{
    public const int SectionFrames = 2048;
    private const int MaxOrder = 32;
    private const int StandardOrder = 16;
    private const int CoefficientScale = 4096;
    private const int GainScale = 256;
    private const int GainLimit = 2 * GainScale;
    private static ReadOnlySpan<byte> TrialOrders => [2, 4, 8, 12, 16, 24, 32];

    private int[]? trial;

    public static int TableCapacity(PcmLayout layout, int frames) =>
        1 + layout.Channels * (2 + ((frames + SectionFrames - 1) / SectionFrames) * (1 + MaxOrder * 2));

    public int Predict(ReadOnlySpan<uint> integers, Span<uint> output, int frames, bool sectional, bool deep, Span<byte> table)
    {
        var samples = MemoryMarshal.Cast<uint, int>(integers);
        var residuals = MemoryMarshal.Cast<uint, int>(output);
        int extent = sectional ? SectionFrames : frames;
        int maxOrder = deep ? MaxOrder : StandardOrder;
        int required = Math.Min(extent, frames);
        if (trial is null || trial.Length < required)
            trial = new int[required];

        int length = 0;
        table[length++] = sectional ? (byte)1 : (byte)0;
        Span<short> coefficients = stackalloc short[MaxOrder];
        int first = 0;
        foreach (int track in layout.ChannelsPerTrack)
        {
            var parent = residuals.Slice(first * frames, frames);
            for (int c = first; c < first + track; c++)
            {
                int gainOffset = length;
                length += sizeof(short);
                var input = samples.Slice(c * frames, frames);
                var lane = residuals.Slice(c * frames, frames);
                for (int start = 0; start < frames; start += extent)
                {
                    int count = Math.Min(extent, frames - start);
                    byte order = Fit(input, start, lane.Slice(start, count), coefficients, trial.AsSpan(0, count), maxOrder);
                    table[length++] = order;
                    for (int i = 0; i < order; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(table[length..], coefficients[i]);
                        length += sizeof(short);
                    }
                }
                short gain = c == first ? (short)0 : Couple(parent, lane);
                BinaryPrimitives.WriteInt16LittleEndian(table[gainOffset..], gain);
            }
            first += track;
        }

        for (int i = 0; i < output.Length; i++)
        {
            int value = residuals[i];
            output[i] = (uint)((value << 1) ^ (value >> 31));
        }

        return length;
    }

    public void Restore(Span<uint> words, int frames, ReadOnlySpan<byte> table)
    {
        if (table.IsEmpty || table[0] > 1)
            throw new InvalidDataException("Invalid linear prediction section size.");
        int extent = table[0] == 0 ? frames : SectionFrames;
        int sections = (frames + extent - 1) / extent;
        for (int i = 0; i < words.Length; i++)
        {
            uint value = words[i];
            words[i] = (value >> 1) ^ (0u - (value & 1));
        }
        Span<int> laneOffsets = stackalloc int[layout.Channels];
        // Restore dependencies while the first lane still contains its temporal residuals.
        var residuals = MemoryMarshal.Cast<uint, int>(words);
        int offset = 1;
        int first = 0;
        foreach (int track in layout.ChannelsPerTrack)
        {
            var parent = residuals.Slice(first * frames, frames);
            for (int c = first; c < first + track; c++)
            {
                if (table.Length - offset < sizeof(short))
                    throw new InvalidDataException("Truncated predictor gain.");
                int gain = BinaryPrimitives.ReadInt16LittleEndian(table[offset..]);
                if (gain is < -GainLimit or > GainLimit || (c == first && gain != 0))
                    throw new InvalidDataException("Invalid within-track predictor gain.");
                offset += sizeof(short);
                laneOffsets[c] = offset;
                for (int section = 0; section < sections; section++)
                {
                    if (offset >= table.Length)
                        throw new InvalidDataException("Truncated linear prediction order.");
                    byte order = table[offset++];
                    if (order != 0 && !TrialOrders.Contains(order))
                        throw new InvalidDataException("Unsupported linear prediction order.");
                    offset += order * sizeof(short);
                    if (offset > table.Length)
                        throw new InvalidDataException("Truncated linear predictor coefficients.");
                }
                if (gain == 0)
                    continue;
                var child = residuals.Slice(c * frames, frames);
                for (int i = 0; i < frames; i++)
                    child[i] += (int)(((long)gain * parent[i]) >> 8);
            }
            first += track;
        }
        if (offset != table.Length)
            throw new InvalidDataException("Unexpected bytes in the linear predictor table.");

        Span<short> coefficients = stackalloc short[MaxOrder];
        for (int c = 0; c < layout.Channels; c++)
        {
            offset = laneOffsets[c];
            var lane = residuals.Slice(c * frames, frames);
            for (int start = 0; start < frames; start += extent)
            {
                int order = table[offset++];
                int end = start + Math.Min(extent, frames - start);
                if (order == 0)
                {
                    SecondDifferenceHistory(lane, start, out int previous, out int delta);
                    for (int i = start; i < end; i++)
                    {
                        delta += lane[i];
                        previous += delta;
                        lane[i] = previous;
                    }
                }
                else
                {
                    for (int i = 0; i < order; i++)
                    {
                        coefficients[i] = BinaryPrimitives.ReadInt16LittleEndian(table[offset..]);
                        offset += sizeof(short);
                    }
                    var weights = coefficients[..order];
                    for (int i = start; i < end; i++)
                        lane[i] += Estimate(lane, i, weights);
                }
            }
        }
    }

    private static int Estimate(ReadOnlySpan<int> data, int i, ReadOnlySpan<short> weights)
    {
        long prediction = 0;
        int count = Math.Min(i, weights.Length);
        for (int lag = 0; lag < count; lag++)
            prediction += (long)weights[lag] * data[i - 1 - lag];
        return (int)(prediction >> 12);
    }

    private static void SecondDifferenceHistory(ReadOnlySpan<int> samples, int start, out int previous, out int delta)
    {
        previous = start > 0 ? samples[start - 1] : 0;
        delta = previous - (start > 1 ? samples[start - 2] : 0);
    }

    private static byte Fit(ReadOnlySpan<int> samples, int start, Span<int> output, Span<short> selected, Span<int> trial,
        int maxOrder)
    {
        SecondDifferenceHistory(samples, start, out int previous, out int previousDelta);
        int end = start + output.Length;
        long best = 0;
        for (int i = start; i < end; i++)
        {
            int delta = samples[i] - previous;
            int residual = delta - previousDelta;
            output[i - start] = residual;
            best += EstimatedBits(residual);
            previous = samples[i];
            previousDelta = delta;
        }

        int limit = Math.Min(maxOrder, (output.Length - 1) / 6);
        if (limit < 2)
            return 0;

        // A small diagonal penalty controls nearly singular fits in silence and steady tones.
        Span<double> lower = stackalloc double[limit * limit];
        Span<double> rhs = stackalloc double[limit];
        lower.Clear();
        rhs.Clear();
        for (int t = Math.Max(start, limit); t < end; t += 4)
        {
            for (int i = 0; i < limit; i++)
            {
                double history = samples[t - 1 - i];
                rhs[i] += history * samples[t];
                for (int j = 0; j <= i; j++)
                    lower[i * limit + j] += history * samples[t - 1 - j];
            }
        }
        double penalty = Math.Max(1.0, lower[0] * 1e-8);
        for (int i = 0; i < limit; i++)
        {
            int iRow = i * limit;
            for (int j = 0; j <= i; j++)
            {
                int jRow = j * limit;
                double value = lower[iRow + j];
                for (int k = 0; k < j; k++)
                    value -= lower[iRow + k] * lower[jRow + k];
                lower[iRow + j] = i == j ? Math.Sqrt(Math.Max(1.0, value + penalty))
                    : value / lower[jRow + j];
            }
            double next = rhs[i];
            for (int j = 0; j < i; j++)
                next -= lower[iRow + j] * rhs[j];
            rhs[i] = next / lower[iRow + i];
        }

        Span<double> solution = stackalloc double[limit];
        Span<short> quantized = stackalloc short[limit];
        byte chosen = 0;
        foreach (byte order in TrialOrders)
        {
            if (order > limit)
                break;
            bool valid = true;
            for (int j = order - 1; j >= 0; j--)
            {
                double value = rhs[j];
                for (int k = j + 1; k < order; k++)
                    value -= lower[k * limit + j] * solution[k];
                solution[j] = value / lower[j * limit + j];
                double rounded = Math.Round(solution[j] * CoefficientScale);
                if (rounded is < short.MinValue or > short.MaxValue)
                {
                    valid = false;
                    break;
                }
                quantized[j] = (short)rounded;
            }
            if (!valid)
                continue;

            var weights = quantized[..order];
            long bits = order * sizeof(short) * 8 * 256;
            for (int i = start; i < end; i++)
            {
                int residual = samples[i] - Estimate(samples, i, weights);
                trial[i - start] = residual;
                bits += EstimatedBits(residual);
            }
            if (bits < best)
            {
                best = bits;
                chosen = order;
                weights.CopyTo(selected);
                trial.CopyTo(output);
            }
        }
        return chosen;
    }

    private static short Couple(ReadOnlySpan<int> parent, Span<int> child)
    {
        double square = 0, mixed = 0;
        long best = 0;
        for (int i = 0; i < parent.Length; i++)
        {
            double x = parent[i];
            square += x * x;
            mixed += x * child[i];
            best += EstimatedBits(child[i]);
        }
        int learned = (int)Math.Clamp(Math.Round(GainScale * mixed / Math.Max(1.0, square)), -GainLimit, GainLimit);
        Span<int> choices = stackalloc int[] { learned, GainScale, -GainScale };
        int gain = 0;
        foreach (int choice in choices)
        {
            if (choice == 0)
                continue;
            long cost = 16 * 256;
            for (int i = 0; i < parent.Length; i++)
                cost += EstimatedBits(child[i] - (int)(((long)choice * parent[i]) >> 8));
            if (cost < best)
            {
                best = cost;
                gain = choice;
            }
        }
        if (gain != 0)
        {
            for (int i = 0; i < parent.Length; i++)
                child[i] -= (int)(((long)gain * parent[i]) >> 8);
        }
        return (short)gain;
    }

    private static int EstimatedBits(int value)
    {
        uint magnitude = (uint)Math.Abs((long)value) + 2;
        int high = BitOperations.Log2(magnitude);
        int fraction = high < 8 ? (int)(magnitude << (8 - high)) : (int)(magnitude >> (high - 8));
        return (high - 1) * 256 + fraction;
    }
}
