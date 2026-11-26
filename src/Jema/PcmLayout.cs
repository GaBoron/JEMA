namespace Jema;

/// <summary>Frame-interleaved little-endian binary32, ordered by track and then channel.</summary>
public sealed class PcmLayout
{
    private readonly ushort[] tracks;

    public PcmLayout(int sampleRate, params ushort[] channelsPerTrack)
    {
        ArgumentNullException.ThrowIfNull(channelsPerTrack);
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channelsPerTrack.Length is < 1 or > 256 || channelsPerTrack.Contains((ushort)0))
            throw new ArgumentException("A layout needs 1–256 nonempty tracks.", nameof(channelsPerTrack));
        int count = channelsPerTrack.Sum(value => (int)value);
        if (count > 256)
            throw new ArgumentException("A layout supports at most 256 channels.", nameof(channelsPerTrack));

        tracks = (ushort[])channelsPerTrack.Clone();
        SampleRate = sampleRate;
        Channels = count;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BytesPerFrame => Channels * sizeof(uint);
    public ReadOnlySpan<ushort> ChannelsPerTrack => tracks;
}

public sealed record EncoderOptions
{
    /// <summary>Zero selects 8192 frames, reduced if necessary to fit the decoded byte limit.</summary>
    public int BlockFrames { get; init; }
    public int CompressionLevel { get; init; } = 5;
    public bool FixedPointPrediction { get; init; } = true;
    public bool CrossTrackSignPrediction { get; init; } = true;
    public bool ExactBlockReferences { get; init; } = true;
}

public sealed record JemaMetadata(byte[]? Id3 = null, byte[]? Application = null);

public readonly record struct DecodeWork(long FileBytesRead, long PcmBytesDecoded, int BlocksDecoded);
