using System.IO;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace MangaGui.Services;

/// <summary>
/// Configurable safety budgets for archive inspection and extraction (A-05).
/// Defaults are generous for real manga volumes; tests inject smaller budgets to verify guards.
/// </summary>
public sealed record PackageBudget
{
    public long MaxArchiveBytes { get; init; } = 1L * 1024 * 1024 * 1024; // 1 GiB
    public int MaxImageCount { get; init; } = 2_000;
    public long MaxTotalUncompressedBytes { get; init; } = 4L * 1024 * 1024 * 1024; // 4 GiB
    public long MaxSingleEntryBytes { get; init; } = 64L * 1024 * 1024; // 64 MiB

    public static PackageBudget Default { get; } = new();
}

/// <summary>
/// Result of inspecting and optionally flattening/repacking a local manga archive before uploading.
/// </summary>
/// <param name="OriginalEntryPaths">
/// When the archive was repacked, the original entry path of each page in upload order
/// (index 0 was uploaded as 0001.*); null when the source archive was uploaded as-is.
/// </param>
public sealed record PreparedZipResult(
    string UploadZipPath,
    int ImageCount,
    bool WasFlattenedOrCopied,
    bool IsTemporaryFile,
    IReadOnlyList<string>? OriginalEntryPaths = null);

/// <summary>
/// Fast offline precheck result run before GPU billing begins (E-01).
/// </summary>
public sealed record ArchivePrecheckResult(
    bool IsValid,
    int ImageCount,
    long ArchiveBytes,
    long EstimatedUncompressedBytes,
    bool NeedsRepack,
    string Summary,
    string? ErrorMessage = null);

/// <summary>
/// Verification result for a downloaded translated archive before promoting it to final output (A-04).
/// </summary>
public sealed record DownloadedPackageVerification(
    bool IsValid,
    int ImageCount,
    long ActualBytes,
    string ActualSha256,
    string? ErrorMessage = null);

/// <summary>
/// Natural path comparer that sorts numeric runs by numeric value segment-by-segment (A-08).
/// Ensures ch1/2.jpg precedes ch1/10.jpg and ch2/1.jpg.
/// </summary>
public sealed class NaturalPathComparer : IComparer<string>
{
    public static NaturalPathComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var nx = x.Replace('\\', '/');
        var ny = y.Replace('\\', '/');

        var sx = nx.Split('/');
        var sy = ny.Split('/');
        int minSegs = Math.Min(sx.Length, sy.Length);

        for (int s = 0; s < minSegs; s++)
        {
            int cmp = CompareSegment(sx[s], sy[s]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return sx.Length.CompareTo(sy.Length);
    }

    private static int CompareSegment(string a, string b)
    {
        int ia = 0, ib = 0;
        int la = a.Length, lb = b.Length;

        while (ia < la && ib < lb)
        {
            bool da = char.IsAsciiDigit(a[ia]);
            bool db = char.IsAsciiDigit(b[ib]);

            if (da && db)
            {
                int startA = ia;
                while (ia < la && char.IsAsciiDigit(a[ia])) ia++;
                int startB = ib;
                while (ib < lb && char.IsAsciiDigit(b[ib])) ib++;

                var spanA = a.AsSpan(startA, ia - startA);
                var spanB = b.AsSpan(startB, ib - startB);

                var trimA = spanA.TrimStart('0');
                var trimB = spanB.TrimStart('0');

                if (trimA.Length != trimB.Length)
                {
                    return trimA.Length.CompareTo(trimB.Length);
                }

                int numCmp = trimA.CompareTo(trimB, StringComparison.Ordinal);
                if (numCmp != 0)
                {
                    return numCmp;
                }

                // Same numeric value (e.g. "01" vs "1"): deterministic tie-break on raw length
                if (spanA.Length != spanB.Length)
                {
                    return spanA.Length.CompareTo(spanB.Length);
                }
            }
            else
            {
                char ca = char.ToUpperInvariant(a[ia]);
                char cb = char.ToUpperInvariant(b[ib]);
                if (ca != cb)
                {
                    return ca.CompareTo(cb);
                }
                ia++;
                ib++;
            }
        }

        return (la - ia).CompareTo(lb - ib);
    }
}

/// <summary>
/// Validates manga archives (.zip / .cbz), enforces size/count/magic-byte budgets (A-05),
/// supports legacy Shift-JIS / GBK manga archive filenames, preserves natural page ordering (A-08),
/// verifies downloaded artifacts (A-04), and resolves safe unique output paths (E-01).
/// </summary>
public static class ZipPreprocessor
{
    private static readonly HashSet<string> SupportedArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".cbz"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff"
    };

