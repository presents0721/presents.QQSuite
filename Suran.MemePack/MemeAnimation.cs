using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace QQSuite.MemePack;

/// <summary>从 GIF 字节里直接解出来的动画信息（纯解析，不依赖图像解码器）。</summary>
public sealed record GifInfo(
    bool IsGif,
    bool Animated,
    int Width,
    int Height,
    int Frames,
    int DurationMs,
    List<int> Delays);

/// <summary>
/// 动图抽帧：把 GIF 的多帧采样拼成一张带时序标注的图，让视觉模型间接看到动画。
/// 之所以必须自己干这件事：任何上游图像管线在把动图交给模型前都会压成单帧，
/// 到视觉模型那一步只剩第一帧，补救不了。
/// 只处理 GIF；APNG 与动态 WebP 的帧结构 GDI+ 解不开，维持单帧行为。
/// </summary>
public static class MemeAnimation
{
    /// <summary>一次最多采样多少帧。</summary>
    public const int MaxFrames = 16;

    /// <summary>格子之间的间隔（像素）。</summary>
    const int TileGap = 6;

    /// <summary>是不是 GIF87a / GIF89a。</summary>
    public static bool IsGif(byte[] bytes)
    {
        return bytes.Length >= 13
               && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46
               && bytes[3] == 0x38 && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61;
    }

    /// <summary>
    /// 逐个数据块走一遍 GIF 流，数出图像描述符个数与每个图形控制扩展里的延时。
    /// 遇到不认识的块就停，宁可少报也不乱报。
    /// </summary>
    public static GifInfo Parse(byte[] bytes)
    {
        if (!IsGif(bytes)) return new GifInfo(false, false, 0, 0, 0, 0, new List<int>());

        int width = bytes[6] | (bytes[7] << 8);
        int height = bytes[8] | (bytes[9] << 8);
        byte screenPacked = bytes[10];

        int position = 13;
        if ((screenPacked & 0x80) != 0)
        {
            position += 3 * (1 << ((screenPacked & 0x07) + 1)); // 全局调色板
        }

        List<int> delays = new();
        int pendingDelay = -1;

        while (position < bytes.Length)
        {
            byte marker = bytes[position++];

            if (marker == 0x3B) break; // 结束符

            if (marker == 0x21) // 扩展块
            {
                if (position >= bytes.Length) break;
                byte label = bytes[position++];
                // 图形控制扩展：块长(1) 打包(1) 延时(2) 透明索引(1) 终止(1)
                if (label == 0xF9 && position + 4 <= bytes.Length)
                {
                    pendingDelay = bytes[position + 2] | (bytes[position + 3] << 8);
                }
                position = SkipSubBlocks(bytes, position);
                continue;
            }

            if (marker == 0x2C) // 图像描述符
            {
                if (position + 9 > bytes.Length) break;
                byte localPacked = bytes[position + 8];
                position += 9;
                if ((localPacked & 0x80) != 0)
                {
                    position += 3 * (1 << ((localPacked & 0x07) + 1)); // 局部调色板
                }
                position++; // LZW 最小码长
                position = SkipSubBlocks(bytes, position);

                delays.Add(pendingDelay < 0 ? 0 : pendingDelay * 10);
                pendingDelay = -1;
                continue;
            }

            break; // 没见过的块，停
        }

        int total = delays.Sum();
        return new GifInfo(true, delays.Count > 1, width, height, delays.Count, total, delays);
    }

    /// <summary>跳过一串子块（长度字节 + 数据），返回终止符之后的位置。</summary>
    static int SkipSubBlocks(byte[] bytes, int position)
    {
        while (position < bytes.Length)
        {
            int size = bytes[position++];
            if (size == 0) return position;
            position += size;
        }
        return position;
    }

    /// <summary>
    /// 均匀采样帧序号：步长取 帧数/采样数 后四舍五入，结果去重且递增。
    /// 帧数不足时全取，永远不超过 16 帧、不超过实际帧数。
    /// </summary>
    public static List<int> SampleIndices(int frames, int maxFrames)
    {
        List<int> result = new();
        if (frames <= 0) return result;

        int take = Math.Clamp(maxFrames, 1, Math.Min(MaxFrames, frames));
        if (take >= frames)
        {
            for (int i = 0; i < frames; i++) result.Add(i);
            return result;
        }

        for (int k = 0; k < take; k++)
        {
            int index = Math.Min(frames - 1, (int)Math.Round((double)k * frames / take, MidpointRounding.AwayFromZero));
            if (result.Count == 0 || result[^1] != index) result.Add(index);
        }
        return result;
    }

    /// <summary>把每帧延时换算成起始时刻。</summary>
    public static List<int> StartTimes(IReadOnlyList<int> delays)
    {
        List<int> starts = new(delays.Count);
        int accumulated = 0;
        foreach (int delay in delays)
        {
            starts.Add(accumulated);
            accumulated += delay;
        }
        return starts;
    }

    /// <summary>第 index 帧的起始毫秒；越界返回 -1。</summary>
    public static int StartAt(IReadOnlyList<int> starts, int index) =>
        index >= 0 && index < starts.Count ? starts[index] : -1;

