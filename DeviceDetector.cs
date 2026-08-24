namespace SDCardImporter;

/// <summary>
/// Identifies the device that created content on an SD card via <c>autoUpdater.txt</c> on the card root.
/// </summary>
public class DeviceDetector
{
    /// <summary>Name of the identification file expected at the root of every card.</summary>
    public const string AutoUpdaterFileName = "autoUpdater.txt";

    private readonly string _rootPath;

    public DeviceDetector(string rootPath)
    {
        _rootPath = rootPath;
    }

    /// <summary>
    /// Reads <c>autoUpdater.txt</c> from the card root to identify the device. If the file is
    /// missing or empty, <see cref="DeviceDetectionResult.NeedsIdentification"/> is set so the
    /// caller can prompt the user and persist the answer via <see cref="WriteAutoUpdaterFile"/>.
    /// </summary>
    public DeviceDetectionResult Detect()
    {
        var result = new DeviceDetectionResult
        {
            RootPath = _rootPath,
            VolumeLabel = GetVolumeLabel()
        };

        var firstLine = ReadAutoUpdaterFirstLine(_rootPath);
        if (!string.IsNullOrEmpty(firstLine) && !firstLine.Equals("Other", StringComparison.OrdinalIgnoreCase))
        {
            result.IdentLabel = firstLine;
            result.DeviceType = DeviceDetectionResult.MapAutoUpdaterLineToDeviceType(firstLine);
        }
        else
        {
            result.NeedsIdentification = true;
        }

        CollectStandardMediaFiles(result);
        return result;
    }

    /// <summary>Reads the first non-empty line of <c>autoUpdater.txt</c> at <paramref name="rootPath"/>, or null if absent/empty.</summary>
    public static string? ReadAutoUpdaterFirstLine(string rootPath)
    {
        var path = Path.Combine(rootPath, AutoUpdaterFileName);
        if (!File.Exists(path))
            return null;
        try
        {
            return File.ReadLines(path).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="deviceName"/> as the first line of <c>autoUpdater.txt</c> on the card root.</summary>
    public static void WriteAutoUpdaterFile(string rootPath, string deviceName)
    {
        File.WriteAllText(Path.Combine(rootPath, AutoUpdaterFileName), deviceName.Trim() + Environment.NewLine);
    }

    private string GetVolumeLabel()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_rootPath)!);
                return driveInfo.VolumeLabel ?? "";
            }
            else
            {
                return GetLinuxVolumeLabel();
            }
        }
        catch
        {
            return "";
        }
    }

    private string GetLinuxVolumeLabel()
    {
        try
        {
            var mountOutput = File.ReadAllText("/proc/mounts");
            var lines = mountOutput.Split('\n');

            foreach (var line in lines)
            {
                if (line.Contains(_rootPath))
                {
                    var parts = line.Split(' ');
                    if (parts.Length > 0)
                    {
                        var device = parts[0];
                        var byLabelPath = "/dev/disk/by-label";
                        if (Directory.Exists(byLabelPath))
                        {
                            foreach (var labelLink in Directory.GetFiles(byLabelPath))
                            {
                                var target = Path.GetFullPath(labelLink);
                                if (target == device)
                                {
                                    return Path.GetFileName(labelLink);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return "";
    }

    /// <summary>DCIM, root, PRIVATE, VIDEO — same extensions as the copy pipeline. Populates <see cref="DeviceDetectionResult.MediaFiles"/>.</summary>
    private void CollectStandardMediaFiles(DeviceDetectionResult result)
    {
        var extensions = new[] { ".mp4", ".mov", ".avi", ".jpg", ".jpeg", ".dng", ".png", ".m4v", ".mkv", ".lrv", ".thm", ".srt" };
        var mediaFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var dcimPath = Path.Combine(_rootPath, "DCIM");
            result.HasDcimFolder = Directory.Exists(dcimPath);

            if (Directory.Exists(dcimPath))
            {
                foreach (var file in Directory.GetFiles(dcimPath, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                        mediaFiles.Add(file);
                }
            }

            foreach (var file in Directory.GetFiles(_rootPath, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (extensions.Contains(ext))
                    mediaFiles.Add(file);
            }

            var privatePath = Path.Combine(_rootPath, "PRIVATE");
            if (Directory.Exists(privatePath))
            {
                foreach (var file in Directory.GetFiles(privatePath, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                        mediaFiles.Add(file);
                }
            }

            var videoPath = Path.Combine(_rootPath, "VIDEO");
            if (Directory.Exists(videoPath))
            {
                foreach (var file in Directory.GetFiles(videoPath, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                        mediaFiles.Add(file);
                }
            }

            result.MediaFiles.AddRange(mediaFiles);
        }
        catch
        {
            // Ignore access errors
        }
    }
}

/// <summary>
/// Result of device identification for a card.
/// </summary>
public class DeviceDetectionResult
{
    public string RootPath { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;
    public bool HasDcimFolder { get; set; }
    public List<string> MediaFiles { get; set; } = new();

    /// <summary>True when no (or an empty) <c>autoUpdater.txt</c> was found — caller must prompt and persist a name.</summary>
    public bool NeedsIdentification { get; set; }

    /// <summary>First line of root <c>autoUpdater.txt</c> once identified; used for display and routing.</summary>
    public string? IdentLabel { get; set; }

    /// <summary>User-facing device name: <see cref="IdentLabel"/> when set, otherwise enum display name.</summary>
    public string GetDeviceDisplayName() =>
        !string.IsNullOrWhiteSpace(IdentLabel) ? IdentLabel.Trim() : DeviceType.GetDisplayName();

    /// <summary>
    /// Destination subfolder name for this card. For a recognized device this is the fixed folder
    /// code (e.g. <c>GP13</c>). For an unrecognized card, uses the raw <see cref="IdentLabel"/> text
    /// from <c>autoUpdater.txt</c> as the folder name (sanitized) instead of dumping it in <c>Other</c>,
    /// so differently-labeled unknown cards don't get merged into one bucket.
    /// </summary>
    public string GetFolderName()
    {
        if (DeviceType != DeviceType.Generic)
            return DeviceType.GetFolderName();

        var label = IdentLabel?.Trim();
        if (string.IsNullOrEmpty(label) || label.Equals("Other", StringComparison.OrdinalIgnoreCase))
            return DeviceType.Generic.GetFolderName();

        return SanitizeFolderName(label);
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim().Trim('.');
        return sanitized.Length == 0 ? DeviceType.Generic.GetFolderName() : sanitized;
    }

    /// <summary>
    /// Maps the first line of <c>autoUpdater.txt</c> to a <see cref="DeviceType"/> for destination folders.
    /// Unrecognized text maps to <see cref="DeviceType.Generic"/> (folder <c>Other</c>).
    /// </summary>
    public static DeviceType MapAutoUpdaterLineToDeviceType(string line)
    {
        var s = line.Trim();
        if (s.Length == 0)
            return DeviceType.Generic;

        foreach (var (code, dt) in AutoUpdaterFolderCodes)
        {
            if (s.Equals(code, StringComparison.OrdinalIgnoreCase))
                return dt;
        }

        return DeviceType.Generic;
    }

    private static readonly (string Code, DeviceType Type)[] AutoUpdaterFolderCodes =
    [
        ("GoggleDJI", DeviceType.DJIGoggles3),
        ("DJIFlip", DeviceType.DJIFlip),
        ("GoggleSZ", DeviceType.SkyZoneAnalog),
        ("DJI04", DeviceType.BetaPavo20Pro),
        ("GP13", DeviceType.GoPro13),
    ];
}
