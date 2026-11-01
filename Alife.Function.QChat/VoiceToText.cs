using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QQSuite.Core;

/// <summary>
/// 本地语音转文字：NapCat 的 get_record 能把 silk 直接转成 wav 并回传 base64，
/// 这里只负责落盘、调 whisper-cli、把结果写回消息文本。全程本机，音频不上传。
/// </summary>
public static class VoiceToText
{
    /// <summary>whisper 是 CPU 密集型，同一时刻只跑一条，避免群里连发语音把机器打满。</summary>
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>同一段语音会被历史回溯/重复推送反复遇到，按 file id 缓存，避免重复识别。</summary>
    static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    const int CacheLimit = 200;

    /// <summary>单条语音字节上限，正常 60 秒语音也就几百 KB。</summary>
    const int MaxAudioBytes = 16 * 1024 * 1024;

    static readonly Regex RecordPattern = new(@"\[CQ:record[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 把文本里的 [CQ:record,...] 换成识别结果。
    /// 未开启 / 失败时返回 null，由调用方回退成占位符，不影响原流程。
    /// </summary>
    public static async Task<string?> TryReplaceAsync(string text, OneBotClient client, QChatServiceConfig? config, CancellationToken cancellationToken = default)
    {
        if (config is not { VoiceToTextEnabled: true }) return null;
        if (text.IndexOf("CQ:record", StringComparison.OrdinalIgnoreCase) < 0) return null;

        string cli = config.WhisperCliPath?.Trim() ?? "";
        string model = config.WhisperModelPath?.Trim() ?? "";
        if (!File.Exists(cli) || !File.Exists(model)) return null;

        StringBuilder sb = new(text.Length + 64);
        int last = 0;
        bool replaced = false;
        foreach (Match match in RecordPattern.Matches(text))
        {
            string file = FieldValue(match.Value, "file");
            string recognized = "";
            if (file.Length > 0)
            {
                try { recognized = await TranscribeAsync(file, client, config, cli, model, cancellationToken); }
                catch { recognized = ""; }
            }
            sb.Append(text, last, match.Index - last);
            sb.Append(recognized.Length > 0 ? "[语音: " + recognized + "]" : "[语音]");
            last = match.Index + match.Length;
            replaced = true;
        }
        if (replaced == false) return null;
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    static async Task<string> TranscribeAsync(string file, OneBotClient client, QChatServiceConfig config, string cli, string model, CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(file, out string? cached)) return cached;

        OneBotFile? record = await client.GetRecord(file, "wav");
        if (record == null) return "";

        byte[] data;
        if (string.IsNullOrEmpty(record.Base64) == false)
        {
            try { data = Convert.FromBase64String(record.Base64); }
            catch { return ""; }
        }
        else if (string.IsNullOrEmpty(record.Path) == false && File.Exists(record.Path))
        {
            data = await File.ReadAllBytesAsync(record.Path, cancellationToken);
        }
        else
        {
            return "";
        }

        // 太短基本是空音频，太大说明不是正常语音（或接口回了别的东西）
        if (data.Length < 64 || data.Length > MaxAudioBytes) return "";

        string dir = Path.Combine(Path.GetTempPath(), "QQSuite-voice");
        Directory.CreateDirectory(dir);
        string wavPath = Path.Combine(dir, Path.GetRandomFileName() + ".wav");
        try
        {
            await File.WriteAllBytesAsync(wavPath, data, cancellationToken);
            await Gate.WaitAsync(cancellationToken);
            try
            {
                string text = await RunWhisperAsync(cli, model, wavPath, config, cancellationToken);
                if (text.Length > 0)
                {
                    if (Cache.Count >= CacheLimit) Cache.Clear();
                    Cache[file] = text;
                }
                return text;
            }
            finally
            {
                Gate.Release();
            }
        }
        finally
        {
            try { File.Delete(wavPath); } catch { }
        }
    }

    static async Task<string> RunWhisperAsync(string cli, string model, string wavPath, QChatServiceConfig config, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(model);
        string language = config.WhisperLanguage?.Trim() ?? "";
        if (language.Length > 0)
        {
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add(language);
        }
        int threads = Math.Clamp(config.WhisperThreads, 1, 16);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));
        // 中文场景下这句提示能让 tiny/base 明显偏向简体输出，不加往往会吐繁体
        string prompt = config.WhisperPrompt?.Trim() ?? "";
        if (prompt.Length > 0)
        {
            startInfo.ArgumentList.Add("--prompt");
            startInfo.ArgumentList.Add(prompt);
        }
        startInfo.ArgumentList.Add("-nt");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(wavPath);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("whisper 启动失败");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        int timeoutSeconds = Math.Clamp(config.VoiceToTextTimeoutSeconds, 5, 300);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return "";
        }

        string output = await stdoutTask;
        _ = await stderrTask;
        return Clean(output);
    }

    /// <summary>whisper-cli 会把日志和正文都打到 stdout，这里只留正文并清掉无声标记。</summary>
    static string Clean(string raw)
    {
        List<string> kept = new();
        foreach (string line in raw.Split('\n'))
        {
            string text = line.Trim();
            if (text.Length == 0) continue;
            if (text.StartsWith("whisper_", StringComparison.Ordinal)) continue;
            if (text.StartsWith("main:", StringComparison.Ordinal)) continue;
            if (text.StartsWith("system_info", StringComparison.Ordinal)) continue;
            if (text.StartsWith("ggml_", StringComparison.Ordinal)) continue;
            kept.Add(text);
        }
        string joined = string.Join(" ", kept).Trim();
        foreach (string junk in new[] { "[BLANK_AUDIO]", "(no speech)", "[silence]", "(silence)", "(music)" })
        {
            joined = joined.Replace(junk, "", StringComparison.OrdinalIgnoreCase);
        }
        joined = Regex.Replace(joined, @"\[[\d:.\->\s]+\]", "");
        joined = Regex.Replace(joined, @"\s{2,}", " ").Trim();
        return joined.Length > 500 ? joined.Substring(0, 500) : joined;
    }

    /// <summary>从 CQ 码里取字段值。CQ 码要求把 , 和 ] 转义成 &#44; / &#93;，所以直接按逗号/方括号切是安全的。</summary>
    static string FieldValue(string segment, string name)
    {
        Match match = Regex.Match(segment, name + @"=(?<v>[^,\]]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value.Trim() : "";
    }
}