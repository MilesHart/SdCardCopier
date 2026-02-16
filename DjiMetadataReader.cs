namespace SDCardImporter;

/// <summary>
/// Reads DJI-specific metadata from video files to distinguish device types.
/// DJI Flip: pb_file:dvtm_flip.proto;
/// DJI O4 Pro: pb_file:dvtm_O4P.proto;
/// </summary>
public static class DjiMetadataReader
{
    private const string FlipMarker = "pb_file:dvtm_flip.proto";
    private const string O4ProMarker = "pb_file:dvtm_O4P.proto";

    // Metadata in MP4/MOV can be at start or end; moov atom at end is common
    private const int MaxBytesToRead = 10 * 1024 * 1024;

    /// <summary>
    /// Scans media files for DJI metadata markers. Returns the detected device type.
    /// </summary>
    public static DeviceType DetectFromFiles(IEnumerable<string> filePaths)
    {
        var flipCount = 0;
        var o4ProCount = 0;

        foreach (var path in filePaths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not ".mp4" and not ".mov" and not ".m4v")
                continue;

            var detected = DetectFromFile(path);
            if (detected == DeviceType.DJIFlip) flipCount++;
            else if (detected == DeviceType.BetaPavo20Pro) o4ProCount++;
        }

        if (flipCount > o4ProCount) return DeviceType.DJIFlip;
        if (o4ProCount > flipCount) return DeviceType.BetaPavo20Pro;
        return DeviceType.Unknown;
    }

    /// <summary>
    /// Reads a single file and returns the device type if a DJI marker is found.
    /// MP4/MOV metadata (moov atom) can be at the start OR end of the file.
    /// </summary>
    public static DeviceType DetectFromFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var fileLen = stream.Length;

            // Read from start (metadata often at start in "faststart" files)
            var fromStart = (int)Math.Min(fileLen, MaxBytesToRead);
            var startBuffer = new byte[fromStart];
            _ = stream.Read(startBuffer, 0, fromStart);

            // Read from end (metadata often at end in standard MP4 files)
            byte[] endBuffer = [];
            if (fileLen > fromStart)
            {
                stream.Seek(-MaxBytesToRead, SeekOrigin.End);
                var fromEnd = (int)Math.Min(fileLen, MaxBytesToRead);
                endBuffer = new byte[fromEnd];
                _ = stream.Read(endBuffer, 0, fromEnd);
            }

            // Search raw bytes first (avoids encoding issues)
            if (ContainsBytes(startBuffer, O4ProMarker) || ContainsBytes(endBuffer, O4ProMarker))
                return DeviceType.BetaPavo20Pro;
            if (ContainsBytes(startBuffer, FlipMarker) || ContainsBytes(endBuffer, FlipMarker))
                return DeviceType.DJIFlip;

            // Fallback: text search with null bytes stripped
            var startText = System.Text.Encoding.UTF8.GetString(startBuffer).Replace("\0", "");
            var endText = System.Text.Encoding.UTF8.GetString(endBuffer).Replace("\0", "");

            if (ContainsMarker(startText, endText, "dvtm_O4P") || ContainsMarker(startText, endText, "O4P.proto"))
                return DeviceType.BetaPavo20Pro;
            if (ContainsMarker(startText, endText, "dvtm_flip") || ContainsMarker(startText, endText, "flip.proto"))
                return DeviceType.DJIFlip;
        }
        catch
        {
            // Ignore read errors
        }

        return DeviceType.Unknown;
    }

    private static bool ContainsBytes(byte[] buffer, string search)
    {
        var searchBytes = System.Text.Encoding.ASCII.GetBytes(search.ToLowerInvariant());
        for (var i = 0; i <= buffer.Length - searchBytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < searchBytes.Length; j++)
            {
                var b = buffer[i + j];
                var c = (char)(b < 128 ? b : 0);
                if (char.ToLowerInvariant(c) != (char)searchBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private static bool ContainsMarker(string start, string end, string marker)
    {
        return start.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
               end.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }
}
