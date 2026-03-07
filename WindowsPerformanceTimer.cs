using System.Runtime.InteropServices;

namespace SDCardImporter;

/// <summary>
/// High-resolution timer using Windows QueryPerformanceCounter API when available;
/// falls back to DateTime.UtcNow.Ticks on other platforms.
/// </summary>
public static class WindowsPerformanceTimer
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);

    private static readonly bool UseWindowsApi = OperatingSystem.IsWindows();
    private static readonly long Frequency = GetFrequencyCore();

    private static long GetFrequencyCore()
    {
        if (UseWindowsApi && QueryPerformanceFrequency(out long freq))
            return freq;
        return TimeSpan.TicksPerSecond;
    }

    /// <summary>Gets current timestamp (QPC counter on Windows, else DateTime.UtcNow.Ticks).</summary>
    public static long GetTimestamp()
    {
        if (UseWindowsApi && QueryPerformanceCounter(out long count))
            return count;
        return DateTime.UtcNow.Ticks;
    }

    /// <summary>Elapsed seconds between two timestamps.</summary>
    public static double ElapsedSeconds(long startTick, long endTick)
    {
        if (Frequency <= 0) return 0;
        return (endTick - startTick) / (double)Frequency;
    }
}
