using System.Collections.Concurrent;

namespace SDCardImporter;

/// <summary>
/// Thread-safe progress state for the web UI and other consumers.
/// </summary>
public static class ProgressMonitor
{
    private static readonly object _lock = new();
    private static string _status = "idle";
    private static string _deviceType = "";
    private static string _sourcePath = "";
    private static string _currentFile = "";
    private static int _fileIndex;
    private static int _totalFiles;
    private static long _bytesCopied;
    private static long _totalBytes;
    private static double? _bytesPerSecond;
    private static DateTime? _startTime;
    private static DateTime? _lastUpdate;
    private static string _lastMessage = "";
    private static readonly ConcurrentQueue<string> _recentMessages = new();
    private static readonly List<FileResult> _fileResults = new();
    private const int MaxRecentMessages = 20;
    private const int MaxFileResults = 500;
    private static string _hostOs = "";
    private static string _hostProfile = "";
    private static string _destinationPath = "";

    /// <summary>Labels the machine running the importer (for the web UI). Call when the web server starts.</summary>
    public static void SetHostContext(string hostOs, string hostProfile, string destinationPath)
    {
        lock (_lock)
        {
            _hostOs = hostOs;
            _hostProfile = hostProfile;
            _destinationPath = destinationPath ?? "";
        }
    }

    public static void AddFileProcessed(string fileName, bool skipped)
    {
        lock (_lock)
        {
            if (_fileResults.Count >= MaxFileResults) return;
            _fileResults.Add(new FileResult { Name = fileName, Skipped = skipped });
        }
    }

    public static void SetIdle(string? message = null)
    {
        lock (_lock)
        {
            _status = "idle";
            _fileResults.Clear();
            _deviceType = _sourcePath = _currentFile = "";
            _fileIndex = _totalFiles = 0;
            _bytesCopied = _totalBytes = 0;
            _bytesPerSecond = null;
            _startTime = _lastUpdate = null;
            if (!string.IsNullOrEmpty(message))
                AddMessage(message);
        }
    }

    public static void SetDetecting(string drivePath)
    {
        lock (_lock)
        {
            _status = "detecting";
            _sourcePath = drivePath;
            _currentFile = "";
            _deviceType = "";
            _fileIndex = _totalFiles = 0;
            _bytesCopied = _totalBytes = 0;
            _bytesPerSecond = null;
            _startTime = DateTime.UtcNow;
            _lastUpdate = _startTime;
            AddMessage($"Analyzing: {drivePath}");
        }
    }

    public static void SetCopying(string deviceType, string sourcePath, int totalFiles, long totalBytes)
    {
        lock (_lock)
        {
            _status = "copying";
            _fileResults.Clear();
            _deviceType = deviceType;
            _sourcePath = sourcePath;
            _totalFiles = totalFiles;
            _totalBytes = totalBytes;
            _fileIndex = 0;
            _bytesCopied = 0;
            _currentFile = "";
            _bytesPerSecond = null;
            _startTime = DateTime.UtcNow;
            _lastUpdate = _startTime;
            AddMessage($"Copying {totalFiles} files ({FormatBytes(totalBytes)})");
        }
    }

    public static void SetCopyProgress(int fileIndex, int totalFiles, long bytesCopied, long totalBytes, string currentFile, bool skipped, double? bytesPerSecond)
    {
        lock (_lock)
        {
            _fileIndex = fileIndex;
            _totalFiles = totalFiles;
            _bytesCopied = bytesCopied;
            _totalBytes = totalBytes;
            _currentFile = currentFile;
            _bytesPerSecond = bytesPerSecond;
            _lastUpdate = DateTime.UtcNow;
        }
    }

    public static void SetComplete(string deviceType, int filesCopied, long bytesCopied, int filesSkipped, string? error = null)
    {
        lock (_lock)
        {
            _status = "complete";
            _deviceType = deviceType;
            _bytesCopied = bytesCopied;
            _fileIndex = _totalFiles; // treat as done
            _lastUpdate = DateTime.UtcNow;
            var msg = error != null
                ? $"Finished with error: {error}"
                : $"Copied {filesCopied} files ({FormatBytes(bytesCopied)})" + (filesSkipped > 0 ? $", {filesSkipped} skipped" : "");
            AddMessage(msg);
        }
    }

    public static void AddMessage(string message)
    {
        _lastMessage = message;
        _recentMessages.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (_recentMessages.Count > MaxRecentMessages)
            _recentMessages.TryDequeue(out _);
    }

    public static ProgressSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var pct = _totalBytes > 0 && _bytesCopied >= 0
                ? (int)Math.Round(_bytesCopied * 100.0 / _totalBytes)
                : 0;
            return new ProgressSnapshot
            {
                ServerTime = DateTime.UtcNow,
                Status = _status,
                DeviceType = _deviceType,
                SourcePath = _sourcePath,
                CurrentFile = _currentFile,
                FileIndex = _fileIndex,
                TotalFiles = _totalFiles,
                BytesCopied = _bytesCopied,
                TotalBytes = _totalBytes,
                Percent = Math.Min(100, pct),
                BytesPerSecond = _bytesPerSecond,
                StartTime = _startTime,
                LastUpdate = _lastUpdate,
                LastMessage = _lastMessage,
                RecentMessages = _recentMessages.ToArray(),
                FileResults = _fileResults.ToArray(),
                HostOs = _hostOs,
                HostProfile = _hostProfile,
                DestinationPath = _destinationPath
            };
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}

public class FileResult
{
    public string Name { get; set; } = "";
    public bool Skipped { get; set; }
}

public class ProgressSnapshot
{
    public DateTime ServerTime { get; set; }
    public string Status { get; set; } = "idle";
    public string DeviceType { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string CurrentFile { get; set; } = "";
    public int FileIndex { get; set; }
    public int TotalFiles { get; set; }
    public long BytesCopied { get; set; }
    public long TotalBytes { get; set; }
    public int Percent { get; set; }
    public double? BytesPerSecond { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? LastUpdate { get; set; }
    public string LastMessage { get; set; } = "";
    public string[] RecentMessages { get; set; } = Array.Empty<string>();
    public FileResult[] FileResults { get; set; } = Array.Empty<FileResult>();

    /// <summary>windows or linux</summary>
    public string HostOs { get; set; } = "";

    /// <summary>pc or pi (ARM Linux hosts use the Pi-oriented tips)</summary>
    public string HostProfile { get; set; } = "";

    /// <summary>Configured footage destination root</summary>
    public string DestinationPath { get; set; } = "";
}
