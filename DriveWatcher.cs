namespace SDCardImporter;

/// <summary>
/// Cross-platform drive watcher that detects when new removable drives are connected
/// </summary>
public class DriveWatcher : IDisposable
{
    private readonly HashSet<string> _knownDrives = new();
    private readonly Timer _pollTimer;
    private readonly int _pollIntervalMs;
    private bool _disposed;

    public event EventHandler<DriveConnectedEventArgs>? DriveConnected;
    public event EventHandler<DriveDisconnectedEventArgs>? DriveDisconnected;

    public DriveWatcher(int pollIntervalMs = 2000)
    {
        _pollIntervalMs = pollIntervalMs;
        
        // Initialize with currently connected drives
        foreach (var drive in GetRemovableDrives())
        {
            _knownDrives.Add(drive);
        }

        _pollTimer = new Timer(CheckForDriveChanges, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _pollTimer.Change(0, _pollIntervalMs);
        Console.WriteLine("Drive watcher started. Waiting for SD cards...");
    }

    public void Stop()
    {
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void CheckForDriveChanges(object? state)
    {
        try
        {
            var currentDrives = GetRemovableDrives().ToHashSet();

            // Check for new drives
            foreach (var drive in currentDrives)
            {
                if (!_knownDrives.Contains(drive))
                {
                    _knownDrives.Add(drive);
                    OnDriveConnected(drive);
                }
            }

            // Check for removed drives
            var removedDrives = _knownDrives.Where(d => !currentDrives.Contains(d)).ToList();
            foreach (var drive in removedDrives)
            {
                _knownDrives.Remove(drive);
                OnDriveDisconnected(drive);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking drives: {ex.Message}");
        }
    }

    private IEnumerable<string> GetRemovableDrives()
    {
        // Use USB-aware detection (includes USB SD card adapters)
        return UsbDriveDetector.GetUsbDrives();
    }

    private void OnDriveConnected(string drivePath)
    {
        DriveConnected?.Invoke(this, new DriveConnectedEventArgs(drivePath));
    }

    private void OnDriveDisconnected(string drivePath)
    {
        DriveDisconnected?.Invoke(this, new DriveDisconnectedEventArgs(drivePath));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _pollTimer.Dispose();
    }
}

public class DriveConnectedEventArgs : EventArgs
{
    public string DrivePath { get; }

    public DriveConnectedEventArgs(string drivePath)
    {
        DrivePath = drivePath;
    }
}

public class DriveDisconnectedEventArgs : EventArgs
{
    public string DrivePath { get; }

    public DriveDisconnectedEventArgs(string drivePath)
    {
        DrivePath = drivePath;
    }
}
