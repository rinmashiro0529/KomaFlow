using System.IO;
using System.IO.Compression;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests;

public sealed class ZipPreprocessorTests : IDisposable
{
    private readonly string _tempDir;

    public ZipPreprocessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"manga_gui_zip_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void PrepareForUpload_FlattensNestedDirectoryAndIgnoresMacOsMetadata()
    {
        var zipPath = Path.Combine(_tempDir, "[作者] 测试漫画 (第1卷).zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteTextEntry(archive, "__MACOSX/._001.jpg", "garbage");
            TestImageFixtures.WriteBytesEntry(archive, "Chapter01/002.jpg", TestImageFixtures.CreateValidJpeg(2));
            TestImageFixtures.WriteBytesEntry(archive, "Chapter01/001.jpg", TestImageFixtures.CreateValidJpeg(1));
            WriteTextEntry(archive, "Chapter01/readme.txt", "notes");
            TestImageFixtures.WriteBytesEntry(archive, "Chapter02/003.png", TestImageFixtures.CreateValidPng(3));
        }

        var result = ZipPreprocessor.PrepareForUpload(zipPath, "job_test_001", _tempDir);

        Assert.True(result.WasFlattenedOrCopied);
        Assert.True(result.IsTemporaryFile);
        Assert.Equal(3, result.ImageCount);

        using var flat = ZipFile.OpenRead(result.UploadZipPath);
        var names = flat.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(["0001.jpg", "0002.jpg", "0003.png"], names);
    }

    [Fact]
    public void PrepareForUpload_PassesThroughCleanFlatZipDirectly()
    {
        var zipPath = Path.Combine(_tempDir, "clean_book.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(archive, "001.jpg", TestImageFixtures.CreateValidJpeg(1));
            TestImageFixtures.WriteBytesEntry(archive, "002.jpg", TestImageFixtures.CreateValidJpeg(2));
        }

        var result = ZipPreprocessor.PrepareForUpload(zipPath, "job_test_002", _tempDir);

        Assert.False(result.WasFlattenedOrCopied);
        Assert.False(result.IsTemporaryFile);
        Assert.Equal(zipPath, result.UploadZipPath);
        Assert.Equal(2, result.ImageCount);
    }

    [Fact]
    public void PrepareForUpload_PreservesNaturalPageOrderAcrossChaptersAndUnpaddedNumbers()
    {
        // A-08: 1.jpg, 2.jpg, 10.jpg MUST stay in natural order 1 -> 2 -> 10 (NOT 1 -> 10 -> 2!)
        var zipPath = Path.Combine(_tempDir, "unpadded_natural.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(archive, "ch1/10.jpg", TestImageFixtures.CreateValidJpeg(10));
            TestImageFixtures.WriteBytesEntry(archive, "ch1/2.jpg", TestImageFixtures.CreateValidJpeg(2));
            TestImageFixtures.WriteBytesEntry(archive, "ch1/1.jpg", TestImageFixtures.CreateValidJpeg(1));
            TestImageFixtures.WriteBytesEntry(archive, "ch2/1.jpg", TestImageFixtures.CreateValidJpeg(21));
            TestImageFixtures.WriteBytesEntry(archive, "ch10/1.jpg", TestImageFixtures.CreateValidJpeg(101));
        }

        var result = ZipPreprocessor.PrepareForUpload(zipPath, "job_natural", _tempDir);

        Assert.True(result.WasFlattenedOrCopied);
        Assert.Equal(5, result.ImageCount);

        using var flat = ZipFile.OpenRead(result.UploadZipPath);
        var tagBytes = flat.Entries.Select(e =>
        {
            using var ms = new MemoryStream();
            using var s = e.Open();
            s.CopyTo(ms);
            return ms.ToArray()[11]; // markerTag byte
        }).ToList();

        Assert.Equal([1, 2, 10, 21, 101], tagBytes);
    }

