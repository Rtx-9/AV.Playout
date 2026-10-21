using System.IO;

namespace AV.Playout.Services;

/// <summary>
/// Minimal append-only log at %LOCALAPPDATA%\AV.Playout\logs\yyyy-MM-dd.log.
/// The crash guard swallows UI exceptions to keep the wall up, so they must land here.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AV.Playout", "logs");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(
                    Path.Combine(Directory, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the playout down.
        }
    }
}
