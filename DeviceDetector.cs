using System.Text.RegularExpressions;

namespace SDCardImporter;

/// <summary>
/// Detects the type of device that created content on an SD card
/// </summary>
public class DeviceDetector
{
    private readonly string _rootPath;

    public DeviceDetector(string rootPath)
    {
        _rootPath = rootPath;
    }

    /// <summary>
    /// Analyzes the SD card and determines the device type
    /// </summary>
    public DeviceDetectionResult Detect()
    {
        var result = new DeviceDetectionResult
        {
            RootPath = _rootPath,
            VolumeLabel = GetVolumeLabel()
        };

        // Check for DCIM folder (standard camera folder)
        var dcimPath = Path.Combine(_rootPath, "DCIM");
        
        if (Directory.Exists(dcimPath))
        {
            result.HasDcimFolder = true;
            result.DeviceType = AnalyzeDcimFolder(dcimPath, result);
        }
        else
        {
            // Check for SkyZone DVR files in root (they often put MOV files directly)
            result.DeviceType = CheckForSkyZoneDvr(result);
        }

        // If still unknown but has DJI files, check for goggles vs drone
        if (result.DeviceType == DeviceType.Unknown)
        {
            result.DeviceType = CheckForMiscPatterns(result);
        }

        // Final fallback: scan for any media in DCIM or root, treat as Generic
        if (result.DeviceType == DeviceType.Unknown)
        {
            result.DeviceType = GatherGenericMediaAndDetect(result);
        }

        return result;
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
                // On Linux, try to get label from /dev/disk/by-label or mount info
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
            // Try to find label from mount point
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
                        // Try to get label from blkid or by-label symlinks
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

    private DeviceType AnalyzeDcimFolder(string dcimPath, DeviceDetectionResult result)
    {
        var subdirs = Directory.GetDirectories(dcimPath);
        var mediaExtensions = new[] { ".mp4", ".mov", ".avi", ".jpg", ".jpeg", ".dng", ".png", ".m4v", ".mkv" };

        foreach (var subdir in subdirs)
        {
            var dirName = Path.GetFileName(subdir);
            
            // GoPro pattern: 100GOPRO, 101GOPRO, etc.
            if (Regex.IsMatch(dirName, @"^\d{3}GOPRO$", RegexOptions.IgnoreCase))
            {
                result.FoundPatterns.Add($"GoPro folder: {dirName}");
                if (VerifyGoProFiles(subdir, result))
                {
                    return DeviceType.GoPro13;
                }
            }
            
            // DJI pattern: 100MEDIA, 101MEDIA, etc.
            if (Regex.IsMatch(dirName, @"^\d{3}MEDIA$", RegexOptions.IgnoreCase))
            {
                result.FoundPatterns.Add($"DJI Media folder: {dirName}");
                return AnalyzeDjiMediaFolder(subdir, result);
            }

            // DJI Flip pattern: DJI_001, DJI_002, etc.
            if (Regex.IsMatch(dirName, @"^DJI_\d{3}$", RegexOptions.IgnoreCase))
            {
                result.FoundPatterns.Add($"DJI Flip folder: {dirName}");
                return AnalyzeDjiFlipFolder(dcimPath, result);
            }

            // Generic camera patterns: 100___01, 101___01, 100CANON, 100_PANA, 100OLYMP, 100EK_001, etc.
            if (Regex.IsMatch(dirName, @"^\d{3}[A-Z_]{2,8}\d*$", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(dirName, @"^\d{3}_\d+$", RegexOptions.IgnoreCase))
            {
                result.FoundPatterns.Add($"Camera folder: {dirName}");
                if (CollectMediaFromFolder(subdir, result, mediaExtensions))
                {
                    return DeviceType.Generic;
                }
            }

            // Any folder with digits: 100, 101, 100_0001, etc.
            if (Regex.IsMatch(dirName, @"^\d{3}"))
            {
                result.FoundPatterns.Add($"Media folder: {dirName}");
                if (CollectMediaFromFolder(subdir, result, mediaExtensions))
                {
                    return DeviceType.Generic;
                }
            }
        }

        return DeviceType.Unknown;
    }

    private static readonly string[] DjiFlipExtraFolders = ["HYPERLAPSE", "PANORAMA"];

    private DeviceType AnalyzeDjiFlipFolder(string dcimPath, DeviceDetectionResult result)
    {
        try
        {
            var mediaExtensions = new[] { ".mp4", ".mov", ".avi", ".jpg", ".jpeg", ".dng", ".png", ".m4v", ".mkv" };
            var found = false;

            // Scan DJI_001, DJI_002, etc. in DCIM
            foreach (var subdir in Directory.GetDirectories(dcimPath))
            {
                var dirName = Path.GetFileName(subdir);
                if (Regex.IsMatch(dirName, @"^DJI_\d{3}$", RegexOptions.IgnoreCase))
                {
                    found |= CollectMediaFromPath(subdir, result, mediaExtensions, "DJI Flip");
                }
            }

            // Scan HYPERLAPSE and PANORAMA (at root or in DCIM)
            foreach (var folderName in DjiFlipExtraFolders)
            {
                var rootFolder = Path.Combine(_rootPath, folderName);
                if (Directory.Exists(rootFolder))
                {
                    result.FoundPatterns.Add($"DJI Flip folder: {folderName}");
                    found |= CollectMediaFromPath(rootFolder, result, mediaExtensions, "DJI Flip");
                }
                var dcimFolder = Path.Combine(dcimPath, folderName);
                if (Directory.Exists(dcimFolder))
                {
                    result.FoundPatterns.Add($"DJI Flip folder: DCIM/{folderName}");
                    found |= CollectMediaFromPath(dcimFolder, result, mediaExtensions, "DJI Flip");
                }
            }

            if (!found) return DeviceType.Unknown;

            // Use metadata (pb_file tags) to distinguish Flip from O4 Pro
            var metadataType = DjiMetadataReader.DetectFromFiles(result.MediaFiles);
            if (metadataType == DeviceType.BetaPavo20Pro)
            {
                result.FoundPatterns.Add("Metadata: pb_file:dvtm_O4P.proto (O4 Pro)");
                return DeviceType.BetaPavo20Pro;
            }
            if (metadataType == DeviceType.DJIFlip)
            {
                result.FoundPatterns.Add("Metadata: pb_file:dvtm_flip.proto (DJI Flip)");
            }

            return DeviceType.DJIFlip;
        }
        catch
        {
            return DeviceType.Unknown;
        }
    }

    private bool CollectMediaFromPath(string folderPath, DeviceDetectionResult result, string[] extensions, string label)
    {
        var found = false;
        foreach (var file in Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (extensions.Contains(ext))
            {
                result.MediaFiles.Add(file);
                result.FoundPatterns.Add($"{label} file: {Path.GetFileName(file)}");
                found = true;
            }
        }
        return found;
    }

    private bool CollectMediaFromFolder(string folderPath, DeviceDetectionResult result, string[] extensions)
    {
        try
        {
            var found = false;
            foreach (var file in Directory.GetFiles(folderPath))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (extensions.Contains(ext))
                {
                    result.MediaFiles.Add(file);
                    found = true;
                }
            }
            return found;
        }
        catch
        {
            return false;
        }
    }

    private bool VerifyGoProFiles(string folderPath, DeviceDetectionResult result)
    {
        try
        {
            var files = Directory.GetFiles(folderPath);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                
                // GoPro file patterns: GOPRXXXX.MP4, GXNNNNNN.MP4, GLXXXXXX.LRV
                if (Regex.IsMatch(fileName, @"^(GOPR|GX|GL|GH)\d+\.(MP4|LRV|THM|JPG)$", RegexOptions.IgnoreCase))
                {
                    result.FoundPatterns.Add($"GoPro file: {fileName}");
                    result.MediaFiles.Add(file);
                    return true;
                }
            }
        }
        catch
        {
            // Ignore access errors
        }
        return false;
    }

