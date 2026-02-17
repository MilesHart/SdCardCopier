using System.Text.Json;
using SDCardImporter;

class Program
{
    private static string _destinationPath = "";
    private static bool _watchMode = false;
    private static bool _verbose = true;
    private static bool _autoConfirm = false;
    private static bool _overwriteAll = false;

    static async Task<int> Main(string[] args)
    {
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

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("===========================================");
        Console.WriteLine("  SD Card Importer for FPV/Action Cameras  ");
        Console.WriteLine("===========================================");
        Console.ResetColor();
        Console.WriteLine();

        if (!ParseArguments(args))
        {
            ShowHelp();
            return 1;
        }

        if (string.IsNullOrEmpty(_destinationPath))
        {
            // Default destination path based on OS
            _destinationPath = GetDefaultDestinationPath();
        }

        Console.WriteLine($"Destination: {_destinationPath}");
        var telegramChatId = TelegramNotifier.GetEffectiveChatId();
        Console.WriteLine(string.IsNullOrEmpty(telegramChatId)
            ? "Telegram: notifications disabled (TELEGRAM_CHAT_ID not set; use --telegram-chat-id <id> to enable)"
            : $"Telegram: notifications enabled (chat ID: {telegramChatId})");
        Console.WriteLine($"Supported devices:");
        Console.WriteLine($"  - DJI Goggles 3 -> {DeviceType.DJIGoggles3.GetFolderName()}");
        Console.WriteLine($"  - DJI Flip -> {DeviceType.DJIFlip.GetFolderName()}");
        Console.WriteLine($"  - SkyZone Analog FPV Goggles -> {DeviceType.SkyZoneAnalog.GetFolderName()}");
        Console.WriteLine($"  - BetaPavo20 Pro (DJI O4 Pro) -> {DeviceType.BetaPavo20Pro.GetFolderName()}");
        Console.WriteLine($"  - GoPro Hero 13 -> {DeviceType.GoPro13.GetFolderName()}");
        Console.WriteLine($"  - Generic/Other (unrecognized cards) -> {DeviceType.Generic.GetFolderName()}");
        Console.WriteLine();

        if (_watchMode)
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
                    return false;

                case "-d":
                case "--destination":
                    if (i + 1 < args.Length)
                    {
                        _destinationPath = args[++i];
                    }
                    else
                    {
                        Console.WriteLine("Error: --destination requires a path argument");
                        return false;
                    }
                    break;

                case "-w":
                case "--watch":
                    _watchMode = true;
                    break;

                case "-q":
                case "--quiet":
                    _verbose = false;
                    break;

                case "-y":
                case "--yes":
                    _autoConfirm = true;
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
                    _overwriteAll = true;
                    break;

                default:
                    // Treat as source path if it looks like a path
                    if (Directory.Exists(args[i]) && string.IsNullOrEmpty(_destinationPath))
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
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -d, --destination <path>  Set the destination root folder for copied files");
        Console.WriteLine("                            Default: ~/FPVFootage (Linux/Mac) or Documents\\FPVFootage (Windows)");
        Console.WriteLine("  -w, --watch               Watch mode: continuously monitor for SD card insertions");
        Console.WriteLine("  -q, --quiet               Quiet mode: minimal output");
        Console.WriteLine("  -y, --yes                 Auto-confirm: don't ask before copying");
        Console.WriteLine("  -o, --overwrite            Overwrite existing files (default: skip if same size)");
        Console.WriteLine("  --check-file <path>        Check DJI metadata in a single file (for debugging)");
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
        Console.WriteLine("  GP13       - GoPro Hero 13");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  SDCardImporter                          # Scan current removable drives");
        Console.WriteLine("  SDCardImporter -w                       # Watch for SD card insertions");
        Console.WriteLine("  SDCardImporter -d /mnt/footage -w -y    # Watch mode with custom destination, auto-copy");
    }

    static string GetDefaultDestinationPath()
    {
        return @"\\dazzle\root\FPV";
    }

    static async Task<int> RunWatchMode()
    {
        Console.WriteLine("Starting watch mode. Press Ctrl+C to exit.");
        Console.WriteLine();

        using var watcher = new DriveWatcher();
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\nShutting down...");
        };

