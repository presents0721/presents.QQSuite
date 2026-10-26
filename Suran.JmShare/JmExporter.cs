using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace QQSuite.JmShare;

/// <summary>
/// JM 下载结果的额外打包方式：
/// 　长图 —— 用 GDI+ 把还原后的图片按页序纵向拼成一张（或多张）JPEG；
/// 　PDF  —— 拼一张内嵌 base64 图片的 HTML，再调用系统无头 Edge/Chrome 的 --print-to-pdf 导出。
/// 两条路径都不引入任何第三方依赖，失败时返回 null 由调用方回退成 ZIP。
/// </summary>
public static class JmExporter
{
    /// <summary>单张长图允许的最大高度；超过就拆成多张，保证宽度不至于被压得看不清。</summary>
    const int MaxPartHeight = 20000;

    /// <summary>JPEG 编码质量。</summary>
    const long JpegQuality = 90L;

    #region 长图

    /// <summary>
    /// 把若干图片按顺序纵向拼成长图，超高时按 MaxPartHeight 拆成多张。
    /// 返回生成的文件路径列表（按页序）；一张都没成功时返回空列表。
    /// </summary>
    public static List<string> BuildLongImages(
        IReadOnlyList<string> imageFiles, string outputBasePath, ILogger logger)
    {
        List<string> outputs = new();
        if (imageFiles.Count == 0) return outputs;

        // 第一遍只读尺寸，避免同时把所有页面解码进内存
        List<(int Width, int Height)> sizes = new(imageFiles.Count);
        foreach (string file in imageFiles)
        {
            try
            {
                using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                sizes.Add((image.Width, image.Height));
            }
            catch (Exception ex)
            {
                logger.LogDebug("JM 长图跳过无法解码的图片 {File}：{Message}", file, ex.Message);
                sizes.Add((0, 0));
            }
        }

        int width = sizes.Where(s => s.Width > 0).Select(s => s.Width).DefaultIfEmpty(0).Max();
        if (width <= 0) return outputs;

        // 统一缩放到最宽页面的宽度，并据此换算出每页高度
        List<(string File, int Height)> pages = new(imageFiles.Count);
        foreach (string file in imageFiles)
        {
            int index = pages.Count;
            (int w, int h) = sizes[index];
            if (w <= 0 || h <= 0) continue;
            int scaled = (int)Math.Round(h * width / (double)w, MidpointRounding.AwayFromZero);
            pages.Add((file, Math.Max(1, scaled)));
        }
        if (pages.Count == 0) return outputs;

        int part = 1;
        int cursor = 0;
        while (cursor < pages.Count)
        {
            int total = 0;
            int end = cursor;
            while (end < pages.Count)
            {
                int next = total + pages[end].Height;
                if (end > cursor && next > MaxPartHeight) break;
                total = next;
                end++;
            }

            string path = outputBasePath + (part == 1 ? "" : "_p" + part.ToString(CultureInfo.InvariantCulture)) + ".jpg";
            if (ComposePart(pages, cursor, end - cursor, width, total, path))
            {
                outputs.Add(path);
            }
            else
            {
                logger.LogWarning("JM 长图第 {Part} 段拼接失败", part);
            }

            cursor = end;
            part++;
        }
        return outputs;
    }

