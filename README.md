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

### Running as a Service (Linux)

Create `/etc/systemd/system/sdcard-importer.service`:

```ini
[Unit]
Description=SD Card Importer Service
After=network.target

[Service]
ExecStart=/path/to/SDCardImporter -w -y -d /home/pi/FPVFootage
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
