using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
#if !ANDROID
using Microsoft.Win32;
#endif

namespace QQSuite.VideoShare;

/// <summary>
/// 把视频简介与热门评论渲染成日报风格的图片：拼 HTML → 无头 Edge/Chrome 截图 → 返回本地 png。
/// 任何一步失败都返回 null，由调用方回退成文字。
/// </summary>
public static class VideoCardRenderer
{
    public static async Task<string?> RenderAsync(
        VideoInfo info, bool includeDescription, bool includeComments, ILogger logger)
    {
        bool hasDescription = includeDescription &&
                              !string.IsNullOrWhiteSpace(info.Description) &&
                              info.Description.Trim() != info.Title.Trim();
        bool hasComments = includeComments && info.Comments.Count > 0;
        if (!hasDescription && !hasComments) return null;

        string? browserPath = FindBrowserExecutable();
        if (browserPath == null)
        {
            logger.LogWarning("未找到系统 Edge/Chrome，简介与评论改用文字输出");
            return null;
        }

        string directory = Path.Combine(Path.GetTempPath(), "QQSuiteVideo", "cards");
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        string safeId = Regex.Replace(info.Id.Length > 0 ? info.Id : "video", "[^0-9A-Za-z_-]", "");
        string htmlPath = Path.Combine(directory, $"card_{info.Platform}_{safeId}_{timestamp}.html");
        string imagePath = Path.Combine(directory, $"card_{info.Platform}_{safeId}_{timestamp}.png");

        try
        {
            Directory.CreateDirectory(directory);
            CleanOldCards(directory);

            (string html, int height) = BuildHtml(info, hasDescription, hasComments);
            await File.WriteAllTextAsync(htmlPath, html, Encoding.UTF8);

            string pageUrl = new Uri(htmlPath).AbsoluteUri;
            string userDataDir = "--user-data-dir=\"" +
                                 Path.Combine(Path.GetTempPath(), "suran_video_card_profile") + "\" ";
            string windowSize = "--window-size=760," + Math.Clamp(height, 500, 6000) + " ";
            string edgeFlag = browserPath.Contains("msedge", StringComparison.OrdinalIgnoreCase)
                ? "--edge-skip-compat-layer-relaunch "
                : "";

            string lastError = "浏览器未产出截图";
            foreach (string headlessFlag in new[] { "--headless=new " + edgeFlag, "--headless " + edgeFlag })
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = browserPath,
                    Arguments = headlessFlag + "--disable-gpu --no-first-run --no-default-browser-check --hide-scrollbars "
                        + userDataDir + windowSize
                        + "--screenshot=\"" + imagePath + "\" "
                        + "\"" + pageUrl + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using Process? browserProcess = Process.Start(startInfo);
                if (browserProcess == null)
                {
                    lastError = "浏览器进程启动失败";
                    continue;
                }

                Task<string> stderrTask = browserProcess.StandardError.ReadToEndAsync();
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
                try
                {
                    await browserProcess.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { browserProcess.Kill(true); } catch { }
                    lastError = "浏览器渲染超时";
                    continue;
                }
                string browserStdError = await stderrTask;

                // Chrome 退出后截图可能由子进程延迟落盘
                for (int wait = 0; wait < 30; wait++)
                {
                    if (File.Exists(imagePath) && new FileInfo(imagePath).Length > 1024)
                        return imagePath;
                    await Task.Delay(TimeSpan.FromMilliseconds(300));
                }

                lastError = "无头渲染未产出截图：" +
                            (browserStdError.Length > 300 ? browserStdError[..300] : browserStdError);
            }

            logger.LogWarning("简介/评论图卡渲染失败（浏览器: {Browser}）：{Error}", browserPath, lastError);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "简介/评论图卡渲染异常");
            return null;
        }
        finally
        {
            try { if (File.Exists(htmlPath)) File.Delete(htmlPath); } catch { }
        }
    }

    static void CleanOldCards(string directory)
    {
        try
        {
            foreach (string old in Directory.GetFiles(directory, "card_*.*"))
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
                    File.Delete(old);
            }
        }
        catch
        {
        }
    }

    static (string Html, int Height) BuildHtml(VideoInfo info, bool includeDescription, bool includeComments)
    {
        StringBuilder body = new();

        body.Append("<div class=\"card header\">")
            .Append("<div class=\"badge\">").Append(Escape(info.Platform)).Append("视频</div>")
            .Append("<div class=\"title\">").Append(Escape(info.Title)).Append("</div>")
            .Append("<div class=\"meta\">");
        if (info.Author.Length > 0) body.Append(Escape(info.Author)).Append(" · ");
        if (info.Duration > 0) body.Append("时长 ").Append(info.DurationText()).Append(" · ");
        body.Append("播放 ").Append(info.Play).Append(" · 点赞 ").Append(info.Like).Append("</div>")
            .Append("<div class=\"link\">").Append(Escape(info.WebUrl)).Append("</div>")
            .Append("</div>");

        int height = 180;

        if (includeDescription)
        {
            string description = Escape(info.Description.Trim());
            body.Append("<div class=\"card\"><div class=\"section-title\">📝 视频简介</div>")
                .Append("<div class=\"desc\">").Append(description).Append("</div></div>");
            height += 80 + Lines(info.Description, 34) * 27;
        }

        if (includeComments)
        {
            StringBuilder comments = new();
            int index = 1;
            foreach (VideoComment item in info.Comments)
            {
                comments.Append("<div class=\"cmt\"><div class=\"cmt-head\">")
                    .Append("<span class=\"rank\">").Append(index++).Append("</span>")
                    .Append("<span class=\"user\">").Append(Escape(item.User)).Append("</span>");
                if (item.Like > 0)
                    comments.Append("<span class=\"like\">").Append(item.Like).Append(" 赞</span>");
                comments.Append("</div><div class=\"cmt-text\">").Append(Escape(item.Text)).Append("</div></div>");
                height += 62 + Lines(item.Text, 32) * 24;
            }

            body.Append("<div class=\"card\"><div class=\"section-title\">💬 热门评论</div>")
                .Append(comments).Append("</div>");
            height += 80;
        }

        body.Append("<div class=\"footer\">Generated by QQSuite.VideoShare · 内容来自视频页面</div>");

        string html = Template.Replace("__BODY__", body.ToString());
        return (html, (int)(height * 1.05) + 30);
    }

    static int Lines(string text, int charsPerLine)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        int lines = 0;
        foreach (string part in text.Replace("\r", "").Split('\n'))
            lines += Math.Max(1, (int)Math.Ceiling(part.Trim().Length / (double)charsPerLine));
        return lines;
    }

    static string Escape(string text) => WebUtility.HtmlEncode(text ?? "");

    const string Template =
