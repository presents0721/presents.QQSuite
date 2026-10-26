using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
#if !ANDROID
using Microsoft.Win32;
#endif

namespace QQSuite.JmShare;

/// <summary>
/// 把 JM 搜索结果 / 本子详情渲染成图片：拼 HTML → 无头 Edge/Chrome 截图 → 返回本地 png。
/// 封面一律由调用方先下载成字节再内联成 base64，浏览器不会再去访问远程图片（避免走代理、防盗链）。
/// 任何一步失败都返回 null，由调用方回退成纯文字。
/// </summary>
public static class JmCardRenderer
{
    /// <summary>搜索结果里的封面素材：条目 + 已下载的封面字节（可能为 null）。</summary>
    public readonly record struct SearchCover(JmSearchItem Item, byte[]? Cover);

    /// <summary>渲染搜索结果卡片；失败返回 null。</summary>
    public static Task<string?> RenderSearchAsync(
        string keyword, int page, long total, IReadOnlyList<SearchCover> items, ILogger logger)
    {
        if (items.Count == 0) return Task.FromResult<string?>(null);
        string body = BuildSearchBody(keyword, page, total, items);
        return RenderAsync("jm_search", body, EstimateSearchHeight(items.Count), logger);
    }

    /// <summary>渲染本子详情卡片；失败返回 null。</summary>
    public static Task<string?> RenderAlbumAsync(
        JmAlbum album, byte[]? cover, string footer, ILogger logger)
    {
        string description = PrepareDescription(album.Description);
        string body = BuildAlbumBody(album, cover, footer, description);
        return RenderAsync("jm_album_" + album.Id, body, EstimateAlbumHeight(album, footer, description), logger);
    }

    #region 渲染主流程

    static async Task<string?> RenderAsync(string tag, string body, int estimatedHeight, ILogger logger)
    {
        string? browserPath = FindBrowserExecutable();
        if (browserPath == null)
        {
            logger.LogWarning("未找到系统 Edge/Chrome，JM 卡片改用文字输出");
            return null;
        }

        string directory = Path.Combine(Path.GetTempPath(), "QQSuiteJm", "cards");
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        string safeTag = Regex.Replace(tag, "[^0-9A-Za-z_-]", "");
        string htmlPath = Path.Combine(directory, safeTag + "_" + timestamp + ".html");
        string imagePath = Path.Combine(directory, safeTag + "_" + timestamp + ".png");

        try
        {
            Directory.CreateDirectory(directory);
            CleanOldCards(directory);

            await File.WriteAllTextAsync(htmlPath, Template.Replace("__BODY__", body), Encoding.UTF8);

            string pageUrl = new Uri(htmlPath).AbsoluteUri;
            string userDataDir = "--user-data-dir=\"" +
                                 Path.Combine(Path.GetTempPath(), "suran_jm_card_profile") + "\" ";
            int height = Math.Clamp(estimatedHeight, 420, 16000);
            string windowSize = "--window-size=760," + height + " ";
            string edgeFlag = browserPath.Contains("msedge", StringComparison.OrdinalIgnoreCase)
                ? "--edge-skip-compat-layer-relaunch "
                : "";

            string lastError = "浏览器未产出截图";
            foreach (string headlessFlag in new[] { "--headless=new " + edgeFlag, "--headless " + edgeFlag })
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = browserPath,
                    Arguments = headlessFlag
                        + "--disable-gpu --no-first-run --no-default-browser-check --hide-scrollbars "
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
                    {
                        // 高度估算取的是上界，裁掉底部多余的纯背景留白
                        TrimBottom(imagePath);
                        return imagePath;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(300));
                }

                lastError = "无头渲染未产出截图：" +
                            (browserStdError.Length > 300 ? browserStdError[..300] : browserStdError);
            }

