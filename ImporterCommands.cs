/// <summary>Public entry points for the Avalonia desktop app (and other hosts).</summary>
public static class ImporterCommands
{
    public static string GetDefaultDestinationPath() => Program.GetDefaultDestinationPath();

    public static string? ValidateDestinationPath(string path) => Program.ValidateDestinationPath(path);

    /// <summary>Runs USB/SD watch mode with current <see cref="ImporterWorkerState"/>.</summary>
    public static Task<int> RunWatchModeAsync(CancellationToken cancellationToken = default) =>
        Program.RunWatchMode(cancellationToken);
}
