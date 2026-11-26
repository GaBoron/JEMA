using System.Buffers.Binary;
using System.IO.Hashing;
using ZstdSharp;

namespace Jema;

internal sealed class BlockCodec(PcmLayout layout, int blockBytes, int? compressionLevel = null) : IDisposable
{
    public const byte Raw = 0;
    public const byte Zstd = 1;
    public const byte Reference = 255;
    private const byte FirstTransform = 2;
    private const byte FirstFixedPoint = 14;
    private const byte FirstAdaptive = 16;
    private readonly SampleTransform transform = new(layout, blockBytes);
    private readonly byte[] shuffled = new byte[blockBytes + AdaptivePrediction.TableCapacity(layout, blockBytes / layout.BytesPerFrame)];
    private readonly byte[] encoded = new byte[blockBytes];
    private readonly Compressor? compressor = compressionLevel.HasValue ? new(compressionLevel.Value) : null;
    private readonly byte[]? candidate = compressionLevel.HasValue
        ? new byte[Compressor.GetCompressBound(blockBytes + AdaptivePrediction.TableCapacity(layout, blockBytes / layout.BytesPerFrame))]
        : null;
    private readonly Decompressor decompressor = new();
    private FixedPointTransform? fixedPoint;
    private byte[]? fixedCandidate;
    private uint[]? integerCore;
    private AdaptivePrediction? prediction;

    public static bool IsStoredMode(byte mode) => mode <= FirstAdaptive + 1;

    public (byte Mode, int Length) Encode(ReadOnlySpan<byte> pcm, Stream destination, EncoderOptions options)
    {
        var scratch = candidate.AsSpan();
        int size = compressor!.Wrap(pcm, scratch);
        byte bestMode = size < pcm.Length ? Zstd : Raw;
        int bestSize = Math.Min(size, pcm.Length);
        if (bestMode == Zstd)
            scratch[..size].CopyTo(encoded);

        Predictor last = options.CrossTrackSignPrediction && layout.ChannelsPerTrack.Length > 1
            ? Predictor.TrackSign
            : Predictor.ChannelSign;
        for (Predictor predictor = Predictor.Word; predictor <= last; predictor++)
        {
            if (predictor == Predictor.ChannelSign && layout.ChannelsPerTrack.Length == layout.Channels)
                continue;

            transform.Predict(pcm, predictor);
            for (Shuffle shuffle = Shuffle.Byte; shuffle <= Shuffle.Bit; shuffle++)
            {
                var data = shuffled.AsSpan(0, pcm.Length);
                transform.ShuffleTo(data, shuffle);
                size = compressor.Wrap(data, scratch);
                if (size < bestSize)
                {
                    bestSize = size;
                    bestMode = (byte)(FirstTransform + 2 * (int)predictor + (int)shuffle);
                    scratch[..size].CopyTo(encoded);
                }
            }
        }

        if (options.FixedPointPrediction)
        {
            (byte mode, int length) = EncodeFixed(pcm, destination, bestSize, options);
            if (length > 0)
                return (mode, length);
        }
        destination.Write(bestMode == Raw ? pcm : encoded.AsSpan(0, bestSize));
        return (bestMode, bestSize);
    }

