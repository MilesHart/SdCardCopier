namespace SDCardImporter;

/// <summary>
/// Reads creation/recording date from video container metadata (MP4/MOV mvhd).
/// No external dependencies.
/// </summary>
public static class VideoMetadataReader
{
    /// <summary>QuickTime/MP4 epoch: January 1, 1904 00:00:00 UTC.</summary>
    private static readonly DateTime Utc1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const int MaxBytesToScan = 2 * 1024 * 1024;

    /// <summary>
    /// Gets the creation/recording date from video metadata. Returns null if not found or invalid.
    /// Supports MP4, MOV, M4V. AVI creation time is not standardized.
    /// </summary>
    public static DateTime? GetCreationDate(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not ".mov" and not ".mp4" and not ".m4v")
            return null;

        try
        {
            using var stream = File.OpenRead(filePath);
            var len = stream.Length;
            var toRead = (int)Math.Min(len, MaxBytesToScan);

            byte[] buffer = new byte[toRead];

            // moov often at end – try end first
            if (len <= MaxBytesToScan)
            {
                _ = stream.Read(buffer, 0, toRead);
            }
            else
            {
                stream.Seek(-toRead, SeekOrigin.End);
                _ = stream.Read(buffer, 0, toRead);
            }

            var date = ParseCreationDateFromBuffer(buffer);
            if (date != null) return date;

            // try from start (faststart)
            if (len > MaxBytesToScan)
            {
                stream.Seek(0, SeekOrigin.Begin);
                _ = stream.Read(buffer, 0, toRead);
                return ParseCreationDateFromBuffer(buffer);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? ParseCreationDateFromBuffer(byte[] buffer)
    {
        var moov = FindAtom(buffer, 0, buffer.Length, "moov");
        if (moov == null) return null;

        var mvhd = FindAtom(buffer, moov.Value.Offset, moov.Value.End, "mvhd");
        if (mvhd == null) return null;

        var payloadStart = mvhd.Value.Offset;
        var payloadEnd = mvhd.Value.End;
        if (payloadStart + 12 > payloadEnd) return null;

        var version = buffer[payloadStart];
        ulong creationTimeSeconds;

        if (version == 0)
        {
            creationTimeSeconds = ReadU32Be(buffer, payloadStart + 4);
        }
        else if (version == 1 && payloadStart + 16 <= payloadEnd)
        {
            creationTimeSeconds = ReadU64Be(buffer, payloadStart + 4);
        }
        else
        {
            return null;
        }

        try
        {
            var dt = Utc1904.AddSeconds(creationTimeSeconds);
            // Sanity check: must be reasonable (2000–2100)
            if (dt.Year < 2000 || dt.Year > 2100) return null;
            return dt.ToLocalTime();
        }
        catch
        {
            return null;
        }
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

    private static ulong ReadU64Be(byte[] b, int i)
    {
        return ((ulong)ReadU32Be(b, i) << 32) | ReadU32Be(b, i + 4);
    }
}
