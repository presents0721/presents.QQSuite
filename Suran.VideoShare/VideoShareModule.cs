using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using QQSuite.Core;

namespace QQSuite.VideoShare;

/// <summary>视频解析与合并转发的配置。</summary>
public class VideoShareConfig
{
    [DisplayName("自动解析视频链接")]
    [Description("在群聊/私聊里检测到抖音或B站视频链接时，自动解析并发送合并转发卡片")]
    public bool AutoParseLinks { get; set; } = true;

    [DisplayName("自动解析仅限已激活群")]
    [Description("开启后只在角色已经开始监听的群里自动解析，避免在所有群里刷屏")]
    public bool AutoParseOnlyActiveGroup { get; set; }

    [DisplayName("每条消息最多解析")]
    [Description("同一条消息里出现多个视频链接时，最多处理几个（1-5）")]
    public int MaxLinksPerMessage { get; set; } = 2;

    [DisplayName("视频放进合并转发")]
    [Description("等视频下载完，把视频段一起放进同一条聊天记录里，只发一条消息；若协议端不支持视频节点，会显示成 [CQ:video,...] 纯文本，此时可关掉本项改为单独发送")]
    public bool IncludeVideoNode { get; set; } = true;

    [DisplayName("视频单独发送")]
    [Description("额外把视频下载到本地，以单独一条视频消息发送（与「视频放进合并转发」同时开就是两条消息）")]
    public bool SendVideoSeparately { get; set; }

    [DisplayName("下载完成后再发")]
    [Description("开启后先等视频下载完再连发消息，避免卡片先到、视频隔很久才到；「视频放进合并转发」本身必须等下载完成，本项只影响单独发送的视频")]
    public bool SendAfterDownload { get; set; } = true;

    [DisplayName("视频大小上限(MB)")]
    [Description("超过该大小的视频不下载、不发送，只在转发卡片里保留原链接")]
    public int MaxVideoSizeMB { get; set; } = 100;

    [DisplayName("抖音解析接口")]
    [Description("留空使用内置公开接口；填第三方接口时用 {url} 代表视频链接、{key} 代表下面的 Key，例如 https://api.example.com/douyin?url={url}&key={key}")]
    public string DouyinApiUrl { get; set; } = "";

    [DisplayName("抖音接口Key")]
    [Description("第三方抖音解析接口需要鉴权时填写")]
    public string DouyinApiKey { get; set; } = "";

    [DisplayName("抓取视频简介")]
    [Description("解析时一并抓取视频简介，放进合并转发卡片和 AI 详情里")]
    public bool FetchDescription { get; set; } = true;

    [DisplayName("抓取热门评论条数")]
    [Description("0 表示不抓取；B站取热门评论，抖音取决于解析接口是否返回评论。建议 0-10，太多会让卡片过长")]
    public int CommentCount { get; set; } = 5;

    [DisplayName("简介/评论渲染成图")]
    [Description("把抓到的简介和热门评论渲染成日报风格的图片，放进合并转发；关闭或渲染失败时以文字节点放进聊天记录")]
    public bool RenderAsImage { get; set; } = true;

    [DisplayName("图卡渲染简介")]
    [Description("关闭后简介不画进图里，改为文字节点放进合并转发卡片")]
    public bool RenderDescriptionInImage { get; set; } = true;

    [DisplayName("图卡渲染评论")]
    [Description("关闭后评论不画进图里，改为文字节点放进合并转发卡片")]
    public bool RenderCommentsInImage { get; set; } = true;

    [DisplayName("防重复分钟")]
    [Description("同一会话里同一个视频在该时间内只自动发送一次（最小 1 分钟，填 0 也按 1 分钟）")]
    public int DedupeMinutes { get; set; } = 10;
}

/// <summary>一条热门评论。</summary>
public sealed class VideoComment
{
    public string User { get; init; } = "";
    public string Text { get; init; } = "";
    public long Like { get; init; }
}

/// <summary>统一后的视频信息。</summary>
public sealed class VideoInfo
{
    public string Platform { get; init; } = "";
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string Cover { get; init; } = "";
    public string VideoUrl { get; init; } = "";
    public string WebUrl { get; init; } = "";
    public long Play { get; init; }
    public long Like { get; init; }
    public long Comment { get; init; }
    public double Duration { get; init; }
    public List<string> Images { get; init; } = [];
    public List<VideoComment> Comments { get; init; } = [];

    public bool IsGallery => Images.Count > 0;

