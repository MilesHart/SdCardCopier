namespace SDCardImporter;

/// <summary>
/// Reads video dimensions from MOV/MP4 container atoms (no external dependencies).
/// Used to detect SkyZone goggle DVR footage (640x480).
/// </summary>
public static class VideoDimensionReader
{
    /// <summary>SkyZone DVR records at 640x480.</summary>
    public const int SkyZoneWidth = 640;
    public const int SkyZoneHeight = 480;

    private const int MaxBytesToScan = 2 * 1024 * 1024;

    /// <summary>
    /// Gets (width, height) from the first video track. Supports MOV, MP4, and AVI.
    /// </summary>
    public static (int Width, int Height)? GetDimensions(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".avi")
            return GetDimensionsAvi(filePath);
        if (ext is not ".mov" and not ".mp4" and not ".m4v")
            return null;

        try
        {
            using var stream = File.OpenRead(filePath);
            var len = stream.Length;
            var toRead = (int)Math.Min(len, MaxBytesToScan);

            // moov can be at start (faststart) or end – try end first (most common)
            byte[] buffer = new byte[toRead];
            if (len <= MaxBytesToScan)
            {
                _ = stream.Read(buffer, 0, toRead);
            }
            else
            {
                stream.Seek(-toRead, SeekOrigin.End);
                _ = stream.Read(buffer, 0, toRead);
            }

            var dim = ParseDimensionsFromBuffer(buffer);
            if (dim != null) return dim;

            // try from start (e.g. faststart MOV)
            if (len > MaxBytesToScan)
            {
                stream.Seek(0, SeekOrigin.Begin);
                _ = stream.Read(buffer, 0, toRead);
                return ParseDimensionsFromBuffer(buffer);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse AVI (RIFF) for video dimensions from first stream's strf (BITMAPINFOHEADER).</summary>
    private static (int Width, int Height)? GetDimensionsAvi(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var toRead = (int)Math.Min(stream.Length, 64 * 1024); // AVI header is near start
            var buffer = new byte[toRead];
            _ = stream.Read(buffer, 0, toRead);
            return ParseAviDimensions(buffer);
        }
        catch
        {
            return null;
        }
    }

    private static (int Width, int Height)? ParseAviDimensions(byte[] buffer)
    {
        // RIFF 'AVI ' then LIST 'hdrl' -> LIST 'strl' -> chunk 'strf' (BITMAPINFOHEADER: width at +4, height at +8, LE)
        if (buffer.Length < 12) return null;
        if (buffer[0] != 'R' || buffer[1] != 'I' || buffer[2] != 'F' || buffer[3] != 'F') return null;
        if (buffer[8] != 'A' || buffer[9] != 'V' || buffer[10] != 'I' || buffer[11] != ' ') return null;
        var strf = FindRiffChunk(buffer, 12, buffer.Length, "LIST", "hdrl", "LIST", "strl", "strf");
        if (strf == null) return null;
        // strf data is BITMAPINFOHEADER: biSize(4), biWidth(4), biHeight(4) - little-endian
        if (strf.Value.Offset + 12 > strf.Value.End) return null;
        var w = ReadU32Le(buffer, strf.Value.Offset + 4);
        var h = ReadU32Le(buffer, strf.Value.Offset + 8);
        var width = (int)w;
        var height = (int)h;
        if (height < 0) height = -height; // top-down bitmap
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;
        return (width, height);
    }

    /// <summary>Find a nested chunk: in first LIST of type listType1, find LIST listType2, then chunk chunkId. Returns chunk data start and end.</summary>
    private static (int Offset, int End)? FindRiffChunk(byte[] b, int start, int end, string listOrChunk, string type1, string listOrChunk2, string type2, string chunkId)
    {
        var list1 = FindRiffListOrChunk(b, start, end, listOrChunk, type1);
        if (list1 == null) return null;
        var list2 = FindRiffListOrChunk(b, list1.Value.Offset, list1.Value.End, listOrChunk2, type2);
        if (list2 == null) return null;
        return FindRiffListOrChunk(b, list2.Value.Offset, list2.Value.End, chunkId, null);
    }

    private static (int Offset, int End)? FindRiffListOrChunk(byte[] b, int start, int end, string fourCc, string? listType)
    {
        var id = System.Text.Encoding.ASCII.GetBytes(fourCc);
        if (id.Length != 4) return null;
        var i = start;
        while (i + 8 <= end)
        {
            var isList = b[i] == 'L' && b[i + 1] == 'I' && b[i + 2] == 'S' && b[i + 3] == 'T';
            var size = (int)ReadU32Le(b, i + 4);
            if (size < 0 || i + 8 + size > end) break;
            var payloadStart = i + 8;
            if (isList && listType != null)
            {
                if (payloadStart + 4 <= end &&
                    b[payloadStart] == listType[0] && b[payloadStart + 1] == listType[1] &&
                    b[payloadStart + 2] == listType[2] && b[payloadStart + 3] == listType[3])
                    return (payloadStart + 4, i + 8 + size);
            }
            if (!isList && listType == null &&
                b[i] == id[0] && b[i + 1] == id[1] && b[i + 2] == id[2] && b[i + 3] == id[3])
                return (payloadStart, payloadStart + size);
            i = payloadStart + size;
            if ((i & 1) != 0) i++; // 2-byte align
        }
        return null;
    }

    private static uint ReadU32Le(byte[] b, int i)
    {
        return b[i] | ((uint)b[i + 1] << 8) | ((uint)b[i + 2] << 16) | ((uint)b[i + 3] << 24);
    }

    /// <summary>True if the file is a video with 640x480 resolution (SkyZone DVR).</summary>
    public static bool Is640x480(string filePath)
    {
        var dim = GetDimensions(filePath);
        return dim is { Width: SkyZoneWidth, Height: SkyZoneHeight };
    }

    private static (int Width, int Height)? ParseDimensionsFromBuffer(byte[] buffer)
    {
        var moov = FindAtom(buffer, 0, buffer.Length, "moov");
        if (moov == null) return null;

        var start = moov.Value.Offset;
        var end = moov.Value.End;
        // Try each trak (first is often video; audio trak can have width/height 0)
        while (start < end)
        {
            var trak = FindAtom(buffer, start, end, "trak");
            if (trak == null) break;

            var tkhd = FindAtom(buffer, trak.Value.Offset, trak.Value.End, "tkhd");
            if (tkhd != null)
            {
                var payloadStart = tkhd.Value.Offset; // Offset is first byte of tkhd payload (after 8-byte header)
                if (payloadStart + 88 <= tkhd.Value.End)
                {
                    var version = buffer[payloadStart];
                    int widthOffset = version == 0 ? 76 : 84;
                    int heightOffset = widthOffset + 4;
                    var w = ReadU32Be(buffer, payloadStart + widthOffset);
                    var h = ReadU32Be(buffer, payloadStart + heightOffset);
                    var width = (int)(w >> 16);
                    var height = (int)(h >> 16);
                    if (width > 0 && height > 0 && width <= 8192 && height <= 8192)
                        return (width, height);
                }
            }
            start = trak.Value.End;
        }
        return null;
    }

    private static (int Offset, int End)? FindAtom(byte[] buffer, int start, int end, string type)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        if (typeBytes.Length != 4) return null;

        var i = start;
        while (i + 8 <= end)
        {
            var size = (int)ReadU32Be(buffer, i);
            if (size < 8) break;
            var actualEnd = i + size;
            if (actualEnd > end) break;

            var match = buffer[i + 4] == typeBytes[0] && buffer[i + 5] == typeBytes[1] &&
                        buffer[i + 6] == typeBytes[2] && buffer[i + 7] == typeBytes[3];
            if (match) return (i + 8, actualEnd);

            if (size == 1 && i + 16 <= end)
            {
                var extSize = (long)ReadU32Be(buffer, i + 8) << 32 | ReadU32Be(buffer, i + 12);
                if (extSize > int.MaxValue || extSize < 8) break;
                actualEnd = i + (int)extSize;
                if (actualEnd > end) break;
            }
            i = actualEnd;
        }
        return null;
    }

    private static uint ReadU32Be(byte[] b, int i)
    {
        return ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
    }
}
