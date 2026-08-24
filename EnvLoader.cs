namespace SDCardImporter;

/// <summary>
/// Loads KEY=VALUE pairs from a .env file into the process environment.
/// Looks for .env next to the executable, then in the current directory.
/// </summary>
public static class EnvLoader
{
    public static void Load()
    {
        var paths = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env")
        };
        // On Linux, also try executable's directory (when launched from .desktop or systemd, cwd may be wrong)
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var exePath = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exePath))
                {
                    var exeDir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(exeDir))
                        paths.Add(Path.Combine(exeDir, ".env"));
                }
            }
            catch { /* ignore */ }
        }
        foreach (var path in paths.Distinct())
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var s = line.Trim();
                    if (string.IsNullOrEmpty(s) || s.StartsWith('#')) continue;
                    var eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = s[..eq].Trim();
                    var value = s[(eq + 1)..].Trim();
                    if (string.IsNullOrEmpty(key)) continue;
                    Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);
                }
            }
            catch
            {
                // Ignore read errors
            }
            return;
        }
    }
}
