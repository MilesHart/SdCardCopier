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

    public FileCopier(string destinationRoot, bool verbose = true, bool overwriteAll = false, DateTime? skyZoneLogDate = null)
    {
        _destinationRoot = destinationRoot;
        _verbose = verbose;
        _overwriteAll = overwriteAll;
        _skyZoneLogDate = skyZoneLogDate;
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
        long bytesCopiedSoFar = 0;
        var fileIndex = 0;
        var copyStartTick = WindowsPerformanceTimer.GetTimestamp();
        long lastBytesForCurrentRate = 0;
        long lastRateTick = copyStartTick;

        foreach (var sourceFile in filesToCopy)
        {
            try
            {
                // For DJI cards, check each file's metadata to route Flip vs O4 Pro correctly
                var deviceTypeForFile = GetDeviceTypeForFile(sourceFile, detection.DeviceType);
                var copied = CopyFile(sourceFile, deviceTypeForFile, result, totalBytes, ref bytesCopiedSoFar, ++fileIndex, filesToCopy.Count, copyStartTick, ref lastBytesForCurrentRate, ref lastRateTick);
                if (copied)
                {
                    result.FilesCopied++;
                    result.SuccessfullyCopiedSourcePaths.Add(sourceFile);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error copying {sourceFile}: {ex.Message}");
            }
        }

        if (_verbose && filesToCopy.Count > 0)
        {
            Console.WriteLine(); // New line after progress bar
        }

        return result;
    }

    private List<string> GatherMediaFiles(DeviceDetectionResult detection)
    {
        var files = new HashSet<string>(detection.MediaFiles, StringComparer.OrdinalIgnoreCase);
        
        // Also scan for additional media files in standard locations
        var extensions = new[] { ".mp4", ".mov", ".avi", ".jpg", ".jpeg", ".dng", ".lrv", ".thm", ".srt", ".png", ".m4v", ".mkv" };
        
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

        return files.ToList();
    }

    /// <summary>
    /// Gets the device type for a specific file. For DJI video files, checks metadata to distinguish Flip vs O4 Pro.
    /// </summary>
    private static DeviceType GetDeviceTypeForFile(string sourceFile, DeviceType cardLevelDevice)
    {
        var ext = Path.GetExtension(sourceFile).ToLowerInvariant();
        if (ext is ".mp4" or ".mov" or ".m4v")
        {
            var fileDevice = DjiMetadataReader.DetectFromFile(sourceFile);
            if (fileDevice == DeviceType.DJIFlip || fileDevice == DeviceType.BetaPavo20Pro)
            {
                return fileDevice;
            }
        }
        return cardLevelDevice;
    }

    private bool CopyFile(string sourceFile, DeviceType deviceType, CopyResult result, long totalBytes, ref long bytesCopiedSoFar, int fileIndex, int totalFiles, long copyStartTick, ref long lastBytesForCurrentRate, ref long lastRateTick)
    {
        var fileInfo = new FileInfo(sourceFile);
        
        // For SkyZone (GoggleSZ), use the user-provided log date when set; otherwise use file date
        var fileDate = (deviceType == DeviceType.SkyZoneAnalog && _skyZoneLogDate.HasValue)
            ? _skyZoneLogDate.Value
            : GetFileDate(fileInfo);
        
        // Build destination path: /{year}/{month}/{day}/{DeviceFolder}/ (month as Jan, Feb, etc.)
        var destFolder = Path.Combine(
            _destinationRoot,
            fileDate.Year.ToString(),
            fileDate.ToString("MMM"),
            fileDate.Day.ToString("D2"),
            deviceType.GetFolderName()
        );

        // Ensure destination folder exists
        Directory.CreateDirectory(destFolder);

        var destFile = Path.Combine(destFolder, fileInfo.Name);

        // Check if file already exists: skip (unless overwrite), or get unique name for different-sized file
        if (File.Exists(destFile))
        {
            var existingInfo = new FileInfo(destFile);
            if (existingInfo.Length == fileInfo.Length && !_overwriteAll)
            {
                bytesCopiedSoFar += fileInfo.Length;
                if (_verbose)
                {
                    UpdateProgressBar(fileIndex, totalFiles, bytesCopiedSoFar, totalBytes, fileInfo.Name, skipped: true, copyStartTick);
                }
                result.FilesSkipped++;
                return false;
            }
            if (!_overwriteAll)
            {
                destFile = GetUniqueFilePath(destFile);
            }
        }

        // Copy the file
        if (_verbose)
        {
            UpdateProgressBar(fileIndex, totalFiles, bytesCopiedSoFar, totalBytes, fileInfo.Name, skipped: false, copyStartTick, lastBytesForCurrentRate, lastRateTick);
        }

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
        
        // Preserve file timestamps
        File.SetCreationTime(destFile, fileInfo.CreationTime);
        File.SetLastWriteTime(destFile, fileInfo.LastWriteTime);

        return true;
    }

    private DateTime GetFileDate(FileInfo fileInfo)
    {
        // Try to get the most accurate date from the file
        // Priority: Creation time, then Last Write time
        var dates = new[]
        {
            fileInfo.CreationTime,
            fileInfo.LastWriteTime
        };

        // Return the earliest reasonable date (not year 1601 which is Windows default for missing timestamps)
        return dates
            .Where(d => d.Year > 2000)
            .OrderBy(d => d)
            .FirstOrDefault(DateTime.Now);
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
    public DeviceType DeviceType { get; set; }
    public string SourcePath { get; set; } = "";
    public int TotalFiles { get; set; }
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public long BytesCopied { get; set; }
    public List<string> Errors { get; set; } = new();
    /// <summary>Source file paths that were successfully copied (for optional delete-after-copy).</summary>
    public List<string> SuccessfullyCopiedSourcePaths { get; set; } = new();

    public bool Success => Errors.Count == 0 && FilesCopied > 0;
    
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
