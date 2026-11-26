using System.Buffers.Binary;
using System.IO.Hashing;

namespace Jema;

internal static class MetadataTrailer
{
    private static ReadOnlySpan<byte> Magic => "JEMAMETA"u8;
    private const int FooterSize = 24;
    private const int MaxPayload = 32 * 1024 * 1024;

    public static void Validate(JemaMetadata? metadata)
    {
        if (metadata?.Id3 is { Length: > MaxPayload } || metadata?.Application is { Length: > MaxPayload })
            throw new InvalidDataException("A metadata payload exceeds 32 MiB.");
        if (metadata?.Id3 is { Length: > 0 } id3 && !IsId3(id3))
            throw new InvalidDataException("Expected a complete ID3v2.3 or ID3v2.4 tag.");
    }

    public static void Write(Stream destination, JemaMetadata? metadata)
    {
        ReadOnlySpan<byte> id3 = metadata?.Id3;
        ReadOnlySpan<byte> app = metadata?.Application;
        if (id3.IsEmpty && app.IsEmpty)
            return;

        destination.Write(id3);
        destination.Write(app);
        Span<byte> footer = stackalloc byte[FooterSize];
        Magic.CopyTo(footer);
        BinaryPrimitives.WriteInt32LittleEndian(footer[8..], id3.Length);
        BinaryPrimitives.WriteInt32LittleEndian(footer[12..], app.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], Crc32.HashToUInt32(id3));
        BinaryPrimitives.WriteUInt32LittleEndian(footer[20..], Crc32.HashToUInt32(app));
        destination.Write(footer);
    }

    public static JemaMetadata Read(Stream source, long audioEnd)
    {
        if (source.Length == audioEnd)
            return new JemaMetadata();
        if (source.Length - audioEnd < FooterSize)
            throw new InvalidDataException("Truncated metadata footer.");

        Span<byte> footer = stackalloc byte[FooterSize];
        source.Position = source.Length - FooterSize;
        source.ReadExactly(footer);
        int id3Size = BinaryPrimitives.ReadInt32LittleEndian(footer[8..]);
        int appSize = BinaryPrimitives.ReadInt32LittleEndian(footer[12..]);
        if (!footer[..8].SequenceEqual(Magic) || id3Size is < 0 or > MaxPayload || appSize is < 0 or > MaxPayload
            || id3Size + appSize == 0 || source.Length - audioEnd != id3Size + appSize + FooterSize)
            throw new InvalidDataException("Invalid metadata lengths.");

        source.Position = audioEnd;
        var result = new JemaMetadata(
            ReadPayload(source, id3Size, BinaryPrimitives.ReadUInt32LittleEndian(footer[16..])),
            ReadPayload(source, appSize, BinaryPrimitives.ReadUInt32LittleEndian(footer[20..])));
        Validate(result);
        return result;
    }

    private static byte[]? ReadPayload(Stream source, int size, uint checksum)
    {
        byte[]? data = size == 0 ? null : new byte[size];
        if (data is not null)
            source.ReadExactly(data);
        if (Crc32.HashToUInt32(data) != checksum)
            throw new InvalidDataException("Metadata checksum mismatch.");
        return data;
    }

    private static bool IsId3(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 10 || !tag[..3].SequenceEqual("ID3"u8) || tag[3] is not (3 or 4)
            || tag[4] == 255 || ((tag[6] | tag[7] | tag[8] | tag[9]) & 128) != 0)
            return false;
        int size = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
        bool hasFooter = tag[3] == 4 && (tag[5] & 16) != 0;
        if (tag.Length != 10 + size + (hasFooter ? 10 : 0))
            return false;
        return !hasFooter || (tag[^10..^7].SequenceEqual("3DI"u8) && tag[^7..].SequenceEqual(tag[3..10]));
    }
}
