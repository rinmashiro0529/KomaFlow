using System.IO;
using System.Text;
using System.Text.Json;
using MangaGui.Models;
using MangaGui.Services;

namespace MangaCli;

/// <summary>
/// Thread-safe NDJSON writer: every line is one JSON object (UTF-8, no BOM) flushed immediately to
/// stdout and optionally appended to <c>--events-file</c>. <c>state</c> and <c>task</c> events are
/// de-duplicated so unchanged values are not re-emitted. stdout carries only these events;
/// diagnostics go to stderr.
/// </summary>
public sealed class EventWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly TextWriter _stdout;
    private readonly StreamWriter? _file;
    private readonly bool _quietLogs;
    private string? _lastStateSignature;
    private readonly Dictionary<string, string> _taskSignatures = new(StringComparer.Ordinal);

    public EventWriter(TextWriter stdout, string? eventsFile, bool quietLogs)
    {
        _stdout = stdout;
        _quietLogs = quietLogs;
        if (!string.IsNullOrWhiteSpace(eventsFile))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(eventsFile));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _file = new StreamWriter(
                new FileStream(eventsFile, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
        }
    }

    public void Log(LogLevel level, string message)
    {
        if (_quietLogs)
        {
            return;
        }

        lock (_gate)
        {
            Emit("log", new Dictionary<string, object?>
            {
                ["level"] = level,
                ["message"] = message
            });
        }
    }

    public void State(Dictionary<string, object?> fields)
    {
        lock (_gate)
        {
            var signature = JsonSerializer.Serialize(fields, CliJson.Options);
            if (signature == _lastStateSignature)
            {
                return;
            }

            _lastStateSignature = signature;
            Emit("state", fields);
        }
    }

    public void TaskEvent(TranslationTaskItem item)
    {
        lock (_gate)
        {
            var fields = TaskFields(item);
            var signature = JsonSerializer.Serialize(fields, CliJson.Options);
            if (_taskSignatures.TryGetValue(item.JobId, out var last) && last == signature)
            {
                return;
            }

            _taskSignatures[item.JobId] = signature;
            Emit("task", fields);
        }
    }

    public void Notice(string title, string message)
    {
        lock (_gate)
        {
            Emit("notice", new Dictionary<string, object?>
            {
                ["title"] = title,
                ["message"] = message
            });
        }
    }

    public void Prompt(string kind, string answer)
    {
        lock (_gate)
        {
            Emit("prompt", new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["answer"] = answer
            });
        }
    }

    public void Billing(BillingSnapshot snapshot)
    {
        lock (_gate)
        {
            Emit("billing", BillingFields(snapshot));
        }
    }

    internal void Result(CommandResult result)
    {
        var fields = new Dictionary<string, object?>
        {
            ["command"] = result.Command,
            ["exit_code"] = result.ExitCode,
            ["ok"] = result.ExitCode == ExitCodes.Ok,
            ["message"] = result.Message,
            ["warnings"] = result.Warnings.ToArray()
        };
        foreach (var kv in result.Fields)
        {
            fields[kv.Key] = kv.Value;
        }

        lock (_gate)
        {
            Emit("result", fields);

            // Sealed: nothing may be written after the result line — it must stay the last NDJSON
            // line even while the host tears down and emits stray task/state events.
            _sealed = true;
        }
    }

    public static Dictionary<string, object?> TaskFields(TranslationTaskItem item) => new()
    {
        ["job_id"] = item.JobId,
        ["file"] = item.FileName,
        ["status"] = item.Status,
        ["completed_pages"] = item.CompletedPages,
        ["total_pages"] = item.TotalPages,
        ["output"] = item.OutputZipPath,
        ["error"] = item.ErrorSummary
    };

    public static Dictionary<string, object?> BillingFields(BillingSnapshot snapshot) => new()
    {
        ["running"] = snapshot.IsRunning,
        ["elapsed_seconds"] = Math.Round(snapshot.Elapsed.TotalSeconds, 1),
        ["estimated_cu"] = Math.Round(snapshot.EstimatedCu, 4),
        ["cu_per_hour"] = Math.Round(snapshot.CuPerHour, 4),
        ["planned_minutes"] = snapshot.PlannedMinutes,
        ["deadline"] = snapshot.DeadlineUtc?.ToString("o"),
        ["severity"] = snapshot.Severity,
        ["start_time_unknown"] = snapshot.StartTimeUnknown
    };

    private bool _sealed;

    /// <summary>Must be called while holding <see cref="_gate"/>.</summary>
    private void Emit(string type, IReadOnlyDictionary<string, object?> fields)
    {
        if (_sealed)
        {
            return;
        }

        var all = new Dictionary<string, object?>(fields.Count + 2)
        {
            ["ts"] = DateTimeOffset.Now.ToString("o"),
            ["type"] = type
        };
        foreach (var kv in fields)
        {
            all[kv.Key] = kv.Value;
        }

        var line = JsonSerializer.Serialize(all, CliJson.Options);
        _stdout.WriteLine(line);
        _stdout.Flush();
        _file?.WriteLine(line);
    }

    public void Dispose() => _file?.Dispose();
}
