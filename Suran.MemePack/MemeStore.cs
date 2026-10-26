using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Alife.Foundation;

namespace QQSuite.MemePack;

/// <summary>一张已收藏的表情包。</summary>
public sealed class MemeEntry
{
    /// <summary>内容 MD5 前 12 位，同时用于去重。</summary>
    public string Id { get; set; } = "";

    /// <summary>相对表情包目录的文件名。</summary>
    public string File { get; set; } = "";

    /// <summary>语义描述（视觉模型生成），用于按含义检索。</summary>
    public string Description { get; set; } = "";

    /// <summary>收藏时间。</summary>
    public string AddedAt { get; set; } = "";

    /// <summary>来源（URL 或本地路径），便于回溯。</summary>
    public string Source { get; set; } = "";

    /// <summary>QQ 表情包 emoji_id；为空表示这只是普通图片。</summary>
    public string EmojiId { get; set; } = "";

    /// <summary>QQ 表情包 emoji_package_id。</summary>
    public string EmojiPackageId { get; set; } = "";

    /// <summary>QQ 表情包的 key。</summary>
    public string EmojiKey { get; set; } = "";

    /// <summary>QQ 表情包的 summary 文案。</summary>
    public string Summary { get; set; } = "";

    /// <summary>被使用次数，用于清单排序。</summary>
    public int UseCount { get; set; }

    /// <summary>最近一次使用时间。</summary>
    public string LastUsed { get; set; } = "";

    /// <summary>是否具备原生表情包发送条件。</summary>
    public bool HasMface => !string.IsNullOrWhiteSpace(EmojiId) && !string.IsNullOrWhiteSpace(EmojiPackageId);
}

/// <summary>
/// 表情包库存：图片落在插件私有目录，元数据（含语义描述）写在 index.json。
/// 目录结构与 JmStateStore 一致，便于整体打包迁移。
/// </summary>
public static class MemeStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    static readonly object Gate = new();

    /// <summary>表情包目录：Storage/Plugins/presents.QQSuite/meme。</summary>
    public static string Directory
    {
        get
        {
            try
            {
                return Path.Combine(AlifePath.StorageFolderPath, "Plugins", "presents.QQSuite", "meme");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "presents.QQSuite", "meme");
            }
        }
    }

    static string IndexPath => Path.Combine(Directory, "index.json");

    /// <summary>读取索引；文件缺失或损坏时返回空列表。</summary>
    public static List<MemeEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(IndexPath)) return new List<MemeEntry>();
                string json = File.ReadAllText(IndexPath);
                List<MemeEntry>? list = JsonSerializer.Deserialize<List<MemeEntry>>(json, Options);
                return list ?? new List<MemeEntry>();
            }
            catch
            {
                return new List<MemeEntry>();
            }
        }
    }

    /// <summary>原子写入索引（先写 .tmp 再替换）。</summary>
    public static void Save(List<MemeEntry> entries)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string temp = IndexPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
                File.Move(temp, IndexPath, true);
            }
            catch
            {
                // 落盘失败不影响本次会话使用
            }
        }
    }

    /// <summary>按 ID 查找。</summary>
    public static MemeEntry? Find(List<MemeEntry> entries, string id) =>
        entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>条目的绝对路径。</summary>
    public static string FullPath(MemeEntry entry) => Path.Combine(Directory, entry.File);

    /// <summary>计算内容指纹。</summary>
    public static string Fingerprint(byte[] bytes)
    {
        byte[] hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>按文件头猜扩展名，避免把 webp 存成 jpg 导致客户端解不出。</summary>
    public static string GuessExtension(byte[] bytes, string? hint)
    {
        if (bytes.Length >= 12)
        {
            // GIF87a / GIF89a
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
            // PNG
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            // WEBP: RIFF....WEBP
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
            // JPEG
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
        }

        string ext = Path.GetExtension(hint ?? "").ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" ? ext : ".jpg";
    }

    /// <summary>把字节写入库存并返回相对文件名。</summary>
    public static string WriteImage(string id, byte[] bytes, string? hint)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string name = id + GuessExtension(bytes, hint);
        File.WriteAllBytes(Path.Combine(Directory, name), bytes);
        return name;
    }
}