    static bool ComposePart(List<(string File, int Height)> pages, int start, int count,
        int width, int totalHeight, string outputPath)
    {
        Bitmap? canvas = null;
        try
        {
            // 高度取整后可能比累计值多 1px，这里以实际位图高度为准，逐页顺延
            int canvasHeight = Math.Max(1, Math.Min(totalHeight, 65500));
            canvas = new Bitmap(width, canvasHeight, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(Color.White);

                int y = 0;
                for (int i = 0; i < count && y < canvasHeight; i++)
                {
                    (string file, int height) = pages[start + i];
                    using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                    int drawHeight = Math.Min(height, canvasHeight - y);
                    if (drawHeight <= 0) break;
                    graphics.DrawImage(image, new Rectangle(0, y, width, height),
                        0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                    y += height;
                }
            }

            SaveJpeg(canvas, outputPath);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            canvas?.Dispose();
        }
    }

    #endregion

    #region PDF

    /// <summary>
    /// 把图片按「一页一张」生成 PDF。无头浏览器不可用或导出失败时返回 false。
    /// </summary>
    public static async Task<bool> BuildPdfAsync(
        IReadOnlyList<string> imageFiles, string outputPath, ILogger logger, CancellationToken ct)
    {
        if (imageFiles.Count == 0) return false;

        string? browserPath = JmCardRenderer.FindBrowserExecutable();
        if (browserPath == null)
        {
            logger.LogWarning("未找到系统 Edge/Chrome，无法导出 PDF");
            return false;
        }

        string directory = Path.Combine(Path.GetTempPath(), "QQSuiteJm", "pdf");
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        string htmlPath = Path.Combine(directory, "jm_" + stamp + ".html");

        try
        {
            Directory.CreateDirectory(directory);
            CleanOldPdf(directory);

            await File.WriteAllTextAsync(htmlPath, BuildPdfHtml(imageFiles), Encoding.UTF8, ct);
            return await RunPrintToPdfAsync(browserPath, htmlPath, outputPath, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "JM PDF 导出异常");
            return false;
        }
        finally
        {
            try { if (File.Exists(htmlPath)) File.Delete(htmlPath); } catch { }
        }
    }

    /// <summary>每张图片各占一页，等比缩放后居中，页面留白用白色填充。</summary>
    static string BuildPdfHtml(IReadOnlyList<string> imageFiles)
    {
        StringBuilder html = new();
        html.Append("""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<style>
@page { size: A4 portrait; margin: 8mm; }
* { margin: 0; padding: 0; box-sizing: border-box; }
html, body { background: #ffffff; }
.page { height: 281mm; display: flex; align-items: center; justify-content: center;
        page-break-after: always; break-after: page; }
.page:last-child { page-break-after: auto; break-after: auto; }
.page img { max-width: 100%; max-height: 100%; object-fit: contain; }
</style>
</head>
<body>
""");

        foreach (string file in imageFiles)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch
            {
                continue;
            }
            string uri = JmCardRenderer.ToDataUri(bytes);
            if (uri.Length == 0) continue;
            html.Append("<div class=\"page\"><img src=\"").Append(uri).Append("\" alt=\"page\"></div>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    static async Task<bool> RunPrintToPdfAsync(string browserPath, string htmlPath, string outputPath,
        ILogger logger, CancellationToken ct)
    {
        string pageUrl = new Uri(htmlPath).AbsoluteUri;
        string userDataDir = "--user-data-dir=\"" +
                             Path.Combine(Path.GetTempPath(), "suran_jm_pdf_profile") + "\" ";
        string edgeFlag = browserPath.Contains("msedge", StringComparison.OrdinalIgnoreCase)
            ? "--edge-skip-compat-layer-relaunch "
            : "";

        string lastError = "浏览器未产出 PDF";
        foreach (string headlessFlag in new[] { "--headless=new " + edgeFlag, "--headless " + edgeFlag })
        {
            if (ct.IsCancellationRequested) return false;
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }

            ProcessStartInfo startInfo = new()
            {
                FileName = browserPath,
                Arguments = headlessFlag
                    + "--disable-gpu --no-first-run --no-default-browser-check "
                    + userDataDir
                    + "--no-pdf-header-footer --print-to-pdf-no-header "
                    + "--print-to-pdf=\"" + outputPath + "\" "
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

            Task<string> stderrTask = browserProcess.StandardError.ReadToEndAsync(ct);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(180));
            try
            {
                await browserProcess.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { browserProcess.Kill(true); } catch { }
                if (ct.IsCancellationRequested) return false;
                lastError = "浏览器导出 PDF 超时";
                continue;
            }
            string browserStdError = await stderrTask;

            for (int wait = 0; wait < 40; wait++)
            {
                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            }

            lastError = "无头导出未产出 PDF：" +
                        (browserStdError.Length > 300 ? browserStdError[..300] : browserStdError);
        }

        logger.LogWarning("JM PDF 导出失败（浏览器: {Browser}）：{Error}", browserPath, lastError);
        return false;
    }

    static void CleanOldPdf(string directory)
    {
        try
        {
            foreach (string old in Directory.GetFiles(directory, "jm_*"))
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

    static void SaveJpeg(Image image, string path)
    {
        ImageCodecInfo? codec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(item => item.FormatID == ImageFormat.Jpeg.Guid);
        if (codec == null)
        {
            image.Save(path, ImageFormat.Jpeg);
            return;
        }

        using EncoderParameters parameters = new(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        image.Save(path, codec, parameters);
    }
}