    [Fact]
    public void PrepareForUpload_EnforcesBudgetsMagicBytesPathTraversalAndCbz()
    {
        // 1. Rejects invalid magic bytes (A-05)
        var fakeImgZip = Path.Combine(_tempDir, "fake_img.zip");
        using (var z = ZipFile.Open(fakeImgZip, ZipArchiveMode.Create))
        {
            WriteTextEntry(z, "001.jpg", "not actually a jpeg header");
        }
        Assert.Throws<InvalidDataException>(() => ZipPreprocessor.PrepareForUpload(fakeImgZip, "job_bad_magic", _tempDir));

        // 2. Rejects path traversal (A-05)
        var evilZip = Path.Combine(_tempDir, "evil.zip");
        using (var z = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(z, "../escape.jpg", TestImageFixtures.CreateValidJpeg(1));
        }
        Assert.Throws<InvalidDataException>(() => ZipPreprocessor.PrepareForUpload(evilZip, "job_evil", _tempDir));

        // 3. Rejects exceeding MaxImageCount & MaxTotalUncompressedBytes with small budget (A-05)
        var threePageCbz = Path.Combine(_tempDir, "volume1.cbz");
        using (var z = ZipFile.Open(threePageCbz, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(z, "1.jpg", TestImageFixtures.CreateValidJpeg(1));
            TestImageFixtures.WriteBytesEntry(z, "2.jpg", TestImageFixtures.CreateValidJpeg(2));
            TestImageFixtures.WriteBytesEntry(z, "3.jpg", TestImageFixtures.CreateValidJpeg(3));
        }

        var tinyCountBudget = new PackageBudget { MaxImageCount = 2 };
        Assert.Throws<InvalidDataException>(() => ZipPreprocessor.PrepareForUpload(threePageCbz, "job_cnt", _tempDir, tinyCountBudget));

        var tinyBytesBudget = new PackageBudget { MaxTotalUncompressedBytes = 20 };
        Assert.Throws<InvalidDataException>(() => ZipPreprocessor.PrepareForUpload(threePageCbz, "job_bytes", _tempDir, tinyBytesBudget));

        // 4. Accepts .cbz with default budget & offline PrecheckArchive succeeds (E-01)
        var precheck = ZipPreprocessor.PrecheckArchive(threePageCbz);
        Assert.True(precheck.IsValid);
        Assert.Equal(3, precheck.ImageCount);

        var cbzOut = ZipPreprocessor.ResolveUniqueOutputPath(threePageCbz, Path.Combine(_tempDir, "custom_out"));
        Assert.EndsWith("volume1_translated.cbz", cbzOut);
        Assert.Contains("custom_out", cbzOut);
    }

    [Fact]
    public void VerifyDownloadedPackage_ValidatesSizeSha256StructureAndPageCount()
    {
        // Valid package passes (A-04)
        var validPath = Path.Combine(_tempDir, "valid_out.zip");
        var (bytes, sha) = TestImageFixtures.CreateValidTranslatedZip(validPath, pageCount: 3);

        var ok = ZipPreprocessor.VerifyDownloadedPackage(validPath, bytes, sha, expectedMinImageCount: 3);
        Assert.True(ok.IsValid);
        Assert.Equal(3, ok.ImageCount);

        // Wrong SHA256 rejected
        var badSha = ZipPreprocessor.VerifyDownloadedPackage(validPath, bytes, new string('0', 64), expectedMinImageCount: 3);
        Assert.False(badSha.IsValid);

        // Missing pages rejected
        var shortPages = ZipPreprocessor.VerifyDownloadedPackage(validPath, bytes, sha, expectedMinImageCount: 4);
        Assert.False(shortPages.IsValid);

        // Empty 22-byte EOCD zip rejected!
        var emptyZipPath = Path.Combine(_tempDir, "empty_22.zip");
        File.WriteAllBytes(emptyZipPath, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var emptyRes = ZipPreprocessor.VerifyDownloadedPackage(emptyZipPath, expectedMinImageCount: 1);
        Assert.False(emptyRes.IsValid);

        // Truncated/corrupt zip rejected!
        var truncPath = Path.Combine(_tempDir, "truncated.zip");
        File.WriteAllBytes(truncPath, [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00]);
        var truncRes = ZipPreprocessor.VerifyDownloadedPackage(truncPath, expectedMinImageCount: 1);
        Assert.False(truncRes.IsValid);
    }

    [Fact]
    public void ResolveUniqueOutputPath_AppendsSuffixWhenFileAlreadyExists()
    {
        var src = Path.Combine(_tempDir, "Sejyo.zip");
        var first = ZipPreprocessor.ResolveUniqueOutputPath(src);
        Assert.Equal(Path.Combine(_tempDir, "Sejyo_translated.zip"), first);

        File.WriteAllText(first, "existing");
        var second = ZipPreprocessor.ResolveUniqueOutputPath(src);
        Assert.Equal(Path.Combine(_tempDir, "Sejyo_translated (2).zip"), second);
    }

    [Fact]
    public void PrecheckAndPrepare_HandlesShiftJisLegacyEncodingArchiveWithoutCorruption()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var shiftJis = System.Text.Encoding.GetEncoding(932);
        var zipPath = Path.Combine(_tempDir, "shift_jis_manga.zip");

        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: shiftJis))
        {
            TestImageFixtures.WriteBytesEntry(archive, "第01巻/ページ2.jpg", TestImageFixtures.CreateValidJpeg(2));
            TestImageFixtures.WriteBytesEntry(archive, "第01巻/ページ1.jpg", TestImageFixtures.CreateValidJpeg(1));
        }

