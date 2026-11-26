using System.IO;
using System.Text.Json;
using MangaGui.Models;

namespace MangaGui.Services;

/// <summary>
/// Persists session.json and settings.json under %APPDATA%\manga_gui\ using atomic file moves,
/// distinguishing missing vs corrupted vs access-denied states and never masking remote billing safety (A-02).
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();

    public string DataDirectory { get; }
    public string SessionFilePath => Path.Combine(DataDirectory, "session.json");
    public string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    public SessionStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "manga_gui");
    }

    public SessionLoadResult LoadSessionWithStatus()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(SessionFilePath))
                {
                    if (Directory.Exists(DataDirectory) || !File.Exists(DataDirectory))
                    {
                        return new SessionLoadResult(StoreLoadStatus.Missing, null);
                    }

                    return new SessionLoadResult(
                        StoreLoadStatus.AccessDenied,
                        null,
                        $"会话存储目录路径不可用（不是目录）：{DataDirectory}");
                }

                var json = File.ReadAllText(SessionFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new SessionLoadResult(
                        StoreLoadStatus.Corrupted,
                        null,
                        $"会话记录文件为空或已损坏：{SessionFilePath}");
                }

                var record = JsonSerializer.Deserialize<SessionRecord>(json, JsonOptions);
                if (record is null || string.IsNullOrWhiteSpace(record.SessionName))
                {
                    return new SessionLoadResult(
                        StoreLoadStatus.Corrupted,
                        null,
                        $"会话记录格式无效：{SessionFilePath}");
                }

                return new SessionLoadResult(StoreLoadStatus.Found, record);
            }
            catch (JsonException ex)
            {
                return new SessionLoadResult(
                    StoreLoadStatus.Corrupted,
                    null,
                    $"会话记录 JSON 损坏（{ex.Message}）：{SessionFilePath}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return new SessionLoadResult(
                    StoreLoadStatus.AccessDenied,
                    null,
                    $"无权限读取会话记录（{ex.Message}）：{SessionFilePath}");
            }
            catch (IOException ex)
            {
                return new SessionLoadResult(
                    StoreLoadStatus.AccessDenied,
                    null,
                    $"读取会话记录 I/O 失败（{ex.Message}）：{SessionFilePath}");
            }
        }
    }

    public SessionRecord? LoadSession() => LoadSessionWithStatus().Record;

    public StoreOperationResult TrySaveSession(SessionRecord record)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var json = JsonSerializer.Serialize(record, JsonOptions);
                WriteAtomic(SessionFilePath, json);
                return new StoreOperationResult(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new StoreOperationResult(false, $"无法写入本地会话记录（{ex.Message}）：{SessionFilePath}");
            }
        }
    }

    public void SaveSession(SessionRecord record)
    {
        var res = TrySaveSession(record);
        if (!res.Success)
        {
            throw new IOException(res.ErrorMessage ?? "Failed to save session record.");
        }
    }

    public StoreOperationResult TryDeleteSession()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(SessionFilePath))
                {
                    File.Delete(SessionFilePath);
                }

                return new StoreOperationResult(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new StoreOperationResult(false, $"无法删除本地会话记录（{ex.Message}）：{SessionFilePath}");
            }
        }
    }

    public void DeleteSession()
    {
        var res = TryDeleteSession();
        if (!res.Success)
        {
            throw new IOException(res.ErrorMessage ?? "Failed to delete session record.");
        }
    }

    public AppSettings LoadSettings()
    {
        lock (_lock)
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new AppSettings();
            }

            try
            {
                var json = File.ReadAllText(SettingsFilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public StoreOperationResult TrySaveSettings(AppSettings settings)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                WriteAtomic(SettingsFilePath, json);
                return new StoreOperationResult(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new StoreOperationResult(false, $"无法保存配置（{ex.Message}）：{SettingsFilePath}");
            }
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        var res = TrySaveSettings(settings);
        if (!res.Success)
        {
            throw new IOException(res.ErrorMessage ?? "Failed to save settings.");
        }
    }

    private static void WriteAtomic(string targetPath, string content)
    {
        var tmpPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmpPath, content, System.Text.Encoding.UTF8);
            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch
            {
                // Ignore temp cleanup failure.
            }
            throw;
        }
    }
}