        watcher.DriveConnected += async (s, e) =>
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"USB drive detected: {e.DrivePath}");
            Console.ResetColor();
            // USB SD adapters can take a moment to become ready
            if (await UsbDriveDetector.WaitForDriveReadyAsync(e.DrivePath))
            {
                await ProcessDrive(e.DrivePath);
            }
            else
            {
                Console.WriteLine($"  Drive not ready after retries. Please try again.");
            }
        };

        watcher.DriveDisconnected += (s, e) =>
        {
            Console.WriteLine($"Drive removed: {e.DrivePath}");
        };

        watcher.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }

        return 0;
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

    static async Task ProcessDrive(string drivePath)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Analyzing: {drivePath}");
        Console.ResetColor();

        var detector = new DeviceDetector(drivePath);
        var detection = detector.Detect();

        Console.WriteLine($"  Volume Label: {detection.VolumeLabel}");
        Console.WriteLine($"  Has DCIM: {detection.HasDcimFolder}");
        
        if (_verbose && detection.FoundPatterns.Count > 0)
        {
            Console.WriteLine("  Detection patterns:");
            foreach (var pattern in detection.FoundPatterns.Take(5))
            {
                Console.WriteLine($"    - {pattern}");
            }
            if (detection.FoundPatterns.Count > 5)
            {
                Console.WriteLine($"    ... and {detection.FoundPatterns.Count - 5} more");
            }
        }

        Console.WriteLine($"  Detected device: {detection.DeviceType.GetDisplayName()}");
        Console.WriteLine($"  Media files found: {detection.MediaFiles.Count}");

        if (detection.DeviceType == DeviceType.Unknown)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Skipping: Could not identify device type.");
            Console.ResetColor();
            await TelegramNotifier.SendSkippedAsync(detection, "Could not identify device type");
            Console.WriteLine();
            return;
        }

        if (detection.MediaFiles.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Skipping: No media files found.");
            Console.ResetColor();
            await TelegramNotifier.SendSkippedAsync(detection, "No media files found");
            Console.WriteLine();
            return;
        }

        // Confirm before copying (unless auto-confirm)
        if (!_autoConfirm)
        {
            Console.Write($"  Copy files to {_destinationPath}? [Y/n] (10s timeout, default Y): ");
            var response = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(10));
            if (response == "n" || response == "no")
            {
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

        // When GoggleSZ (SkyZone) is detected, prompt for date to log files under (default today, 10s timeout)
        DateTime? skyZoneLogDate = null;
        if (detection.DeviceType == DeviceType.SkyZoneAnalog)
        {
            skyZoneLogDate = await PromptSkyZoneDateAsync();
        }

        // Show device type prominently before copy with ASCII art and color
        Console.ForegroundColor = ConsoleColor.Cyan;
        foreach (var line in detection.DeviceType.GetAsciiArt().Split('\n'))
        {
            Console.WriteLine($"  {line}");
        }
        Console.WriteLine($"  Device: {detection.DeviceType.GetDisplayName()} ({detection.DeviceType.GetFolderName()})");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("  Copying files...");
        Console.ResetColor();

        var copier = new FileCopier(_destinationPath, _verbose, _overwriteAll, skyZoneLogDate);
        var result = copier.CopyFiles(detection);

        // Report results
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Copy complete!");
        Console.ResetColor();
        Console.WriteLine($"    Files copied: {result.FilesCopied}");
        Console.WriteLine($"    Files skipped: {result.FilesSkipped}");
        Console.WriteLine($"    Data copied: {result.GetFormattedSize()}");

        if (result.Errors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"    Errors: {result.Errors.Count}");
            foreach (var error in result.Errors.Take(5))
            {
                Console.WriteLine($"      - {error}");
            }
            Console.ResetColor();
        }

        await TelegramNotifier.SendCopyCompleteAsync(detection, result);

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
    static async Task<int> TelegramGetChatIdAsync()
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            token = TelegramNotifier.DefaultBotToken;

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
    static async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout)
    {
        var readTask = Task.Run(() => Console.ReadLine());
        var completed = await Task.WhenAny(readTask, Task.Delay(timeout));
        if (completed != readTask)
        {
            Console.WriteLine(" (timeout, proceeding with copy)");
            return null;
        }
        return (await readTask)?.Trim();
    }
}