    public string DurationText()
    {
        if (Duration <= 0) return "";
        int total = (int)Math.Round(Duration);
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>单行化并截断，避免简介/评论把卡片撑得过长。</summary>
    static string Clip(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string flat = Regex.Replace(text.Replace('\r', ' ').Replace('\n', ' '), @"\s{2,}", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    public string ToDetailText(bool includeComments = true)
    {
        StringBuilder sb = new();
        sb.AppendLine($"平台：{Platform}（{Id}）");
        sb.AppendLine($"标题：{Title}");
        if (Author.Length > 0) sb.AppendLine($"作者：{Author}");
        if (Play > 0 || Like > 0 || Comment > 0)
            sb.AppendLine($"数据：播放 {Play}，点赞 {Like}，评论 {Comment}");
        if (Duration > 0) sb.AppendLine($"时长：{DurationText()}");
        if (Description.Length > 0 && Description.Trim() != Title.Trim())
            sb.AppendLine($"简介：{Clip(Description, 500)}");
        sb.AppendLine($"链接：{WebUrl}");
        if (IsGallery) sb.AppendLine($"图集：{Images.Count} 张");
        if (VideoUrl.Length > 0) sb.AppendLine($"直链：{VideoUrl}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>简介单独成段，作为普通聊天文本发送，链接方便直接复制。</summary>
    public string ToDescriptionText(int max = 1500)
    {
        if (Description.Length == 0 || Description.Trim() == Title.Trim()) return "";
        return "简介：" + Clip(Description, max);
    }

    /// <summary>评论单独成段，供 AI 分开发送，避免单条消息过长。</summary>
    public string ToCommentsText()
    {
        if (Comments.Count == 0) return "";
        StringBuilder sb = new();
        sb.AppendLine($"热门评论（{Comments.Count} 条）：");
        foreach (VideoComment item in Comments)
            sb.AppendLine($"· {item.User}：{Clip(item.Text, 200)}" + (item.Like > 0 ? $"（{item.Like}赞）" : ""));
        string text = sb.ToString().TrimEnd();
        // QQ 单条文本有长度上限，超出时截断而不是整条发送失败
        return text.Length <= 3500 ? text : text[..3500] + "…";
    }

    public string ToForwardText(bool includeDescription = true, bool includeComments = true)
    {
        StringBuilder sb = new();
        sb.AppendLine($"【{Platform}视频】");
        sb.AppendLine(Title);
        if (Author.Length > 0) sb.AppendLine("作者：" + Author);
        if (Play > 0 || Like > 0)
            sb.AppendLine($"播放 {Play} · 点赞 {Like}");
        if (Duration > 0) sb.AppendLine("时长：" + DurationText());
        if (includeDescription && Description.Length > 0 && Description.Trim() != Title.Trim())
            sb.AppendLine("简介：" + Clip(Description, 200));
        sb.AppendLine("原链接：" + WebUrl);
        if (includeComments && Comments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("热门评论：");
            foreach (VideoComment item in Comments)
                sb.AppendLine($"· {item.User}：{Clip(item.Text, 80)}" + (item.Like > 0 ? $"（{item.Like}赞）" : ""));
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// QQ 视频解析：抖音/B站 视频信息解析、合并转发发送、自动识别聊天中的视频链接。
/// 合并转发消息内容的读取由 QQ聊天 的 QForward 与自动展开提供。
/// </summary>
public class VideoShareModule(
    XmlFunctionCaller functionCaller,
    ILogger<VideoShareModule> logger,
    Interactor<VideoShareModule> interactor,
    QChatService qChatService
) : ChatBehaviour, IConfigurable<VideoShareConfig>
{
    public VideoShareConfig Configuration { get; set; } = null!;

    const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    const string AndroidUserAgent =
        "Mozilla/5.0 (Linux; Android 8.0.0; SM-G955U Build/R16NW) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Mobile Safari/537.36";

    const string DefaultDouyinApi = "https://api.xingzhige.com/API/douyin/?url={url}";

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>下载视频用（大文件需要长超时，由 CancellationToken 控制）。</summary>
    static readonly HttpClient videoHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>B站专用客户端：关闭 Cookie 容器，避免自动 Cookie 与手动 Cookie 重复触发风控。</summary>
    static readonly HttpClient biliHttp = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>自动解析去重：会话 + 视频 ID -> 上次发送时间。
    /// static：模块被重复加载、QChat 重连后仍共用同一份去重表，避免同一条视频被两条链路各发一遍。</summary>
    static readonly ConcurrentDictionary<string, DateTime> recentSent = new();

    /// <summary>已处理过的消息键，防止同一条 OneBot 消息被重复投递时自动解析两次。</summary>
    static readonly ConcurrentDictionary<string, byte> handledEvents = new();

    static readonly object dedupeGate = new();

    OneBotClient? subscribedClient;

    #region 对外 AI 函数

    [XmlFunction(FunctionMode.OneShot)]
    [Description("解析抖音或B站视频链接（支持 v.douyin.com 短链、b23.tv 短链、电脑端链接），返回标题、作者、简介、热门评论、播放数据、时长和视频直链")]
    public async Task VideoParse([Description("视频链接或包含链接的整段分享文本")] string url)
    {
        try
        {
            VideoInfo info = await ParseAsync(url);
            // 简介/评论分开发，避免 AI 单条消息过长
            interactor.Poke(info.ToDetailText(includeComments: false));
            string commentsText = info.ToCommentsText();
            if (commentsText.Length > 0) interactor.Poke(commentsText);
        }
        catch (Exception ex)
        {
            interactor.Poke("视频解析失败：" + ex.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("解析抖音/B站视频链接，并以合并转发（聊天记录卡片）的形式发送到指定群或私聊：文字信息+封面+简介/评论图卡+视频，图集则逐张发图")]
    public async Task VideoForward(
        [Description("视频链接或包含链接的整段分享文本")] string url,
        [Description("目标群号或对方QQ")] long targetId,
        [Description("group 或 private，留空时自动判断")] string messageType = "")
    {
        OneBotClient? client = qChatService.Client;
        if (client is not { IsConnected: true })
        {
            interactor.Poke(QQSuiteTransport.NotConnectedMessage);
            return;
        }
        if (targetId == 0)
        {
            interactor.Poke("targetId 不能为 0");
            return;
        }

        try
        {
            VideoInfo info = await ParseAsync(url);
            bool isGroup = await DetectIsGroupAsync(client, targetId, messageType);
            if (!TryClaimSend(targetId, info, url, out string dedupeKey))
            {
                // 自动解析刚发过同一条视频时，AI 再调一次也不该重复刷屏
                interactor.Poke($"这条视频 {Math.Max(1, Configuration.DedupeMinutes)} 分钟内已经发过了，跳过重复发送");
                return;
            }
            try
            {
                // 视频下载与图卡渲染并行，减少等待
                Task<string?> videoTask = PrepareVideoFileAsync(info);
                Task<string?> cardTask = RenderCardAsync(info);
                await Task.WhenAll(videoTask, cardTask);
                List<object> nodes = BuildNodes(info, videoTask.Result, cardTask.Result);
                await SendVideoResultAsync(client, isGroup, targetId, info, nodes, cardTask.Result, videoTask.Result);
            }
            catch
            {
                // 没发出去就释放去重占用，避免一次失败把这条视频锁死一整个窗口
                ReleaseClaim(dedupeKey);
                throw;
            }
        }
        catch (Exception ex)
        {
            interactor.Poke("视频合并转发失败：" + ex.Message);
        }
    }

    #endregion

    #region 解析

    /// <summary>解析一个链接，自动识别平台。</summary>
    public async Task<VideoInfo> ParseAsync(string input)
    {
        // 分享文本里常混有头像/图标等无关链接，优先挑抖音/B站链接
        List<string> links = ExtractLinks(input);
        string url = links.FirstOrDefault(link => IsBiliLink(link) || IsDouyinLink(link))
                     ?? links.FirstOrDefault()
                     ?? input.Trim();
        if (url.Length == 0) throw new Exception("没有找到链接");

        if (IsBiliLink(url))
            return await ParseBiliAsync(url);
        if (IsDouyinLink(url))
            return await ParseDouyinAsync(input, url);

        // 先展开短链再判断
        string resolved = await ResolveRedirectAsync(url);
        if (IsBiliLink(resolved)) return await ParseBiliAsync(resolved);
        if (IsDouyinLink(resolved)) return await ParseDouyinAsync(input, resolved);

        throw new Exception("只支持抖音和B站链接（当前链接：" + url + "）");
    }

    static bool IsBiliLink(string url) =>
        url.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("b23.tv", StringComparison.OrdinalIgnoreCase);

    static bool IsDouyinLink(string url) =>
        url.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    static readonly Regex LinkRegex = new(@"https?://[^\s\u3000""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从一段文本（可能含转义）里提取链接。</summary>
    static List<string> ExtractLinks(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        string normalized = text
            .Replace("&amp;", "&")
            .Replace("\\/", "/").Replace("\\u002F", "/").Replace("\\u002f", "/");
        return LinkRegex.Matches(normalized)
            .Select(match => match.Value.TrimEnd('.', ',', '，', '。', ')', '）'))
            .Where(value => value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
    }

    async Task<string> ResolveRedirectAsync(string url)
    {
        if (!url.Contains("v.douyin.com", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("b23.tv", StringComparison.OrdinalIgnoreCase))
            return url;

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", AndroidUserAgent);
            using HttpResponseMessage response =
                await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            string? finalUrl = response.RequestMessage?.RequestUri?.ToString();
            if (!string.IsNullOrEmpty(finalUrl)) return finalUrl;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "短链展开失败 {Url}", url);
        }
        return url;
    }

    async Task<VideoInfo> ParseBiliAsync(string url)
    {
        string workUrl = await ResolveRedirectAsync(url);
        Match bvMatch = Regex.Match(workUrl, "BV[0-9A-Za-z]{10}");
        if (!bvMatch.Success)
            throw new Exception("没有从B站链接里找到 BV 号（支持视频与 b23.tv 短链）");

        string bvid = bvMatch.Value;
        try
        {
            return await ParseBiliDetailAsync(bvid);
        }
        catch (Exception apiError)
        {
            logger.LogWarning("B站接口解析失败，尝试网页元数据兜底：{Message}", apiError.Message);
            try
            {
                return await ParseBiliSharePageAsync(bvid);
            }
            catch (Exception pageError)
            {
                throw new Exception("B站解析失败：" + apiError.Message + "；网页兜底也失败：" + pageError.Message);
            }
        }
    }

    async Task<VideoInfo> ParseBiliDetailAsync(string bvid)
    {
        // 旧的 x/web-interface/view 在部分出口 IP 会直接返回 412 网页，wbi 版本仍可用
        JsonElement view = await BiliGetAsync("https://api.bilibili.com/x/web-interface/wbi/view?bvid=" + bvid);

        string title = Pick(view, "title") ?? "";
        string author = Pick(view, "owner.name") ?? "";
        string description = Configuration.FetchDescription ? Pick(view, "desc") ?? "" : "";
        string cover = Pick(view, "pic") ?? "";
        if (cover.StartsWith("http://")) cover = "https://" + cover[7..];

        long play = PickLong(view, "stat.view");
        long like = PickLong(view, "stat.like");
        long comment = PickLong(view, "stat.reply");
        double duration = PickDouble(view, "duration");
        long cid = PickLong(view, "cid");
        long aid = PickLong(view, "aid");

        List<VideoComment> comments = await FetchBiliCommentsAsync(aid);

        string videoUrl = "";
        try
        {
            // fnval=0 返回 durl（自带音轨的 mp4），更适合直接下载后作为视频消息发送
            JsonElement playData = await BiliGetAsync(
                "https://api.bilibili.com/x/player/wbi/playurl?bvid=" + bvid + "&cid=" + cid + "&qn=80&fnval=0");
            videoUrl = Pick(playData, "durl.0.url") ?? "";
            if (videoUrl.Length == 0)
            {
                JsonElement dashData = await BiliGetAsync(
                    "https://api.bilibili.com/x/player/wbi/playurl?bvid=" + bvid + "&cid=" + cid + "&qn=80&fnval=16");
                videoUrl = Pick(dashData, "dash.video.0.base_url") ?? "";
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "B站直链获取失败 {Bvid}", bvid);
        }

        return new VideoInfo
        {
            Platform = "B站",
            Id = bvid,
            Title = title,
            Author = author,
            Description = description,
            Cover = cover,
            VideoUrl = videoUrl,
            WebUrl = "https://www.bilibili.com/video/" + bvid,
            Play = play,
            Like = like,
            Comment = comment,
            Duration = duration,
            Comments = comments
        };
    }

    /// <summary>抓取 B站热门评论（sort=2 按热度，取第一页的前 N 条）。失败时不影响视频解析。</summary>
    async Task<List<VideoComment>> FetchBiliCommentsAsync(long aid)
    {
        int count = Math.Clamp(Configuration.CommentCount, 0, 20);
        if (aid <= 0 || count == 0) return [];

        List<VideoComment> list = [];
        try
        {
            JsonElement data = await BiliGetAsync(
                "https://api.bilibili.com/x/v2/reply?oid=" + aid + "&type=1&sort=2&ps=" + count + "&pn=1");

            foreach (string key in new[] { "top_replies", "replies" })
            {
                if (!data.TryGetProperty(key, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement item in array.EnumerateArray())
                {
                    string text = Pick(item, "content.message") ?? "";
                    if (text.Trim().Length == 0) continue;
                    list.Add(new VideoComment
                    {
                        User = Pick(item, "member.uname") ?? "匿名用户",
                        Text = text,
                        Like = PickLong(item, "like")
                    });
                    if (list.Count >= count) return list;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "B站评论抓取失败，已跳过");
        }
        return list;
    }

    /// <summary>接口被风控时的兜底：从视频网页的 og 元数据里取标题与封面。</summary>
    async Task<VideoInfo> ParseBiliSharePageAsync(string bvid)
    {
        string webUrl = "https://www.bilibili.com/video/" + bvid;
        using HttpRequestMessage request = new(HttpMethod.Get, webUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        using HttpResponseMessage response = await biliHttp.SendAsync(request);
        string html = await response.Content.ReadAsStringAsync();

        string title = DecodeMeta(ExtractMeta(html, "og:title"));
        string cover = DecodeMeta(ExtractMeta(html, "og:image"));
        if (cover.StartsWith("//")) cover = "https:" + cover;

        string author = "";
        Match authorMatch = Regex.Match(html, "\"owner\"\\s*:\\s*\\{[^}]*?\"name\"\\s*:\\s*\"([^\"]+)\"");
        if (authorMatch.Success) author = DecodeMeta(authorMatch.Groups[1].Value);

        string description = Configuration.FetchDescription
            ? DecodeMeta(ExtractMeta(html, "og:description"))
            : "";

        if (title.Length == 0)
            throw new Exception("网页里没有找到视频信息（同样被风控）");

        return new VideoInfo
        {
            Platform = "B站",
            Id = bvid,
            Title = title,
            Author = author,
            Description = description,
            Cover = cover,
            WebUrl = webUrl
        };
    }

    static string ExtractMeta(string html, string name)
    {
        Match meta = Regex.Match(html, "<meta[^>]*" + Regex.Escape(name) + "[^>]*>", RegexOptions.IgnoreCase);
        if (!meta.Success) return "";
        Match content = Regex.Match(meta.Value, "content\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return content.Success ? content.Groups[1].Value : "";
    }

    static string DecodeMeta(string value) =>
        string.IsNullOrEmpty(value) ? "" : WebUtility.HtmlDecode(value).Trim();

    static string? biliCookie;
    static readonly SemaphoreSlim biliCookieLock = new(1, 1);

    async Task<JsonElement> BiliGetAsync(string url)
    {
        string lastIssue = "网络请求失败";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string body = await BiliGetBodyAsync(url);
            if (!body.TrimStart().StartsWith("{"))
            {
                // 返回的是 HTML 验证页，换一份新的 buvid 指纹 Cookie 再试
                lastIssue = "返回了网页而不是数据";
                logger.LogWarning("B站接口返回非 JSON（第 {Attempt} 次），刷新 Cookie 重试", attempt + 1);
                await GetBiliCookieAsync(true);
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement.Clone();
            int code = root.TryGetProperty("code", out JsonElement codeElement) &&
                       codeElement.ValueKind == JsonValueKind.Number
                ? codeElement.GetInt32()
                : -1;
            if (code is -412 or -352)
            {
                // 风控拦截：换指纹 Cookie 后重试
                lastIssue = "code " + code + "（风控拦截）";
                logger.LogWarning("B站接口被风控拦截 code={Code}（第 {Attempt} 次），刷新 Cookie 重试", code, attempt + 1);
                await GetBiliCookieAsync(true);
                continue;
            }
            if (code != 0)
            {
                string message = Pick(root, "message") ?? "";
                throw new Exception("B站接口返回 " + code + (message.Length > 0 ? "：" + message : ""));
            }
            return root.TryGetProperty("data", out JsonElement data) ? data.Clone() : default;
        }
        throw new Exception("B站接口连续 3 次失败（最后一次：" + lastIssue + "），可能触发风控，请稍后重试");
    }

    async Task<string> BiliGetBodyAsync(string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        string cookie = await GetBiliCookieAsync(false);
        if (cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using HttpResponseMessage response = await biliHttp.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        if (logger.IsEnabled(LogLevel.Debug))
        {
            string head = body.Length > 160 ? body[..160] : body;
            logger.LogDebug("B站请求 {Url} -> {Status}，前160字：{Head}", url, (int)response.StatusCode, head);
        }
        return body;
    }

    /// <summary>获取 B站 buvid 指纹 Cookie（风控拦截时重新申请）。</summary>
    async Task<string> GetBiliCookieAsync(bool forceRefresh)
    {
        if (!forceRefresh && biliCookie != null) return biliCookie;

        await biliCookieLock.WaitAsync();
        try
        {
            if (!forceRefresh && biliCookie != null) return biliCookie;

            try
            {
                using HttpRequestMessage request =
                    new(HttpMethod.Get, "https://api.bilibili.com/x/frontend/finger/spi");
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                using HttpResponseMessage response = await biliHttp.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                using JsonDocument document = JsonDocument.Parse(body);
                string? buvid3 = Pick(document.RootElement, "data.b_3");
                string? buvid4 = Pick(document.RootElement, "data.b_4");
                biliCookie = buvid3 != null
                    ? "buvid3=" + buvid3 + (buvid4 != null ? "; buvid4=" + buvid4 : "")
                    : "buvid3=1; b_nut=1";
            }
            catch
            {
                biliCookie = "buvid3=1; b_nut=1";
            }
            return biliCookie;
        }
        finally
        {
            biliCookieLock.Release();
        }
    }

    async Task<VideoInfo> ParseDouyinAsync(string shareText, string url)
    {
        string workUrl = await ResolveRedirectAsync(url);
        Match idMatch = Regex.Match(workUrl, @"(?:/video|/note|/slides|/share/video)/(\d{15,25})");
        if (!idMatch.Success)
            idMatch = Regex.Match(workUrl, @"(?:modal_id|aweme_id|item_ids)=(\d{15,25})");
        if (!idMatch.Success)
            idMatch = Regex.Match(shareText, @"(\d{19})");
        string id = idMatch.Success ? idMatch.Groups[1].Value : "";

        // 解析接口与分享页兜底并行跑，谁先成功用谁；接口 404/超时就不用干等 20 秒
        Exception? apiError = null;
        Exception? pageError = null;

        async Task<VideoInfo?> TryApiAsync()
        {
            try
            {
                return await ParseDouyinByApiAsync(url, id);
            }
            catch (Exception ex)
            {
                apiError = ex;
                logger.LogWarning("抖音解析接口失败，等待分享页兜底：{Message}", ex.Message);
                return null;
            }
        }

        async Task<VideoInfo?> TryPageAsync()
        {
            try
            {
                return await ParseDouyinSharePageAsync(id);
            }
            catch (Exception ex)
            {
                pageError = ex;
                logger.LogWarning("抖音分享页兜底失败：{Message}", ex.Message);
                return null;
            }
        }

        Task<VideoInfo?> apiTask = TryApiAsync();
        Task<VideoInfo?> pageTask = id.Length > 0 ? TryPageAsync() : Task.FromResult<VideoInfo?>(null);

        if (id.Length == 0)
        {
            // 短链没展开出 ID，只能靠接口自己解析短链
            VideoInfo? fromApi = await apiTask;
            if (fromApi != null) return fromApi;
            throw new Exception("抖音短链展开失败，没能拿到视频 ID；请稍后重试或配置第三方解析接口");
        }

        Task<VideoInfo?> finished = await Task.WhenAny(apiTask, pageTask);
        VideoInfo? first = await finished;
        if (first != null) return first;

        VideoInfo? second = await (finished == apiTask ? pageTask : apiTask);
        if (second != null) return second;

        throw new Exception("抖音解析失败（接口与分享页都没成功）：" +
                            (apiError?.Message ?? pageError?.Message ?? "未知错误"));
    }

    async Task<VideoInfo> ParseDouyinByApiAsync(string url, string id)
    {
        string template = string.IsNullOrWhiteSpace(Configuration.DouyinApiUrl)
            ? DefaultDouyinApi
            : Configuration.DouyinApiUrl.Trim();
        string apiUrl = template
            .Replace("{url}", Uri.EscapeDataString(url))
            .Replace("{key}", Uri.EscapeDataString(Configuration.DouyinApiKey ?? ""));

        using HttpRequestMessage request = new(HttpMethod.Get, apiUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        string body = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
        if (!body.TrimStart().StartsWith("{"))
            throw new Exception("抖音解析接口返回了网页而不是数据（接口可能失效，可在面板配置第三方接口）");
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement.Clone();

        string? codeText = Pick(root, "code");
        if (codeText != null && codeText != "0" && codeText != "200")
        {
            string message = Pick(root, "msg", "message") ?? "接口返回错误";
            throw new Exception(message + "（code=" + codeText + "）");
        }

        string title = Pick(root, "data.item.title", "data.desc", "data.title", "title", "desc") ?? "";
        string author = Pick(root, "data.author.name", "data.author.nickname", "data.nickname", "author") ?? "";
        string description = Configuration.FetchDescription
            ? Pick(root, "data.desc", "data.item.desc", "data.item.title", "desc") ?? ""
            : "";
        string cover = Pick(root, "data.item.cover", "data.cover", "data.pic", "cover") ?? "";
        string videoUrl = Pick(root, "data.item.url", "data.video_url", "data.url", "data.video", "video") ?? "";
        double duration = PickDouble(root, "data.item.duration", "data.duration");
        List<string> images = PickImages(root);
        List<VideoComment> comments = PickComments(root,
            "data.comments", "data.comment_list", "data.item.comments", "comments", "data.item.comment_list");
        string realId = Pick(root, "data.jx.item_id", "data.item.aweme_id", "aweme_id", "item_id") ?? id;

        if (title.Length == 0 && videoUrl.Length == 0)
            throw new Exception("接口没有返回有效视频信息");

        // 图集时接口给的 url 可能是音乐地址，不当作视频
        if (images.Count > 0 && videoUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            videoUrl = "";
        if (videoUrl.StartsWith("http://")) videoUrl = "https://" + videoUrl[7..];
        if (cover.StartsWith("http://")) cover = "https://" + cover[7..];

        return new VideoInfo
        {
            Platform = "抖音",
            Id = realId,
            Title = title.Length > 0 ? title : "抖音视频 " + (realId.Length > 0 ? realId : url),
            Author = author,
            Description = description,
            Cover = cover,
            VideoUrl = videoUrl,
            WebUrl = realId.Length > 0 ? "https://www.douyin.com/video/" + realId : url,
            Play = PickLong(root, "data.stat.play", "data.statistics.play_count"),
            Like = PickLong(root, "data.stat.like", "data.statistics.digg_count"),
            Comment = PickLong(root, "data.stat.comment", "data.statistics.comment_count"),
            Duration = duration,
            Images = images,
            Comments = comments
        };
    }

    /// <summary>分享页兜底：从 window._ROUTER_DATA 里找 videoInfoRes（部分网络环境仍可用）。</summary>
    async Task<VideoInfo> ParseDouyinSharePageAsync(string id)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Get, "https://www.iesdouyin.com/share/video/" + id + "/");
        request.Headers.TryAddWithoutValidation("User-Agent", AndroidUserAgent);
        request.Headers.TryAddWithoutValidation("Referer",
            "https://www.douyin.com/?is_from_mobile_home=1&recommend=1");
        string html = await (await http.SendAsync(request)).Content.ReadAsStringAsync();

        string? json = ExtractJsonObjectAfter(html, "window._ROUTER_DATA = ");
        if (json == null)
        {
            // 部分页面编码为 \u002F，替换后再找
            string decoded = html.Replace("\\u002F", "/");
            json = ExtractJsonObjectAfter(decoded, "window._ROUTER_DATA = ");
        }
        if (json == null) throw new Exception("分享页里没有找到数据（官方风控）");

        using JsonDocument document = JsonDocument.Parse(json.Replace("\\u002F", "/").Replace("\\/", "/"));
        JsonElement item = default;
        bool found = false;
        if (document.RootElement.TryGetProperty("loaderData", out JsonElement loaderData) &&
            loaderData.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty page in loaderData.EnumerateObject())
            {
                if (page.Value.ValueKind == JsonValueKind.Object &&
                    page.Value.TryGetProperty("videoInfoRes", out JsonElement info) &&
                    info.ValueKind == JsonValueKind.Object &&
                    info.TryGetProperty("item_list", out JsonElement list) &&
                    list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                {
                    item = list[0];
                    found = true;
                    break;
                }
            }
        }
        if (!found) throw new Exception("分享页数据里没有视频信息（官方风控）");

        string uri = Pick(item, "video.play_addr.uri") ?? "";
        string videoUrl = uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? uri
            : uri.Length > 0
                ? "https://www.douyin.com/aweme/v1/play/?video_id=" + uri
                : "";

        List<string> images = PickImages(item);
        double durationMs = PickDouble(item, "video.duration");

        return new VideoInfo
        {
            Platform = "抖音",
            Id = Pick(item, "aweme_id") ?? id,
            Title = Pick(item, "desc") ?? "抖音视频 " + id,
            Author = Pick(item, "author.nickname") ?? "",
            Description = Configuration.FetchDescription ? Pick(item, "desc") ?? "" : "",
            Cover = Pick(item, "video.cover.url_list.0") ?? "",
            VideoUrl = videoUrl,
            WebUrl = "https://www.douyin.com/video/" + id,
            Play = PickLong(item, "statistics.play_count"),
            Like = PickLong(item, "statistics.digg_count"),
            Comment = PickLong(item, "statistics.comment_count"),
            Duration = durationMs > 1000 ? durationMs / 1000 : durationMs,
            Images = images
        };
    }

    /// <summary>从 marker 后扫描配对的花括号，返回完整 JSON 对象。</summary>
    static string? ExtractJsonObjectAfter(string text, string marker)
    {
        int start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        int braceStart = text.IndexOf('{', start);
        if (braceStart < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = braceStart; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(braceStart, i - braceStart + 1);
            }
        }
        return null;
    }

    #endregion

    #region 合并转发

    /// <summary>把简介/评论渲染成图；关闭或失败时返回 null，由文字回退。</summary>
    async Task<string?> RenderCardAsync(VideoInfo info)
    {
        if (!Configuration.RenderAsImage) return null;
        if (!Configuration.RenderDescriptionInImage && !Configuration.RenderCommentsInImage) return null;

        return await VideoCardRenderer.RenderAsync(
            info, Configuration.RenderDescriptionInImage, Configuration.RenderCommentsInImage, logger);
    }

    List<object> BuildNodes(VideoInfo info, string? localVideo = null, string? cardImage = null)
    {
        long botId = qChatService.BotId;
        string uin = (botId == 0 ? 10000 : botId).ToString(CultureInfo.InvariantCulture);
        const string name = "视频解析";

        // NapCat 的转发节点不解析字符串 CQ 码，图片/视频必须用消息段数组
        object TextNode(string content) => new { type = "node", data = new { name, nickname = name, uin, content } };
        object ImageNode(string file) => new
        {
            type = "node",
            data = new
            {
                name, nickname = name, uin,
                content = new object[] { new { type = "image", data = new { file } } }
            }
        };
        object VideoNode(string file) => new
        {
            type = "node",
            data = new
            {
                name, nickname = name, uin,
                content = new object[] { new { type = "video", data = new { file } } }
            }
        };

        bool imageHasDescription = cardImage != null && Configuration.RenderDescriptionInImage;
        bool imageHasComments = cardImage != null && Configuration.RenderCommentsInImage;

        List<object> nodes = [TextNode(info.ToForwardText(includeDescription: false, includeComments: false))];

        if (!string.IsNullOrWhiteSpace(info.Cover))
            nodes.Add(ImageNode(info.Cover));

        // 没画进图卡的简介/评论仍放进聊天记录，只是改成文字节点
        if (!imageHasDescription)
        {
            string description = info.ToDescriptionText();
            if (description.Length > 0) nodes.Add(TextNode(description));
        }

        if (cardImage != null)
            nodes.Add(ImageNode("file:///" + cardImage.Replace('\\', '/')));

        if (!imageHasComments)
        {
            string comments = info.ToCommentsText();
            if (comments.Length > 0) nodes.Add(TextNode(comments));
        }

        if (info.IsGallery)
        {
            object[] gallery = info.Images.Take(9)
                .Select(image => (object)new { type = "image", data = new { file = image } })
                .ToArray();
            if (gallery.Length > 0)
                nodes.Add(new { type = "node", data = new { name, nickname = name, uin, content = gallery } });
        }
        else if (Configuration.IncludeVideoNode && localVideo != null)
        {
            // 只用本地文件：远端直链会因防盗链被协议端当成纯文本；下载失败时宁可不放视频，原链接已在文字节点里
            nodes.Add(VideoNode("file:///" + localVideo.Replace('\\', '/')));
        }

        return nodes;
    }

    async Task SendForwardAsync(OneBotClient client, bool isGroup, long targetId, List<object> nodes, int timeoutSeconds = 30)
    {
        string action = isGroup ? "send_group_forward_msg" : "send_private_forward_msg";
        object parameters = isGroup
            ? new { group_id = targetId, messages = nodes }
            : new { user_id = targetId, messages = nodes };

        JsonElement raw = await client.CallActionRawAsync(action, parameters, timeoutSeconds);
        string? status = Pick(raw, "status");
        int retCode = (int)PickLong(raw, "retcode");
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) || retCode > 1)
        {
            string message = Pick(raw, "message", "wording") ?? "协议端返回失败";
            throw new Exception(message + "（retcode=" + retCode + "）");
        }
    }

    /// <summary>需要把视频放进转发节点，或选择“下载完成后再发”时，先把视频下到本地共用。</summary>
    async Task<string?> PrepareVideoFileAsync(VideoInfo info)
    {
        if (info.IsGallery || string.IsNullOrWhiteSpace(info.VideoUrl)) return null;

        bool needFile = Configuration.IncludeVideoNode ||
                        (Configuration.SendVideoSeparately && Configuration.SendAfterDownload);
        if (!needFile) return null;
        return await DownloadVideoAsync(info);
    }

    /// <summary>发送聊天记录：优先把视频放进同一条转发；协议端不认视频节点时退回「卡片 + 单独视频」，不至于什么都发不出去。</summary>
    async Task SendVideoResultAsync(OneBotClient client, bool isGroup, long targetId, VideoInfo info, List<object> nodes, string? cardImage, string? localVideo)
    {
        // 带视频的转发要上传文件，30 秒不够，给 180 秒
        int timeout = localVideo != null ? 180 : 30;
        try
        {
            await SendForwardAsync(client, isGroup, targetId, nodes, timeout);
            return;
        }
        catch (Exception ex)
        {
            if (localVideo == null) throw;
            logger.LogWarning("带视频的聊天记录发送失败，退回「卡片 + 单独视频」：{Message}", ex.Message);
        }

        await SendForwardAsync(client, isGroup, targetId, BuildNodes(info, null, cardImage));
        try
        {
            await SendVideoMessageAsync(client, isGroup, targetId, info, localVideo, force: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning("视频单独发送也失败，已保留聊天记录卡片（协议端可能不支持视频文件）：{Message}", ex.Message);
        }
    }

    /// <summary>单独发送视频消息：先下载到本地，绕开 B站直链的 Referer/防盗链限制。</summary>
    async Task SendVideoMessageAsync(OneBotClient client, bool isGroup, long targetId, VideoInfo info, string? localFile = null, bool force = false)
    {
        if (!Configuration.SendVideoSeparately && !force) return;
        if (info.IsGallery || string.IsNullOrWhiteSpace(info.VideoUrl)) return;

        string? file = localFile ?? await DownloadVideoAsync(info);
        if (file == null) return;

        object message = new object[]
        {
            new { type = "video", data = new { file = "file:///" + file.Replace('\\', '/') } }
        };
        object parameters = isGroup
            ? new { group_id = targetId, message }
            : new { user_id = targetId, message };

        try
        {
            JsonElement raw = await client.CallActionRawAsync(
                isGroup ? "send_group_msg" : "send_private_msg", parameters, 180);
            string? status = Pick(raw, "status");
            int retCode = (int)PickLong(raw, "retcode");
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) && retCode > 1)
                logger.LogWarning("视频消息发送失败 retcode={RetCode} {Message}",
                    retCode, Pick(raw, "message", "wording"));
        }
        catch (Exception ex)
        {
            logger.LogWarning("视频消息发送失败：{Message}", ex.Message);
        }
    }

    async Task SendTextAsync(OneBotClient client, bool isGroup, long targetId, string text)
    {
        try
        {
            object message = new object[] { new { type = "text", data = new { text } } };
            object parameters = isGroup
                ? new { group_id = targetId, message }
                : new { user_id = targetId, message };
            await client.CallActionRawAsync(
                isGroup ? "send_group_msg" : "send_private_msg", parameters, 30);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "发送失败提示失败");
        }
    }

    async Task<string?> DownloadVideoAsync(VideoInfo info)
    {
        long limit = Math.Max(10, Configuration.MaxVideoSizeMB) * 1024L * 1024L;
        string dir = Path.Combine(Path.GetTempPath(), "QQSuiteVideo");
        try { Directory.CreateDirectory(dir); }
        catch { return null; }

        try
        {
            foreach (string old in Directory.GetFiles(dir, "*.mp4"))
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
                    File.Delete(old);
            }
        }
        catch { }

        string safeId = Regex.Replace(info.Id.Length > 0 ? info.Id : "video", "[^0-9A-Za-z_-]", "");
        string file = Path.Combine(dir, $"{info.Platform}_{safeId}_{DateTime.Now:HHmmss}.mp4");

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, info.VideoUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Referer",
                info.Platform == "B站" ? "https://www.bilibili.com/" : "https://www.douyin.com/");
            using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
            using HttpResponseMessage response =
                await videoHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("视频下载失败 HTTP {Code}", (int)response.StatusCode);
                return null;
            }
            if (response.Content.Headers.ContentLength is long length && length > limit)
            {
                logger.LogInformation("视频 {Size}MB 超过上限，跳过下载", length / 1024 / 1024);
                return null;
            }

            Stream input = await response.Content.ReadAsStreamAsync(cts.Token);
            FileStream output = new(file, FileMode.Create, FileAccess.Write, FileShare.None);
            long total = 0;
            bool tooBig = false;
            try
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cts.Token)) > 0)
                {
                    total += read;
                    if (total > limit)
                    {
                        tooBig = true;
                        break;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                }
            }
            finally
            {
                await output.DisposeAsync();
                await input.DisposeAsync();
            }

            if (tooBig || total < 1024)
            {
                if (tooBig) logger.LogInformation("视频超过上限 {Limit}MB，已取消", Configuration.MaxVideoSizeMB);
                try { File.Delete(file); } catch { }
                return null;
            }
            return file;
        }
        catch (Exception ex)
        {
            logger.LogWarning("视频下载失败：{Message}", ex.Message);
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            return null;
        }
    }

    async Task<bool> DetectIsGroupAsync(OneBotClient client, long targetId, string messageType)
    {
        if (messageType == "group") return true;
        if (messageType == "private") return false;
        if (qChatService.GroupStates.ContainsKey(targetId)) return true;

        try
        {
            await client.CallActionAsync<JsonElement>("get_group_info", new { group_id = targetId });
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 自动识别链接

    void OnBotEvent(OneBotBaseEvent botEvent)
    {
        try
        {
            if (Configuration?.AutoParseLinks != true) return;
            if (botEvent is not OneBotMessageEvent message) return;
            if (!TryAcceptEvent(message)) return;

            long botId = qChatService.BotId;
            if (botId != 0 && message.UserId == botId) return;

            if (message.GroupId != 0 && Configuration.AutoParseOnlyActiveGroup)
            {
                if (!qChatService.GroupStates.TryGetValue(message.GroupId, out GroupMessageSource? state) ||
                    !state.IsEnabled)
                    return;
            }

            List<string> links = ExtractLinks(message.RawMessage)
                .Where(link => IsDouyinLink(link) || IsBiliLink(link))
                .Distinct()
                .Take(Math.Clamp(Configuration.MaxLinksPerMessage, 1, 5))
                .ToList();
            if (links.Count == 0) return;

            OneBotClient? client = qChatService.Client;
            if (client is not { IsConnected: true }) return;

            long groupId = message.GroupId;
            long peerId = message.UserId;
            _ = Task.Run(() => AutoSendAsync(client, groupId, peerId, links));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "自动解析入口异常");
        }
    }

    /// <summary>同一条消息只自动解析一次：优先用 message_id 作键，拿不到就退回时间 + 发送者 + 原文。</summary>
    bool TryAcceptEvent(OneBotMessageEvent message)
    {
        long messageId = 0;
        if (!string.IsNullOrEmpty(message.RawJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(message.RawJson!);
                if (doc.RootElement.TryGetProperty("message_id", out JsonElement id) &&
                    id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value))
                    messageId = value;
            }
            catch
            {
                // RawJson 不是合法 JSON 时退回内容指纹
            }
        }

        string key = messageId != 0
            ? $"m{message.SelfId}:{messageId.ToString(CultureInfo.InvariantCulture)}"
            : $"t{message.SelfId}|{message.Time}|{message.GroupId}|{message.UserId}|{message.RawMessage}";
        if (!handledEvents.TryAdd(key, 0)) return false;
        if (handledEvents.Count > 2048) handledEvents.Clear();
        return true;
    }

    /// <summary>原子去重：同一会话 + 同一视频在 DedupeMinutes 内只发一次，返回 false 表示刚发过。</summary>
    bool TryClaimSend(long targetId, VideoInfo info, string link, out string key)
    {
        key = $"{targetId}:{(info.Id.Length > 0 ? info.Id : link)}";
        DateTime now = DateTime.UtcNow;
        int minutes = Math.Max(1, Configuration.DedupeMinutes);
        lock (dedupeGate)
        {
            if (recentSent.TryGetValue(key, out DateTime last) && (now - last).TotalMinutes < minutes)
                return false;
            recentSent[key] = now;
            return true;
        }
    }

    /// <summary>发送失败时释放去重占用，让用户重试时不会被窗口锁死。</summary>
    void ReleaseClaim(string key)
    {
        lock (dedupeGate)
        {
            recentSent.TryRemove(key, out _);
        }
    }

    async Task AutoSendAsync(OneBotClient client, long groupId, long peerId, List<string> links)
    {
        bool isGroup = groupId != 0;
        long targetId = isGroup ? groupId : peerId;
        int succeeded = 0;
        string? lastError = null;

        foreach (string link in links)
        {
            string? dedupeKey = null;
            try
            {
                VideoInfo info = await ParseAsync(link);
                if (!TryClaimSend(targetId, info, link, out string key))
                {
                    logger.LogInformation("跳过重复视频 {Key}：{Minutes} 分钟内已向该会话发过", key, Math.Max(1, Configuration.DedupeMinutes));
                    continue;
                }
                dedupeKey = key;

                // 视频下载与图卡渲染并行；选了“下载完成后再发”时等两者都完成，再连续发卡片和视频
                Task<string?> videoTask = PrepareVideoFileAsync(info);
                Task<string?> cardTask = RenderCardAsync(info);
                await Task.WhenAll(videoTask, cardTask);
                List<object> nodes = BuildNodes(info, videoTask.Result, cardTask.Result);
                await SendVideoResultAsync(client, isGroup, targetId, info, nodes, cardTask.Result, videoTask.Result);
                logger.LogInformation("视频已发送 {Key}：节点 {Nodes} 个，视频{State}", dedupeKey, nodes.Count, videoTask.Result == null ? "未附带" : "已附带");
                succeeded++;
                await Task.Delay(800);
            }
            catch (Exception ex)
            {
                // 没发出去就释放去重占用，避免一次失败把这条视频锁死一整个窗口
                if (dedupeKey != null) ReleaseClaim(dedupeKey);
                lastError = ex.Message;
                logger.LogWarning("自动解析视频失败 {Link}：{Type}：{Message}", link, ex.GetType().Name, ex.Message);
            }
        }

        // 全部失败时给用户一个明确反馈，避免“什么都没发生”
        if (succeeded == 0 && lastError != null)
        {
            string tip = lastError.Length > 160 ? lastError[..160] + "…" : lastError;
            await SendTextAsync(client, isGroup, targetId, "视频解析失败：" + tip);
        }

        // 顺手清理过期去重记录，避免字典无限增长
        DateTime expire = DateTime.UtcNow.AddMinutes(-Math.Max(10, Configuration.DedupeMinutes * 2));
        foreach ((string key, DateTime time) in recentSent)
        {
            if (time < expire) recentSent.TryRemove(key, out _);
        }
    }

    #endregion

    #region 生命周期

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "解析抖音/B站视频链接并发送合并转发卡片，也能自动识别聊天中的视频链接。",
            Explanation = "VideoParse 解析链接返回标题/作者/封面/直链；VideoForward 把解析结果用合并转发（聊天记录卡片）发到群或私聊。用户发来抖音(v.douyin.com)或B站(b23.tv/BV号)链接时，开启「自动解析视频链接」会直接发送。读取合并转发里的聊天记录用 QQ聊天 的 QForward。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        logger.LogInformation("QQ视频解析已启动（B站走 wbi 接口 + 指纹重试 + 网页兜底）");
        Subscribe();
        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        // QQ聊天 的连接实例可能被重建，发现变化时重新挂接事件
        if (!ReferenceEquals(subscribedClient, qChatService.Client))
            Subscribe();
        return Task.CompletedTask;
    }

    void Subscribe()
    {
        subscribedClient?.EventReceived -= OnBotEvent;
        subscribedClient = qChatService.Client;
        subscribedClient?.EventReceived += OnBotEvent;
    }

    protected override Task OnDestroy()
    {
        subscribedClient?.EventReceived -= OnBotEvent;
        subscribedClient = null;
        recentSent.Clear();
        return Task.CompletedTask;
    }

    #endregion

    #region 工具

    /// <summary>按点分路径取值，支持数组下标（如 stat.view、dash.video.0.base_url）。</summary>
    static string? Pick(JsonElement root, params string[] paths)
    {
        foreach (string path in paths)
        {
            JsonElement current = root;
            bool ok = true;
            foreach (string part in path.Split('.'))
            {
                if (current.ValueKind == JsonValueKind.Object &&
                    current.TryGetProperty(part, out JsonElement next))
                {
                    current = next;
                    continue;
                }
                if (current.ValueKind == JsonValueKind.Array &&
                    int.TryParse(part, out int index) &&
                    index >= 0 && index < current.GetArrayLength())
                {
                    current = current[index];
                    continue;
                }
                ok = false;
                break;
            }
            if (!ok) continue;

            string? value = current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.ToString(),
                JsonValueKind.True => "true",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    static long PickLong(JsonElement root, params string[] paths)
    {
        string? text = Pick(root, paths);
        if (text == null) return 0;
        return long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out long value)
            ? value
            : 0;
    }

    static double PickDouble(JsonElement root, params string[] paths)
    {
        string? text = Pick(root, paths);
        if (text == null) return 0;
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
    }

    /// <summary>从第三方接口返回里尽力提取评论；字段因接口而异，找不到数组就返回空。</summary>
    List<VideoComment> PickComments(JsonElement root, params string[] paths)
    {
        List<VideoComment> list = [];
        int count = Math.Clamp(Configuration.CommentCount, 0, 20);
        if (count == 0) return list;

        foreach (string path in paths)
        {
            JsonElement node = PickNode(root, path);
            if (node.ValueKind != JsonValueKind.Array) continue;

            foreach (JsonElement item in node.EnumerateArray())
            {
                string? text = Pick(item, "text", "content", "comment", "message");
                if (string.IsNullOrWhiteSpace(text)) continue;
                list.Add(new VideoComment
                {
                    User = Pick(item, "user.nickname", "nickname", "user_name", "user.unique_id") ?? "网友",
                    Text = text,
                    Like = PickLong(item, "digg_count", "like", "likes")
                });
                if (list.Count >= count) return list;
            }
            if (list.Count > 0) return list;
        }
        return list;
    }

    static JsonElement PickNode(JsonElement root, string path)
    {
        JsonElement node = root;
        foreach (string part in path.Split('.'))
        {
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(part, out JsonElement next))
            {
                node = next;
                continue;
            }
            if (node.ValueKind == JsonValueKind.Array && int.TryParse(part, out int index) &&
                index >= 0 && index < node.GetArrayLength())
            {
                node = node[index];
                continue;
            }
            return default;
        }
        return node;
    }

    static List<string> PickImages(JsonElement root)
    {
        List<string> result = [];
        foreach (string path in new[]
                 {
                     "data.item.images", "data.images", "item.images", "videoInfoRes.item_list.0.images",
                     "images", "image_post_info.images"
                 })
        {
            JsonElement node = root;
            bool ok = true;
            foreach (string part in path.Split('.'))
            {
                if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(part, out JsonElement next))
                {
                    node = next;
                }
                else if (node.ValueKind == JsonValueKind.Array && int.TryParse(part, out int index) &&
                         index >= 0 && index < node.GetArrayLength())
                {
                    node = node[index];
                }
                else
                {
                    ok = false;
                    break;
                }
            }
            if (!ok || node.ValueKind != JsonValueKind.Array) continue;

            foreach (JsonElement image in node.EnumerateArray())
            {
                if (image.ValueKind == JsonValueKind.String)
                {
                    string? direct = image.GetString();
                    if (!string.IsNullOrWhiteSpace(direct)) result.Add(direct);
                }
                else if (image.ValueKind == JsonValueKind.Object)
                {
                    string? url = Pick(image, "url_list.0", "url", "display_image.url_list.0");
                    if (!string.IsNullOrWhiteSpace(url)) result.Add(url);
                }
            }
            if (result.Count > 0) break;
        }
        return result.Distinct().ToList();
    }

    #endregion
}