    private static readonly char[] BatchUnsafeChars = ['&', '|', '<', '>', '^', '%'];

    static ZipPreprocessor()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Safe fallback if CodePages provider is unavailable in an exotic host.
        }
    }

    public static bool IsSupportedArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var ext = Path.GetExtension(path);
            return SupportedArchiveExtensions.Contains(ext);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool HasBatchUnsafePath(string path) =>
        !string.IsNullOrEmpty(path) && path.IndexOfAny(BatchUnsafeChars) >= 0;

    /// <summary>
    /// Fast offline precheck for UI enqueue before any GPU billing begins (E-01).
    /// Hardened to never leak uncaught filesystem/ZIP/compression exceptions to callers.
    /// </summary>
    public static ArchivePrecheckResult PrecheckArchive(
        string sourceZipPath,
        PackageBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        budget ??= PackageBudget.Default;

        if (string.IsNullOrWhiteSpace(sourceZipPath))
        {
            return new ArchivePrecheckResult(false, 0, 0, 0, false, "路径为空", "文件路径为空。");
        }

        if (Directory.Exists(sourceZipPath))
        {
            return new ArchivePrecheckResult(false, 0, 0, 0, false, "非压缩包", "路径是文件夹而非 .zip / .cbz 压缩包。");
        }

        if (!File.Exists(sourceZipPath))
        {
            return new ArchivePrecheckResult(false, 0, 0, 0, false, "文件不存在", "找不到源压缩包文件（可能已被移动或为未下载的虚拟路径）。");
        }

        if (!IsSupportedArchivePath(sourceZipPath))
        {
            return new ArchivePrecheckResult(false, 0, 0, 0, false, "格式不支持", "仅支持 .zip 或 .cbz 漫画压缩包。");
        }

        long fileLength = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            fileLength = new FileInfo(sourceZipPath).Length;
            if (fileLength <= 0)
            {
                return new ArchivePrecheckResult(false, 0, 0, 0, false, "空文件", "压缩包大小为 0 字节。");
            }

            if (fileLength > budget.MaxArchiveBytes)
            {
                return new ArchivePrecheckResult(
                    false, 0, fileLength, 0, false, "超出体积上限",
                    $"压缩包体积 {FormatSize(fileLength)} 超出上限 {FormatSize(budget.MaxArchiveBytes)}。");
            }

            using var archive = OpenZipWithBestEncoding(sourceZipPath, out bool usedLegacyEncoding);
            var scan = ScanAndValidateEntries(
                archive,
                sourceZipPath,
                budget,
                verifyActualBytes: false,
                forceRepack: usedLegacyEncoding,
                cancellationToken);
            var summary = scan.NeedsRepack
                ? $"{scan.ImageEntries.Count} 页 · {FormatSize(fileLength)} · 需自动整理顺序/拍平"
                : $"{scan.ImageEntries.Count} 页 · {FormatSize(fileLength)} · 可直传";
            return new ArchivePrecheckResult(
                true,
                scan.ImageEntries.Count,
                fileLength,
                scan.DeclaredUncompressedBytes,
                scan.NeedsRepack,
                summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            return new ArchivePrecheckResult(
                false, 0, fileLength, 0, false, "压缩格式不支持",
                "压缩包可能已加密或使用了不受支持的压缩算法（如 Deflate64 / LZMA / BZip2），请重新打包为标准无密码 ZIP 后再试。");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new ArchivePrecheckResult(
                false, 0, fileLength, 0, false, "无权访问",
                $"无法读取该文件（权限不足或正被其他程序占用）：{ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            return new ArchivePrecheckResult(false, 0, fileLength, 0, false, "校验失败", ex.Message);
        }
    }

    public static PreparedZipResult PrepareForUpload(
        string sourceZipPath,
        string jobId,
        string? tempRootDirectory = null,
        PackageBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        budget ??= PackageBudget.Default;

        if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("找不到源压缩包文件。", sourceZipPath);
        }

        if (!IsSupportedArchivePath(sourceZipPath))
        {
            throw new InvalidDataException("仅支持 .zip 或 .cbz 漫画压缩包。");
        }

        var fileLength = new FileInfo(sourceZipPath).Length;
        if (fileLength <= 0)
        {
            throw new InvalidDataException("压缩包为空文件（0 字节）。");
        }

        if (fileLength > budget.MaxArchiveBytes)
        {
            throw new InvalidDataException(
                $"压缩包体积 {FormatSize(fileLength)} 超出上限 {FormatSize(budget.MaxArchiveBytes)}。");
        }

        using var archive = OpenZipWithBestEncoding(sourceZipPath, out bool usedLegacyEncoding);
        var scan = ScanAndValidateEntries(
            archive,
            sourceZipPath,
            budget,
            verifyActualBytes: true,
            forceRepack: usedLegacyEncoding,
            cancellationToken);

        if (!scan.NeedsRepack)
        {
            return new PreparedZipResult(
                sourceZipPath,
                scan.ImageEntries.Count,
                WasFlattenedOrCopied: false,
                IsTemporaryFile: false);
        }

        var tempDir = tempRootDirectory ?? Path.Combine(Path.GetTempPath(), "manga_gui_prep");
        Directory.CreateDirectory(tempDir);
        var tempZipPath = Path.Combine(tempDir, $"{jobId}.zip");
        TryDeleteFile(tempZipPath);

        try
        {
            long totalCopiedBytes = 0;
            var buffer = new byte[81920];
            using (var outArchive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                int padWidth = Math.Max(4, scan.ImageEntries.Count.ToString().Length);
                var header = new byte[12];
                for (int i = 0; i < scan.ImageEntries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var srcEntry = scan.ImageEntries[i];
                    var declaredExt = Path.GetExtension(srcEntry.FullName).ToLowerInvariant();

                    using var srcStream = srcEntry.Open();
                    int headerRead = ReadHeaderBytes(srcStream, header);
                    if (headerRead == 0)
                    {
                        throw new InvalidDataException($"图片文件为空或已损坏：{srcEntry.FullName}");
                    }

                    var headerSpan = header.AsSpan(0, headerRead);
                    string effectiveExt;
                    if (HasValidImageMagicBytes(headerSpan, declaredExt))
                    {
                        effectiveExt = declaredExt;
                    }
                    else if (DetectImageFormatExtension(headerSpan) is string detected)
                    {
                        effectiveExt = detected;
                    }
                    else
                    {
                        throw new InvalidDataException($"图片文件头无效或已损坏：{srcEntry.FullName}");
                    }

                    long entryCopied = headerRead;
                    totalCopiedBytes += headerRead;
                    if (entryCopied > budget.MaxSingleEntryBytes)
                    {
                        throw new InvalidDataException(
                            $"单页图片解压后体积超出上限（{FormatSize(budget.MaxSingleEntryBytes)}）：{srcEntry.FullName}");
                    }
                    if (totalCopiedBytes > budget.MaxTotalUncompressedBytes)
                    {
                        throw new InvalidDataException(
                            $"压缩包解压总大小超出上限（{FormatSize(budget.MaxTotalUncompressedBytes)}），已中止预处理。");
                    }

                    var flatName = $"{(i + 1).ToString().PadLeft(padWidth, '0')}{effectiveExt}";
                    var dstEntry = outArchive.CreateEntry(flatName, CompressionLevel.NoCompression);
                    using var dstStream = dstEntry.Open();
                    dstStream.Write(header, 0, headerRead);

                    int read;
                    while ((read = srcStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        entryCopied += read;
                        totalCopiedBytes += read;

                        if (entryCopied > budget.MaxSingleEntryBytes)
                        {
                            throw new InvalidDataException(
                                $"单页图片解压后体积超出上限（{FormatSize(budget.MaxSingleEntryBytes)}）：{srcEntry.FullName}");
                        }

                        if (totalCopiedBytes > budget.MaxTotalUncompressedBytes)
                        {
                            throw new InvalidDataException(
                                $"压缩包解压总大小超出上限（{FormatSize(budget.MaxTotalUncompressedBytes)}），已中止预处理。");
                        }

                        dstStream.Write(buffer, 0, read);
                    }
                }
            }

            return new PreparedZipResult(
                tempZipPath,
                scan.ImageEntries.Count,
                WasFlattenedOrCopied: true,
                IsTemporaryFile: true,
                OriginalEntryPaths: scan.ImageEntries.Select(e => e.FullName.Replace('\\', '/')).ToList());
        }
        catch
        {
            TryDeleteFile(tempZipPath);
            throw;
        }
    }

    public static Task<PreparedZipResult> PrepareForUploadAsync(
        string sourceZipPath,
        string jobId,
        string? tempRootDirectory = null,
        PackageBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => PrepareForUpload(sourceZipPath, jobId, tempRootDirectory, budget, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Verifies a downloaded translated archive before promoting it to the final output path (A-04):
    /// checks non-empty size, exact byte length (if known), SHA256 (if known), valid ZIP structure,
    /// image count >= expectedMinImageCount, and valid image headers.
    /// </summary>
    public static DownloadedPackageVerification VerifyDownloadedPackage(
        string zipPath,
        long? expectedBytes = null,
        string? expectedSha256 = null,
        int expectedMinImageCount = 1,
        PackageBudget? budget = null)
    {
        budget ??= PackageBudget.Default;

        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            return new DownloadedPackageVerification(false, 0, 0, string.Empty, "下载文件不存在。");
        }

        var actualBytes = new FileInfo(zipPath).Length;
        if (actualBytes <= 0)
        {
            return new DownloadedPackageVerification(false, 0, 0, string.Empty, "下载文件为空（0 字节）。");
        }

        if (expectedBytes.HasValue && expectedBytes.Value > 0 && actualBytes != expectedBytes.Value)
        {
            return new DownloadedPackageVerification(
                false, 0, actualBytes, string.Empty,
                $"下载文件大小不匹配（预期 {expectedBytes.Value} 字节，实际 {actualBytes} 字节）。");
        }

        string actualSha256;
        try
        {
            using var fs = File.OpenRead(zipPath);
            actualSha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return new DownloadedPackageVerification(false, 0, actualBytes, string.Empty, $"计算下载文件校验和失败：{ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !string.Equals(actualSha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new DownloadedPackageVerification(
                false, 0, actualBytes, actualSha256,
                $"下载文件 SHA256 校验失败（预期 {expectedSha256[..Math.Min(12, expectedSha256.Length)]}...，实际 {actualSha256[..12]}...）。");
        }

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            int imageCount = 0;
            long totalDeclaredBytes = 0;
            var header = new byte[12];

            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(normalized) || normalized.EndsWith('/'))
                {
                    continue;
                }

                if (IsUnsafeEntryPath(normalized))
                {
                    return new DownloadedPackageVerification(
                        false, 0, actualBytes, actualSha256, $"下载压缩包包含非法路径：{entry.FullName}");
                }

                var ext = Path.GetExtension(normalized);
                if (!ImageExtensions.Contains(ext))
                {
                    continue;
                }

                if (entry.Length <= 0 || entry.Length > budget.MaxSingleEntryBytes)
                {
                    return new DownloadedPackageVerification(
                        false, imageCount, actualBytes, actualSha256, $"下载结果中的图片体积异常：{entry.FullName}");
                }

                totalDeclaredBytes += entry.Length;
                if (totalDeclaredBytes > budget.MaxTotalUncompressedBytes)
                {
                    return new DownloadedPackageVerification(
                        false, imageCount, actualBytes, actualSha256, "下载结果解压总大小超出安全上限。");
                }

                using var stream = entry.Open();
                int headerRead = ReadHeaderBytes(stream, header);
                var headerSpan = header.AsSpan(0, headerRead);
                if (!HasValidImageMagicBytes(headerSpan, ext) && DetectImageFormatExtension(headerSpan) is null)
                {
                    return new DownloadedPackageVerification(
                        false, imageCount, actualBytes, actualSha256, $"下载结果中的图片已损坏（魔数不匹配）：{entry.FullName}");
                }

                imageCount++;
            }

            if (imageCount == 0)
            {
                return new DownloadedPackageVerification(
                    false, 0, actualBytes, actualSha256, "下载的压缩包不包含任何有效翻译图片。");
            }

            if (expectedMinImageCount > 0 && imageCount < expectedMinImageCount)
            {
                return new DownloadedPackageVerification(
                    false, imageCount, actualBytes, actualSha256,
                    $"下载结果缺页（预期至少 {expectedMinImageCount} 页，实际仅 {imageCount} 页）。");
            }

            return new DownloadedPackageVerification(true, imageCount, actualBytes, actualSha256);
        }
        catch (NotSupportedException)
        {
            return new DownloadedPackageVerification(
                false, 0, actualBytes, actualSha256, "下载的压缩包使用了不受支持的压缩格式或已加密。");
        }
        catch (InvalidDataException ex)
        {
            return new DownloadedPackageVerification(
                false, 0, actualBytes, actualSha256, $"下载的压缩包结构损坏或截断：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new DownloadedPackageVerification(
                false, 0, actualBytes, actualSha256, $"读取下载压缩包失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Rewrites a translated archive whose pages are named by upload order (0001.jpg, 0002.png, …) so
    /// each page gets back its original folder and file name; the extension produced by the translator
    /// is kept. Entries that do not map to a page, or would collide, keep their current name.
    /// </summary>
    public static void RestoreOriginalNames(string translatedZipPath, IReadOnlyList<string> originalEntryPaths, string destinationZipPath)
    {
        TryDeleteFile(destinationZipPath);
        try
        {
            using var source = ZipFile.OpenRead(translatedZipPath);
            using var destination = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[81920];

            foreach (var entry in source.Entries)
            {
                var currentName = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(currentName) || currentName.EndsWith('/'))
                {
                    continue;
                }

                var targetName = currentName;
                var stem = Path.GetFileNameWithoutExtension(currentName);
                if (!currentName.Contains('/')
                    && int.TryParse(stem, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pageNumber)
                    && pageNumber >= 1
                    && pageNumber <= originalEntryPaths.Count)
                {
                    var original = originalEntryPaths[pageNumber - 1].Replace('\\', '/');
                    var slash = original.LastIndexOf('/');
                    var directory = slash >= 0 ? original[..(slash + 1)] : string.Empty;
                    var restored = directory + Path.GetFileNameWithoutExtension(original) + Path.GetExtension(currentName);
                    if (!IsUnsafeEntryPath(restored))
                    {
                        targetName = restored;
                    }
                }

                if (!usedNames.Add(targetName))
                {
                    targetName = currentName;
                    if (!usedNames.Add(targetName))
                    {
                        throw new InvalidDataException($"还原页名时出现重复条目：{currentName}");
                    }
                }

                var destinationEntry = destination.CreateEntry(targetName, CompressionLevel.NoCompression);
                using var input = entry.Open();
                using var output = destinationEntry.Open();
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                }
            }
        }
        catch
        {
            TryDeleteFile(destinationZipPath);
            throw;
        }
    }

    public static string ResolveUniqueOutputPath(string sourceZipPath, string? outputDirectory = null, string suffix = "_translated")
    {
        var dir = !string.IsNullOrWhiteSpace(outputDirectory)
            ? outputDirectory.Trim()
            : (Path.GetDirectoryName(sourceZipPath) ?? Environment.CurrentDirectory);
        var baseName = Path.GetFileNameWithoutExtension(sourceZipPath);
        var ext = Path.GetExtension(sourceZipPath);
        if (!SupportedArchiveExtensions.Contains(ext))
        {
            ext = ".zip";
        }
        else
        {
            ext = ext.ToLowerInvariant();
        }

        var candidate = Path.Combine(dir, $"{baseName}{suffix}{ext}");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (int index = 2; index < 10_000; index++)
        {
            var next = Path.Combine(dir, $"{baseName}{suffix} ({index}){ext}");
            if (!File.Exists(next))
            {
                return next;
            }
        }

        return Path.Combine(dir, $"{baseName}{suffix}_{DateTime.Now:yyyyMMddHHmmss}{ext}");
    }

    public static bool HasValidImageMagicBytes(ReadOnlySpan<byte> header, string extension)
    {
        if (header.Length < 2)
        {
            return false;
        }

        switch (extension.ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
                return header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

            case ".png":
                return header.Length >= 8
                       && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                       && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;

            case ".webp":
                return header.Length >= 12
                       && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                       && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P';

            case ".bmp":
                return header[0] == 0x42 && header[1] == 0x4D;

            case ".tif":
            case ".tiff":
                return header.Length >= 4
                       && ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00)
                           || (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A));

            default:
                return false;
        }
    }

    /// <summary>
    /// Detects the true image format extension from magic bytes, allowing real-world manga archives
    /// where a PNG or WebP image was saved with a ".jpg" filename to be safely repacked with its true extension.
    /// </summary>
    public static string? DetectImageFormatExtension(ReadOnlySpan<byte> header)
    {
        if (HasValidImageMagicBytes(header, ".jpg")) return ".jpg";
        if (HasValidImageMagicBytes(header, ".png")) return ".png";
        if (HasValidImageMagicBytes(header, ".webp")) return ".webp";
        if (HasValidImageMagicBytes(header, ".bmp")) return ".bmp";
        if (HasValidImageMagicBytes(header, ".tif")) return ".tif";
        return null;
    }

    /// <summary>
    /// Opens a ZIP archive using UTF-8 first, falling back to Shift-JIS (CP932) and GBK (CP936)
    /// when legacy Japanese/Chinese archives lack the UTF-8 general-purpose bit flag (bit 11).
    /// </summary>
    private static ZipArchive OpenZipWithBestEncoding(string zipPath, out bool usedLegacyEncoding)
    {
        usedLegacyEncoding = false;
        var utf8Archive = ZipFile.OpenRead(zipPath);
        int utf8Replacements = CountReplacementChars(utf8Archive);
        if (utf8Replacements == 0)
        {
            return utf8Archive;
        }

        // Legacy archive without UTF-8 flag: try Japanese Shift-JIS (932) then Chinese GBK (936).
        int[] fallbackCodePages = [932, 936];
        ZipArchive bestArchive = utf8Archive;
        int bestReplacements = utf8Replacements;

        foreach (int codePage in fallbackCodePages)
        {
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(codePage);
            }
            catch
            {
                continue;
            }

            ZipArchive? candidate = null;
            try
            {
                candidate = ZipFile.Open(zipPath, ZipArchiveMode.Read, encoding);
                int replacements = CountReplacementChars(candidate);
                if (replacements < bestReplacements)
                {
                    bestArchive.Dispose();
                    bestArchive = candidate;
                    bestReplacements = replacements;
                    usedLegacyEncoding = true;
                    candidate = null;

                    if (bestReplacements == 0)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Ignore encoding open failures and keep best candidate so far.
            }
            finally
            {
                candidate?.Dispose();
            }
        }

        return bestArchive;
    }

    private static int CountReplacementChars(ZipArchive archive)
    {
        int count = 0;
        foreach (var entry in archive.Entries)
        {
            foreach (char ch in entry.FullName)
            {
                if (ch == '\uFFFD')
                {
                    count++;
                }
            }
        }
        return count;
    }

    private sealed record ScanResult(
        List<ZipArchiveEntry> ImageEntries,
        long DeclaredUncompressedBytes,
        bool NeedsRepack,
        Dictionary<ZipArchiveEntry, string> CorrectedExtensions);

    private static ScanResult ScanAndValidateEntries(
        ZipArchive archive,
        string sourceZipPath,
        PackageBudget budget,
        bool verifyActualBytes,
        bool forceRepack,
        CancellationToken cancellationToken)
    {
        var imageEntries = new List<ZipArchiveEntry>();
        var correctedExtensions = new Dictionary<ZipArchiveEntry, string>();
        var seenNormalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasNonFlatStructure = false;
        bool hasExtraItems = false;
        long totalDeclaredBytes = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedName = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(normalizedName))
            {
                continue;
            }

            if (IsUnsafeEntryPath(normalizedName))
            {
                throw new InvalidDataException($"压缩包包含非法或越界路径：{entry.FullName}");
            }

            // Ignore macOS metadata artifacts (triggers repack so VM only sees real pages).
            if (normalizedName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(normalizedName).StartsWith("._", StringComparison.Ordinal))
            {
                hasExtraItems = true;
                continue;
            }

            if (normalizedName.EndsWith('/'))
            {
                hasNonFlatStructure = true;
                continue;
            }

            var ext = Path.GetExtension(normalizedName);
            if (!ImageExtensions.Contains(ext))
            {
                hasExtraItems = true;
                continue;
            }

            if (!seenNormalizedPaths.Add(normalizedName))
            {
                throw new InvalidDataException($"压缩包中存在重复图片路径：{entry.FullName}");
            }

            if (normalizedName.Contains('/'))
            {
                hasNonFlatStructure = true;
            }

            if (entry.Length <= 0)
            {
                throw new InvalidDataException($"检测到空图片文件（0 字节）：{entry.FullName}");
            }

            if (entry.Length > budget.MaxSingleEntryBytes)
            {
                throw new InvalidDataException(
                    $"单页图片体积 {FormatSize(entry.Length)} 超出上限 {FormatSize(budget.MaxSingleEntryBytes)}：{entry.FullName}");
            }

            totalDeclaredBytes += entry.Length;
            if (totalDeclaredBytes > budget.MaxTotalUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"压缩包解压总大小超出上限 {FormatSize(budget.MaxTotalUncompressedBytes)}。");
            }

            imageEntries.Add(entry);
            if (imageEntries.Count > budget.MaxImageCount)
            {
                throw new InvalidDataException(
                    $"图片页数超出单次任务上限（最多 {budget.MaxImageCount} 页）。");
            }
        }

        if (imageEntries.Count == 0)
        {
            throw new InvalidDataException("压缩包中未检测到任何支持的图片文件（jpg/png/webp/bmp/tif）。");
        }

        // Natural-order sorting (A-08)
        var naturalSorted = new List<ZipArchiveEntry>(imageEntries);
        naturalSorted.Sort((a, b) => NaturalPathComparer.Instance.Compare(
            a.FullName.Replace('\\', '/'),
            b.FullName.Replace('\\', '/')));

        // Check whether natural order differs from standard ASCII/ordinal filename order (e.g., 1.jpg, 2.jpg, 10.jpg)
        var ordinalSorted = new List<ZipArchiveEntry>(imageEntries);
        ordinalSorted.Sort((a, b) => StringComparer.Ordinal.Compare(
            a.FullName.Replace('\\', '/'),
            b.FullName.Replace('\\', '/')));

        bool orderDiffersFromOrdinal = false;
        for (int i = 0; i < naturalSorted.Count; i++)
        {
            if (!ReferenceEquals(naturalSorted[i], ordinalSorted[i]))
            {
                orderDiffersFromOrdinal = true;
                break;
            }
        }

        bool hasBatchUnsafePath = HasBatchUnsafePath(sourceZipPath);
        bool needsRepack = forceRepack || hasNonFlatStructure || hasExtraItems || hasBatchUnsafePath || orderDiffersFromOrdinal;

        // If passing through directly OR running precheck, verify magic bytes (and actual decompressed size on prepare).
        if (!needsRepack || !verifyActualBytes)
        {
            long totalActualBytes = 0;
            var header = new byte[12];
            var drain = verifyActualBytes ? new byte[65536] : null;

            foreach (var entry in naturalSorted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(entry.FullName);

                using var stream = entry.Open();
                int headerRead = ReadHeaderBytes(stream, header);
                var headerSpan = header.AsSpan(0, headerRead);
                if (!HasValidImageMagicBytes(headerSpan, ext))
                {
                    var detectedExt = DetectImageFormatExtension(headerSpan);
                    if (detectedExt is not null)
                    {
                        correctedExtensions[entry] = detectedExt;
                        needsRepack = true;
                    }
                    else
                    {
                        throw new InvalidDataException($"图片文件头无效或已损坏（非合法 {ext} 图片）：{entry.FullName}");
                    }
                }

                if (verifyActualBytes && drain is not null)
                {
                    long entryActual = headerRead;
                    totalActualBytes += headerRead;
                    int r;
                    while ((r = stream.Read(drain, 0, drain.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        entryActual += r;
                        totalActualBytes += r;
                        if (entryActual > budget.MaxSingleEntryBytes)
                        {
                            throw new InvalidDataException(
                                $"单页图片实际解压大小超出上限 {FormatSize(budget.MaxSingleEntryBytes)}：{entry.FullName}");
                        }
                        if (totalActualBytes > budget.MaxTotalUncompressedBytes)
                        {
                            throw new InvalidDataException(
                                $"压缩包实际解压总大小超出上限 {FormatSize(budget.MaxTotalUncompressedBytes)}。");
                        }
                    }
                }
            }
        }

        return new ScanResult(naturalSorted, totalDeclaredBytes, needsRepack, correctedExtensions);
    }

    private static bool IsUnsafeEntryPath(string normalizedPath)
    {
        if (normalizedPath.Contains('\0'))
        {
            return true;
        }

        if (normalizedPath.StartsWith('/') || normalizedPath.StartsWith('\\'))
        {
            return true;
        }

        var segments = normalizedPath.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg == "..")
            {
                return true;
            }
            if (i == 0 && seg.Contains(':'))
            {
                return true;
            }
        }

        return false;
    }

    private static int ReadHeaderBytes(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
