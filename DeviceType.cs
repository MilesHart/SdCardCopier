namespace SDCardImporter;

/// <summary>
/// Represents the type of device that created the SD card content
/// </summary>
public enum DeviceType
{
    Unknown,
    DJIGoggles3,      // GoggleDJI
    DJIFlip,          // DJIFlip - DJI Flip camera
    SkyZoneAnalog,    // GoggleSZ
    BetaPavo20Pro,    // DJI04
    GoPro13,          // GP13
    Generic           // Other - unrecognized card with media files
}

public static class DeviceTypeExtensions
{
    /// <summary>
    /// Gets the folder name suffix for the device type
    /// </summary>
    public static string GetFolderName(this DeviceType deviceType) => deviceType switch
    {
        DeviceType.DJIGoggles3 => "GoggleDJI",
        DeviceType.DJIFlip => "DJIFlip",
        DeviceType.SkyZoneAnalog => "GoggleSZ",
        DeviceType.BetaPavo20Pro => "DJI04",
        DeviceType.GoPro13 => "GP13",
        DeviceType.Generic => "Other",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets a display name for the device type
    /// </summary>
    public static string GetDisplayName(this DeviceType deviceType) => deviceType switch
    {
        DeviceType.DJIGoggles3 => "DJI Goggles 3",
        DeviceType.DJIFlip => "DJI Flip",
        DeviceType.SkyZoneAnalog => "SkyZone Analog FPV Goggles",
        DeviceType.BetaPavo20Pro => "BetaPavo20 Pro (DJI O4 Pro)",
        DeviceType.GoPro13 => "GoPro Hero 13",
        DeviceType.Generic => "Generic / Other (unrecognized)",
        _ => "Unknown Device"
    };

    /// <summary>
    /// Gets 3-line cute ASCII art for the device type
    /// </summary>
    public static string GetAsciiArt(this DeviceType deviceType) => deviceType switch
    {
        DeviceType.DJIGoggles3 => "  __o__  __o__\n /     \\/     \\\n|   DJI G3    |",
        DeviceType.DJIFlip => "  .----.\n  |Flip|\n  '----'",
        DeviceType.SkyZoneAnalog => "  .---.  .---.\n | SZ  | | SZ  |\n  '---'  '---'",
        DeviceType.BetaPavo20Pro => "    /\\\n   /O4\\\n   \\__/",
        DeviceType.GoPro13 => "  +------+\n | GP13  |\n  +------+",
        DeviceType.Generic => "  .-----.\n |Other |\n  '-----'",
        _ => "   ?  ?  ?\n  (  ?  )\n   -----"
    };
}
