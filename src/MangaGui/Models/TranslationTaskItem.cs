using CommunityToolkit.Mvvm.ComponentModel;

namespace MangaGui.Models;

public enum TranslationTaskStatus
{
    Pending,
    Preparing,
    Uploading,
    Translating,
    Packing,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Observable view model / entity representing a single manga archive (.zip/.cbz) translation job.
/// </summary>
public partial class TranslationTaskItem : ObservableObject
{
    public string JobId { get; }
    public string SourceZipPath { get; }
    public string FileName { get; }
    public string? TargetOutputDirectory { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(CanViewLog))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    private TranslationTaskStatus _status = TranslationTaskStatus.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private int _completedPages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private int _totalPages;

    [ObservableProperty]
    private string? _outputZipPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private string? _errorSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanViewLog))]
    private string _rawLog = string.Empty;

    [ObservableProperty]
    private string? _detailHint;

    [ObservableProperty]
    private string? _precheckSummary;

    public TranslationTaskItem(string jobId, string sourceZipPath, string? targetOutputDirectory = null)
    {
        JobId = jobId;
        SourceZipPath = sourceZipPath;
        FileName = System.IO.Path.GetFileName(sourceZipPath);
        TargetOutputDirectory = string.IsNullOrWhiteSpace(targetOutputDirectory) ? null : targetOutputDirectory;
    }

    public bool IsActive => Status is TranslationTaskStatus.Preparing
        or TranslationTaskStatus.Uploading
        or TranslationTaskStatus.Translating
        or TranslationTaskStatus.Packing
        or TranslationTaskStatus.Downloading;

    public bool IsCompleted => Status == TranslationTaskStatus.Completed;

    public bool IsFailed => Status == TranslationTaskStatus.Failed;

    public bool CanViewLog => IsFailed && !string.IsNullOrWhiteSpace(RawLog);

    public bool CanRemove => Status == TranslationTaskStatus.Pending;

    public bool CanRetry => Status is TranslationTaskStatus.Failed or TranslationTaskStatus.Cancelled;

    public double ProgressPercent
    {
        get
        {
            return Status switch
            {
                TranslationTaskStatus.Pending => 0,
                TranslationTaskStatus.Preparing => 5,
                TranslationTaskStatus.Uploading => 12,
                TranslationTaskStatus.Translating when TotalPages > 0 =>
                    15 + Math.Clamp((double)CompletedPages / TotalPages, 0, 1) * 70,
                TranslationTaskStatus.Translating => 20,
                TranslationTaskStatus.Packing => 88,
                TranslationTaskStatus.Downloading => 94,
                TranslationTaskStatus.Completed => 100,
                _ => 0
            };
        }
    }

    public string StatusDisplay => Status switch
    {
        TranslationTaskStatus.Pending => TotalPages > 0 ? $"等待中 ({TotalPages} 页)" : "等待中",
        TranslationTaskStatus.Preparing => "预处理中",
        TranslationTaskStatus.Uploading => "上传中",
        TranslationTaskStatus.Translating when TotalPages > 0 => $"翻译中 {CompletedPages}/{TotalPages} 页",
        TranslationTaskStatus.Translating => "翻译中...",
        TranslationTaskStatus.Packing => "打包中",
        TranslationTaskStatus.Downloading => "下载中",
        TranslationTaskStatus.Completed => "已完成",
        TranslationTaskStatus.Failed => string.IsNullOrWhiteSpace(ErrorSummary) ? "失败" : $"失败：{ErrorSummary}",
        TranslationTaskStatus.Cancelled => "已取消",
        _ => Status.ToString()
    };
}
