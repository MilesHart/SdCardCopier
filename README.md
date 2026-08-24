# SD Card Importer

A cross-platform .NET 8 application that automatically detects and copies media files from FPV/action camera SD cards into an organized folder structure. **Supports USB SD card adapters** — detects drives connected via USB, including dedicated SD card readers.

## Supported Devices

Every card is identified by an `autoUpdater.txt` file at its root — there is no automatic
detection from file metadata or naming patterns. See [Device Identification](#device-identification-autoupdatertxt) below.

| Device | Folder Name |
|--------|-------------|
| DJI Goggles 3 | `GoggleDJI` |
| DJI Flip | `DJIFlip` |
| SkyZone Analog FPV Goggles | `GoggleSZ` |
| BetaPavo20 Pro (DJI O4 Pro) | `DJI04` |
| GoPro (Hero family, incl. Session 5) | `GP13` |
| Generic/Other (unrecognized) | `Other` |

## Output Structure

Files are copied to:
```
{destination}/{year}/{Jan|Feb|...}/{day}/{DeviceFolder}/
```

Example:
```
C:\Users\mhart\Documents\FPVFootage\2026\Feb\16\GoggleDJI\DJI_0001.MP4
C:\Users\mhart\Documents\FPVFootage\2026\Feb\16\GP13\GOPR0001.MP4
```

## Installation

### Prerequisites
- .NET 8.0 SDK or Runtime

### Build from Source

```bash
# Clone or copy the source
cd SDCardImporter

# Build
dotnet build -c Release

# Run
dotnet run
```

### Publish for Distribution

```bash
# Windows (self-contained)
dotnet publish -c Release -r win-x64 --self-contained -o publish/win-x64

# Linux x64 (self-contained)
dotnet publish -c Release -r linux-x64 --self-contained -o publish/linux-x64

# Linux ARM (Raspberry Pi)
dotnet publish -c Release -r linux-arm --self-contained -o publish/linux-arm

# Linux ARM64 (Raspberry Pi 4/5)
dotnet publish -c Release -r linux-arm64 --self-contained -o publish/linux-arm64
```

## Usage

```bash
# Show help
SDCardImporter --help

# Scan currently connected removable drives
SDCardImporter

# Watch mode - continuously monitor for SD cards
SDCardImporter -w

# Watch mode - identify inserted card type only (no copy)
SDCardImporter -c

# Custom destination with auto-confirm
SDCardImporter -d /mnt/footage -y

# Full watch mode with custom destination
SDCardImporter -d C:\Footage -w -y
```

### Command Line Options

| Option | Description |
|--------|-------------|
| `-d, --destination <path>` | Set the destination folder (default: Documents/FPVFootage) |
| `-w, --watch` | Watch mode: continuously monitor for SD card insertions |
| `-c, --card-watch` | Watch removable media and identify inserted card/device type only (no copy) |
| `-q, --quiet` | Quiet mode: minimal output |
| `-y, --yes` | Auto-confirm: don't ask before copying |
| `-h, --help` | Show help message |

### Telegram notifications

After each card copy completes, a short summary can be sent to Telegram (MiloEventbot). Set the environment variable **`TELEGRAM_CHAT_ID`** to the chat ID where you want messages (e.g. your user or a group). To get your chat ID: message the bot, then open `https://api.telegram.org/bot<YOUR_BOT_TOKEN>/getUpdates` and read `result.message.chat.id`. Optionally set **`TELEGRAM_BOT_TOKEN`** to use a different bot.

## Platform-Specific Notes

### Windows
- Uses WMI to detect **USB-connected drives** (including USB SD card adapters)
- Falls back to all removable drives if WMI is unavailable
- Files are copied to `Documents\FPVFootage` by default

### Linux (including Raspberry Pi)
- Detects USB storage via `/sys/block` and `/proc/mounts`
- Monitors `/media`, `/mnt`, and `/run/media/{username}` for mounted drives
- Includes both USB SD adapters and built-in SD slots (mmcblk)
- For Raspberry Pi, ensure the SD card auto-mounts (usually handled by desktop environments)
- Files are copied to `~/FPVFootage` by default

### USB SD Adapters
- **Watch mode** waits for USB drives to become ready before processing (USB devices can take a moment after connection)
- Compatible with any USB SD card reader/adapter

### Deploy to Raspberry Pi

Deploy to a Pi at `192.168.1.251` (or set `PI_HOST`, `PI_USER`, `PI_PATH`):

**From Windows (PowerShell):**
```powershell
cd SdCardCopier
.\deploy\deploy-to-pi.ps1
```

**Options:** `-PiHost 192.168.1.251` `-User pi` `-RemotePath /home/pi/SdCardCopier` `-Runtime linux-arm64` (or `linux-arm` for 32-bit Pi OS). Add **`-FrameworkDependent`** to publish without the .NET runtime (smaller; requires .NET 8 on the Pi).

**Requirements:** OpenSSH (e.g. `ssh pi@192.168.1.251` works) and the Pi has `mkdir`/`scp` target path writable.

**If you see "Signature specified is zero-sized" on the Pi:** the deployed files may be corrupted or the self-contained layout can trigger this. Redeploy using **framework-dependent** so the Pi uses its own .NET runtime:
```powershell
.\deploy\deploy-to-pi.ps1 -FrameworkDependent
```
Then on the Pi install the .NET 8 runtime once. **You must add Microsoft’s package repo first** (Raspberry Pi OS doesn’t include it):

```bash
# 1) See your Debian version (e.g. 12 = Bookworm, 11 = Bullseye)
cat /etc/os-release | grep VERSION_ID

# 2) Add Microsoft repo — use 12 for Bookworm, 11 for Bullseye
DEB_VER=12
wget https://packages.microsoft.com/config/debian/${DEB_VER}/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# 3) Install .NET 8 runtime
sudo apt-get update && sudo apt-get install -y dotnet-runtime-8.0
```

If `dotnet-runtime-8.0` is still not found (e.g. on some ARM setups), install via the official script: [Scripted install - Linux](https://learn.microsoft.com/dotnet/core/install/linux-scripted-manual#scripted-install) (download `install-dotnet.sh`, then run e.g. `./install-dotnet.sh --channel 8.0 --runtime dotnet --install-dir ~/.dotnet` and add `~/.dotnet` to PATH).

**On the Pi after deploy:** ensure the binary is executable and set destination (e.g. network share or local path):
```bash
chmod +x /home/pi/SdCardCopier/SDCardImporter
# Example: watch mode, auto-confirm, destination /mnt/footage
/home/pi/SdCardCopier/SDCardImporter -w -y -d /mnt/footage
```

### Running as a Service (Linux)

Create `/etc/systemd/system/sdcard-importer.service`:

```ini
[Unit]
Description=SD Card Importer Service
After=network.target

[Service]
ExecStart=/home/pi/SdCardCopier/SDCardImporter -w -y -d /mnt/footage
User=pi
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
```

Enable and start:
```bash
sudo systemctl enable sdcard-importer
sudo systemctl start sdcard-importer
```

## Device Identification (autoUpdater.txt)

Every card is identified by a file named `autoUpdater.txt` in the **root** of the SD card (same level as `DCIM`). The first non-empty line is used to identify the device for display and for destination routing:

- Use a **folder code** for an exact match: `GoggleDJI`, `DJIFlip`, `GoggleSZ`, `DJI04`, `GP13`, or `Other`.
- Or write a **free-form** line (for example `GoPro Hero Session 5`); the importer maps common phrases to the same device types, and still shows your exact line as the device name.

If `autoUpdater.txt` is missing or empty when a card is scanned in an interactive console session, the importer prompts for a device name and writes it to `autoUpdater.txt` on the card, so the card is remembered on future imports. In unattended contexts (desktop app, or watch mode with redirected/no console input) the card is skipped with "Could not identify device type" until `autoUpdater.txt` is added.

## License

MIT License - Feel free to modify and distribute.
