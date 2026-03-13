# SD Card Importer

A cross-platform .NET 8 application that automatically detects and copies media files from FPV/action camera SD cards into an organized folder structure. **Supports USB SD card adapters** — detects drives connected via USB, including dedicated SD card readers.

## Supported Devices

| Device | Folder Name | Detection Method |
|--------|-------------|------------------|
| DJI Goggles 3 | `GoggleDJI` | DCIM/100MEDIA folders with DJI_* files |
| SkyZone Analog FPV Goggles | `GoggleSZ` | MOV/AVI files in root or VIDEO folder |
| BetaPavo20 Pro (DJI O4 Pro) | `DJI04` | DCIM/100MEDIA with SRT files or large 4K files |
| GoPro Hero 13 | `GP13` | DCIM/100GOPRO folders with GOPR*/GX* files |

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

## Device Detection Logic

### DJI Goggles 3 / Generic DJI
- Has `DCIM` folder
- Contains `100MEDIA`, `101MEDIA`, etc. subfolders
- Files named `DJI_XXXX.MP4`, `DJI_XXXX.JPG`
- Smaller file sizes (DVR quality feed)

### BetaPavo20 Pro (DJI O4 Pro)
- Same folder structure as DJI Goggles
- Contains `.SRT` subtitle files (GPS data)
- Larger file sizes (4K60 recordings)

### GoPro Hero 13
- Has `DCIM` folder
- Contains `100GOPRO`, `101GOPRO`, etc. subfolders
- Files named `GOPRXXXX.MP4`, `GXNNNNNN.MP4`, `GHNNNNNN.MP4`

### SkyZone Analog Goggles
- No `DCIM` folder
- `.MOV` or `.AVI` files in root directory
- May have `VIDEO` folder with recordings
- H264 encoded DVR recordings

## License

MIT License - Feel free to modify and distribute.