    private (byte Mode, int Length) EncodeFixed(ReadOnlySpan<byte> pcm, Stream destination, int limit, EncoderOptions options)
    {
        fixedPoint ??= new FixedPointTransform(layout, blockBytes);
        fixedCandidate ??= new byte[candidate!.Length];
        var words = transform.PredictedWords(pcm.Length / 4);
        int frames = pcm.Length / layout.BytesPerFrame;
        int sideBytes = fixedPoint.Decompose(pcm, words);
        if (options.AdaptivePrediction)
        {
            integerCore ??= new uint[blockBytes / 4];
            words.CopyTo(integerCore);
            prediction ??= new AdaptivePrediction(layout);
        }

        FixedPointTransform.PredictSecondDifference(words, frames);
        byte mode = FirstFixedPoint;
        int coreBytes = int.MaxValue;
        TryCore(pcm.Length, FirstFixedPoint, 0, ref mode, ref coreBytes);
        if (options.AdaptivePrediction)
            TryAdaptiveCores(words, frames, options.DeepPrediction, ref mode, ref coreBytes);

        int prefixBytes = sizeof(int) + layout.Channels;
        if (prefixBytes + coreBytes >= limit)
            return default;
        var side = fixedPoint.Side[..sideBytes];
        int compressed = compressor!.Wrap(side, candidate);
        int size = prefixBytes + coreBytes + Math.Min(compressed, sideBytes);
        if (size >= limit)
            return default;

        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, coreBytes);
        destination.Write(length);
        destination.Write(fixedPoint.Exponents);
        destination.Write(fixedCandidate.AsSpan(0, coreBytes));
        destination.Write(compressed < sideBytes ? candidate.AsSpan(0, compressed) : side);
        return (mode, size);
    }

    private void TryAdaptiveCores(Span<uint> words, int frames, bool deep, ref byte mode, ref int coreBytes)
    {
        var original = integerCore.AsSpan(0, words.Length);
        int pcmBytes = words.Length * sizeof(uint);
        int tableBytes = prediction!.Predict(original, words, frames, sectional: false, deep: false, shuffled);
        TryCore(pcmBytes, FirstAdaptive, tableBytes, ref mode, ref coreBytes);
        bool sectional = frames > AdaptivePrediction.SectionFrames;
        if (sectional)
        {
            tableBytes = prediction.Predict(original, words, frames, sectional: true, deep: false, shuffled);
            TryCore(pcmBytes, FirstAdaptive, tableBytes, ref mode, ref coreBytes);
        }
        if (deep)
        {
            tableBytes = prediction.Predict(original, words, frames, sectional, deep: true, shuffled);
            TryCore(pcmBytes, FirstAdaptive, tableBytes, ref mode, ref coreBytes);
        }
    }

    private void TryCore(int pcmBytes, byte baseMode, int tableBytes, ref byte mode, ref int coreBytes)
    {
        var data = shuffled.AsSpan(0, tableBytes + pcmBytes);
        for (int index = (int)Shuffle.Bit; index >= (int)Shuffle.Byte; index--)
        {
            transform.ShuffleTo(data[tableBytes..], (Shuffle)index);
            int length = compressor!.Wrap(data, candidate);
            if (length < coreBytes)
            {
                coreBytes = length;
                mode = (byte)(baseMode + index);
                candidate.AsSpan(0, length).CopyTo(fixedCandidate);
            }
        }
    }

    public void Decode(Stream source, IndexEntry entry, Span<byte> pcm)
    {
        source.Position = entry.Position;
        if (entry.Mode == Raw)
        {
            source.ReadExactly(pcm);
        }
        else
        {
            var compressed = encoded.AsSpan(0, entry.Length);
            source.ReadExactly(compressed);
            if (entry.Mode >= FirstFixedPoint)
            {
                DecodeFixed(compressed, pcm, entry.Mode);
            }
            else
            {
                var data = entry.Mode == Zstd ? pcm : shuffled.AsSpan(0, pcm.Length);
                Inflate(compressed, data);
                if (entry.Mode != Zstd)
                {
                    int recipe = entry.Mode - FirstTransform;
                    transform.Unshuffle(data, (Shuffle)(recipe % 2));
                    transform.Restore(pcm, (Predictor)(recipe / 2));
                }
            }
        }
        if (Crc32.HashToUInt32(pcm) != entry.PcmCrc)
            throw new InvalidDataException("PCM block checksum mismatch.");
    }

    private void DecodeFixed(ReadOnlySpan<byte> compressed, Span<byte> pcm, byte mode)
    {
        fixedPoint ??= new FixedPointTransform(layout, blockBytes);
        int prefixBytes = sizeof(int) + layout.Channels;
        if (compressed.Length <= prefixBytes)
            throw new InvalidDataException("Truncated fixed-point payload prefix.");
        int coreBytes = BinaryPrimitives.ReadInt32LittleEndian(compressed);
        compressed.Slice(sizeof(int), layout.Channels).CopyTo(fixedPoint.Exponents);
        if (coreBytes <= 0 || coreBytes >= compressed.Length - prefixBytes)
            throw new InvalidDataException("Invalid compressed fixed-point core size.");

        int frames = pcm.Length / layout.BytesPerFrame;
        bool adaptive = mode >= FirstAdaptive;
        var compressedCore = compressed.Slice(prefixBytes, coreBytes);
        int capacity = pcm.Length + (adaptive ? AdaptivePrediction.TableCapacity(layout, frames) : 0);
        var core = shuffled.AsSpan(0, capacity);
        int written = Inflate(compressedCore, core, exact: !adaptive);
        int tableBytes = written - pcm.Length;
        if (adaptive && tableBytes <= 0)
            throw new InvalidDataException("Fixed-point core is missing its linear predictor table.");

        transform.Unshuffle(core.Slice(tableBytes, pcm.Length), (Shuffle)(mode & 1));
        var words = transform.PredictedWords(pcm.Length / 4);
        if (adaptive)
        {
            prediction ??= new AdaptivePrediction(layout);
            prediction.Restore(words, frames, core[..tableBytes]);
        }
        else
        {
            FixedPointTransform.RestoreSecondDifference(words, frames);
        }
        var (remainderBits, sideBytes) = fixedPoint.MeasureSide(words, frames);
        var side = fixedPoint.Side[..sideBytes];
        var input = compressed[(prefixBytes + coreBytes)..];
        if (input.Length == side.Length)
            input.CopyTo(side);
        else if (input.Length < side.Length)
            Inflate(input, side);
        else
            throw new InvalidDataException("Invalid fixed-point side stream size.");
        fixedPoint.Restore(pcm, words, remainderBits);
    }

    private int Inflate(ReadOnlySpan<byte> compressed, Span<byte> output, bool exact = true)
    {
        try
        {
            if (!decompressor.TryUnwrap(compressed, output, out int written) || (exact && written != output.Length))
                throw new InvalidDataException("Incorrect block decompressed size.");
            return written;
        }
        catch (ZstdException error)
        {
            throw new InvalidDataException("Invalid Zstandard payload.", error);
        }
    }

    public void Dispose()
    {
        compressor?.Dispose();
        decompressor.Dispose();
    }
}
