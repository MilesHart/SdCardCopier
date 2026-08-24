using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SDCardImporter;

/// <summary>
/// Copies media files to an organized directory structure
/// </summary>
public class FileCopier
{
    private const uint PROGRESS_CONTINUE = 0;
    private const double ProgressUpdateIntervalSeconds = 5.0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CopyFileEx(
        string lpExistingFileName,
        string lpNewFileName,
        CopyProgressRoutine? pProgressRoutine,
        IntPtr lpData,
        IntPtr pbCancel,
        uint dwCopyFlags);

    private delegate uint CopyProgressRoutine(
        long TotalFileSize,
        long TotalBytesTransferred,
        long StreamSize,
        long StreamBytesTransferred,
        uint dwStreamNumber,
        uint dwCallbackReason,
        IntPtr hSourceFile,
        IntPtr hDestinationFile,
        IntPtr lpData);

    private sealed class CopyProgressContext
    {
        public long BytesBeforeThisFile;
        public int FileIndex;
        public int TotalFiles;
        public long TotalBytes;
        public string FileName = "";
        public long CopyStartTick;
        public long LastDisplayBytes;
        public long LastDisplayTick;
        public long LastProgressUpdateTick;
    }
    private readonly string _destinationRoot;
    private readonly bool _verbose;
    private readonly bool _overwriteAll;
    private readonly DateTime? _skyZoneLogDate;
    private readonly DateTime? _defaultDate;
    private readonly Action<int, int, long, long, string, bool>? _onProgress;
    private readonly Action<string, bool>? _onFileComplete;
    private readonly bool _deleteSkipped;
    private readonly bool _skipSpaceCheck;
    private readonly bool _dryRun;

    public FileCopier(string destinationRoot, bool verbose = true, bool overwriteAll = false, DateTime? skyZoneLogDate = null, DateTime? defaultDate = null, Action<int, int, long, long, string, bool>? onProgress = null, Action<string, bool>? onFileComplete = null, bool deleteSkipped = false, bool skipSpaceCheck = false, bool dryRun = false)
    {
        _destinationRoot = destinationRoot;
        _verbose = verbose;
        _overwriteAll = overwriteAll;
        _skyZoneLogDate = skyZoneLogDate;
        _defaultDate = defaultDate;
        _onProgress = onProgress;
        _onFileComplete = onFileComplete;
        _deleteSkipped = deleteSkipped;
        _skipSpaceCheck = skipSpaceCheck;
        _dryRun = dryRun;
    }

    private enum CopyPlanKind
    {
        Copy,
        SkipDuplicate,
        CopyUnique
    }

    private sealed class CopyPlan
    {
        public required string SourcePath { get; init; }
        public required DeviceType DeviceType { get; init; }
        public required string FolderName { get; init; }
        public required DateTime FileDate { get; init; }
        public required string DateSource { get; init; }
        public required string DestFolder { get; init; }
        public required string DestFile { get; init; }
        public required CopyPlanKind Kind { get; init; }
    }

    public sealed class CopyPreviewItem
    {
        public required string SourcePath { get; init; }
        public required string DestFolder { get; init; }
        public required string DestFile { get; init; }
        public required bool WillCopy { get; init; }
    }

