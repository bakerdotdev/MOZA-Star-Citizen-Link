using System.IO;

namespace MozaStarCitizen.App.Diagnostics;

public static class AppLog
{
    private static readonly object Sync = new();
    private static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MozaStarCitizen",
        "app.log");

    public static string DiagnosticsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MozaStarCitizen",
        "diagnostics.log");

    public static void Write(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (Sync)
            {
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [pid:{ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static void Write(Exception exception, string context)
    {
        Write($"{context}: {exception}");
    }

    /// <summary>
    /// Appends a timestamped diagnostics snapshot to <see cref="DiagnosticsPath"/>
    /// so the on-screen panel can be captured and shared.
    /// </summary>
    public static void WriteDiagnostics(IEnumerable<string> lines)
    {
        try
        {
            var directory = Path.GetDirectoryName(DiagnosticsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var header = $"===== {DateTimeOffset.Now:O} [pid:{ProcessId}] diagnostics snapshot =====";
            var body = string.Join(Environment.NewLine, lines);
            var block = header + Environment.NewLine + body + Environment.NewLine + Environment.NewLine;

            lock (Sync)
            {
                File.AppendAllText(DiagnosticsPath, block);
            }
        }
        catch
        {
        }
    }
}
