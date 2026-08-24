using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SDCardImporter;

namespace SDCardImporter.Desktop;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _watchCts;
    private Task? _watchTask;
    private DispatcherTimer? _pollTimer;

    public MainWindow()
    {
        InitializeComponent();
        DestBox.Text = Environment.GetEnvironmentVariable("DESTINATION_PATH")?.Trim();
        if (string.IsNullOrWhiteSpace(DestBox.Text))
            DestBox.Text = ImporterCommands.GetDefaultDestinationPath();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _pollTimer.Tick += (_, _) => RefreshUiFromMonitor();
        _pollTimer.Start();

        Closing += (_, e) =>
        {
            _pollTimer?.Stop();
            StopImporterCore();
        };

        RefreshHostBadgeStatic();
    }

    private void RefreshHostBadgeStatic()
    {
        if (OperatingSystem.IsWindows())
            HostBadge.Text = "This PC: Windows";
        else if (OperatingSystem.IsLinux()
 && (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
 is System.Runtime.InteropServices.Architecture.Arm
                     or System.Runtime.InteropServices.Architecture.Arm64))
            HostBadge.Text = "This PC: Raspberry Pi (Linux ARM)";
        else if (OperatingSystem.IsLinux())
            HostBadge.Text = "This PC: Linux";
        else
            HostBadge.Text = "This PC";
    }

    private async void BrowseClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select footage destination folder",
            AllowMultiple = false
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
            DestBox.Text = path;
    }

    private void StartClick(object? sender, RoutedEventArgs e)
    {
        DestError.IsVisible = false;
        var path = (DestBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(path))
        {
            ShowDestError("Enter a destination folder.");
            return;
        }

        var err = ImporterCommands.ValidateDestinationPath(path);
        if (err != null)
        {
            ShowDestError(err);
            return;
        }

        StopImporterCore();

        ImporterWorkerState.ResetToDefaults();
        ImporterWorkerState.DestinationPath = path;
        ImporterWorkerState.WatchMode = true;
        ImporterWorkerState.AutoConfirm = true;
        ImporterWorkerState.Verbose = true;
        ImporterWorkerState.ProgressUiEnabled = true;
        ImporterWorkerState.UseDesktopDefaultsForPrompts = true;
        ImporterWorkerState.WebEnabled = false;

        if (int.TryParse(Environment.GetEnvironmentVariable("WEB_PORT")?.Trim(), out var webPort)
            && webPort is > 0 and < 65536)
            ImporterWorkerState.WebPort = webPort;

        var hostOs = OperatingSystem.IsWindows() ? "windows" : "linux";
        var hostProfile = OperatingSystem.IsLinux()
                          && (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                              is System.Runtime.InteropServices.Architecture.Arm
                              or System.Runtime.InteropServices.Architecture.Arm64)
            ? "pi"
            : "pc";
        ProgressMonitor.SetHostContext(hostOs, hostProfile, path);
        ProgressMonitor.SetIdle("Watching for SD cards…");

        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        _watchTask = Task.Run(async () =>
        {
            try
            {
                await ImporterCommands.RunWatchModeAsync(token);
            }
            catch (OperationCanceledException)
            {
                /* normal */
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ShowDestError("Importer stopped: " + ex.Message);
                    SetRunningUi(false);
                });
            }
        }, token);

        SetRunningUi(true);
        StatusTitle.Text = "Watching";
        StatusDetail.Text = "Insert a USB SD reader or card. Destination: " + path;
        ProgressBar.IsVisible = false;
    }

    private void StopClick(object? sender, RoutedEventArgs e)
    {
        StopImporterCore();
        SetRunningUi(false);
        ProgressMonitor.SetIdle("Stopped.");
        StatusTitle.Text = "Idle";
        StatusDetail.Text = "Click Start watching to run again.";
        ProgressBar.IsVisible = false;
        ProgressSub.Text = "";
 }

    private void StopImporterCore()
    {
        _watchCts?.Cancel();
        _watchCts = null;
        _watchTask = null;
        ImporterWorkerState.WebServer?.Stop();
        ImporterWorkerState.WebServer = null;
    }

    private void SetRunningUi(bool running)
    {
        StartBtn.IsEnabled = !running;
        StopBtn.IsEnabled = running;
        BrowseBtn.IsEnabled = !running;
        DestBox.IsReadOnly = running;
    }

    private void ShowDestError(string message)
    {
        DestError.Text = message;
        DestError.IsVisible = true;
    }

    private void RefreshUiFromMonitor()
    {
        var snap = ProgressMonitor.GetSnapshot();
        var refUtc = snap.LastUpdate;
        var ageSec = refUtc == null
            ? -1
            : (int)Math.Floor((DateTime.UtcNow - refUtc.Value).TotalSeconds);

        Dispatcher.UIThread.Post(() =>
        {
            if (_watchTask is { IsCompleted: true } && StopBtn.IsEnabled)
            {
                SetRunningUi(false);
                _watchTask = null;
                StatusTitle.Text = "Idle";
                StatusDetail.Text = "Watch stopped.";
                PulseText.Text = "";
                return;
            }

            if (!StopBtn.IsEnabled)
            {
                PulseText.Text = "";
                return;
            }

            var st = string.IsNullOrEmpty(snap.Status) ? "idle" : snap.Status.ToLowerInvariant();
            StatusTitle.Text = st.Length > 0 ? char.ToUpperInvariant(st[0]) + st[1..] : "Idle";

            if (st == "copying")
            {
                ProgressBar.IsVisible = true;
                ProgressBar.Value = snap.Percent;
                StatusDetail.Text = $"{snap.DeviceType} — {snap.SourcePath}";
                var spd = snap.BytesPerSecond is { } bps
                    ? $" @ {FormatSpeed(bps)}"
                    : "";
                ProgressSub.Text =
                    $"{snap.FileIndex}/{snap.TotalFiles} files — {FormatBytes(snap.BytesCopied)} / {FormatBytes(snap.TotalBytes)}{spd}";
                if (!string.IsNullOrEmpty(snap.CurrentFile))
                    ProgressSub.Text += $" — {snap.CurrentFile}";
            }
            else if (st == "detecting")
            {
                ProgressBar.IsVisible = false;
                StatusDetail.Text = snap.SourcePath ?? "Scanning…";
                ProgressSub.Text = "";
            }
            else
            {
                ProgressBar.IsVisible = false;
                StatusDetail.Text = snap.LastMessage ?? (st == "idle" ? "Waiting for card…" : "");
                ProgressSub.Text = "";
            }

            if (snap.RecentMessages is { Length: > 0 } lines)
                ActivityLog.Text = string.Join(Environment.NewLine, lines.TakeLast(30));
            else
                ActivityLog.Text = "";

            if (ageSec < 0)
                PulseText.Text = "";
            else if (ageSec <= 4)
                PulseText.Text = "● Activity live";
            else if (ageSec <= 20)
                PulseText.Text = $"● Last activity {ageSec}s ago";
            else
                PulseText.Text = $"● Idle {ageSec}s — card may be stuck or copy finished";
        });
    }

    private static string FormatSpeed(double bps)
    {
        if (bps < 1024) return $"{bps:F0} B/s";
        if (bps < 1048576) return $"{bps / 1024:F1} KB/s";
        return $"{bps / 1048576:F1} MB/s";
    }

    private static string FormatBytes(long n)
    {
        if (n < 1024) return $"{n} B";
        if (n < 1048576) return $"{n / 1024.0:F1} KB";
        if (n < 1073741824) return $"{n / (1024.0 * 1024):F1} MB";
        return $"{n / (1024.0 * 1024 * 1024):F2} GB";
    }
}
