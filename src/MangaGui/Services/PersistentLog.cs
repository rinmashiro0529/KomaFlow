using System.IO;
using System.Text;
using MangaGui.Models;

namespace MangaGui.Services;

/// <summary>
/// Appends every GUI-visible log line (plus recorded raw CLI output on failures) to
/// &lt;DataDirectory&gt;\logs\yyyyMMdd.log, sanitized via <see cref="LogSanitizer"/>.
/// Keeps the newest <see cref="MaxFiles"/> files and never throws on I/O errors.
/// </summary>
public sealed class PersistentLog
{
    public const int MaxFiles = 14;

    private readonly object _gate = new();
    private string? _currentFileName;

    public string LogsDirectory { get; }

    public PersistentLog(string dataDirectory)
    {
        LogsDirectory = Path.Combine(dataDirectory, "logs");
    }

    public void Append(LogLevel level, string message) =>
        Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");

    public void AppendRawOutput(string title, string content) =>
        Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [RAW] {title}\n{content}");

    private void Write(string text)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(LogsDirectory);
                var fileName = $"{DateTime.Now:yyyyMMdd}.log";
                File.AppendAllText(
                    Path.Combine(LogsDirectory, fileName),
                    LogSanitizer.Redact(text) + "\n",
                    Encoding.UTF8);
                if (!string.Equals(fileName, _currentFileName, StringComparison.Ordinal))
                {
                    _currentFileName = fileName;
                    PruneOldFiles();
                }
            }
            catch
            {
                // Persistent logging must never break the app.
            }
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            var files = Directory.GetFiles(LogsDirectory, "*.log");
            if (files.Length <= MaxFiles)
            {
                return;
            }

            foreach (var old in files
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(MaxFiles))
            {
                try
                {
                    File.Delete(old);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
