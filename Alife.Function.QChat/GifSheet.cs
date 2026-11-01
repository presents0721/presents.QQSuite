using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using QQSuite.MemePack;

namespace QQSuite.Core;

/// <summary>
/// 动图转一屏：上游多模态管线通常只保留 GIF 第一帧，
/// 这里先在本地抽帧拼成一张静图，把路径写进消息，AI 再用 LoadImage 打开就能看到动作。
/// </summary>
public static class GifSheet
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>抽帧上限。9~16 张对动作理解已经够用，再多只是烧 token。</summary>
    const int MaxSamples = 16;

    /// <summary>拼图长边上限。再大也会被上游压回去，单格反而更糊。</summary>
    const int MaxEdge = 1200;

    static readonly Regex PlaceholderPattern = new(@"\[图片: (?<v>[^\]]+)\]", RegexOptions.Compiled);

    static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "QQSuite-gif");

    /// <summary>只认 QQ 图床，避免群友发的任意链接被本地拉取。</summary>
    static bool IsQqImageHost(Uri uri) =>
        uri.Scheme is "http" or "https" &&
        (uri.Host.Contains("qpic.cn", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Contains("qq.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Contains("qq.com.cn", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把已经过 FilterImage 的文本里、指向 QQ 图床的动图占位符换成「已抽帧拼图 + 本地路径」。
    /// 非动图、未开启、下载或解码失败时返回 null，调用方继续沿用原占位符。
    /// </summary>
    public static async Task<string?> TryReplaceAsync(string text, QChatServiceConfig? config)
    {
        if (config is not { GifSheetEnabled: true }) return null;
        if (text.IndexOf("[图片:", StringComparison.Ordinal) < 0) return null;

        int maxSamples = Math.Clamp(config.GifSheetSamples, 3, MaxSamples);
        int maxMb = Math.Clamp(config.GifSheetMaxMb, 1, 32);

        StringBuilder sb = new(text.Length + 128);
        int last = 0;
        bool replaced = false;
        foreach (Match match in PlaceholderPattern.Matches(text))
        {
            sb.Append(text, last, match.Index - last);
            last = match.Index + match.Length;

            string note = await TryBuildAsync(match.Groups["v"].Value.Trim(), maxSamples, maxMb);
            if (note == null)
            {
                sb.Append(match.Value); // 原样保留，静态图走原占位符
                continue;
            }
            sb.Append(note);
            replaced = true;
        }
        if (replaced == false) return null;
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    static async Task<string?> TryBuildAsync(string url, int maxSamples, int maxMb)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) == false) return null;
        if (IsQqImageHost(uri) == false) return null;

        // 先按链接形态排除明显不是动图的，省掉一次下载
        string lower = url.ToLowerInvariant();
        if (lower.Contains(".gif") == false && lower.Contains("gif") == false) return null;

        byte[] bytes;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(uri);
            if (response.IsSuccessStatusCode == false) return null;
            if (response.Content.Headers.ContentLength > maxMb * 1024L * 1024L) return null;
            bytes = await response.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            return null;
        }

        if (bytes.Length < 64 || bytes.Length > maxMb * 1024L * 1024L) return null;
        GifInfo info = MemeAnimation.Parse(bytes);
        if (info.IsGif == false || info.Animated == false || info.Frames <= 1) return null;

        List<int> sampled = MemeAnimation.SampleIndices(info.Frames, maxSamples);
        if (sampled.Count == 0) return null;
        List<int> starts = MemeAnimation.StartTimes(info.Delays);

        Directory.CreateDirectory(CacheDir);
        string path = Path.Combine(CacheDir, SanitizeFileName(info.Frames + "_" + Hash(url)) + ".png");
        if (File.Exists(path) == false)
        {
            if (MemeAnimation.TryComposeSheet(bytes, sampled, starts, MaxEdge, path, out string error) == false)
            {
                return null;
            }
        }
        TrimCache();
        return BuildNote(info, sampled, path);
    }

    /// <summary>给 AI 看的说明：帧数、采样情况、以及要用哪个函数打开哪张图。</summary>
    public static string BuildNote(GifInfo info, IReadOnlyList<int> sampled, string path)
    {
        string fps = info.DurationMs > 0
            ? $"，约 {Math.Round(info.Frames * 1000.0 / info.DurationMs, 1)} fps"
            : "";
        return $"[动图: {path}]（原 GIF {info.Width}x{info.Height} 共 {info.Frames} 帧{fps}，已均匀抽取 " +
               $"{sampled.Count} 帧拼成一张图；如需查看，调用 LoadImage，pathOrUrl 传上面的路径）";
    }

    static string Hash(string value)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    static string SanitizeFileName(string name)
    {
        char[] invalid = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        StringBuilder sb = new(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    /// <summary>拼图只是中间产物，攒够就清，避免长期运行把临时目录撑爆。</summary>
    static void TrimCache()
    {
        try
        {
            string[] files = Directory.GetFiles(CacheDir, "*.png");
            if (files.Length <= 200) return;
            Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
            for (int i = 0; i < files.Length - 200; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch { }
    }
}