    private DeviceType AnalyzeDjiMediaFolder(string folderPath, DeviceDetectionResult result)
    {
        try
        {
            var files = Directory.GetFiles(folderPath);
            bool hasDjiFiles = false;
            bool hasO4ProMarkers = false;

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                
                // DJI file pattern: DJI_XXXX.MP4, DJI_XXXX_D.MP4, etc.
                if (Regex.IsMatch(fileName, @"^DJI_\d+.*\.(MP4|MOV|JPG)$", RegexOptions.IgnoreCase))
                {
                    hasDjiFiles = true;
                    result.FoundPatterns.Add($"DJI file: {fileName}");
                    result.MediaFiles.Add(file);
                }
            }

            if (!hasDjiFiles) return DeviceType.Unknown;

            // Use metadata (pb_file tags) to distinguish Flip from O4 Pro - most reliable
            var metadataType = DjiMetadataReader.DetectFromFiles(result.MediaFiles);
            if (metadataType == DeviceType.DJIFlip)
            {
                result.FoundPatterns.Add("Metadata: pb_file:dvtm_flip.proto (DJI Flip)");
                return DeviceType.DJIFlip;
            }
            if (metadataType == DeviceType.BetaPavo20Pro)
            {
                result.FoundPatterns.Add("Metadata: pb_file:dvtm_O4P.proto (O4 Pro)");
                return DeviceType.BetaPavo20Pro;
            }

            // Fallback: Check for O4 Pro specific markers (SRT, large files)
            hasO4ProMarkers = CheckForO4ProMarkers(folderPath, result);

            // Check parent folders for additional clues
            var miscPath = Path.Combine(Path.GetDirectoryName(folderPath)!, "..", "MISC");
            if (Directory.Exists(miscPath))
            {
                result.FoundPatterns.Add("MISC folder present (common in DJI devices)");
            }

            // Priority: O4 Pro > Goggles (if we can distinguish)
            if (hasO4ProMarkers)
            {
                return DeviceType.BetaPavo20Pro;
            }
            
            // Default DJI media to Goggles 3 (most common use case for this tool)
            return DeviceType.DJIGoggles3;
        }
        catch
        {
            return DeviceType.Unknown;
        }
    }

    private bool CheckForO4ProMarkers(string folderPath, DeviceDetectionResult result)
    {
        try
        {
            // O4 Pro files are typically 4K60 and larger file sizes
            // Also check for LRF (Low Resolution File) companions
            var files = Directory.GetFiles(folderPath);
            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                var fileName = Path.GetFileName(file);
                
                // O4 Pro often has accompanying .SRT subtitle files with GPS data
                if (fileName.EndsWith(".SRT", StringComparison.OrdinalIgnoreCase))
                {
                    result.FoundPatterns.Add("SRT subtitle file found (O4 Pro marker)");
                    return true;
                }
                
                // Check for very large files (4K60 is typically >500MB per minute)
                if (fileInfo.Extension.Equals(".MP4", StringComparison.OrdinalIgnoreCase) &&
                    fileInfo.Length > 500_000_000) // >500MB suggests 4K
                {
                    result.FoundPatterns.Add("Large video file detected (possible 4K from O4 Pro)");
                    return true;
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return false;
    }

    private bool CheckForGogglesMarkers(string folderPath, DeviceDetectionResult result)
    {
        try
        {
            // Goggles recordings are typically lower resolution feed recordings
            var files = Directory.GetFiles(folderPath, "*.MP4");
            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                
                // Goggles DVR files are typically smaller (720p or 1080p feed)
                // Usually <100MB per minute at lower bitrate
                if (fileInfo.Length < 200_000_000) // <200MB
                {
                    result.FoundPatterns.Add("Smaller video file (typical of Goggles DVR)");
                    return true;
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return false;
    }

    private DeviceType CheckForSkyZoneDvr(DeviceDetectionResult result)
    {
        try
        {
            // SkyZone goggles save MOV files with H264 encoding
            // They typically save directly to root or a simple VIDEO folder
            var movFiles = Directory.GetFiles(_rootPath, "*.MOV", SearchOption.TopDirectoryOnly);
            
            if (movFiles.Length > 0)
            {
                foreach (var file in movFiles)
                {
                    var fileName = Path.GetFileName(file);
                    result.FoundPatterns.Add($"MOV file in root: {fileName}");
                    result.MediaFiles.Add(file);
                }
                
                // Check for typical SkyZone naming pattern (often timestamp-based)
                if (movFiles.Any(f => Regex.IsMatch(Path.GetFileName(f), @"^\d{8}_\d{6}\.MOV$", RegexOptions.IgnoreCase) ||
                                      Regex.IsMatch(Path.GetFileName(f), @"^VID_\d+\.MOV$", RegexOptions.IgnoreCase) ||
                                      Regex.IsMatch(Path.GetFileName(f), @"^\d+\.MOV$", RegexOptions.IgnoreCase)))
                {
                    result.FoundPatterns.Add("SkyZone-style filename pattern detected");
                    return DeviceType.SkyZoneAnalog;
                }
                
                // If we have MOV files but no DCIM, likely SkyZone
                return DeviceType.SkyZoneAnalog;
            }

            // Also check for AVI files (older SkyZone models)
            var aviFiles = Directory.GetFiles(_rootPath, "*.AVI", SearchOption.TopDirectoryOnly);
            if (aviFiles.Length > 0)
            {
                foreach (var file in aviFiles)
                {
                    result.FoundPatterns.Add($"AVI file in root: {Path.GetFileName(file)}");
                    result.MediaFiles.Add(file);
                }
                return DeviceType.SkyZoneAnalog;
            }

            // Check VIDEO folder
            var videoPath = Path.Combine(_rootPath, "VIDEO");
            if (Directory.Exists(videoPath))
            {
                var videoFiles = Directory.GetFiles(videoPath, "*.*")
                    .Where(f => f.EndsWith(".MOV", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".AVI", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                
                if (videoFiles.Any())
                {
                    result.FoundPatterns.Add("VIDEO folder with recordings found");
                    result.MediaFiles.AddRange(videoFiles);
                    return DeviceType.SkyZoneAnalog;
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        
        return DeviceType.Unknown;
    }

    private DeviceType CheckForMiscPatterns(DeviceDetectionResult result)
    {
        // Additional fallback checks
        try
        {
            // Check volume label for clues
            var label = result.VolumeLabel?.ToUpperInvariant() ?? "";
            
            if (label.Contains("GOPRO"))
            {
                result.FoundPatterns.Add($"Volume label contains 'GOPRO': {result.VolumeLabel}");
                return DeviceType.GoPro13;
            }
            
            if (label.Contains("DJI"))
            {
                result.FoundPatterns.Add($"Volume label contains 'DJI': {result.VolumeLabel}");
                return DeviceType.DJIGoggles3;
            }
            
            if (label.Contains("SKYZONE") || label.Contains("SKY"))
            {
                result.FoundPatterns.Add($"Volume label contains 'SKY': {result.VolumeLabel}");
                return DeviceType.SkyZoneAnalog;
            }
        }
        catch
        {
            // Ignore errors
        }
        
        return DeviceType.Unknown;
    }

    /// <summary>
    /// Final fallback: recursively scan for any media files and treat as Generic
    /// </summary>
    private DeviceType GatherGenericMediaAndDetect(DeviceDetectionResult result)
    {
        var extensions = new[] { ".mp4", ".mov", ".avi", ".jpg", ".jpeg", ".dng", ".png", ".m4v", ".mkv", ".lrv", ".thm", ".srt" };
        var mediaFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Scan DCIM recursively
            var dcimPath = Path.Combine(_rootPath, "DCIM");
            if (Directory.Exists(dcimPath))
            {
                foreach (var file in Directory.GetFiles(dcimPath, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                    {
                        mediaFiles.Add(file);
                        result.FoundPatterns.Add($"Generic media: {Path.GetFileName(file)}");
                    }
                }
            }

            // Scan root for media
            foreach (var file in Directory.GetFiles(_rootPath, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (extensions.Contains(ext))
                {
                    mediaFiles.Add(file);
                    result.FoundPatterns.Add($"Root media: {Path.GetFileName(file)}");
                }
            }

            // Scan PRIVATE (common on some cameras)
            var privatePath = Path.Combine(_rootPath, "PRIVATE");
            if (Directory.Exists(privatePath))
            {
                foreach (var file in Directory.GetFiles(privatePath, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                    {
                        mediaFiles.Add(file);
                    }
                }
            }

            // Scan VIDEO folder
            var videoPath = Path.Combine(_rootPath, "VIDEO");
            if (Directory.Exists(videoPath))
            {
                foreach (var file in Directory.GetFiles(videoPath, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                    {
                        mediaFiles.Add(file);
                    }
                }
            }

            if (mediaFiles.Count > 0)
            {
                result.MediaFiles.AddRange(mediaFiles);
                result.FoundPatterns.Add($"Generic detection: {mediaFiles.Count} media files found");
                return DeviceType.Generic;
            }
        }
        catch
        {
            // Ignore access errors
        }

        return DeviceType.Unknown;
    }
}

/// <summary>
/// Result of device detection analysis
/// </summary>
public class DeviceDetectionResult
{
    public string RootPath { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;
    public bool HasDcimFolder { get; set; }
    public List<string> FoundPatterns { get; set; } = new();
    public List<string> MediaFiles { get; set; } = new();
}
