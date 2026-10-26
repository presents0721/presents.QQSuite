using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace QQSuite.JmShare;

/// <summary>把下载好的图片目录打包成 ZIP（可选 ZipCrypto 加密）。</summary>
public static class JmPacker
{
    /// <summary>打包目录下所有文件（不递归），返回压缩包字节数。</summary>
    public static long PackDirectory(string directory, string zipPath)
        => PackDirectory(directory, zipPath, null);

    /// <summary>
    /// 打包目录下所有文件（不递归），password 非空时改用 ZipCrypto 加密。
    /// 返回压缩包字节数。
    /// </summary>
    public static long PackDirectory(string directory, string zipPath, string? password)
    {
        string[] files = ListPackableFiles(directory, zipPath);
        if (!string.IsNullOrEmpty(password))
        {
            return JmZipCrypto.Pack(files, zipPath, password);
        }

        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (FileStream output = new(zipPath, FileMode.CreateNew, FileAccess.Write))
        using (ZipArchive archive = new(output, ZipArchiveMode.Create))
        {
            foreach (string file in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                using Stream entryStream = entry.Open();
                using FileStream source = File.OpenRead(file);
                source.CopyTo(entryStream);
            }
        }

        return new FileInfo(zipPath).Length;
    }

    /// <summary>
    /// 列出目录下应当打包的文件（页序）：排除压缩包自身与还原过程留下的 .src 中间文件。
    /// </summary>
    public static string[] ListPackableFiles(string directory, string zipPath)
        => Directory.GetFiles(directory)
            .Where(file => !string.Equals(file, zipPath, StringComparison.OrdinalIgnoreCase)
                           && !file.EndsWith(".src", StringComparison.OrdinalIgnoreCase)
                           && !file.EndsWith(".src.jpg", StringComparison.OrdinalIgnoreCase)
                           && !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                           && !file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
