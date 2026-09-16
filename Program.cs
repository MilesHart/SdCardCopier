using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using SDCardImporter;

class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            return await MainCore(args);
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            return 1;
        }
    }

    static async Task<int> MainCore(string[] args)
    {
        EnvLoader.Load();
        if (int.TryParse(Environment.GetEnvironmentVariable("WEB_PORT")?.Trim(), out var webPortEnv) && webPortEnv > 0 && webPortEnv < 65536)
            ImporterWorkerState.WebPort = webPortEnv;

        // Handle --telegram-test (send test message to verify Telegram setup)
        if (args.Any(a => a.Equals("--telegram-test", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Sending Telegram test message...");
            await TelegramNotifier.SendTestAsync();
            Console.WriteLine("Done. Check Telegram for the test message.");
            return 0;
        }

        // Handle --check-file before normal parsing (standalone diagnostic)
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--check-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                CheckFileMetadata(args[i + 1]);
                return 0;
            }
        }
        // Handle --telegram-get-chat-id (fetch chat ID from bot updates)
        if (args.Any(a => a.Equals("--telegram-get-chat-id", StringComparison.OrdinalIgnoreCase)))
        {
            return await TelegramGetChatIdAsync();
        }

        // Handle --web-check (test web server in isolation)
        if (args.Any(a => a.Equals("--web-check", StringComparison.OrdinalIgnoreCase)))
        {
            var server = new WebServer(ImporterWorkerState.WebPort);
            if (!server.Start())
            {
                Console.WriteLine("Web server failed to start.");
                return 1;
            }
            await Task.Delay(500);
            try
            {
                using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var r = await c.GetAsync($"http://127.0.0.1:{ImporterWorkerState.WebPort}/");
                Console.WriteLine(r.IsSuccessStatusCode ? "Web server OK – responding at http://localhost:" + ImporterWorkerState.WebPort + "/" : "Web server returned " + (int)r.StatusCode);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Web server test failed: " + ex.Message);
                server.Stop();
                return 1;
            }
            server.Stop();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("===========================================");
        Console.WriteLine("  SD Card Importer for FPV/Action Cameras  ");
        Console.WriteLine("===========================================");
        Console.ResetColor();
        Console.WriteLine();

        // If no args, use suggested defaults: watch mode, auto-confirm, web UI
        if (args.Length == 0)
        {
            ImporterWorkerState.WatchMode = true;
            ImporterWorkerState.AutoConfirm = true;
            ImporterWorkerState.WebEnabled = true;
        }
        if (!ParseArguments(args))
        {
            ShowHelp();
            return 1;
        }

        if (string.IsNullOrEmpty(ImporterWorkerState.DestinationPath))
        {
            ImporterWorkerState.DestinationPath = Environment.GetEnvironmentVariable("DESTINATION_PATH")?.Trim() ?? GetDefaultDestinationPath();
        }

        var destError = ValidateDestinationPath(ImporterWorkerState.DestinationPath);
        if (destError != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Destination path error: {destError}");
            if (OperatingSystem.IsLinux())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  Tip: Create the directory and give your user access, e.g.:");
                Console.WriteLine("       sudo mkdir -p /mnt/footage && sudo chown $USER:$USER /mnt/footage");
                Console.WriteLine("  Or use a path you can write to (e.g. /home/pi/footage).");
                Console.ResetColor();
            }
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"Destination: {ImporterWorkerState.DestinationPath}");
        var telegramChatId = TelegramNotifier.GetEffectiveChatId();
        Console.WriteLine(string.IsNullOrEmpty(telegramChatId)
            ? "Telegram: notifications disabled (TELEGRAM_CHAT_ID not set; use --telegram-chat-id <id> to enable)"
            : $"Telegram: notifications enabled (chat ID: {telegramChatId})");
        Console.WriteLine($"Supported devices:");
        Console.WriteLine($"  - DJI Goggles 3 -> {DeviceType.DJIGoggles3.GetFolderName()}");
        Console.WriteLine($"  - DJI Flip -> {DeviceType.DJIFlip.GetFolderName()}");
        Console.WriteLine($"  - SkyZone Analog FPV Goggles -> {DeviceType.SkyZoneAnalog.GetFolderName()}");
        Console.WriteLine($"  - BetaPavo20 Pro (DJI O4 Pro) -> {DeviceType.BetaPavo20Pro.GetFolderName()}");
        Console.WriteLine($"  - GoPro (Hero family, incl. Session 5) -> {DeviceType.GoPro13.GetFolderName()}");
        Console.WriteLine($"  - Generic/Other (unrecognized cards) -> {DeviceType.Generic.GetFolderName()}");
        Console.WriteLine();

        if (ImporterWorkerState.UiProgress)
        {
            var hostOs = OperatingSystem.IsWindows() ? "windows" : "linux";
            var hostProfile = OperatingSystem.IsLinux()
                && (RuntimeInformation.ProcessArchitecture == Architecture.Arm
                    || RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                ? "pi"
                : "pc";
            ProgressMonitor.SetHostContext(hostOs, hostProfile, ImporterWorkerState.DestinationPath);
            ProgressMonitor.SetIdle();
        }

        if (ImporterWorkerState.WebEnabled)
        {
            ImporterWorkerState.WebServer = new WebServer(ImporterWorkerState.WebPort);
            if (ImporterWorkerState.WebServer.Start())
            {
                ImporterWorkerState.WebPort = ImporterWorkerState.WebServer.BoundPort;
                var monitorUrl = GetWebMonitorUrl();
                if (string.IsNullOrEmpty(monitorUrl))
                    Console.WriteLine("  Web: Monitor URL not detected for Telegram. Add WEB_URL=http://<Pi-IP>:" + ImporterWorkerState.WebPort + "/ to .env");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500);
                        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                        var r = await c.GetAsync($"http://127.0.0.1:{ImporterWorkerState.WebPort}/");
                        if (r.IsSuccessStatusCode)
                            Console.WriteLine("  Web: Server is responding.");
                        else
                            Console.WriteLine("  Web: Self-check got " + (int)r.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  Web: Self-check failed: " + ex.Message);
                    }
                });
            }
            else
                Console.WriteLine("  Web: Server failed. Check if ports 5050-5052 are free: ss -tlnp | grep -E '505[0-2]'");
        }

        if (ImporterWorkerState.WatchMode)
        {
            return await RunWatchMode();
        }
        else
        {
            // Single scan mode - scan provided path or all removable drives
            return await RunSingleScan(args);
        }
    }

    static bool ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLowerInvariant();

            switch (arg)
            {
                case "-h":
                case "--help":
                case "/?":
                    return false;

                case "-d":
                case "--destination":
                    if (i + 1 < args.Length)
                    {
                        ImporterWorkerState.DestinationPath = args[++i];
                    }
                    else
                    {
                        Console.WriteLine("Error: --destination requires a path argument");
                        return false;
                    }
                    break;

                case "-w":
                case "--watch":
                    ImporterWorkerState.WatchMode = true;
                    break;

                case "-c":
                case "--card-watch":
                    ImporterWorkerState.WatchMode = true;
                    ImporterWorkerState.CardIdentifyOnly = true;
                    break;

                case "-q":
                case "--quiet":
                    ImporterWorkerState.Verbose = false;
                    break;

                case "-y":
                case "--yes":
                    ImporterWorkerState.AutoConfirm = true;
                    break;

                case "--telegram-chat-id":
                    if (i + 1 < args.Length)
                    {
                        TelegramNotifier.ChatIdOverride = args[++i];
                    }
                    else
                    {
                        Console.WriteLine("Error: --telegram-chat-id requires a value (e.g. 7788144113)");
                        return false;
                    }
                    break;

                case "-o":
                case "--overwrite":
                    ImporterWorkerState.OverwriteAll = true;
                    break;

                case "--delete-skipped":
                    ImporterWorkerState.DeleteSkipped = true;
                    break;

                case "--skip-space-check":
                    ImporterWorkerState.SkipSpaceCheck = true;
                    break;

                case "--date":
                    if (i + 1 < args.Length && DateTime.TryParse(args[i + 1], out var parsedDate))
                    {
                        ImporterWorkerState.DefaultDate = parsedDate.Date;
                        i++;
                    }
                    else
                    {
                        Console.WriteLine("Error: --date requires a valid date (e.g. 2026-03-20)");
                        return false;
                    }
                    break;

                case "--safe":
                case "--dry-run":
                    ImporterWorkerState.SafeMode = true;
                    break;

                case "--web":
                    ImporterWorkerState.WebEnabled = true;
                    break;

                case "-p":
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var port) && port > 0 && port < 65536)
                    {
                        ImporterWorkerState.WebPort = port;
                        i++;
                    }
                    break;

                default:
                    // Treat as source path if it looks like a path
                    if (Directory.Exists(args[i]) && string.IsNullOrEmpty(ImporterWorkerState.DestinationPath))
                    {
                        // This might be the source, not destination
                    }
                    break;
            }
        }

        return true;
    }

    static void ShowHelp()
    {
        Console.WriteLine("Usage: SDCardImporter [options]");
        Console.WriteLine("  With no arguments: watch mode (-w), auto-confirm (-y), and web UI (--web) are enabled.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -d, --destination <path>  Set the destination root folder for copied files");
        Console.WriteLine("                            Default: DESTINATION_PATH in .env, or \\\\dazzle.local\\\\root\\\\fpv");
        Console.WriteLine("                            UNC (\\\\server\\\\share), //server/share, or DriveType.Network paths must exist; never auto-created.");
        Console.WriteLine("  -w, --watch               Watch mode: continuously monitor for SD card insertions");
        Console.WriteLine("  -c, --card-watch          Watch removable media and only identify inserted card type (no copy)");
        Console.WriteLine("  -q, --quiet               Quiet mode: minimal output");
        Console.WriteLine("  -y, --yes                 Auto-confirm: don't ask before copying");
        Console.WriteLine("  -o, --overwrite            Overwrite existing files (default: skip if same size)");
        Console.WriteLine("  --delete-skipped           Delete skipped files from source (already copied, same size)");
        Console.WriteLine("  --skip-space-check         Skip destination free-space check (use when detection is wrong)");
        Console.WriteLine("  --date <YYYY-MM-DD>        Default date when filename and video metadata lack date (default: today)");
        Console.WriteLine("  --safe, --dry-run          List copy plan only (no copy, no delete, no Telegram)");
        Console.WriteLine("  --web                      Enable web UI for progress monitoring (default port 5050)");
        Console.WriteLine("  -p, --port <port>          Web server port (with --web)");
        Console.WriteLine("  --check-file <path>        Check DJI metadata in a single file (for debugging)");
        Console.WriteLine("  --telegram-test             Send a test message to verify Telegram is configured");
        Console.WriteLine("  --telegram-get-chat-id     Get your Telegram chat ID (message the bot first, then run this)");
        Console.WriteLine("  --telegram-chat-id <id>    Send notifications to this Telegram chat ID (overrides env var)");
        Console.WriteLine("  -h, --help                Show this help message");
        Console.WriteLine();
        Console.WriteLine("Output structure:");
        Console.WriteLine("  {destination}/{year}/{Jan|Feb|...}/{day}/{DeviceFolder}/");
        Console.WriteLine();
        Console.WriteLine("Device folders:");
        Console.WriteLine("  GoggleDJI  - DJI Goggles 3");
        Console.WriteLine("  GoggleSZ   - SkyZone Analog FPV Goggles");
        Console.WriteLine("  DJI04      - BetaPavo20 Pro (DJI O4 Pro)");
        Console.WriteLine("  GP13       - GoPro (Hero family, incl. Session 5)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  SDCardImporter                          # Scan current removable drives");
        Console.WriteLine("  SDCardImporter -w                       # Watch for SD card insertions");
        Console.WriteLine("  SDCardImporter -c                       # Watch and identify inserted cards (no copy)");
        Console.WriteLine("  SDCardImporter -d /mnt/footage -w -y    # Watch mode with custom destination, auto-copy");
    }

    /// <summary>Report an unhandled exception without calling ToString() (which can throw and cause 'Cannot print exception string' on some runtimes).</summary>
    static void ReportCrash(Exception ex)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "sdcard-importer-crash.txt");
        try
        {
            using var w = new StreamWriter(logPath, append: false);
            w.WriteLine(DateTime.UtcNow.ToString("o"));
            w.WriteLine("Type: " + ex.GetType().FullName);
            try { w.WriteLine("Message: " + ex.Message); } catch { w.WriteLine("Message: [could not get]"); }
            try { w.WriteLine("StackTrace:\n" + ex.StackTrace); } catch { }
            if (ex.InnerException != null)
            {
                w.WriteLine("Inner: " + ex.InnerException.GetType().FullName + " - " + ex.InnerException.Message);
            }
        }
        catch { /* ignore */ }

        try
        {
            Console.Error.WriteLine("SD Card Importer crashed.");
            Console.Error.WriteLine("Exception: " + ex.GetType().Name);
            try { Console.Error.WriteLine("Message: " + ex.Message); } catch { }
            Console.Error.WriteLine("Details written to: " + logPath);
        }
        catch
        {
            // Last resort: nothing we can do
        }
    }

    /// <summary>Windows UNC (\\server\share) or SMB URL style (//server/share) used on Linux — must exist; never create locally.</summary>
    static bool IsNetworkSharePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim();
        if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\')
            return true;
        // Linux/Pi: CIFS paths are often //host/share (not a file:// URL)
        if (path.Length >= 4 && path[0] == '/' && path[1] == '/' && path[2] != '/')
            return path.IndexOf('/', 2) > 2;
        return false;
    }

    /// <summary>
    /// Finds the drive whose root best contains <paramref name="path"/> (longest root wins).
    /// On Windows, UNC and mapped drives often report <see cref="DriveType.Network"/>.
    /// On Linux/Pi, CIFS/NFS may report <see cref="DriveType.Network"/>; some setups still report <see cref="DriveType.Fixed"/> — use UNC or mount paths when in doubt.
    /// </summary>
    static bool TryGetDriveInfoForPath(string path, out DriveInfo? drive)
    {
        drive = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch { return false; }
        if (!Path.IsPathRooted(full)) return false;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        DriveInfo? best = null;
        var bestLen = -1;

        foreach (var d in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                root = d.RootDirectory?.FullName ?? "";
                if (string.IsNullOrEmpty(root)) continue;
                root = Path.GetFullPath(root);
            }
            catch { continue; }

            if (!IsPathUnderDriveRoot(full, root, comparison)) continue;
            if (root.Length > bestLen) { bestLen = root.Length; best = d; }
        }

        drive = best;
        return best != null;
    }

    static bool IsPathUnderDriveRoot(string fullPath, string driveRoot, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(driveRoot)) return false;
        try
        {
            fullPath = Path.GetFullPath(fullPath);
            driveRoot = Path.GetFullPath(driveRoot);
            if (string.Equals(fullPath, driveRoot, comparison)) return true;
            if (fullPath.Length <= driveRoot.Length) return false;
            if (!fullPath.StartsWith(driveRoot, comparison)) return false;
            return fullPath[driveRoot.Length] == Path.DirectorySeparatorChar
                || fullPath[driveRoot.Length] == Path.AltDirectorySeparatorChar;
        }
        catch { return false; }
    }

    /// <summary>True when <see cref="DriveInfo.DriveType"/> is <see cref="DriveType.Network"/> for the path's drive.</summary>
    static bool IsDriveInfoNetworkPath(string path)
    {
        if (!TryGetDriveInfoForPath(path, out var d) || d == null) return false;
        try { return d.DriveType == DriveType.Network; }
        catch { return false; }
    }

    /// <summary>UNC must be \\server\share\... ; //server/share must have host and share segments.</summary>
    static string? ValidateNetworkSharePathShape(string path)
    {
        path = path.Trim().TrimEnd('\\', '/');
        if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\')
        {
            var rest = path.Substring(2);
            var segments = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                return "UNC destination must include a share name (e.g. \\\\dazzle\\\\Footage), not \\\\server alone. If the share is offline, mount it first — do not let the app create a local folder.";
        }
        else if (path.Length >= 4 && path[0] == '/' && path[1] == '/' && path[2] != '/')
        {
            var tail = path.Substring(2);
            var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                return "SMB path must be //server/share (include share name). Mount the share if it is not available.";
        }
        return null;
    }

    /// <summary>Returns null if the path is available and writable; otherwise an error message.</summary>
    internal static string? ValidateDestinationPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Destination path is empty.";
        path = path.Trim();

        var uncOrSmbUrl = IsNetworkSharePath(path);
        if (uncOrSmbUrl)
        {
            var shapeErr = ValidateNetworkSharePathShape(path);
            if (shapeErr != null)
                return shapeErr;
        }

        // UNC / //... OR path lies on a drive .NET reports as Network (e.g. mapped or CIFS — when the runtime sets DriveType correctly).
        var treatAsNetwork = uncOrSmbUrl || IsDriveInfoNetworkPath(path);
        if (treatAsNetwork)
        {
            if (!Directory.Exists(path))
            {
                return "Network destination is not available: " + path + ". Ensure the SMB/CIFS share is reachable before starting. "
                    + (OperatingSystem.IsLinux()
                        ? "On Raspberry Pi, mount the share (e.g. sudo mount -t cifs //dazzle/share /mnt/dazzle) and set DESTINATION_PATH to the mount point (e.g. /mnt/dazzle) instead of a UNC string."
                        : "Mount or map the drive in Windows so the path exists.");
            }
        }
        else
        {
            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                return $"Destination path is not available or could not be created: {ex.Message}";
            }
        }

        string testFile = Path.Combine(path, ".sd-importer-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(testFile, Array.Empty<byte>());
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            return $"Destination path is not writeable: {ex.Message}";
        }
        return null;
    }

    /// <summary>Returns true if the path exists and we can create/delete a file there (e.g. for delete-after-copy).</summary>
    static bool IsPathWritable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;
        path = path.Trim();
        string testFile = Path.Combine(path, ".sd-importer-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(testFile, Array.Empty<byte>());
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string? GetWebMonitorUrl()
    {
        // Prefer detected local IP so Telegram shows the actual machine (Pi, PC, etc.)
        var ip = GetLanIp();
        if (ip != null)
            return $"http://{ip}:{ImporterWorkerState.WebPort}/";
        var urlFromEnv = Environment.GetEnvironmentVariable("WEB_URL")?.Trim();
        if (!string.IsNullOrEmpty(urlFromEnv))
            return urlFromEnv.TrimEnd('/') + "/";
        return null;
    }

    static string? GetLanIp()
    {
        // UDP socket trick: very reliable on Windows and Linux — discovers local IP by "connecting" to external
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Connect("8.8.8.8", 65530);
            var localEp = (IPEndPoint?)sock.LocalEndPoint;
            if (localEp?.Address != null && !IPAddress.IsLoopback(localEp.Address))
                return localEp.Address.ToString();
        }
        catch { /* ignore */ }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                using var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "hostname",
                        ArgumentList = { "-I" },
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(2000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    foreach (var part in output.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!string.IsNullOrEmpty(part) && IPAddress.TryParse(part, out var parsed)
                            && parsed.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(parsed))
                            return part;
                    }
                }
            }
            catch { /* ignore */ }
        }
        // UDP connect to external IP - very reliable on Windows and Linux
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Connect("8.8.8.8", 65530);
            var localEp = (IPEndPoint?)sock.LocalEndPoint;
            if (localEp?.Address != null && !IPAddress.IsLoopback(localEp.Address))
                return localEp.Address.ToString();
        }
        catch { /* ignore */ }
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.GetIPProperties().UnicastAddresses.Any());
            var ip = nic?.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (ip != null && !IPAddress.IsLoopback(ip))
                return ip.ToString();
        }
        catch { /* ignore */ }
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            var ip = host.AddressList.FirstOrDefault(a =>
                a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            return ip?.ToString();
        }
        catch { /* ignore */ }
        // Fallback: connect UDP to external IP to discover local address (works when other methods fail)
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Connect("8.8.8.8", 65530);
            var localEp = (IPEndPoint?)sock.LocalEndPoint;
            if (localEp?.Address != null && !IPAddress.IsLoopback(localEp.Address))
                return localEp.Address.ToString();
        }
        catch { /* ignore */ }
        return null;
    }

    internal static string GetDefaultDestinationPath()
    {
        return @"\\dazzle.local\root\fpv";
    }

    internal static async Task<int> RunWatchMode(CancellationToken appCancellation = default)
    {
        if (ImporterWorkerState.CardIdentifyOnly)
            Console.WriteLine("Starting card-identify watch mode. Press Ctrl+C to exit.");
        else
            Console.WriteLine("Starting watch mode. Press Ctrl+C to exit.");
        Console.WriteLine();

        using var watcher = new DriveWatcher();
        using var keyboardCts = new CancellationTokenSource();
        using var linkedCts = appCancellation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(appCancellation, keyboardCts.Token)
            : null;
        var stopToken = linkedCts?.Token ?? keyboardCts.Token;

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            keyboardCts.Cancel();
            ImporterWorkerState.WebServer?.Stop();
            Console.WriteLine("\nShutting down...");
        };

        watcher.DriveConnected += async (s, e) =>
        {
            // Send Telegram immediately when drive is detected (before waiting for readiness),
            // except in card-identify-only mode.
            if (!ImporterWorkerState.CardIdentifyOnly)
            {
                var monitorUrl = ImporterWorkerState.WebEnabled ? GetWebMonitorUrl() : null;
                await TelegramNotifier.SendCardDetectedAsync(e.DrivePath, monitorUrl);
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"USB drive detected: {e.DrivePath}");
            Console.ResetColor();
            // USB SD adapters can take a moment to become ready
            if (await UsbDriveDetector.WaitForDriveReadyAsync(e.DrivePath))
            {
                if (ImporterWorkerState.CardIdentifyOnly)
                    await IdentifyDrive(e.DrivePath);
                else
                    await ProcessDrive(e.DrivePath, skipCardDetectedTelegram: true);
            }
            else
            {
                Console.WriteLine($"  Drive not ready after retries. Please try again.");
            }
        };

        watcher.DriveDisconnected += (s, e) =>
        {
            Console.WriteLine($"Drive removed: {e.DrivePath}");
            if (ImporterWorkerState.UiProgress)
                ProgressMonitor.SetIdle("Card removed. Waiting for next card...");
        };

        watcher.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, stopToken);
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }

        return 0;
    }

    static async Task IdentifyDrive(string drivePath)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Identifying card: {drivePath}");
        Console.ResetColor();

        var detector = new DeviceDetector(drivePath);
        var detection = detector.Detect();
        await EnsureIdentifiedAsync(detection, drivePath);

        Console.WriteLine($"  Volume Label: {detection.VolumeLabel}");
        Console.WriteLine($"  Has DCIM: {detection.HasDcimFolder}");
        Console.WriteLine($"  Detected device: {detection.GetDeviceDisplayName()}");
        Console.WriteLine($"  Media files found: {detection.MediaFiles.Count}");
        Console.WriteLine("  Files that would be copied (grouped by destination):");

        try
        {
            var copier = new FileCopier(
                ImporterWorkerState.DestinationPath,
                verbose: false,
                overwriteAll: ImporterWorkerState.OverwriteAll,
                skyZoneLogDate: DateTime.Today,
                defaultDate: ImporterWorkerState.DefaultDate,
                deleteSkipped: ImporterWorkerState.DeleteSkipped,
                skipSpaceCheck: true,
                dryRun: true);
            var copyPreview = copier.BuildCopyPreview(detection)
                .Where(x => x.WillCopy)
                .OrderBy(x => x.DestFolder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DestFile, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (copyPreview.Count == 0)
            {
                Console.WriteLine("    (no files would be copied)");
                Console.WriteLine();
                return;
            }

            foreach (var group in copyPreview.GroupBy(item => item.DestFolder))
            {
                Console.WriteLine($"    {group.Key}");
                foreach (var item in group)
                    Console.WriteLine($"      - {Path.GetFileName(item.DestFile)}");
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    Failed to list files: {ex.Message}");
            Console.ResetColor();
        }

        Console.WriteLine();
    }

    static async Task<int> RunSingleScan(string[] args)
    {
        // Find removable drives to scan
        var drivesToScan = GetRemovableDrives().ToList();

        if (drivesToScan.Count == 0)
        {
            Console.WriteLine("No removable drives found.");
            Console.WriteLine("Insert an SD card and run again, or use --watch mode.");
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Found {drivesToScan.Count} removable drive(s):");
        Console.ResetColor();
        foreach (var drive in drivesToScan)
        {
            Console.WriteLine($"  {drive}");
        }
        Console.WriteLine();

        foreach (var drive in drivesToScan)
        {
            await ProcessDrive(drive);
        }

        return 0;
    }

    static IEnumerable<string> GetRemovableDrives()
    {
        // USB-aware detection (includes USB SD card adapters)
        return UsbDriveDetector.GetUsbDrives();
    }

    static async Task ProcessDrive(string drivePath, bool skipCardDetectedTelegram = false)
    {
        var monitorUrl = ImporterWorkerState.WebEnabled ? GetWebMonitorUrl() : null;
        if (!skipCardDetectedTelegram && !ImporterWorkerState.SafeMode)
        {
            await TelegramNotifier.SendCardDetectedAsync(drivePath, monitorUrl);
        }

        if (ImporterWorkerState.UiProgress)
            ProgressMonitor.SetDetecting(drivePath);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Analyzing: {drivePath}");
        Console.ResetColor();

        if (!IsPathWritable(drivePath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Warning: SD card is read-only. You will not be able to delete files from the card after copying.");
            Console.ResetColor();
        }

        var detector = new DeviceDetector(drivePath);
        var detection = detector.Detect();
        await EnsureIdentifiedAsync(detection, drivePath);

        Console.WriteLine($"  Volume Label: {detection.VolumeLabel}");
        Console.WriteLine($"  Has DCIM: {detection.HasDcimFolder}");
        Console.WriteLine($"  Detected device: {detection.GetDeviceDisplayName()}");
        Console.WriteLine($"  Media files found: {detection.MediaFiles.Count}");

        if (detection.DeviceType == DeviceType.Unknown)
        {
            if (ImporterWorkerState.UiProgress) ProgressMonitor.SetIdle("Skipped: Could not identify device type");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Skipping: Could not identify device type.");
            Console.ResetColor();
            await TelegramNotifier.SendSkippedAsync(detection, "Could not identify device type");
            Console.WriteLine();
            return;
        }

        if (detection.MediaFiles.Count == 0)
        {
            if (ImporterWorkerState.UiProgress) ProgressMonitor.SetIdle("Skipped: No media files found");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Skipping: No media files found.");
            Console.ResetColor();
            await TelegramNotifier.SendSkippedAsync(detection, "No media files found");
            Console.WriteLine();
            return;
        }

        // When GoggleSZ (SkyZone) is detected, prompt for date to log files under (default today, 10s timeout)
        DateTime? skyZoneLogDate = null;
        if (detection.DeviceType == DeviceType.SkyZoneAnalog)
        {
            if (ImporterWorkerState.SafeMode || ImporterWorkerState.UseDesktopDefaultsForPrompts)
                skyZoneLogDate = DateTime.Today;
            else
                skyZoneLogDate = await PromptSkyZoneDateAsync();
        }

        // Confirm before copying (unless auto-confirm or safe preview)
        if (!ImporterWorkerState.AutoConfirm && !ImporterWorkerState.SafeMode)
        {
            var confirmationCopier = new FileCopier(
                ImporterWorkerState.DestinationPath,
                verbose: false,
                overwriteAll: ImporterWorkerState.OverwriteAll,
                skyZoneLogDate: skyZoneLogDate,
                defaultDate: ImporterWorkerState.DefaultDate,
                deleteSkipped: ImporterWorkerState.DeleteSkipped,
                skipSpaceCheck: true,
                dryRun: true);

            var confirmationPreview = confirmationCopier.BuildCopyPreview(detection)
                .Where(x => x.WillCopy)
                .OrderBy(x => x.DestFolder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DestFile, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (confirmationPreview.Count == 0)
            {
                Console.WriteLine($"  Copy {detection.MediaFiles.Count} file(s)? [Y/n] (10s timeout, default Y): ");
            }
            else
            {
                Console.WriteLine($"  Copy {confirmationPreview.Count} file(s) to:");
                foreach (var folder in confirmationPreview.Select(x => x.DestFolder).Distinct(StringComparer.OrdinalIgnoreCase).Take(5))
                    Console.WriteLine($"    {folder}");
                var remainingFolders = confirmationPreview.Select(x => x.DestFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count() - 5;
                if (remainingFolders > 0)
                    Console.WriteLine($"    ... and {remainingFolders} more folder(s)");
                Console.Write("  Proceed? [Y/n] (10s timeout, default Y): ");
            }

            var response = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(10));
            if (response == "n" || response == "no")
            {
                if (ImporterWorkerState.UiProgress) ProgressMonitor.SetIdle("Skipped by user");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  Skipped by user.");
                Console.ResetColor();
                await TelegramNotifier.SendSkippedAsync(detection, "Skipped by user");
                Console.WriteLine();
                return;
            }
            if (response != null)
            {
                Console.WriteLine(); // New line after user input
            }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Device: {detection.GetDeviceDisplayName()} ({detection.GetFolderName()})");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(ImporterWorkerState.SafeMode ? "  Preview (safe mode)..." : "  Copying files...");
        Console.ResetColor();

        var totalFiles = detection.MediaFiles.Count;
        var totalBytes = 0L;
        try
        {
            var dcim = Path.Combine(detection.RootPath, "DCIM");
            if (Directory.Exists(dcim))
            {
                foreach (var f in Directory.GetFiles(dcim, "*.*", SearchOption.AllDirectories))
                    totalBytes += new FileInfo(f).Length;
            }
            if (totalBytes == 0)
                totalBytes = detection.MediaFiles.Sum(f => new FileInfo(f).Length);
        }
        catch { totalBytes = detection.MediaFiles.Sum(f => new FileInfo(f).Length); }
        if (ImporterWorkerState.UiProgress)
            ProgressMonitor.SetCopying(detection.GetDeviceDisplayName(), drivePath, totalFiles, totalBytes);

        Action<int, int, long, long, string, bool>? onProgress = ImporterWorkerState.UiProgress && !ImporterWorkerState.SafeMode
            ? (fi, tf, bc, tb, cf, sk) => ProgressMonitor.SetCopyProgress(fi, tf, bc, tb, cf, sk, null)
            : null;
        Action<string, bool>? onFileComplete = ImporterWorkerState.UiProgress && !ImporterWorkerState.SafeMode
            ? (name, sk) => ProgressMonitor.AddFileProcessed(name, sk)
            : null;
        var copier = new FileCopier(ImporterWorkerState.DestinationPath, ImporterWorkerState.Verbose, ImporterWorkerState.OverwriteAll, skyZoneLogDate, ImporterWorkerState.DefaultDate, onProgress, onFileComplete, ImporterWorkerState.DeleteSkipped, ImporterWorkerState.SkipSpaceCheck, dryRun: ImporterWorkerState.SafeMode);
        var result = copier.CopyFiles(detection);

        if (ImporterWorkerState.UiProgress && !ImporterWorkerState.SafeMode)
            ProgressMonitor.SetComplete(detection.GetDeviceDisplayName(), result.FilesCopied, result.BytesCopied, result.FilesSkipped, result.Errors.Count > 0 ? string.Join("; ", result.Errors) : null);

        // Report results
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(result.DryRun ? "  Preview complete (no changes made)." : "  Copy complete!");
        Console.ResetColor();
        if (result.DryRun)
        {
            Console.WriteLine($"    Would copy: {result.FilesCopied} file(s)");
            Console.WriteLine($"    Would skip: {result.FilesSkipped} duplicate(s)");
            Console.WriteLine($"    Would transfer: {result.GetFormattedSize()}");
        }
        else
        {
            Console.WriteLine($"    Files copied: {result.FilesCopied}");
            Console.WriteLine($"    Files skipped: {result.FilesSkipped}" + (result.FilesSkippedAndDeleted > 0 ? $" ({result.FilesSkippedAndDeleted} deleted from source)" : ""));
            Console.WriteLine($"    Data copied: {result.GetFormattedSize()}");
        }

        if (result.Errors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"    Errors: {result.Errors.Count}");
            foreach (var (message, count) in result.GetGroupedErrors())
            {
                Console.WriteLine(count > 1 ? $"      - {message} ({count})" : $"      - {message}");
            }
            Console.ResetColor();
        }

        if (!ImporterWorkerState.SafeMode)
            await TelegramNotifier.SendCopyCompleteAsync(detection, result, monitorUrl);

        if (ImporterWorkerState.UiProgress && !ImporterWorkerState.SafeMode)
            ProgressMonitor.SetComplete(detection.GetDeviceDisplayName(), result.FilesCopied, result.BytesCopied, result.FilesSkipped,
                result.Errors.Count > 0 ? string.Join("; ", result.Errors.Take(3)) : null);

        // Prompt to delete successfully copied files from source (files only, 10s timeout)
        if (!ImporterWorkerState.UseDesktopDefaultsForPrompts && !ImporterWorkerState.SafeMode && result.SuccessfullyCopiedSourcePaths.Count > 0)
        {
            Console.Write($"  Delete {result.SuccessfullyCopiedSourcePaths.Count} successfully copied files from source? [Y/n] (10s timeout, default Y): ");
            var deleteAnswer = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(10), "timeout, deleting source files");
            if (deleteAnswer != null && string.Equals(deleteAnswer, "n", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Skipped deleting source files.");
            }
            else
            {
                var deleted = 0;
                var deleteErrors = 0;
                foreach (var path in result.SuccessfullyCopiedSourcePaths)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            FileCopier.DeleteSourceFileAndCompanions(path);
                            deleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        deleteErrors++;
                        if (deleteErrors <= 3)
                            Console.WriteLine($"  Failed to delete {path}: {ex.Message}");
                    }
                }
                Console.WriteLine($"  Deleted {deleted} files from source." + (deleteErrors > 0 ? $" ({deleteErrors} errors)" : ""));
            }
        }

        Console.WriteLine();
    }

    static void CheckFileMetadata(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            return;
        }
        var detected = DjiMetadataReader.DetectFromFile(filePath);
        Console.WriteLine($"File: {filePath}");
        Console.WriteLine($"Detected: {detected.GetDisplayName()} ({detected.GetFolderName()})");
        var dim = VideoDimensionReader.GetDimensions(filePath);
        if (dim != null)
        {
            Console.WriteLine($"Video dimensions: {dim.Value.Width} x {dim.Value.Height}");
            if (VideoDimensionReader.Is640x480(filePath))
                Console.WriteLine("  -> 640x480 (SkyZone DVR)");
        }
    }

    /// <summary>
    /// Ensures the card has been identified via <see cref="DeviceDetector.AutoUpdaterFileName"/>. When missing,
    /// prompts for a device name on an interactive console and writes it to the card. In unattended contexts
    /// (desktop host, redirected stdin) the prompt is skipped and <see cref="DeviceDetectionResult.NeedsIdentification"/>
    /// stays true, so the existing "could not identify device type" skip path applies.
    /// </summary>
    static async Task EnsureIdentifiedAsync(DeviceDetectionResult detection, string drivePath)
    {
        if (!detection.NeedsIdentification)
            return;

        if (ImporterWorkerState.UseDesktopDefaultsForPrompts || Console.IsInputRedirected)
            return;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  No {DeviceDetector.AutoUpdaterFileName} found on this card.");
        Console.ResetColor();
        Console.WriteLine("  Known codes: GoggleDJI, DJIFlip, GoggleSZ, DJI04, GP13 — or enter any free text to use as the destination folder name.");

        string? name = null;
        while (string.IsNullOrWhiteSpace(name))
        {
            Console.Write("  Enter device name for this card: ");
            name = (await Task.Run(() => Console.ReadLine()))?.Trim();
        }

        try
        {
            DeviceDetector.WriteAutoUpdaterFile(drivePath, name);
            Console.WriteLine($"  Saved {DeviceDetector.AutoUpdaterFileName} to card.");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Failed to write {DeviceDetector.AutoUpdaterFileName}: {ex.Message}");
            Console.ResetColor();
        }

        detection.IdentLabel = name;
        detection.DeviceType = DeviceDetectionResult.MapAutoUpdaterLineToDeviceType(name);
        detection.NeedsIdentification = false;
        Console.WriteLine($"  Device: {name} ({detection.GetFolderName()})");
    }

    /// <summary>
    /// Prompts for the date to log SkyZone files under. Default is today with 10 second timeout.
    /// </summary>
    static async Task<DateTime> PromptSkyZoneDateAsync()
    {
        var today = DateTime.Today;
        Console.Write($"  Date to log SkyZone files under (YYYY-MM-DD) [default: {today:yyyy-MM-dd}, 10s]: ");
        var line = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(10));
        if (string.IsNullOrEmpty(line))
        {
            Console.WriteLine($"  Using {today:yyyy-MM-dd} (today).");
            return today;
        }
        if (DateTime.TryParse(line, out var parsed))
        {
            return parsed.Date;
        }
        Console.WriteLine($"  Invalid date, using {today:yyyy-MM-dd} (today).");
        return today;
    }

    /// <summary>
    /// Fetches Telegram getUpdates and prints chat IDs so the user can set TELEGRAM_CHAT_ID.
    /// User must message the bot first, then run this.
    /// </summary>
    static async Task<int> RunWebCheckAsync()
    {
        ImporterWorkerState.WebEnabled = true;
        ImporterWorkerState.WebServer = new WebServer(ImporterWorkerState.WebPort);
        if (!ImporterWorkerState.WebServer.Start())
        {
            Console.WriteLine("Web server failed to start.");
            return 1;
        }
        ImporterWorkerState.WebPort = ImporterWorkerState.WebServer.BoundPort;
        try
        {
            await Task.Delay(500);
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var r = await c.GetAsync($"http://127.0.0.1:{ImporterWorkerState.WebPort}/");
            Console.WriteLine(r.IsSuccessStatusCode ? $"Web server OK (http://localhost:{ImporterWorkerState.WebPort}/)" : $"Web server returned {(int)r.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Web check failed: " + ex.Message);
            return 1;
        }
        finally { ImporterWorkerState.WebServer.Stop(); }
        return 0;
    }

    static async Task<int> TelegramGetChatIdAsync()
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("TELEGRAM_BOT_TOKEN not set. Add it to a .env file or set the environment variable.");
            return 1;
        }

        Console.WriteLine("Fetching recent messages from your Telegram bot...");
        Console.WriteLine("(If you haven't already: open Telegram, find MiloEventbot, and send any message like /start)");
        Console.WriteLine();

        try
        {
            using var http = new HttpClient();
            var url = $"https://api.telegram.org/bot{token}/getUpdates";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
            {
                Console.WriteLine("Bot API returned ok: false. Check your bot token.");
                if (root.TryGetProperty("description", out var desc))
                    Console.WriteLine(desc.GetString());
                return 1;
            }
            if (!root.TryGetProperty("result", out var result) || result.GetArrayLength() == 0)
            {
                Console.WriteLine("No updates found (result is empty).");
                Console.WriteLine();
                Console.WriteLine("Do this:");
                Console.WriteLine("  1. Open Telegram and search for your bot (e.g. MiloEventbot).");
                Console.WriteLine("  2. Send a message to the bot (e.g. /start or 'hi').");
                Console.WriteLine("  3. Run this command again: SDCardImporter --telegram-get-chat-id");
                return 1;
            }
            var chatIds = new HashSet<long>();
            foreach (var update in result.EnumerateArray())
            {
                if (update.TryGetProperty("message", out var msg) && msg.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var idEl))
                {
                    chatIds.Add(idEl.GetInt64());
                }
            }
            Console.WriteLine("Set TELEGRAM_CHAT_ID to one of these (then run the importer):");
            foreach (var id in chatIds)
                Console.WriteLine($"  {id}");
            Console.WriteLine();
            Console.WriteLine("Example: set TELEGRAM_CHAT_ID=" + chatIds.First());
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Reads a line from console with timeout. Returns null on timeout (treated as default/yes).
    /// </summary>
    static async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout, string? timeoutMessage = null)
    {
        var readTask = Task.Run(() => Console.ReadLine());
        var completed = await Task.WhenAny(readTask, Task.Delay(timeout));
        if (completed != readTask)
        {
            Console.WriteLine(timeoutMessage != null ? $" ({timeoutMessage})" : " (timeout, proceeding with copy)");
            return null;
        }
        return (await readTask)?.Trim();
    }
}
