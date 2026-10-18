using System.Buffers.Binary;

namespace Tommy.Tools.Internal;

/// <summary>
/// Reads image format + pixel dimensions from file headers with no native
/// decoder — avoiding the Linux native-dependency trap (System.Drawing is
/// Windows-only). Covers PNG, JPEG, GIF, and BMP.
/// </summary>
internal static class ImageHeader
{
    public readonly record struct Info(string Format, int Width, int Height);

    public static bool TryRead(string path, out Info info)
    {
        info = default;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[32];
            var read = fs.Read(head);
            if (read < 24) return TryJpeg(fs, out info); // JPEG needs a full scan

            // PNG: 89 50 4E 47 0D 0A 1A 0A, then IHDR width/height (big-endian).
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            {
                var w = BinaryPrimitives.ReadInt32BigEndian(head.Slice(16, 4));
                var h = BinaryPrimitives.ReadInt32BigEndian(head.Slice(20, 4));
                info = new Info("PNG", w, h);
                return true;
            }

            // GIF: "GIF87a"/"GIF89a", then width/height (little-endian uint16).
            if (head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F')
            {
                var w = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(6, 2));
                var h = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(8, 2));
                info = new Info("GIF", w, h);
                return true;
            }

            // BMP: "BM", then DIB header width/height (little-endian int32 at 18/22).
            if (head[0] == (byte)'B' && head[1] == (byte)'M')
            {
                var w = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(18, 4));
                var h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head.Slice(22, 4)));
                info = new Info("BMP", w, h);
                return true;
            }

            // JPEG: FF D8 — scan the marker segments for a start-of-frame.
            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                fs.Position = 2;
                return TryJpeg(fs, out info);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryJpeg(Stream fs, out Info info)
    {
        info = default;
        try
        {
            fs.Position = 2;
            Span<byte> marker = stackalloc byte[2];
            Span<byte> len = stackalloc byte[2];
            // SOF payload: length(2) + precision(1) + height(2) + width(2).
            Span<byte> frame = stackalloc byte[7];

            while (fs.Read(marker) == 2)
            {
                if (marker[0] != 0xFF) return false;
                var code = marker[1];

                // Start-of-frame markers carry the dimensions.
                if (code is >= 0xC0 and <= 0xC3 || code is >= 0xC5 and <= 0xC7 || code is >= 0xC9 and <= 0xCB)
                {
                    if (fs.Read(frame) != 7) return false;
                    var h = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3, 2));
                    var w = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(5, 2));
                    info = new Info("JPEG", w, h);
                    return true;
                }

                // Standalone markers (no length) — skip.
                if (code is 0xD8 or 0xD9 || code is >= 0xD0 and <= 0xD7) continue;

                if (fs.Read(len) != 2) return false;
                var segment = BinaryPrimitives.ReadUInt16BigEndian(len);
                if (segment < 2) return false;
                fs.Position += segment - 2;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