    /// <summary>
    /// Copies all media files from source to organized destination
    /// Structure: /{year}/{Jan|Feb|...}/{day}/{DeviceFolder}/
    /// </summary>
    public CopyResult CopyFiles(DeviceDetectionResult detection)
    {
        var result = new CopyResult
        {
            DeviceType = detection.DeviceType,
            SourcePath = detection.RootPath
        };

        if (detection.DeviceType == DeviceType.Unknown)
        {
            result.Errors.Add("Cannot copy files: device type is unknown");
            return result;
        }

        // Find all media files to copy
        var filesToCopy = GatherMediaFiles(detection);
        result.TotalFiles = filesToCopy.Count;

        if (filesToCopy.Count == 0)
        {
            result.Errors.Add("No media files found to copy");
            return result;
        }

        var totalBytes = filesToCopy.Sum(f => new FileInfo(f).Length);
        const long marginBytes = 100L * 1024 * 1024; // 100 MB margin
        if (!_skipSpaceCheck)
        {
            var freeSpace = GetAvailableFreeSpaceForPath(_destinationRoot);
            if (freeSpace.HasValue && freeSpace.Value < totalBytes + marginBytes)
            {
                var msg = $"Not enough space on destination. Need {FormatBytes(totalBytes + marginBytes)}, available {FormatBytes(freeSpace.Value)}. Free some space and try again.";
                if (_dryRun)
                    result.Errors.Add(msg);
                else
                {
                    result.Errors.Add(msg);
                    return result;
                }
            }
        }

        if (_dryRun)
            return DryRunCopyFiles(detection, filesToCopy, totalBytes, result);

        long bytesCopiedSoFar = 0;
        var fileIndex = 0;
        var copyStartTick = WindowsPerformanceTimer.GetTimestamp();
        long lastBytesForCurrentRate = 0;
        long lastRateTick = copyStartTick;

        foreach (var sourceFile in filesToCopy)
        {
            try
            {
                var copied = CopyFile(sourceFile, detection.DeviceType, detection.GetFolderName(), result, totalBytes, ref bytesCopiedSoFar, ++fileIndex, filesToCopy.Count, copyStartTick, ref lastBytesForCurrentRate, ref lastRateTick);
                if (copied)
                {
                    result.FilesCopied++;
                    result.SuccessfullyCopiedSourcePaths.Add(sourceFile);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error copying {sourceFile}: {ex.Message}");
                // Don't add "Destination ran out of space" - network/Samba paths often misreport, causing false alarms.
            }
        }

        if (_verbose && filesToCopy.Count > 0)
        {
            Console.WriteLine(); // New line after progress bar
        }

        return result;
    }

    /// <summary>
    /// Builds copy preview items using the same routing/duplicate logic as real copy mode.
    /// </summary>
    public List<CopyPreviewItem> BuildCopyPreview(DeviceDetectionResult detection)
    {
        var preview = new List<CopyPreviewItem>();
        if (detection.DeviceType == DeviceType.Unknown)
            return preview;

        var folderName = detection.GetFolderName();
        var filesToCopy = GatherMediaFiles(detection);
        foreach (var sourceFile in filesToCopy)
        {
            var plan = BuildCopyPlan(sourceFile, detection.DeviceType, folderName);
            preview.Add(new CopyPreviewItem
            {
                SourcePath = sourceFile,
                DestFolder = plan.DestFolder,
                DestFile = plan.DestFile,
                WillCopy = plan.Kind != CopyPlanKind.SkipDuplicate
            });
        }

        return preview;
    }

    private CopyResult DryRunCopyFiles(DeviceDetectionResult detection, List<string> filesToCopy, long totalBytes, CopyResult result)
    {
        result.DryRun = true;
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  SAFE MODE — preview only (no copy, no delete)");
        Console.ResetColor();
        Console.WriteLine();

        var folderName = detection.GetFolderName();
        var index = 0;
        foreach (var sourceFile in filesToCopy)
        {
            index++;
            var plan = BuildCopyPlan(sourceFile, detection.DeviceType, folderName);
            var fileInfo = new FileInfo(sourceFile);
            string action;
            switch (plan.Kind)
            {
                case CopyPlanKind.SkipDuplicate:
                    action = "skip (duplicate, same size)";
                    break;
                case CopyPlanKind.CopyUnique:
                    action = "copy (rename — destination exists, different size)";
                    break;
                default:
                    {
                        var canonical = Path.Combine(plan.DestFolder, fileInfo.Name);
                        var overwrite = File.Exists(canonical) && string.Equals(plan.DestFile, canonical, StringComparison.OrdinalIgnoreCase) && _overwriteAll;
                        action = overwrite ? "copy (overwrite existing)" : "copy";
                        break;
                    }
            }

            var deleteNote = plan.Kind == CopyPlanKind.SkipDuplicate
                ? (_deleteSkipped ? "would delete source (+ companions if present)" : "no")
                : "after copy: optional prompt to delete from source (not automatic)";

            Console.WriteLine($"  [{index}/{filesToCopy.Count}] {fileInfo.Name}");
            Console.WriteLine($"      Source:      {sourceFile}");
            Console.WriteLine($"      Action:      {action}");
            Console.WriteLine($"      Destination: {plan.DestFile}");
            Console.WriteLine($"      Date folder: {plan.FileDate:yyyy-MM-dd} ({plan.DateSource})");
            Console.WriteLine($"      Device:      {plan.FolderName}");
            Console.WriteLine($"      Delete:      {deleteNote}");
            Console.WriteLine();

            if (plan.Kind == CopyPlanKind.SkipDuplicate)
                result.FilesSkipped++;
            else
            {
                result.FilesCopied++;
                result.BytesCopied += fileInfo.Length;
            }
        }

        if (result.Errors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (var e in result.Errors)
                Console.WriteLine($"  Warning: {e}");
            Console.ResetColor();
        }

        return result;
    }

    private List<string> GatherMediaFiles(DeviceDetectionResult detection)
    {
        var files = new HashSet<string>(detection.MediaFiles, StringComparer.OrdinalIgnoreCase);
        
        // Also scan for additional media files in standard locations
        var extensions = new[] { ".mp4", ".mov", ".avi", ".jpg", ".jpeg", ".dng", ".lrv", ".lrf", ".lfd", ".thm", ".srt", ".png", ".m4v", ".mkv" };
        
        // Check DCIM folder recursively
        var dcimPath = Path.Combine(detection.RootPath, "DCIM");
        if (Directory.Exists(dcimPath))
        {
            try
            {
                foreach (var file in Directory.GetFiles(dcimPath, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                    {
                        files.Add(file);
                    }
                }
            }
            catch
            {
                // Ignore access errors
            }
        }

        // For Generic/Unknown, scan all common media locations
        if (detection.DeviceType == DeviceType.Generic)
        {
            try
            {
                foreach (var file in Directory.GetFiles(detection.RootPath, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                    {
                        files.Add(file);
                    }
                }
                var privatePath = Path.Combine(detection.RootPath, "PRIVATE");
                if (Directory.Exists(privatePath))
                {
                    foreach (var file in Directory.GetFiles(privatePath, "*.*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (extensions.Contains(ext))
                        {
                            files.Add(file);
                        }
                    }
                }
                var videoPath = Path.Combine(detection.RootPath, "VIDEO");
                if (Directory.Exists(videoPath))
                {
                    foreach (var file in Directory.GetFiles(videoPath, "*.*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (extensions.Contains(ext))
                        {
                            files.Add(file);
                        }
                    }
                }
            }
            catch
            {
                // Ignore access errors
            }
        }

        // For DJI Flip, also check HYPERLAPSE and PANORAMA folders
        if (detection.DeviceType == DeviceType.DJIFlip)
        {
            try
            {
                foreach (var folderName in new[] { "HYPERLAPSE", "PANORAMA" })
                {
                    var folderPath = Path.Combine(detection.RootPath, folderName);
                    if (Directory.Exists(folderPath))
                    {
                        foreach (var file in Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories))
                        {
                            var ext = Path.GetExtension(file).ToLowerInvariant();
                            if (extensions.Contains(ext))
                            {
                                files.Add(file);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore access errors
            }
        }

        // For SkyZone, also check root and VIDEO folder
        if (detection.DeviceType == DeviceType.SkyZoneAnalog)
        {
            try
            {
                foreach (var file in Directory.GetFiles(detection.RootPath, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                    {
                        files.Add(file);
                    }
                }
                
                var videoPath = Path.Combine(detection.RootPath, "VIDEO");
                if (Directory.Exists(videoPath))
                {
                    foreach (var file in Directory.GetFiles(videoPath, "*.*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (extensions.Contains(ext))
                        {
                            files.Add(file);
                        }
                    }
                }
            }
            catch
            {
                // Ignore access errors
            }
        }

        // Add companion files: for each file, include any same-dir same-base-name with supporting extensions
        var companionExtensions = new[] { ".lrv", ".lrf", ".lfd", ".thm", ".srt" };
        var toAdd = new List<string>();
        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file);
            var baseName = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var ext in companionExtensions)
            {
                var companion = Path.Combine(dir, baseName + ext);
                if (File.Exists(companion) && !files.Contains(companion))
                    toAdd.Add(companion);
            }
        }
        foreach (var f in toAdd) files.Add(f);

        return files.ToList();
    }

    private CopyPlan BuildCopyPlan(string sourceFile, DeviceType deviceType, string folderName)
    {
        var fileInfo = new FileInfo(sourceFile);
        var (fileDate, dateSource) = GetFileDateWithSource(fileInfo, deviceType);
        var destFolder = Path.Combine(
            _destinationRoot,
            fileDate.Year.ToString(),
            fileDate.ToString("MMM"),
            fileDate.Day.ToString("D2"),
            folderName
        );

        var destFile = Path.Combine(destFolder, fileInfo.Name);
        CopyPlanKind kind;

        if (File.Exists(destFile))
        {
            var existingInfo = new FileInfo(destFile);
            if (existingInfo.Length == fileInfo.Length && !_overwriteAll)
                kind = CopyPlanKind.SkipDuplicate;
            else if (!_overwriteAll)
            {
                destFile = GetUniqueFilePath(destFile);
                kind = CopyPlanKind.CopyUnique;
            }
            else
                kind = CopyPlanKind.Copy;
        }
        else
            kind = CopyPlanKind.Copy;

        return new CopyPlan
        {
            SourcePath = sourceFile,
            DeviceType = deviceType,
            FolderName = folderName,
            FileDate = fileDate,
            DateSource = dateSource,
            DestFolder = destFolder,
            DestFile = destFile,
            Kind = kind
        };
    }

    private bool CopyFile(string sourceFile, DeviceType deviceType, string folderName, CopyResult result, long totalBytes, ref long bytesCopiedSoFar, int fileIndex, int totalFiles, long copyStartTick, ref long lastBytesForCurrentRate, ref long lastRateTick)
    {
        var fileInfo = new FileInfo(sourceFile);
        var plan = BuildCopyPlan(sourceFile, deviceType, folderName);
        Directory.CreateDirectory(plan.DestFolder);
        var destFile = plan.DestFile;

        if (plan.Kind == CopyPlanKind.SkipDuplicate)
        {
            bytesCopiedSoFar += fileInfo.Length;
            if (_verbose)
            {
                UpdateProgressBar(fileIndex, totalFiles, bytesCopiedSoFar, totalBytes, fileInfo.Name, skipped: true, copyStartTick);
            }
            _onProgress?.Invoke(fileIndex, totalFiles, bytesCopiedSoFar, totalBytes, fileInfo.Name, true);
            _onFileComplete?.Invoke(fileInfo.Name, true);
            result.FilesSkipped++;
            if (_deleteSkipped)
            {
                try
                {
                    DeleteSourceFileAndCompanions(sourceFile);
                    result.FilesSkippedAndDeleted++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to delete skipped file {sourceFile}: {ex.Message}");
                }
            }
            return false;
        }

        // Copy the file
        if (_verbose)
        {
            UpdateProgressBar(fileIndex, totalFiles, bytesCopiedSoFar, totalBytes, fileInfo.Name, skipped: false, copyStartTick, lastBytesForCurrentRate, lastRateTick);
        }
        _onProgress?.Invoke(fileIndex, totalFiles, bytesCopiedSoFar, totalBytes, fileInfo.Name, false);

        var prevBytes = lastBytesForCurrentRate;
        var prevTick = lastRateTick;
        bool copySucceeded;
        if (OperatingSystem.IsWindows() && _verbose)
        {
            copySucceeded = CopyFileWithProgressEx(sourceFile, destFile, bytesCopiedSoFar, fileIndex, totalFiles, totalBytes, fileInfo.Name, copyStartTick);
        }
        else
        {
            File.Copy(sourceFile, destFile, overwrite: _overwriteAll);
            copySucceeded = true;
        }
        if (copySucceeded)
        {
            result.BytesCopied += fileInfo.Length;
            bytesCopiedSoFar += fileInfo.Length;
            lastBytesForCurrentRate = bytesCopiedSoFar;
            lastRateTick = WindowsPerformanceTimer.GetTimestamp();
        }

        if (_verbose)
        {
            UpdateProgressBar(fileIndex, totalFiles, bytesCopiedSoFar, totalBytes, fileInfo.Name, skipped: false, copyStartTick, prevBytes, prevTick);
        }
        _onProgress?.Invoke(fileIndex, totalFiles, bytesCopiedSoFar, totalBytes, fileInfo.Name, false);

        // Preserve file timestamps
        File.SetCreationTime(destFile, fileInfo.CreationTime);
        File.SetLastWriteTime(destFile, fileInfo.LastWriteTime);

        _onFileComplete?.Invoke(fileInfo.Name, false);
        return true;
    }

    private (DateTime Date, string Source) GetFileDateWithSource(FileInfo fileInfo, DeviceType deviceType)
    {
        if (deviceType == DeviceType.SkyZoneAnalog && _skyZoneLogDate.HasValue)
            return (_skyZoneLogDate.Value, "SkyZone log date");

        // DJI Goggles 3 (and similar) use short names like DJI_0001.MOV — no date in the name; see video metadata below.
        if (TryParseDateFromFileName(fileInfo.Name, out var parsedDate))
            return (parsedDate, "filename");

        if (VideoMetadataReader.GetCreationDate(fileInfo.FullName) is { } metaDate)
            return (metaDate, "video metadata");

        var fileSystemDate = fileInfo.CreationTime;
        if (fileSystemDate.Year >= 2000 && fileSystemDate.Year <= 2100)
            return (fileSystemDate, "file creation time");

        var preset = _defaultDate ?? DateTime.Now;
        return (preset, _defaultDate.HasValue ? "preset (--date)" : "default (now)");
    }

    /// <summary>
    /// Tries to extract YYYYMMDD from filenames when the first 8 characters after "DJI_" are a calendar date.
    /// DJI Goggles 3 files are typically <c>DJI_0001.MOV</c> (sequence only) — this does not match; use <see cref="VideoMetadataReader"/> instead.
    /// Some other DJI devices use long names like <c>DJI_20260320202017_0067_D.MP4</c> where YYYYMMDD is embedded.
    /// </summary>
    private static bool TryParseDateFromFileName(string fileName, out DateTime date)
    {
        date = default;
        if (string.IsNullOrEmpty(fileName)) return false;

        // Long-form DJI name: DJI_20260320... -> parse yyyyMMdd at position 4 (not DJI_0001.MOV)
        if (fileName.StartsWith("DJI_", StringComparison.OrdinalIgnoreCase) && fileName.Length >= 12)
        {
            if (DateTime.TryParseExact(fileName.Substring(4, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out date)
                && date.Year > 2000 && date.Year < 2100)
                return true;
        }

        return false;
    }

    private static void UpdateProgressBar(int fileIndex, int totalFiles, long bytesCopied, long totalBytes, string fileName, bool skipped, long copyStartTick, long lastBytesForCurrentRate = 0, long lastRateTick = 0)
    {
        var pct = totalBytes > 0 ? (int)(bytesCopied * 100 / totalBytes) : 0;
        var barWidth = 25;
        var filled = totalBytes > 0 ? (int)(barWidth * bytesCopied / totalBytes) : 0;
        var bar = new string('=', filled) + new string(' ', barWidth - filled);
        var status = skipped ? " (skip)" : "";
        var displayName = fileName.Length > 40 ? fileName[..37] + "..." : fileName;

        var nowTick = WindowsPerformanceTimer.GetTimestamp();
        var elapsedTotal = WindowsPerformanceTimer.ElapsedSeconds(copyStartTick, nowTick);
        var meanRate = elapsedTotal > 0.1 ? bytesCopied / elapsedTotal : 0;
        double currentRate = 0;
        var elapsedSinceLast = 0.0;
        if (lastRateTick != 0)
        {
            elapsedSinceLast = WindowsPerformanceTimer.ElapsedSeconds(lastRateTick, nowTick);
            var bytesSinceLast = bytesCopied - lastBytesForCurrentRate;
            if (elapsedSinceLast > 0.01 && bytesSinceLast >= 0)
                currentRate = bytesSinceLast / elapsedSinceLast;
        }
        // Prefer current rate when we have a recent sample (>= 0.5s) so the display
        // reflects actual transfer speed and isn't dragged down by the long-term average.
        var effectiveRate = (currentRate > 0 && elapsedSinceLast >= 0.5) ? currentRate : meanRate;
        var speedStr = effectiveRate > 0 ? $" {FormatSpeed(effectiveRate)}" : "";

        var etaStr = "";
        if (!skipped && totalBytes > 0 && bytesCopied < totalBytes && effectiveRate > 1024)
        {
            var remainingBytes = totalBytes - bytesCopied;
            var etaSeconds = (int)(remainingBytes / effectiveRate);
            var timeSpanRemaining = TimeSpan.FromSeconds(etaSeconds);
            var completionTime = DateTime.Now + timeSpanRemaining;
            etaStr = $" ETA {FormatTime(etaSeconds)} ({completionTime:h:mm}{completionTime.ToString("tt").ToLowerInvariant()})";
        }

        var output = $"  [{bar}] {pct,3}% ({fileIndex}/{totalFiles}) {displayName}{status}{speedStr}{etaStr}";
        var clearWidth = Math.Max(120, GetConsoleWidth());

        Console.Write("\r");
        Console.ForegroundColor = skipped ? ConsoleColor.Yellow : ConsoleColor.Green;
        Console.Write(output);
        Console.ResetColor();
        var padding = Math.Max(0, clearWidth - output.Length);
        if (padding > 0) Console.Write(new string(' ', padding));
    }

    private static string FormatTime(int totalSeconds)
    {
        if (totalSeconds < 0) return "0:00";
        var mins = totalSeconds / 60;
        var secs = totalSeconds % 60;
        return $"{mins}:{secs:D2}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:F0} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Console.WindowWidth > 0 ? Console.WindowWidth : 80;
        }
        catch
        {
            return 80;
        }
    }

    /// <summary>Returns available free space in bytes for the path's volume, or null if unknown.</summary>
    private static long? GetAvailableFreeSpaceForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var fullPath = Path.GetFullPath(path);
                // GetDiskFreeSpaceExW works with UNC paths (\\server\share); DriveInfo does not
                if (GetDiskFreeSpaceExW(fullPath, out var freeBytes, out _, out _))
                    return (long)freeBytes;
                var root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrEmpty(root) || fullPath.StartsWith("\\\\")) return null;
                try
                {
                    var drive = new DriveInfo(root);
                    if (!drive.IsReady) return null;
                    return drive.AvailableFreeSpace;
                }
                catch { return null; }
            }
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // Windows UNC (\\server\share) can resolve to wrong volume via GetFullPath; normalize for Samba
                var dfPath = path.Replace('\\', '/');
                if (dfPath.StartsWith("//"))
                {
                    // Network path: must exist or df reports wrong filesystem (e.g. root)
                    if (!Directory.Exists(dfPath))
                        return null;
                }
                else
                {
                    dfPath = Path.GetFullPath(path);
                    // If path doesn't exist, df may report wrong volume (e.g. Pi root); skip
                    if (!Directory.Exists(dfPath))
                        return null;
                }
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "df",
                        ArgumentList = { "-k", dfPath },
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(2000);
                if (proc.ExitCode != 0) return null;
                var lines = output.Split('\n');
                if (lines.Length < 2) return null;
                var cols = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Header: Filesystem 1K-blocks Used Available Use% Mounted
                if (cols.Length < 4) return null;
                if (long.TryParse(cols[3], out var availK))
                    return availK * 1024;
                return null;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static bool IsOutOfSpaceError(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("ENOSPC", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not enough space", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deletes the file and any companion .LRV/.LRF/.LFD files (same base name in same directory).</summary>
    public static void DeleteSourceFileAndCompanions(string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        var dir = Path.GetDirectoryName(path);
        var baseName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(dir)) return;
        foreach (var ext in new[] { ".lrv", ".lrf", ".lfd" })
        {
            var companion = Path.Combine(dir, baseName + ext);
            try { if (File.Exists(companion)) File.Delete(companion); } catch { /* best effort */ }
        }
    }

    private static bool CopyFileWithProgressEx(string sourceFile, string destFile, long bytesBeforeThisFile, int fileIndex, int totalFiles, long totalBytes, string fileName, long copyStartTick)
    {
        var ctx = new CopyProgressContext
        {
            BytesBeforeThisFile = bytesBeforeThisFile,
            FileIndex = fileIndex,
            TotalFiles = totalFiles,
            TotalBytes = totalBytes,
            FileName = fileName,
            CopyStartTick = copyStartTick,
            LastDisplayBytes = bytesBeforeThisFile,
            LastDisplayTick = copyStartTick,
            LastProgressUpdateTick = copyStartTick
        };
        var handle = GCHandle.Alloc(ctx);
        try
        {
            var callback = new CopyProgressRoutine(CopyProgressCallback);
            if (!CopyFileEx(sourceFile, destFile, callback, GCHandle.ToIntPtr(handle), IntPtr.Zero, 0))
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CopyFileEx failed: {err}");
            }
            return true;
        }
        finally
        {
            handle.Free();
        }
    }

    private static uint CopyProgressCallback(long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred, uint dwStreamNumber, uint dwCallbackReason, IntPtr hSourceFile, IntPtr hDestinationFile, IntPtr lpData)
    {
        var ctx = (CopyProgressContext)GCHandle.FromIntPtr(lpData).Target!;
        long bytesCopiedSoFar = ctx.BytesBeforeThisFile + totalBytesTransferred;
        long nowTick = WindowsPerformanceTimer.GetTimestamp();
        if (WindowsPerformanceTimer.ElapsedSeconds(ctx.LastProgressUpdateTick, nowTick) >= ProgressUpdateIntervalSeconds)
        {
            UpdateProgressBar(ctx.FileIndex, ctx.TotalFiles, bytesCopiedSoFar, ctx.TotalBytes, ctx.FileName, skipped: false, ctx.CopyStartTick, ctx.LastDisplayBytes, ctx.LastDisplayTick);
            ctx.LastDisplayBytes = bytesCopiedSoFar;
            ctx.LastDisplayTick = nowTick;
            ctx.LastProgressUpdateTick = nowTick;
        }
        return PROGRESS_CONTINUE;
    }

    private string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return filePath;
        }

        var directory = Path.GetDirectoryName(filePath)!;
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);

        int counter = 1;
        string newPath;
        do
        {
            newPath = Path.Combine(directory, $"{fileNameWithoutExt}_{counter}{extension}");
            counter++;
        } while (File.Exists(newPath) && counter < 1000);

        return newPath;
    }
}

/// <summary>
/// Result of the copy operation
/// </summary>
public class CopyResult
{
    /// <summary>True when <c>--safe</c> mode ran a preview with no copy/delete.</summary>
    public bool DryRun { get; set; }

    public DeviceType DeviceType { get; set; }
    public string SourcePath { get; set; } = "";
    public int TotalFiles { get; set; }
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesSkippedAndDeleted { get; set; }
    public long BytesCopied { get; set; }
    public List<string> Errors { get; set; } = new();
    /// <summary>Source file paths that were successfully copied (for optional delete-after-copy).</summary>
    public List<string> SuccessfullyCopiedSourcePaths { get; set; } = new();

    public bool Success => Errors.Count == 0 && FilesCopied > 0;

    /// <summary>Groups errors by type (message after last ": " or full message) and returns (message, count) for display.</summary>
    public IEnumerable<(string Message, int Count)> GetGroupedErrors()
    {
        return Errors
            .GroupBy(e =>
            {
                var idx = e.LastIndexOf(": ", StringComparison.Ordinal);
                return idx >= 0 ? e[(idx + 2)..].Trim() : e;
            })
            .Select(g => (g.Key, g.Count()));
    }

    public string GetFormattedSize()
    {
        if (BytesCopied < 1024)
            return $"{BytesCopied} B";
        if (BytesCopied < 1024 * 1024)
            return $"{BytesCopied / 1024.0:F2} KB";
        if (BytesCopied < 1024 * 1024 * 1024)
            return $"{BytesCopied / (1024.0 * 1024):F2} MB";
        return $"{BytesCopied / (1024.0 * 1024 * 1024):F2} GB";
    }
}
