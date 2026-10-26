using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using QQSuite.Core;

namespace QQSuite.PixivShare;

/// <summary>Pixiv 拉图配置。</summary>
public class PixivShareConfig
{
    [DisplayName("Pixiv Refresh Token")]
    [Description("必填。Pixiv 登录后获取的 refresh_token（与单独安装的 PixivSearcher 插件用的是同一个）")]
    public string RefreshToken { get; set; } = "";

    [DisplayName("HTTP 代理")]
    [Description("访问 Pixiv 需要代理时填写，例如 http://127.0.0.1:7890；留空直连")]
    public string Proxy { get; set; } = "";

    [DisplayName("最低收藏数")]
    [Description("关键词搜索时过滤低于该收藏数的作品，0 表示不过滤")]
    public int MinBookmarks { get; set; } = 100;

    [DisplayName("排除标签")]
    [Description("逗号分隔，命中任一标签的作品跳过，例如 AI,R-18,裸足")]
    public string ExcludeTags { get; set; } = "AI,R-18,裸足";

    [DisplayName("允许 R-18")]
    [Description("关闭时跳过所有 R-18/R-18G 作品；群聊建议保持关闭")]
    public bool AllowR18 { get; set; }

    [DisplayName("下载原图")]
    [Description("开启后优先下载原图，画质更好但更慢、更占空间")]
    public bool DownloadOriginal { get; set; }

    [DisplayName("图片缓存目录")]
    [Description("留空使用 Storage/Plugins/presents.QQSuite/pixiv；填写时相对 Storage 根目录")]
    public string ImageStorageDir { get; set; } = "";

    [DisplayName("单次最多张数")]
    [Description("AI 一次请求最多发送几张（1-10）")]
    public int MaxImagesPerRequest { get; set; } = 5;

    [DisplayName("关键词去重分钟")]
    [Description("同一关键词的结果在该时间内不重复发送；按 PID 拉图不受影响，0 表示不去重")]
    public int DedupeMinutes { get; set; } = 60;

    [DisplayName("请求超时(秒)")]
    public int Timeout { get; set; } = 90;
}

/// <summary>Pixiv 作品摘要。</summary>
public sealed class PixivItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public int Bookmarks { get; init; }
    public int XRestrict { get; init; }
    public string ImageUrl { get; init; } = "";
    public string OriginalUrl { get; init; } = "";

    public string ToCaption()
    {
        return $"Pixiv 作品 {Id}\n{Title}\n作者：{Author} · 收藏 {Bookmarks}\nhttps://www.pixiv.net/artworks/{Id}";
    }
}