            logger.LogWarning("JM 卡片渲染失败（浏览器: {Browser}）：{Error}", browserPath, lastError);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JM 卡片渲染异常");
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
            foreach (string old in Directory.GetFiles(directory, "jm_*.*"))
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-6))
                    File.Delete(old);
            }
        }
        catch
        {
        }
    }

    #endregion

    #region 搜索结果卡片

    static string BuildSearchBody(string keyword, int page, long total, IReadOnlyList<SearchCover> items)
    {
        StringBuilder body = new();

        // 关键词只作展示，过长会撑高标题行，这里截断保证头部高度可控
        string shownKeyword = keyword.Length > 40 ? keyword[..40] + "…" : keyword;
        body.Append("<div class=\"card\"><div class=\"hd-title\"><span class=\"badge\">JM</span>搜索 <span class=\"kw\">")
            .Append(Escape(shownKeyword)).Append("</span></div>")
            .Append("<div class=\"hd-sub\">共 ").Append(total).Append(" 条结果 · 第 ").Append(page)
            .Append(" 页 · 本页返回 ").Append(items.Count).Append(" 条</div></div>");

        int podiumCount = Math.Min(3, items.Count);
        if (podiumCount > 0)
        {
            // 三张时把第 1 名放中间（左 2、中 1、右 3）；两张 / 一张则按顺序
            int[] order = podiumCount == 3 ? new[] { 1, 0, 2 } : Enumerable.Range(0, podiumCount).ToArray();
            body.Append("<div class=\"card\"><div class=\"podium\">");
            foreach (int index in order)
            {
                SearchCover entry = items[index];
                int rank = index + 1;
                body.Append("<div class=\"pod pod").Append(rank).Append("\">")
                    .Append("<div class=\"no no").Append(rank).Append("\">").Append(rank).Append("</div>")
                    .Append("<div class=\"id\">#").Append(entry.Item.Id).Append("</div>");
                string cover = ToDataUri(entry.Cover);
                if (cover.Length > 0)
                    body.Append("<img src=\"").Append(cover).Append("\" alt=\"cover\">");
                else
                    body.Append("<div class=\"ph\">无封面</div>");
                body.Append("<div class=\"cap\"><span class=\"capi\">").Append(Escape(entry.Item.Name))
                    .Append("</span></div></div>");
            }
            body.Append("</div>");

            // 前三名的作者 / 分类做成一行说明
            body.Append("<div class=\"pod-meta\">");
            foreach (int index in order)
            {
                SearchCover entry = items[index];
                body.Append("<div class=\"pm\"><b>#").Append(entry.Item.Id).Append("</b> ")
                    .Append(Escape(Summary(entry.Item))).Append("</div>");
            }
            body.Append("</div></div>");
        }

        if (items.Count > podiumCount)
        {
            body.Append("<div class=\"card\"><div class=\"sec-title\">更多结果</div>");
            for (int i = podiumCount; i < items.Count; i++)
            {
                JmSearchItem item = items[i].Item;
                body.Append("<div class=\"row\"><div class=\"rid\">#").Append(item.Id).Append("</div>")
                    .Append("<div class=\"rt\"><div class=\"rn\">").Append(Escape(item.Name)).Append("</div>")
                    .Append("<div class=\"rm\">").Append(Escape(Summary(item))).Append("</div></div></div>");
            }
            body.Append("</div>");
        }

        body.Append("<div class=\"footer\">QQSuite · JM漫画　用 JmInfo 查看详情，用 JmDownload 下载</div>");
        return body.ToString();
    }

    static string Summary(JmSearchItem item)
    {
        string author = string.IsNullOrWhiteSpace(item.Author) ? "未知作者" : item.Author;
        string category = string.IsNullOrWhiteSpace(item.Category) ? "未分类" : item.Category;
        if (!string.IsNullOrWhiteSpace(item.SubCategory)) category += " / " + item.SubCategory;
        return "作者 " + author + "｜" + category;
    }

    static int EstimateSearchHeight(int itemCount)
    {
        int podiumCount = Math.Min(3, itemCount);
        int height = 150;                                    // 头部卡片（含内边距与卡片间距）
        if (podiumCount > 0) height += 470;                  // 领奖台卡片 + 名次说明行
        if (itemCount > podiumCount) height += 100 + (itemCount - podiumCount) * 110;
        return height + 100;                                 // 页脚 + 安全余量
    }

    #endregion

    #region 详情卡片

    /// <summary>简介最多渲染的字符数，超出以省略号收尾（卡片不追求全文）。</summary>
    const int DescriptionMaxChars = 400;

    /// <summary>简介最多保留的显式换行行数，避免被超长换行撑爆。</summary>
    const int DescriptionMaxLines = 15;

    static string BuildAlbumBody(JmAlbum album, byte[]? cover, string footer, string description)
    {
        StringBuilder body = new();

        body.Append("<div class=\"card\"><div class=\"grid\">");

        // 左列：封面 + 统计
        body.Append("<div class=\"left\">");
        string coverUri = ToDataUri(cover);
        body.Append("<div class=\"cover-wrap\">");
        if (coverUri.Length > 0) body.Append("<img src=\"").Append(coverUri).Append("\" alt=\"cover\">");
        else body.Append("<div class=\"ph\">无封面</div>");
        body.Append("</div>");

        List<(string Value, string Label)> stats = new();
        if (!string.IsNullOrWhiteSpace(album.Views)) stats.Add((album.Views, "浏览"));
        if (!string.IsNullOrWhiteSpace(album.Likes)) stats.Add((album.Likes, "喜欢"));
        if (album.CommentCount > 0) stats.Add((album.CommentCount.ToString(CultureInfo.InvariantCulture), "评论"));
        if (stats.Count > 0)
        {
            body.Append("<div class=\"stats\">");
            foreach ((string value, string label) in stats)
            {
                body.Append("<div class=\"stat\"><b>").Append(Escape(value)).Append("</b><span>")
                    .Append(label).Append("</span></div>");
            }
            body.Append("</div>");
        }
        body.Append("</div>");

        // 右列：标题 / 作者 / 信息 / 标签 / 相关作品 / 简介
        body.Append("<div class=\"right\">")
            .Append("<div class=\"a-name\">").Append(Escape(album.Name))
            .Append(" <span class=\"id-badge\">#").Append(album.Id).Append("</span></div>");

        string authors = album.Authors.Count == 0 ? "未知" : string.Join("、", album.Authors);
        body.Append("<div class=\"authors\">作者：").Append(Escape(authors)).Append("</div>");

        body.Append("<div class=\"info\">");
        AppendInfo(body, "发布日期", string.IsNullOrWhiteSpace(album.PubDate) ? "—" : album.PubDate);
        AppendInfo(body, "更新日期", string.IsNullOrWhiteSpace(album.UpdateDate) ? "—" : album.UpdateDate);
        AppendInfo(body, "页数", album.TotalPhotos + " 页");
        AppendInfo(body, "章节数", album.Chapters.Count + " 章");
        body.Append("</div>");

        if (album.Tags.Count > 0)
        {
            body.Append("<div class=\"sec-title\">标签</div><div class=\"tags\">");
            foreach (string tag in album.Tags.Take(16)) body.Append("<span class=\"tag\">").Append(Escape(tag)).Append("</span>");
            if (album.Tags.Count > 16)
                body.Append("<span class=\"tag\">+").Append(album.Tags.Count - 16).Append("</span>");
            body.Append("</div>");
        }

        if (album.Works.Count > 0)
        {
            body.Append("<div class=\"sec-title\">相关作品</div><div class=\"tags\">");
            foreach (string work in album.Works.Take(10)) body.Append("<span class=\"tag\">").Append(Escape(work)).Append("</span>");
            if (album.Works.Count > 10)
                body.Append("<span class=\"tag\">+").Append(album.Works.Count - 10).Append("</span>");
            body.Append("</div>");
        }

        if (description.Length > 0)
        {
            body.Append("<div class=\"sec-title\">简介</div><div class=\"desc\">")
                .Append(Escape(description)).Append("</div>");
        }

        body.Append("</div></div></div>");
        if (!string.IsNullOrWhiteSpace(footer))
            body.Append("<div class=\"footer\">").Append(Escape(footer)).Append("</div>");
        return body.ToString();
    }

    static void AppendInfo(StringBuilder body, string label, string value)
    {
        body.Append("<div><div class=\"lab\">").Append(label).Append("</div><div class=\"val\">")
            .Append(Escape(value)).Append("</div></div>");
    }

    static int EstimateAlbumHeight(JmAlbum album, string footer, string description)
    {
        int left = 360 + 90;                                       // 封面 + 统计栏
        // 标题可能换行：按每行 16 字保守估算，不设上限（宁可多留，后面会裁掉）
        int titleLines = Math.Max(1, (int)Math.Ceiling((album.Name ?? "").Length / 16.0));
        int right = titleLines * 33 + 60;                          // 标题 + 作者
        right += 140;                                              // 信息网格
        if (album.Tags.Count > 0) right += 60 + TagRows(Math.Min(album.Tags.Count, 17)) * 40;
        if (album.Works.Count > 0) right += 60 + TagRows(Math.Min(album.Works.Count, 11)) * 40;
        if (description.Length > 0) right += 60 + Lines(description, 30) * 26;

        int content = Math.Max(left, right);
        int footerHeight = string.IsNullOrWhiteSpace(footer) ? 0 : 50;
        return content + 36 + 16 + footerHeight + 100;             // 卡片内边距 + 卡片间距 + 页脚 + 安全余量
    }

    /// <summary>标签按每行最多 4 个估算行数（上界）。</summary>
    static int TagRows(int count) => Math.Max(1, (int)Math.Ceiling(count / 4.0));

    /// <summary>
    /// 简介渲染前的规整：统一换行、压缩空行、限制长度与显式行数。
    /// 渲染与高度估算共用同一份结果，保证估算不会低于实际内容高度。
    /// </summary>
    static string PrepareDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        string text = description.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        if (text.Length > DescriptionMaxChars) text = text[..DescriptionMaxChars] + "…";

        string[] lines = text.Split('\n');
        if (lines.Length > DescriptionMaxLines)
            text = string.Join('\n', lines.Take(DescriptionMaxLines).Select(line => line)).TrimEnd() + "\n…";
        return text;
    }

    static int Lines(string text, int charsPerLine)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        int lines = 0;
        foreach (string part in text.Replace("\r", "").Split('\n'))
            lines += Math.Max(1, (int)Math.Ceiling(part.Trim().Length / (double)charsPerLine));
        return Math.Min(lines, DescriptionMaxLines + DescriptionMaxChars / charsPerLine + 2);
    }

    #endregion

    #region 工具

    /// <summary>裁剪底部留白时额外保留的像素，近似还原 body 的底部内边距。</summary>
    const int TrimPaddingPx = 14;

    /// <summary>判定「与页面背景色一致」的通道容差。</summary>
    const int BackgroundTolerance = 4;

    static string Escape(string text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>
    /// 截图用的是「高度上界」，底部会留下纯背景留白；这里自底向上找到最后一行非背景像素，
    /// 保留到该行下方一点，再覆盖写回原文件。任何失败都保留原图，不影响主流程。
    /// </summary>
    static void TrimBottom(string pngPath)
    {
        string tempPath = pngPath + ".trim.png";
        try
        {
            int width;
            int height;
            int newHeight;
            using (Bitmap source = new(pngPath))
            {
                width = source.Width;
                height = source.Height;
                if (width < 2 || height < 2) return;

                int lastContentRow = -1;
                BitmapData data = source.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    byte[] buffer = new byte[stride * height];
                    Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                    // 页面最底部一定是纯背景，取左下角像素作为背景色基准
                    int backgroundOffset = (height - 1) * stride;
                    byte backgroundBlue = buffer[backgroundOffset];
                    byte backgroundGreen = buffer[backgroundOffset + 1];
                    byte backgroundRed = buffer[backgroundOffset + 2];

                    for (int y = height - 1; y >= 0; y--)
                    {
                        if (RowHasContent(buffer, y * stride, width, backgroundBlue, backgroundGreen, backgroundRed))
                        {
                            lastContentRow = y;
                            break;
                        }
                    }
                }
                finally
                {
                    source.UnlockBits(data);
                }

                // 找不到内容、或内容已贴到底边（可能被截断）时不做裁剪
                if (lastContentRow < 0 || lastContentRow >= height - 2) return;

                newHeight = Math.Min(height, lastContentRow + 1 + TrimPaddingPx);
                using Bitmap cropped = source.Clone(
                    new Rectangle(0, 0, width, newHeight), PixelFormat.Format32bppArgb);
                cropped.Save(tempPath, ImageFormat.Png);
            }

            // 源文件句柄已释放，用裁剪结果替换原图
            File.Delete(pngPath);
            File.Move(tempPath, pngPath);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    static bool RowHasContent(byte[] buffer, int rowStart, int width,
        byte backgroundBlue, byte backgroundGreen, byte backgroundRed)
    {
        for (int x = 0; x < width; x++)
        {
            int offset = rowStart + x * 4;
            if (Math.Abs(buffer[offset] - backgroundBlue) > BackgroundTolerance
                || Math.Abs(buffer[offset + 1] - backgroundGreen) > BackgroundTolerance
                || Math.Abs(buffer[offset + 2] - backgroundRed) > BackgroundTolerance)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>把图片字节转成 data URI；字节为空时返回空串。</summary>
    internal static string ToDataUri(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 16) return "";
        string mime = "image/jpeg";
        if (bytes[0] == 0x89 && bytes[1] == 0x50) mime = "image/png";
        else if (bytes[0] == 0x47 && bytes[1] == 0x49) mime = "image/gif";
        else if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[8] == 0x57 && bytes[9] == 0x45) mime = "image/webp";
        return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
    }

    #endregion

    #region 无头浏览器

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

    /// <summary>查找系统里的 Edge / Chrome 可执行文件；找不到返回 null（供卡片截图与 PDF 导出共用）。</summary>
    internal static string? FindBrowserExecutable()
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

    #endregion

    const string Template =
"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<style>
:root {
    --bg: #f5f6fa; --card: #ffffff; --primary: #7659ff; --primary-dark: #5e48e8;
    --text: #2d3748; --soft: #718096; --line: #e9ecf5; --tag-bg: rgba(118,89,255,.1);
}
* { margin: 0; padding: 0; box-sizing: border-box; }
html { background: var(--bg); }
body {
    background: var(--bg); color: var(--text); width: 760px; padding: 20px;
    font-family: "Microsoft YaHei", "PingFang SC", sans-serif;
    -webkit-font-smoothing: antialiased;
}
.card {
    background: var(--card); border-radius: 14px; padding: 18px 20px; margin-bottom: 16px;
    box-shadow: 0 4px 14px rgba(45,55,72,.08);
}
.hd-title { font-size: 21px; font-weight: 800; line-height: 1.4; }
.hd-sub { font-size: 13px; color: var(--soft); margin-top: 8px; }
.kw { color: var(--primary); }
.badge {
    display: inline-block; background: var(--tag-bg); color: var(--primary-dark);
    padding: 3px 10px; border-radius: 8px; font-size: 12px; font-weight: 700; margin-right: 8px;
}
.podium { display: flex; gap: 16px; align-items: flex-end; justify-content: center; }
.pod {
    position: relative; border-radius: 12px; overflow: hidden; background: #eef0f7;
    box-shadow: 0 8px 20px rgba(45,55,72,.15);
}
.pod img { width: 100%; height: 100%; object-fit: cover; display: block; }
.pod .ph { width: 100%; height: 100%; display: flex; align-items: center; justify-content: center;
           color: var(--soft); font-size: 13px; }
.pod .no {
    position: absolute; top: 8px; left: 8px; width: 26px; height: 26px; border-radius: 50%;
    display: flex; align-items: center; justify-content: center; color: #fff;
    font-weight: 800; font-size: 14px; box-shadow: 0 2px 6px rgba(0,0,0,.3);
}
.no1 { background: linear-gradient(135deg,#ffd700,#ffa500); }
.no2 { background: linear-gradient(135deg,#c0c0c0,#a9a9a9); }
.no3 { background: linear-gradient(135deg,#cd7f32,#8b4513); }
.pod .id {
    position: absolute; top: 8px; right: 8px; background: rgba(118,89,255,.92); color: #fff;
    font-size: 11px; font-weight: 700; padding: 3px 7px; border-radius: 6px;
}
.pod .cap {
    position: absolute; left: 0; right: 0; bottom: 0; padding: 30px 10px 9px; color: #fff;
    background: linear-gradient(to top, rgba(0,0,0,.88), rgba(0,0,0,.15) 72%, transparent);
}
.pod .cap .capi {
    display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden;
    font-size: 13px; font-weight: 700; line-height: 1.35;
}
.pod1 { width: 222px; height: 306px; }
.pod2, .pod3 { width: 196px; height: 268px; }
.pod-meta { margin-top: 16px; border-top: 1px solid var(--line); padding-top: 12px; }
.pod-meta .pm { font-size: 12.5px; color: var(--soft); line-height: 1.9;
                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.pod-meta .pm b { color: var(--primary); }
.row { display: flex; align-items: center; padding: 11px 12px; border-radius: 10px;
       background: #f8f9fd; margin-bottom: 9px; }
.row:last-child { margin-bottom: 0; }
.row .rid { background: var(--primary); color: #fff; font-size: 12px; font-weight: 700;
            padding: 4px 8px; border-radius: 6px; margin-right: 12px; flex: 0 0 auto; min-width: 56px; text-align: center; }
.row .rt { flex: 1; min-width: 0; }
.row .rn { font-size: 14px; font-weight: 700; line-height: 1.35;
           display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
.row .rm { font-size: 12px; color: var(--soft); margin-top: 4px; line-height: 1.45;
           display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
.sec-title { font-size: 16px; font-weight: 800; color: var(--primary-dark); margin: 18px 0 12px; }
.grid { display: flex; gap: 24px; }
.left { flex: 0 0 auto; width: 240px; }
.right { flex: 1; min-width: 0; }
.cover-wrap { width: 240px; border-radius: 12px; overflow: hidden; background: #eef0f7;
              box-shadow: 0 10px 22px rgba(45,55,72,.16); }
.cover-wrap img { width: 100%; display: block; aspect-ratio: 2/3; object-fit: cover; }
.cover-wrap .ph { width: 240px; height: 360px; display: flex; align-items: center;
                  justify-content: center; color: var(--soft); font-size: 13px; }
.stats { display: flex; margin-top: 14px; background: var(--card); border-radius: 12px;
         box-shadow: 0 4px 14px rgba(45,55,72,.08); overflow: hidden; }
.stat { flex: 1; text-align: center; padding: 12px 4px; }
.stat + .stat { border-left: 1px solid var(--line); }
.stat b { display: block; font-size: 20px; color: var(--primary); }
.stat span { font-size: 12px; color: var(--soft); }
.a-name { font-size: 23px; font-weight: 800; line-height: 1.4; }
.id-badge { display: inline-block; background: var(--tag-bg); color: var(--primary);
            font-size: 14px; font-weight: 700; padding: 3px 10px; border-radius: 8px; }
.authors { margin-top: 10px; font-size: 14px; }
.info { display: grid; grid-template-columns: 1fr 1fr; gap: 14px 18px; margin-top: 16px;
        border-top: 1px solid var(--line); border-bottom: 1px solid var(--line); padding: 14px 0; }
.info .lab { font-size: 12px; color: var(--soft); }
.info .val { font-size: 15px; font-weight: 700; margin-top: 3px; }
.tag { display: inline-block; background: var(--tag-bg); color: var(--primary-dark);
       padding: 5px 11px; border-radius: 8px; font-size: 12.5px; font-weight: 600; margin: 0 7px 7px 0; }
.desc { font-size: 13px; line-height: 1.75; color: #4a5568; white-space: pre-wrap; word-break: break-word; }
.footer { text-align: center; font-size: 11px; color: var(--soft); padding-top: 2px; }
</style>
</head>
<body>
__BODY__
</body>
</html>
""";
}
