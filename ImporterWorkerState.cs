namespace SDCardImporter;

/// <summary>
/// Shared importer configuration for console and desktop hosts. Thread access: main / importer thread only.
/// </summary>
public static class ImporterWorkerState
{
    public static string DestinationPath { get; set; } = "";
    public static bool WatchMode { get; set; }
    public static bool CardIdentifyOnly { get; set; }
    public static bool Verbose { get; set; } = true;
    public static bool AutoConfirm { get; set; }
    public static bool OverwriteAll { get; set; }
    public static bool DeleteSkipped { get; set; }
    public static bool SkipSpaceCheck { get; set; } = true;
    public static DateTime? DefaultDate { get; set; }
    public static bool SafeMode { get; set; }
    public static bool WebEnabled { get; set; }
    /// <summary>Update <see cref="ProgressMonitor"/> without starting the HTTP server (desktop UI).</summary>
    public static bool ProgressUiEnabled { get; set; }

    /// <summary>Avoid <see cref="Console"/> prompts (SkyZone date, delete-after-copy); use safe defaults for GUI hosts.</summary>
    public static bool UseDesktopDefaultsForPrompts { get; set; }
    public static int WebPort { get; set; } = 5050;
    public static WebServer? WebServer { get; set; }

    public static bool UiProgress => WebEnabled || ProgressUiEnabled;

    public static void ResetToDefaults()
    {
        DestinationPath = "";
        WatchMode = false;
        CardIdentifyOnly = false;
        Verbose = true;
        AutoConfirm = false;
        OverwriteAll = false;
        DeleteSkipped = false;
        SkipSpaceCheck = true;
        DefaultDate = null;
        SafeMode = false;
        WebEnabled = false;
        ProgressUiEnabled = false;
        UseDesktopDefaultsForPrompts = false;
        WebPort = 5050;
        WebServer = null;
    }
}
