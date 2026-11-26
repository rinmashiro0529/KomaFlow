using System.IO;
using System.Security.Cryptography;

namespace MangaGui.Services;

/// <summary>
/// Local helpers for moving large archives through `colab upload` / `colab download`, which send a
/// whole file as one Base64 request: split into parts, re-join, hash, and size-based timeouts.
/// </summary>
public static class ChunkedTransfer
{
    /// <summary>Largest file sent in a single request; larger ones are split into parts of this size.</summary>
    public const long DefaultChunkBytes = 48L * 1024 * 1024;

    public static IReadOnlyList<string> SplitFile(string sourcePath, string partsDirectory, long chunkBytes)
    {
        if (chunkBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkBytes));
        }

        if (Directory.Exists(partsDirectory))
        {
            Directory.Delete(partsDirectory, recursive: true);
        }
        Directory.CreateDirectory(partsDirectory);

        var parts = new List<string>();
        var buffer = new byte[81920];
        using var source = File.OpenRead(sourcePath);
        long remaining = source.Length;
        while (remaining > 0)
        {
            var partPath = Path.Combine(partsDirectory, $"part_{parts.Count:D4}");
            long partRemaining = Math.Min(chunkBytes, remaining);
            using (var part = File.Create(partPath))
            {
                while (partRemaining > 0)
                {
                    int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, partRemaining));
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"读取 {sourcePath} 时提前结束。");
                    }

                    part.Write(buffer, 0, read);
                    partRemaining -= read;
                    remaining -= read;
                }
            }

            parts.Add(partPath);
        }

        return parts;
    }

    public static void ConcatFiles(IReadOnlyList<string> partPaths, string destinationPath)
    {
        using var destination = File.Create(destinationPath);
        foreach (var partPath in partPaths)
        {
            using var part = File.OpenRead(partPath);
            part.CopyTo(destination);
        }
    }

    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>10 minutes plus the time needed at 256 KiB/s, capped at 30 minutes.</summary>
    public static TimeSpan EstimateTimeout(long bytes)
    {
        double minutes = 10 + Math.Max(0, bytes) / (256.0 * 1024) / 60;
        return TimeSpan.FromMinutes(Math.Min(30, minutes));
    }
}
