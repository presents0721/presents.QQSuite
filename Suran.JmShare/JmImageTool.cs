using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace QQSuite.JmShare;

/// <summary>
/// JM 图片还原：按 jmcomic 的分块规则把纵向条带逆序拼回去。
/// GDI+ 不能解码 WebP，遇到 WebP 时用系统 WIC（WinRT）先转成 JPEG 再还原。
/// </summary>
public static class JmImageTool
{
    /// <summary>计算分块数 num；0 表示不还原。</summary>
    public static int ScrambleNum(long aid, string fileName, long scrambleId)
    {
        if (scrambleId <= 0 || aid < scrambleId) return 0;
        if (aid < 268850) return 10;

        int x = aid < 421926 ? 10 : 8;
        string hex = Md5Hex(aid.ToString(CultureInfo.InvariantCulture) + fileName);
        int last = hex[^1]; // 十六进制最后一个字符的 ASCII 码
        return last % x * 2 + 2;
    }

    /// <summary>
    /// 还原并保存，返回实际写入的文件路径。
    /// num &gt; 0 时输出 JPEG；无法解码（WebP 且 WIC 也不可用）时退回原样写入。
    /// </summary>
    public static async Task<string> SaveRestoredAsync(byte[] data, string targetBasePath, string originalExtension,
        long aid, string fileName, long scrambleId)
    {
        string extension = string.IsNullOrWhiteSpace(originalExtension) ? ".jpg" : originalExtension;
        int num = ScrambleNum(aid, fileName, scrambleId);
        string originalPath = targetBasePath + extension;

        if (num <= 0)
        {
            await File.WriteAllBytesAsync(originalPath, data);
            return originalPath;
        }

        string jpegPath = targetBasePath + ".jpg";
        if (TryRestoreWithGdi(data, jpegPath, num)) return jpegPath;

        // GDI+ 不支持 WebP：交给 WIC 解成 JPEG，再走同一套分块还原
        string converted = await DecodeWithWicAsync(data, targetBasePath + ".src");
        if (converted != null)
        {
            try
            {
                byte[] jpegData = await File.ReadAllBytesAsync(converted);
                if (TryRestoreWithGdi(jpegData, jpegPath, num)) return jpegPath;
            }
            catch
            {
                // 继续走原图兜底
            }
            finally
            {
                try { File.Delete(converted); } catch { }
            }
        }

        // 仍然还原不了：原样保存，调用方会统计“未能还原”的张数
        await File.WriteAllBytesAsync(originalPath, data);
        return originalPath;
    }

    /// <summary>用 GDI+ 解码并按条带还原成 JPEG；失败返回 false。</summary>
    static bool TryRestoreWithGdi(byte[] data, string jpegPath, int num)
    {
        Bitmap? output = null;
        try
        {
            using MemoryStream input = new(data);
            using Image source = Image.FromStream(input);
            int width = source.Width;
            int height = source.Height;
            if (num > height) num = height;
            if (num <= 0) return false;

            output = new Bitmap(width, height);
            using (Graphics graphics = Graphics.FromImage(output))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;

                int move = height / num;
                int over = height % num;
                for (int i = 0; i < num; i++)
                {
                    int sourceY = height - move * (i + 1) - over;
                    int targetY = i == 0 ? 0 : move * i + over;
                    int copyHeight = move + (i == 0 ? over : 0);
                    if (copyHeight <= 0 || sourceY < 0 || targetY + copyHeight > height) continue;

                    graphics.DrawImage(source,
                        new Rectangle(0, targetY, width, copyHeight),
                        new Rectangle(0, sourceY, width, copyHeight),
                        GraphicsUnit.Pixel);
                }
            }

            SaveJpeg(output, jpegPath);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            output?.Dispose();
        }
    }

    /// <summary>用 WIC（WinRT）把任意可解码格式（含 WebP）转成一个临时 JPEG，失败返回 null。</summary>
    static async Task<string?> DecodeWithWicAsync(byte[] data, string tempPath)
    {
        string jpegPath = tempPath + ".jpg";
        try
        {
            await File.WriteAllBytesAsync(tempPath, data);
            StorageFile inputFile = await StorageFile.GetFileFromPathAsync(tempPath);
            using IRandomAccessStream read = await inputFile.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(read);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            string? directory = Path.GetDirectoryName(jpegPath);
            if (string.IsNullOrEmpty(directory)) return null;
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(directory);
            StorageFile outputFile = await folder.CreateFileAsync(
                Path.GetFileName(jpegPath), CreationCollisionOption.ReplaceExisting);
            using IRandomAccessStream write = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, write);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            return jpegPath;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

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
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
        image.Save(path, codec, parameters);
    }

    static string Md5Hex(string text)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