        var precheck = ZipPreprocessor.PrecheckArchive(zipPath);
        Assert.True(precheck.IsValid, precheck.ErrorMessage);
        Assert.Equal(2, precheck.ImageCount);
        Assert.True(precheck.NeedsRepack);

        var prep = ZipPreprocessor.PrepareForUpload(zipPath, "job_sjis", _tempDir);
        Assert.True(prep.WasFlattenedOrCopied);
        Assert.Equal(2, prep.ImageCount);

        using var flat = ZipFile.OpenRead(prep.UploadZipPath);
        Assert.Equal(["0001.jpg", "0002.jpg"], flat.Entries.Select(e => e.FullName).ToList());
    }

    [Fact]
    public void PrecheckAndPrepare_AutoCorrectsMisnamedValidImageExtension()
    {
        // S-02: Real PNG image saved with .jpg extension (common in scraped manga) must pass and repack as .png!
        var zipPath = Path.Combine(_tempDir, "misnamed_png_as_jpg.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(archive, "001.jpg", TestImageFixtures.CreateValidPng(7));
            TestImageFixtures.WriteBytesEntry(archive, "002.jpg", TestImageFixtures.CreateValidJpeg(8));
        }

        var precheck = ZipPreprocessor.PrecheckArchive(zipPath);
        Assert.True(precheck.IsValid, precheck.ErrorMessage);
        Assert.Equal(2, precheck.ImageCount);
        Assert.True(precheck.NeedsRepack);

        var prep = ZipPreprocessor.PrepareForUpload(zipPath, "job_misnamed", _tempDir);
        Assert.True(prep.WasFlattenedOrCopied);
        Assert.Equal(2, prep.ImageCount);

        using var flat = ZipFile.OpenRead(prep.UploadZipPath);
        Assert.Equal(["0001.png", "0002.jpg"], flat.Entries.Select(e => e.FullName).ToList());
    }

    [Fact]
    public void PrecheckArchive_GracefullyRejectsMissingDirectoryAndUnsupportedCompressionWithoutThrowing()
    {
        // Missing file
        var missing = ZipPreprocessor.PrecheckArchive(Path.Combine(_tempDir, "does_not_exist.zip"));
        Assert.False(missing.IsValid);
        Assert.Contains("找不到", missing.ErrorMessage);

        // Directory path
        var dirRes = ZipPreprocessor.PrecheckArchive(_tempDir);
        Assert.False(dirRes.IsValid);

        // Crafted ZIP whose compression method is patched to 9 (Deflate64 -> NotSupportedException on entry.Open())
        var unsupportedZip = Path.Combine(_tempDir, "deflate64_like.zip");
        using (var archive = ZipFile.Open(unsupportedZip, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(archive, "001.jpg", TestImageFixtures.CreateValidJpeg(1));
        }
        var bytes = File.ReadAllBytes(unsupportedZip);
        // Patch compression method in local file header (offset 8..9) and central directory header (PK\x01\x02 + 10..11)
        bytes[8] = 9;
        bytes[9] = 0;
        for (int i = 0; i < bytes.Length - 12; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
            {
                bytes[i + 10] = 9;
                bytes[i + 11] = 0;
                break;
            }
        }
        File.WriteAllBytes(unsupportedZip, bytes);

        var res = ZipPreprocessor.PrecheckArchive(unsupportedZip);
        Assert.False(res.IsValid);
        Assert.NotNull(res.ErrorMessage);
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
