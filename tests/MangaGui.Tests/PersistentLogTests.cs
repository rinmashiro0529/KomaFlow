using System.IO;
using MangaGui.Models;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests;

/// <summary>PersistentLog: dated sanitized files under logs\, newest-14 pruning, never-throw.</summary>
public sealed class PersistentLogTests : IDisposable
{
    private readonly string _dir;

    public PersistentLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "manga_plog_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Append_WritesSanitizedLineToDatedFile()
    {
        var log = new PersistentLog(_dir);
        log.Append(LogLevel.Warning, @"token=abc123def456 line from C:\Users\rin\x");
        log.AppendRawOutput("任务失败原始输出 · a.zip", "raw access_token=zzz999 output");

        var files = Directory.GetFiles(Path.Combine(_dir, "logs"), "*.log");
        var file = Assert.Single(files);
        Assert.Equal($"{DateTime.Now:yyyyMMdd}.log", Path.GetFileName(file));

        var text = File.ReadAllText(file);
        Assert.Contains("[Warning]", text);
        Assert.Contains("[RAW]", text);
        Assert.DoesNotContain("abc123def456", text);
        Assert.DoesNotContain("zzz999", text);
        Assert.Contains("<USER>", text);
    }

    [Fact]
    public void Write_PrunesToNewest14Files()
    {
        var logsDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logsDir);
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(logsDir, $"202601{i:D2}.log"), "old\n");
        }

        var log = new PersistentLog(_dir);
        log.Append(LogLevel.Info, "hi");

        var remaining = Directory.GetFiles(logsDir, "*.log").Select(Path.GetFileName).ToArray();
        Assert.Equal(PersistentLog.MaxFiles, remaining.Length);
        Assert.DoesNotContain("20260100.log", remaining);
        Assert.Contains($"{DateTime.Now:yyyyMMdd}.log", remaining);
    }

    [Fact]
    public void IoFailure_NeverThrows()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var log = new PersistentLog(blocker); // <file>\logs can never be a directory

        var ex = Record.Exception(() =>
        {
            log.Append(LogLevel.Info, "x");
            log.AppendRawOutput("t", "y");
        });
        Assert.Null(ex);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }
}