/// <summary>
/// Pixiv 图片：按 PID / 关键词拉取图片并直接发送到 QQ。
/// 复用 QQ聊天 的 OneBot 连接，不额外占用协议端。
/// </summary>
public class PixivShareModule(
    XmlFunctionCaller functionCaller,
    ILogger<PixivShareModule> logger,
    Interactor<PixivShareModule> interactor,
    QChatService qChatService
) : ChatBehaviour, IConfigurable<PixivShareConfig>
{
    public PixivShareConfig Configuration { get; set; } = null!;

    const string BaseUrl = "https://app-api.pixiv.net";
    const string AppUserAgent = "PixivAndroidApp/5.0.234 (Android 11; Pixel 5)";
    const string WebReferer = "https://www.pixiv.net/";

    static readonly object authLock = new();
    static string cachedRefreshToken = "";
    static string cachedAccessToken = "";
    static DateTime cachedTokenExpiry = DateTime.MinValue;

    /// <summary>关键词去重：关键词 -> 上次发送时间。</summary>
    readonly ConcurrentDictionary<string, DateTime> recentSearches = new();

    #region 对外 AI 函数

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按关键词搜索 Pixiv 高人气图片，下载后直接发送到指定 QQ 群或私聊，返回作品 PID、标题、作者和收藏数")]
    public async Task PixivSearch(
        [Description("搜索关键词，例如 初音未来")] string keyword,
        [Description("目标群号或对方QQ")] long targetId,
        [Description("group 或 private，留空时自动判断")] string messageType = "",
        [Description("发送张数（1-10）")] int count = 1,
        [Description("是否下载原图")] bool original = false)
    {
        if (keyword.Trim().Length == 0)
        {
            interactor.Poke("搜索关键词不能为空");
            return;
        }
        await RunAsync(async client =>
        {
            using PixivSession session = await CreateSessionAsync();
            int want = Math.Clamp(count, 1, Math.Max(1, Configuration.MaxImagesPerRequest));
            List<PixivItem> items = await SearchKeywordAsync(session, keyword.Trim(), want);
            if (items.Count == 0)
            {
                await SendTextAsync(client, await IsGroupAsync(client, targetId, messageType), targetId,
                    "没有找到符合条件的新图片（关键词：" + keyword + "）");
                return "";
            }

            bool isGroup = await IsGroupAsync(client, targetId, messageType);
            List<string> lines = [];
            int sent = 0;
            int skipped = 0;
            foreach (PixivItem item in items)
            {
                if (Configuration.DedupeMinutes > 0 && WasSent(keyword.Trim(), item.Id))
                {
                    skipped++;
                    continue;
                }

                string? path = await DownloadImageAsync(session, item, original);
                if (path == null) continue;

                await SendImageAsync(client, isGroup, targetId, path, item.ToCaption());
                MarkSent(keyword.Trim(), item.Id);
                lines.Add($"{item.Id}｜{item.Title}｜收藏 {item.Bookmarks}");
                sent++;
            }

            if (sent == 0)
            {
                string tip = skipped > 0
                    ? "找到的图片最近已经发过了，稍后再试或换个关键词"
                    : "图片下载失败，可能是网络或防盗链问题，请稍后重试";
                await SendTextAsync(client, isGroup, targetId, tip);
                return "";
            }
            logger.LogInformation("Pixiv 关键词搜索「{Keyword}」发送 {Count} 张", keyword, sent);
            return "已发送 " + sent + " 张 Pixiv 图片（" + keyword + "）：\n" + string.Join("\n", lines);
        }, "Pixiv 搜索失败");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按 Pixiv 作品 PID 拉取图片，下载后直接发送到指定 QQ 群或私聊，返回标题、作者和收藏数")]
    public async Task PixivById(
        [Description("Pixiv 作品 PID，例如 12345678")] long pid,
        [Description("目标群号或对方QQ")] long targetId,
        [Description("group 或 private，留空时自动判断")] string messageType = "",
        [Description("是否下载原图")] bool original = false)
    {
        if (pid <= 0)
        {
            interactor.Poke("PID 不正确");
            return;
        }
        await RunAsync(async client =>
        {
            using PixivSession session = await CreateSessionAsync();
            PixivItem item = await GetByIdAsync(session, pid);
            CheckAccess(item);

            string? path = await DownloadImageAsync(session, item, original);
            if (path == null) throw new Exception("图片下载失败，可能是网络或防盗链问题");

            bool isGroup = await IsGroupAsync(client, targetId, messageType);
            await SendImageAsync(client, isGroup, targetId, path, item.ToCaption());
            logger.LogInformation("Pixiv PID {Pid} 已发送到 {Target}", pid, targetId);
            return "已发送 Pixiv 作品 " + pid + "：" + item.Title + "（作者 " + item.Author +
                   "，收藏 " + item.Bookmarks + "）";
        }, "Pixiv PID 拉图失败");
    }

    #endregion

    #region 执行壳

    async Task RunAsync(Func<OneBotClient, Task<string>> action, string errorPrefix)
    {
        OneBotClient? client = qChatService.Client;
        if (client is not { IsConnected: true })
        {
            interactor.Poke(QQSuiteTransport.NotConnectedMessage);
            return;
        }
        if (Configuration.RefreshToken.Trim().Length == 0)
        {
            interactor.Poke("Pixiv 未配置 Refresh Token，请在 QQ全能套件 面板的「Pixiv图片」设置里填写");
            return;
        }

        try
        {
            string result = await action(client);
            if (result.Length > 0) interactor.Poke(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Prefix}", errorPrefix);
            interactor.Poke(errorPrefix + "：" + ex.Message);
        }
    }

    #endregion

    #region Pixiv 接口

    sealed class PixivSession(HttpClient api, HttpClient image) : IDisposable
    {
        public HttpClient Api { get; } = api;
        public HttpClient Image { get; } = image;

        public void Dispose()
        {
            Api.Dispose();
            Image.Dispose();
        }
    }

    HttpClientHandler CreateHandler()
    {
        HttpClientHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        if (Configuration.Proxy.Trim().Length > 0)
            handler.Proxy = new WebProxy(Configuration.Proxy.Trim());
        return handler;
    }

    async Task<PixivSession> CreateSessionAsync()
    {
        HttpClient api = new(CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(Configuration.Timeout, 10, 300))
        };
        api.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppUserAgent);
        api.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://app-api.pixiv.net/");

        string token = await GetAccessTokenAsync(api);
        api.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);

        HttpClient image = new(CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(Configuration.Timeout, 10, 300))
        };
        image.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppUserAgent);
        image.DefaultRequestHeaders.TryAddWithoutValidation("Referer", WebReferer);
        return new PixivSession(api, image);
    }

    async Task<string> GetAccessTokenAsync(HttpClient client)
    {
        string refreshToken = Configuration.RefreshToken.Trim();
        lock (authLock)
        {
            if (cachedRefreshToken == refreshToken &&
                cachedAccessToken.Length > 0 &&
                cachedTokenExpiry > DateTime.Now.AddMinutes(2))
                return cachedAccessToken;
        }

        using FormUrlEncodedContent form = new(
        [
            new KeyValuePair<string, string>("client_id", "MOBrBDS8blbauoSck0ZfDbtuzpyT"),
            new KeyValuePair<string, string>("client_secret", "lsACyCD94FhDUtGTXi3QzcFE2uU1hqtDaKeqrdwj"),
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", refreshToken)
        ]);
        using HttpResponseMessage response = await client.PostAsync("https://oauth.secure.pixiv.net/auth/token", form);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception("Pixiv 认证失败（HTTP " + (int)response.StatusCode + "），请检查 Refresh Token 或代理");
        if (!body.TrimStart().StartsWith("{"))
            throw new Exception("Pixiv 认证返回了网页而不是数据，通常是代理不通或需要验证");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string? token = root.TryGetProperty("access_token", out JsonElement tokenElement)
            ? tokenElement.GetString()
            : root.GetProperty("response").GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(token)) throw new Exception("Pixiv 认证没有返回 access_token");

        int expiresIn = 3000;
        if (root.TryGetProperty("expires_in", out JsonElement expiresElement) &&
            expiresElement.ValueKind == JsonValueKind.Number)
            expiresIn = expiresElement.GetInt32();

        lock (authLock)
        {
            cachedRefreshToken = refreshToken;
            cachedAccessToken = token;
            cachedTokenExpiry = DateTime.Now.AddSeconds(Math.Max(120, expiresIn - 120));
        }
        return token;
    }

    async Task<List<PixivItem>> SearchKeywordAsync(PixivSession session, string keyword, int want)
    {
        List<PixivItem> result = [];
        HashSet<string> excludes = Configuration.ExcludeTags
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string word = keyword;
        if (Configuration.MinBookmarks > 0) word += " " + Configuration.MinBookmarks + "users入り";

        for (int page = 0; page < 4 && result.Count < want; page++)
        {
            string query = "word=" + Uri.EscapeDataString(word)
                           + "&search_target=partial_match_for_tags&sort=popular_desc&filter=for_ios";
            if (page > 0) query += "&offset=" + (page * 30);

            using HttpResponseMessage response = await session.Api.GetAsync(BaseUrl + "/v1/search/illust?" + query);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception("Pixiv 搜索失败（HTTP " + (int)response.StatusCode + "）");
            if (!body.TrimStart().StartsWith("{"))
                throw new Exception("Pixiv 返回了网页而不是数据，通常是代理不通或触发验证");

            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("illusts", out JsonElement illusts)) break;

            foreach (JsonElement illust in illusts.EnumerateArray())
            {
                if (result.Count >= want) break;
                PixivItem? item = ParseItem(illust);
                if (item == null) continue;
                if (excludes.Overlaps(ReadTags(illust))) continue;
                if (Configuration.MinBookmarks > 0 && item.Bookmarks < Configuration.MinBookmarks) continue;
                if (!Configuration.AllowR18 && item.XRestrict != 0) continue;
                result.Add(item);
            }
        }
        return result;
    }

    async Task<PixivItem> GetByIdAsync(PixivSession session, long pid)
    {
        using HttpResponseMessage response =
            await session.Api.GetAsync(BaseUrl + "/v1/illust/detail?illust_id=" + pid);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new Exception("Pixiv 作品 " + pid + " 不存在");
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception("Pixiv 接口失败（HTTP " + (int)response.StatusCode + "）");
        if (!body.TrimStart().StartsWith("{"))
            throw new Exception("Pixiv 返回了网页而不是数据，通常是代理不通或触发验证");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
        {
            string message = error.TryGetProperty("message", out JsonElement messageElement)
                ? messageElement.GetString() ?? "未知错误"
                : "未知错误";
            throw new Exception("Pixiv 返回错误：" + message);
        }
        if (root.TryGetProperty("illust", out JsonElement illust) && illust.ValueKind == JsonValueKind.Object)
        {
            PixivItem? item = ParseItem(illust);
            if (item == null) throw new Exception("Pixiv 作品数据不完整");
            return item;
        }
        throw new Exception("Pixiv 响应格式异常");
    }

    void CheckAccess(PixivItem item)
    {
        if (!Configuration.AllowR18 && item.XRestrict != 0)
            throw new Exception("作品 " + item.Id + " 为 R-18/R-18G，当前设置禁止发送");
    }

    static PixivItem? ParseItem(JsonElement illust)
    {
        long id = ReadLong(illust, "id");
        if (id <= 0) return null;

        string largeUrl = "";
        if (illust.TryGetProperty("image_urls", out JsonElement urls) &&
            urls.TryGetProperty("large", out JsonElement large) &&
            !string.IsNullOrWhiteSpace(large.GetString()))
            largeUrl = large.GetString()!;

        string originalUrl = largeUrl;
        if (illust.TryGetProperty("meta_single_page", out JsonElement single) &&
            single.TryGetProperty("original_image_url", out JsonElement original) &&
            !string.IsNullOrWhiteSpace(original.GetString()))
            originalUrl = original.GetString()!;

        if (largeUrl.Length == 0) return null;

        return new PixivItem
        {
            Id = id,
            Title = ReadString(illust, "title"),
            Author = illust.TryGetProperty("user", out JsonElement user) ? ReadString(user, "name") : "",
            Bookmarks = (int)ReadLong(illust, "total_bookmarks"),
            XRestrict = (int)ReadLong(illust, "x_restrict"),
            ImageUrl = largeUrl,
            OriginalUrl = originalUrl
        };
    }

    static List<string> ReadTags(JsonElement illust)
    {
        if (!illust.TryGetProperty("tags", out JsonElement tags) || tags.ValueKind != JsonValueKind.Array)
            return [];
        return tags.EnumerateArray()
            .Select(tag => tag.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "")
            .Where(name => name.Length > 0)
            .ToList();
    }

    static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? "" : "";

    static long ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return 0;
        if (value.ValueKind == JsonValueKind.Number) return (long)value.GetDouble();
        return long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : 0;
    }

    #endregion

    #region 下载与发送

    async Task<string?> DownloadImageAsync(PixivSession session, PixivItem item, bool original)
    {
        try
        {
            string url = original || Configuration.DownloadOriginal ? item.OriginalUrl : item.ImageUrl;
            if (string.IsNullOrWhiteSpace(url)) url = item.ImageUrl;

            string extension = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";

            string directory = ImageDirectory();
            Directory.CreateDirectory(directory);

            // 同一 PID 优先复用已下载的缓存（先找任意已存在的扩展名）
            string? cached = Directory.GetFiles(directory, item.Id + ".*")
                .FirstOrDefault(file => new FileInfo(file).Length > 1024);
            if (cached != null) return cached;

            string path = Path.Combine(directory, item.Id + extension);
            using HttpResponseMessage response = await session.Image.GetAsync(url);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return null;
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Pixiv 图片 {Id} 下载失败：{Message}", item.Id, ex.Message);
            return null;
        }
    }

    string ImageDirectory()
    {
        if (Configuration.ImageStorageDir.Trim().Length > 0)
            return Path.Combine(AlifePath.StorageFolderPath, Configuration.ImageStorageDir.Trim());
        return Path.Combine(AlifePath.StorageFolderPath, "Plugins", "presents.QQSuite", "pixiv");
    }

    async Task SendImageAsync(OneBotClient client, bool isGroup, long targetId, string path, string caption)
    {
        string fileReference = "file:///" + path.Replace('\\', '/');
        try
        {
            await SendRawAsync(client, isGroup, targetId, caption, fileReference);
            return;
        }
        catch (Exception ex)
        {
            logger.LogInformation("Pixiv 本地路径图片未被协议端接受，改用 base64：{Message}", ex.Message);
        }
        string base64 = "base64://" + Convert.ToBase64String(await File.ReadAllBytesAsync(path));
        await SendRawAsync(client, isGroup, targetId, caption, base64);
    }

    async Task SendRawAsync(OneBotClient client, bool isGroup, long targetId,
        string caption, string fileReference)
    {
        object message = new object[]
        {
            new { type = "text", data = new { text = caption } },
            new { type = "image", data = new { file = fileReference } }
        };
        object parameters = isGroup
            ? new { group_id = targetId, message }
            : new { user_id = targetId, message };

        JsonElement raw = await client.CallActionRawAsync(
            isGroup ? "send_group_msg" : "send_private_msg", parameters, 120);
        string? status = Pick(raw, "status");
        int retCode = (int)PickLong(raw, "retcode");
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) && retCode > 1)
            throw new Exception(Pick(raw, "message", "wording") ?? ("retcode=" + retCode));
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
            logger.LogDebug(ex, "Pixiv 提示文本发送失败");
        }
    }

    async Task<bool> IsGroupAsync(OneBotClient client, long targetId, string messageType)
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

    bool WasSent(string keyword, long id)
    {
        string key = keyword + "|" + id;
        lock (recentSearches)
        {
            if (recentSearches.TryGetValue(key, out DateTime last) &&
                (DateTime.Now - last).TotalMinutes < Configuration.DedupeMinutes)
                return true;
        }
        return false;
    }

    void MarkSent(string keyword, long id)
    {
        string key = keyword + "|" + id;
        recentSearches[key] = DateTime.Now;

        // 顺便清理超过 24 小时的记录，避免常驻内存膨胀
        DateTime cutoff = DateTime.Now.AddHours(-24);
        foreach (KeyValuePair<string, DateTime> pair in recentSearches)
        {
            if (pair.Value < cutoff) recentSearches.TryRemove(pair.Key, out _);
        }
    }

    #endregion

    #region 生命周期

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "从 Pixiv 按关键词搜索或按 PID 拉取图片，下载后直接发送到 QQ 群聊/私聊。",
            Explanation = "PixivSearch 按关键词搜索高人气图片；PixivById 按 PID 精确拉图。两者都会下载图片并发送到 targetId 指定的群或私聊，支持 R-18 过滤与关键词去重。需要先在面板填写 Pixiv Refresh Token。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        logger.LogInformation("QQ Pixiv 图片已启动（按 PID / 关键词拉图并发送到 QQ）");
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        recentSearches.Clear();
        return Task.CompletedTask;
    }

    #endregion

    #region 工具

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

    #endregion
}
