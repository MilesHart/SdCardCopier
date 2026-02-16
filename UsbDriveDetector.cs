using System.Management;
using System.Runtime.Versioning;

namespace SDCardImporter;

/// <summary>
/// Detects drives connected via USB, including USB SD card adapters.
/// USB SD adapters report InterfaceType='USB' to Windows.
/// </summary>
public static class UsbDriveDetector
{
    /// <summary>
    /// Gets drive paths for USB-connected storage devices (including USB SD card adapters).
    /// Falls back to all removable drives if USB-specific detection fails.
    /// </summary>
    public static IEnumerable<string> GetUsbDrives()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsUsbDrives();
        }
        if (OperatingSystem.IsLinux())
        {
            return GetLinuxUsbDrives();
        }
        if (OperatingSystem.IsMacOS())
        {
            return GetMacUsbDrives();
        }

        return Enumerable.Empty<string>();
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetWindowsUsbDrives()
    {
        try
        {
            var usbDriveLetters = GetWindowsUsbDriveLettersViaWmi();
            if (usbDriveLetters.Count > 0)
            {
                return usbDriveLetters
                    .Select(d => d.TrimEnd('\\') + "\\")
                    .Where(d => IsDriveReady(d));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WMI USB detection failed: {ex.Message}. Falling back to removable drives.");
        }

        // Fallback: all removable drives (USB SD adapters typically report as Removable)
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Removable)
            .Select(d => d.RootDirectory.FullName);
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<string> GetWindowsUsbDriveLettersViaWmi()
    {
        var driveLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_DiskDrive WHERE InterfaceType='USB' OR MediaType='Removable Media'");

        foreach (var drive in searcher.Get().Cast<ManagementObject>())
        {
            try
            {
                foreach (var partition in drive.GetRelated("Win32_DiskPartition").Cast<ManagementObject>())
                {
                    foreach (var disk in partition.GetRelated("Win32_LogicalDisk").Cast<ManagementObject>())
                    {
                        var name = disk["Name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            driveLetters.Add(name.TrimEnd(':') + ":\\");
                        }
                    }
                }
            }
            catch
            {
                // Skip drives that fail to enumerate
            }
        }

        return driveLetters;
    }

    private static IEnumerable<string> GetLinuxUsbDrives()
    {
        var drives = new List<string>();

        // Get mounted filesystems from /proc/mounts
        try
        {
            var mounts = File.ReadAllLines("/proc/mounts");
            var mountPoints = new List<(string MountPoint, string Device)>();

            foreach (var line in mounts)
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2)
                {
                    var device = parts[0];
                    var mountPoint = parts[1];

                    // Skip non-block devices and virtual filesystems
                    if (!device.StartsWith("/dev/sd") && !device.StartsWith("/dev/mmcblk"))
                        continue;
                    if (mountPoint.StartsWith("/sys") || mountPoint.StartsWith("/proc") || mountPoint.StartsWith("/dev"))
                        continue;

                    if (IsUsbBlockDevice(device))
                    {
                        mountPoints.Add((mountPoint, device));
                    }
                }
            }

            // Also check standard mount locations for any mounted USB storage
            var searchPaths = new[]
            {
                "/media",
                "/mnt",
                $"/run/media/{Environment.UserName}"
            };

            foreach (var basePath in searchPaths)
            {
                if (!Directory.Exists(basePath)) continue;

                try
                {
                    foreach (var dir in Directory.GetDirectories(basePath))
                    {
                        if (IsValidMountPoint(dir) && !drives.Contains(dir))
                        {
                            // Prefer USB-filtered list; if empty, include all (some systems don't expose /sys reliably)
                            if (mountPoints.Count == 0 || mountPoints.Any(m => m.MountPoint == dir))
                            {
                                drives.Add(dir);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore access errors
                }
            }

            // If we found USB-specific mounts, use those; otherwise return all valid mount points
            if (mountPoints.Count > 0)
            {
                return mountPoints
                    .Select(m => m.MountPoint)
                    .Where(IsValidMountPoint)
                    .Distinct();
            }
        }
        catch
        {
            // Fall through to non-USB detection
        }

        // Fallback: all removable media mount points (same as original logic)
        return GetLinuxRemovableFallback();
    }

    private static bool IsUsbBlockDevice(string devicePath)
    {
        try
        {
            // /dev/sdb -> sdb, /dev/mmcblk0p1 -> mmcblk0
            var deviceName = Path.GetFileName(devicePath).Replace("p1", "").Replace("p2", "");
            var sysPath = $"/sys/block/{deviceName}/device";

            if (!Directory.Exists(sysPath))
                return true; // Assume USB if we can't determine (e.g. mmcblk)

            // Check if device is on USB bus (subsystem is a symlink to e.g. /sys/bus/usb)
            var subsystemPath = Path.Combine(sysPath, "subsystem");
            if (Directory.Exists(subsystemPath))
            {
                var subsystemInfo = new DirectoryInfo(subsystemPath);
                var target = subsystemInfo.ResolveLinkTarget(false);
                if (target != null && target.Name.Equals("usb", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Alternative: check parent path for "usb"
            var realPath = Path.GetFullPath(sysPath);
            if (realPath.IndexOf("usb", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // mmcblk devices (e.g. built-in SD slot) - include them as they're SD cards
            if (deviceName.StartsWith("mmcblk"))
                return true;

            return false;
        }
        catch
        {
            return true; // Include if we can't determine
        }
    }

    private static IEnumerable<string> GetLinuxRemovableFallback()
    {
        var drives = new List<string>();
        var searchPaths = new[] { "/media", "/mnt", $"/run/media/{Environment.UserName}" };

        foreach (var basePath in searchPaths)
        {
            if (!Directory.Exists(basePath)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(basePath))
                {
                    if (IsValidMountPoint(dir) && !drives.Contains(dir))
                        drives.Add(dir);
                }
            }
            catch { }
        }

        return drives.Distinct();
    }

    private static IEnumerable<string> GetMacUsbDrives()
    {
        var volumes = new List<string>();
        try
        {
            var volumesPath = "/Volumes";
            if (Directory.Exists(volumesPath))
            {
                foreach (var dir in Directory.GetDirectories(volumesPath))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName != "Macintosh HD" && IsValidMountPoint(dir))
                        volumes.Add(dir);
                }
            }
        }
        catch { }

        return volumes;
    }

    private static bool IsValidMountPoint(string path)
    {
        try
        {
            return Directory.Exists(path) &&
                   (Directory.GetFiles(path).Any() || Directory.GetDirectories(path).Any());
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDriveReady(string drivePath)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(drivePath)!);
            return drive.IsReady;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for a drive to become ready (USB devices can take a moment after connection).
    /// </summary>
    public static async Task<bool> WaitForDriveReadyAsync(string drivePath, int maxAttempts = 10, int delayMs = 500)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            if (IsDriveReady(drivePath))
                return true;
            await Task.Delay(delayMs);
        }
        return false;
    }
}
