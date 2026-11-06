using System.IO;
using System.Text;

namespace AnyPS5Studio;

/// <summary>The app's own log, for what goes wrong in the app rather than in a game.</summary>
public static class AppLog
{
    private const long MaxBytes = 5L * 1024 * 1024;
    private static readonly object Gate = new();

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyPS5 Studio", "logs");

    public static string FilePath => Path.Combine(Folder, "app.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string context, Exception e) => Write("ERROR", $"{context}{Environment.NewLine}{e}");

    private static void Write(string level, string message)
    {
        // A logging failure must never take the app down with it.
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(Folder, "app.old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