"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<style>
:root {
    --bg: #fdf6e3; --card: #fffdf7; --primary: #e6a23c; --accent: #b8860b;
    --text: #5b4636; --soft: #9c8a72; --line: #f0e2c8;
}
* { margin: 0; padding: 0; box-sizing: border-box; }
html { background: var(--bg); }
body {
    background: var(--bg); color: var(--text);
    font-family: "Microsoft YaHei", "PingFang SC", sans-serif;
    width: 720px; padding: 24px 20px;
}
.card {
    background: var(--card); border: 2px solid var(--line); border-radius: 16px;
    padding: 16px 20px; margin-bottom: 16px; box-shadow: 0 2px 8px rgba(0,0,0,.06);
}
.header { padding: 22px 20px; }
.badge {
    display: inline-block; padding: 2px 10px; border-radius: 8px;
    background: var(--primary); color: #fff; font-size: 12px; font-weight: 700;
}
.title { font-size: 22px; font-weight: 800; line-height: 1.4; margin: 10px 0 8px; }
.meta { font-size: 13px; color: var(--soft); }
.link { font-size: 12px; color: var(--accent); margin-top: 8px; word-break: break-all; }
.section-title { font-size: 16px; font-weight: 800; color: var(--accent); margin-bottom: 10px; }
.desc { font-size: 15px; line-height: 1.75; white-space: pre-wrap; word-break: break-word; }
.cmt { background: var(--bg); border-radius: 10px; padding: 10px 12px; margin-bottom: 8px; }
.cmt-head { display: flex; align-items: center; margin-bottom: 6px; }
.rank { width: 24px; font-weight: 800; color: var(--primary); }
.user { font-size: 14px; font-weight: 700; }
.like { margin-left: auto; font-size: 12px; color: var(--primary); font-weight: 700; }
.cmt-text { font-size: 14px; line-height: 1.65; word-break: break-word; }
.footer { text-align: center; font-size: 11px; color: var(--soft); margin-top: 4px; }
</style>
</head>
<body>
__BODY__
</body>
</html>
""";

#if !ANDROID
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? FindBrowserFromRegistry()
    {
        foreach (string browserName in new[] { "msedge.exe", "chrome.exe" })
        {
            string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + browserName;
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using RegistryKey? baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using RegistryKey? appKey = baseKey.OpenSubKey(keyPath);
                        if (appKey?.GetValue(null) is string registeredPath && registeredPath.Length > 0)
                        {
                            string cleanPath = Environment.ExpandEnvironmentVariables(registeredPath.Trim().Trim('"'));
                            if (File.Exists(cleanPath)) return cleanPath;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        return null;
    }
#endif

    static string? FindBrowserExecutable()
    {
#if !ANDROID
        if (OperatingSystem.IsWindows())
        {
            string? registered = FindBrowserFromRegistry();
            if (registered != null) return registered;
        }
#endif

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Chromium", "Application", "chrome.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