    /// <summary>
    /// 交给模型的文字头：说清这是动图、采样了哪几帧、以及在哪儿有盲区。
    /// 「帧之间看不到」必须写出来，否则模型会把抽样帧当成连续画面编动作。
    /// </summary>
    public static string BuildHeader(GifInfo info, IReadOnlyList<int> sampled)
    {
        StringBuilder builder = new();
        builder.Append("image/gif 动图，").Append(info.Width).Append('x').Append(info.Height).Append(" 像素，共 ")
            .Append(info.Frames).Append(" 帧");
        if (info.DurationMs > 0)
        {
            builder.Append("，全片 ").Append(info.DurationMs).Append(" 毫秒");
            if (info.Frames > 0)
            {
                double fps = info.Frames * 1000.0 / info.DurationMs;
                builder.Append("，约 ").Append(fps.ToString("0.#", CultureInfo.InvariantCulture)).Append(" fps");
            }
        }
        builder.Append("。\n配图是从这段动画里均匀采样的 ").Append(sampled.Count)
            .Append(" 帧，按时间先后排列（先左右、再上下），每格下方标注了帧序号与起始毫秒。\n");

        builder.Append("只采到第 ").Append(string.Join("、", sampled)).Append(" 帧（共 ").Append(info.Frames)
            .Append(" 帧），所以相邻两格之间的中间动作是看不到的，不要凭想象补全。\n");

        builder.Append("看每一格时请从上到下、从左到右整块扫一遍：动来动去的主体、旁边静止的配角与道具、"
                       + "字幕文字、背景布景都算信息，别只盯住最显眼的那个，否则会漏掉半张画面。");
        return builder.ToString();
    }

    /// <summary>
    /// 按采样帧序号把动图拼成一张 PNG（每格下方写「帧序号 起始毫秒」）。
    /// 解不开就返回 false，调用方退回单帧描述。
    /// </summary>
    public static bool TryComposeSheet(
        byte[] bytes,
        IReadOnlyList<int> frames,
        IReadOnlyList<int> starts,
        int maxEdge,
        string outputPath,
        out string error)
    {
        error = "";
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using Image image = Image.FromStream(stream);
            FrameDimension dimension = new(FrameDimension.Time.Guid);

            int decoded = image.GetFrameCount(dimension);
            if (decoded <= 0)
            {
                error = "解码后没有可用帧";
                return false;
            }

            List<int> use = frames.Where(index => index >= 0 && index < decoded).ToList();
            if (use.Count == 0)
            {
                error = "采样帧序号超出解码范围（解码 " + decoded + " 帧）";
                return false;
            }

            int edge = Math.Max(image.Width, image.Height);
            double scale = edge > maxEdge ? (double)maxEdge / edge : 1.0;
            int tileWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
            int tileHeight = Math.Max(1, (int)Math.Round(image.Height * scale));

            // 标注字号跟着格子大小走，格子大时字大、格子小时别把画面挤没了
            float fontSize = Math.Clamp(Math.Min(tileWidth, tileHeight) / 22f, 11f, 18f);
            int captionHeight = (int)Math.Round(fontSize) + 10;

            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(use.Count)));
            int rows = (int)Math.Ceiling((double)use.Count / columns);
            int sheetWidth = columns * tileWidth + (columns + 1) * TileGap;
            int sheetHeight = rows * (tileHeight + captionHeight) + (rows + 1) * TileGap;

            using Bitmap sheet = new(sheetWidth, sheetHeight, PixelFormat.Format24bppRgb);
            using (Graphics canvas = Graphics.FromImage(sheet))
            {
                // 底色用浅灰，格子填白：这样帧与帧之间自带分隔，模型一眼看出这是多张独立的帧
                canvas.Clear(Color.FromArgb(240, 240, 240));
                canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using Font caption = CreateCaptionFont(fontSize);
                using Brush ink = new SolidBrush(Color.FromArgb(70, 70, 70));
                using Brush paper = new SolidBrush(Color.White);

                for (int i = 0; i < use.Count; i++)
                {
                    int column = i % columns;
                    int row = i / columns;
                    int x = TileGap + column * (tileWidth + TileGap);
                    int y = TileGap + row * (tileHeight + captionHeight + TileGap);

                    image.SelectActiveFrame(dimension, use[i]);
                    canvas.FillRectangle(paper, x, y, tileWidth, tileHeight);
                    canvas.DrawImage(image, new Rectangle(x, y, tileWidth, tileHeight));

                    int start = StartAt(starts, use[i]);
                    string label = start >= 0
                        ? "#" + use[i] + "  " + start + "ms"
                        : "#" + use[i];
                    canvas.DrawString(label, caption, ink, x, y + tileHeight + 2);
                }
            }

            string? folder = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);
            sheet.Save(outputPath, ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>标注条用的字体；系统没装雅黑时退回通用无衬线（标注是纯 ASCII，够用）。</summary>
    static Font CreateCaptionFont(float size)
    {
        try
        {
            return new Font("Microsoft YaHei", size, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        catch
        {
            return new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular, GraphicsUnit.Pixel);
        }
    }
}
