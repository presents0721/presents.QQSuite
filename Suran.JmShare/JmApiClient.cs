using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace QQSuite.JmShare;

/// <summary>JM 搜索结果中的单条记录。</summary>
public sealed class JmSearchItem
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Category { get; init; } = "";
    public string SubCategory { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>JM 搜索结果。</summary>
public sealed class JmSearchResult
{
    public long Total { get; init; }
    /// <summary>搜索直接把关键词重定向到某个本子时给出，0 表示没有。</summary>
    public long RedirectAid { get; init; }
    public List<JmSearchItem> Items { get; init; } = new();
}

/// <summary>JM 章节（series 里的一项）。</summary>
public sealed class JmChapter
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int Sort { get; init; }
}

/// <summary>JM 本子详情。</summary>
public sealed class JmAlbum
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public List<string> Authors { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public string Description { get; init; } = "";
    public long TotalPhotos { get; init; }
    public long SeriesId { get; init; }
    public List<JmChapter> Chapters { get; init; } = new();

    /// <summary>发布日期（接口原始字符串，可能为空）。</summary>
    public string PubDate { get; init; } = "";
    /// <summary>更新日期（接口原始字符串，可能为空）。</summary>
    public string UpdateDate { get; init; } = "";
    /// <summary>点赞数（接口可能返回 "1.2万" 之类的字符串，原样展示）。</summary>
    public string Likes { get; init; } = "";
    /// <summary>观看数（接口可能返回字符串，原样展示）。</summary>
    public string Views { get; init; } = "";
    /// <summary>评论数。</summary>
    public long CommentCount { get; init; }
    /// <summary>图片乱序基准值，接口在详情里直接给出时可直接复用，省去逐章请求。</summary>
    public long ScrambleId { get; init; }
    /// <summary>相关/系列作品名。</summary>
    public List<string> Works { get; init; } = new();
}

/// <summary>JM 账号会话（jsessionid + remember_id），持久化到 state/session.json 供下次直接复用。</summary>
public sealed class JmSessionState
{
    public string Username { get; set; } = "";
    /// <summary>登录接口返回的昵称/用户名，用于展示。</summary>
    public string Nickname { get; set; } = "";
    public string JsessionId { get; set; } = "";
    public string RememberId { get; set; } = "";
    public string SavedAt { get; set; } = "";

    public bool IsUsable => !string.IsNullOrWhiteSpace(JsessionId);
}

/// <summary>请求失败但值得换域名重试的异常（HTTP 5xx / 403 / 返回网页）。</summary>
sealed class JmTransientException(string message) : Exception(message);

/// <summary>
/// JM API 客户端（API 模式）。
/// 负责：域名池轮换、token / AES 解密、Cookie、搜索 / 详情 / 章节 / scramble_id、图片下载。
/// </summary>
public sealed class JmApiClient : IDisposable
{
    const string UserAgent =
        "Mozilla/5.0 (Linux; Android 9; MI 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.120 Mobile Safari/537.36";

    /// <summary>普通接口签名密钥。</summary>
    const string SecretNormal = "185Hcomic3PAPP7R";
    /// <summary>scramble 相关接口（chapter_view_template）必须换用这个密钥，否则 403。</summary>
    const string SecretScramble = "18comicAPPContent";
    const string AppVersion = "2.1.12";

    /// <summary>
    /// 内置 API 域名池，顺序即优先级：越靠前越先尝试。
    /// 2026-10-26 实测（大陆直连，每域名 6 轮 /setting）：cdngwc.club 5/6、cdngwc.cc 4/6 可用；
    /// cdngwc.net 与 cdnhjk.net 0/6（链接超时），故后置兜底。
    /// 这些域名随时可能变动，最稳的做法是在配置「自定义域名」里填当前实测可用的域名。
    /// </summary>
    static readonly string[] DefaultDomains =
    [
        "www.cdngwc.club",
        "www.cdngwc.cc",
        "www.cdngwc.net",
        "www.cdnhjk.net"
    ];

    /// <summary>
    /// 图片 CDN 域名池，同样按优先级排列。
    /// 2026-10-26 实测（每域名 3 轮真实图片）：jmapiproxy2.cc 2/3；msp2/msp3.jmapiproxy2.cc 1/3；
    /// jmapiproxy1.cc 与两个 jmapinodeudzn.net 均 0/3。
    /// </summary>
    static readonly string[] CdnHosts =
    [
        "cdn-msp.jmapiproxy2.cc",
        "cdn-msp2.jmapiproxy2.cc",
        "cdn-msp3.jmapiproxy2.cc",
        "cdn-msp.jmapiproxy1.cc",
        "cdn-msp.jmapinodeudzn.net",
        "cdn-msp3.jmapinodeudzn.net"
    ];

    readonly JmShareConfig config;
    readonly ILogger logger;
    readonly CookieContainer cookies = new();
    readonly HttpClient apiHttp;
    readonly HttpClient imageHttp;
    readonly List<string> domains = new();
    readonly List<string> rawCookies = new();
    readonly ConcurrentDictionary<string, bool> cookieReady = new(StringComparer.OrdinalIgnoreCase);
    readonly object cookieLock = new();

    /// <summary>AVS 登录态的取值（已去掉 avs= 前缀），空串表示未配置。</summary>
    readonly string avsValue;

    /// <summary>已保存的账号会话；登录成功后写入并落盘。</summary>
    JmSessionState session;

    int domainIndex;
    /// <summary>-1 起步，使 NextCdn 首次返回 CdnHosts[0]（即优先级最高的 CDN）。</summary>
    int cdnCursor = -1;

    public JmApiClient(JmShareConfig config, ILogger logger)
    {
        this.config = config;
        this.logger = logger;

        IEnumerable<string> source = string.IsNullOrWhiteSpace(config.Domain) ? DefaultDomains : CustomDomains(config.Domain);
        foreach (string domain in source.Select(NormalizeDomain).Where(d => d.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            domains.Add(domain);
        }
        if (domains.Count == 0)
        {
            domains.AddRange(DefaultDomains);
        }
        // 这里刻意不打乱顺序：列表已按「实测可用优先」排列，首次请求就应落在最可能可用的域名上。
        // 轮换由 RotateDomain 在失败重试时完成，仍能分摊到其它域名。

        // AVS 登录态：注入到 CookieContainer（API 域名）与 rawCookies（图片请求手动带 Cookie）
        avsValue = NormalizeAvs(config.AvsCookie);
        if (avsValue.Length > 0)
        {
            rawCookies.Add("avs=" + avsValue);
            foreach (string domain in domains)
            {
                try
                {
                    cookies.Add(new Uri("https://" + domain), new Cookie("avs", avsValue));
                }
                catch
                {
                    // 域名不合法时忽略，rawCookies 仍然会带上
                }
            }
        }

        TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(config.Timeout, 10, 300));
        apiHttp = new HttpClient(CreateHandler()) { Timeout = timeout };
        apiHttp.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", UserAgent);
        imageHttp = new HttpClient(CreateHandler()) { Timeout = timeout };

        // 上次登录留下的会话：账号一致（或未配置账号）时直接复用，避免每次重启都重新登录
        session = JmStateStore.Load("session", () => new JmSessionState());
        string configuredUser = (config.Username ?? "").Trim();
        if (session.IsUsable && (configuredUser.Length == 0 || session.Username == configuredUser))
        {
            ApplySessionCookies();
        }
    }

    /// <summary>当前会话对应的账号名；未登录时为空串。</summary>
    public string SessionUsername => session.IsUsable ? session.Username : "";

    public bool HasSession => session.IsUsable;

    /// <summary>把会话 Cookie 注入到请求头（rawCookies）与 CookieContainer。</summary>
    void ApplySessionCookies()
    {
        if (session.JsessionId.Length > 0) AddCookie("jsessionid", session.JsessionId);
        if (session.RememberId.Length > 0) AddCookie("remember_id", session.RememberId);
    }

    void AddCookie(string name, string value)
    {
        lock (cookieLock)
        {
            string pair = name + "=" + value;
            // 同名的旧值先移除，避免出现两个 jsessionid
            rawCookies.RemoveAll(item => item.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
            rawCookies.Add(pair);
        }
        foreach (string domain in domains)
        {
            try
            {
                cookies.Add(new Uri("https://" + domain), new Cookie(name, value));
            }
            catch
            {
                // 域名不合法时忽略，rawCookies 仍然会带上
            }
        }
    }

    /// <summary>当前使用的 API 域名（不含 https://）。</summary>
    public string CurrentDomain => domains[Math.Clamp(domainIndex, 0, domains.Count - 1)];

    HttpClientHandler CreateHandler()
    {
        HttpClientHandler handler = new()
        {
            // 自动解压 gzip / deflate，等价于带上 Accept-Encoding
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            CookieContainer = cookies,
            UseCookies = true
        };
        if (!string.IsNullOrWhiteSpace(config.Proxy))
        {
            handler.Proxy = new WebProxy(config.Proxy.Trim());
            handler.UseProxy = true;
        }
        return handler;
    }

    static IEnumerable<string> CustomDomains(string value) =>
        value.Split([',', ';', ' ', '，', '；'], StringSplitOptions.RemoveEmptyEntries);

    static string NormalizeDomain(string value)
    {
        string domain = value.Trim();
        if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) domain = domain[7..];
        else if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) domain = domain[8..];
        return domain.TrimEnd('/');
    }

    /// <summary>把 "avs=xxx" / "\"xxx\"" 之类写法统一成纯取值。</summary>
    static string NormalizeAvs(string value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith("avs=", StringComparison.OrdinalIgnoreCase)) text = text[4..].Trim();
        return text.Trim().Trim('"', '\'');
    }

    void RotateDomain()
    {
        if (domains.Count <= 1) return;
        domainIndex = (domainIndex + 1) % domains.Count;
    }

    #region 搜索 / 详情 / 章节

    /// <summary>关键词搜索。type 支持 all/work/author/tag/actor。</summary>
    public async Task<JmSearchResult> SearchAsync(string keyword, int page, string type, CancellationToken ct)
    {
        string mainTag = type.Trim().ToLowerInvariant() switch
        {
            "work" or "works" => "1",
            "author" => "2",
            "tag" => "3",
            "actor" => "4",
            _ => "0"
        };
        string path = "/search?main_tag=" + mainTag
                      + "&search_query=" + Uri.EscapeDataString(keyword)
                      + "&page=" + Math.Max(1, page)
                      + "&o=mr&t=a";

        JsonElement data = await GetJsonAsync(path, ct);

        List<JmSearchItem> items = new();
        if (data.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in content.EnumerateArray())
            {
                long id = ReadLong(element, "id");
                if (id <= 0) continue;
                items.Add(new JmSearchItem
                {
                    Id = id,
                    Name = ReadString(element, "name"),
                    Author = ReadString(element, "author"),
                    Category = ReadNested(element, "category", "title"),
                    SubCategory = ReadNested(element, "category_sub", "title"),
                    Description = ReadString(element, "description")
                });
            }
        }

        return new JmSearchResult
        {
            Total = ReadLong(data, "total"),
            RedirectAid = ReadLong(data, "redirect_aid"),
            Items = items
        };
    }

    /// <summary>本子详情。若传入的其实是章节 id，会自动改用 series_id 重新请求。</summary>
    public async Task<JmAlbum> GetAlbumAsync(long albumId, CancellationToken ct)
    {
        JsonElement data = await GetJsonAsync("/album?id=" + albumId, ct);

        long id = ReadLong(data, "id");
        long seriesId = ReadLong(data, "series_id");
        if (seriesId > 0 && seriesId != id)
        {
            // 请求的是章节 id：回退到所属的本子
            data = await GetJsonAsync("/album?id=" + seriesId, ct);
            id = ReadLong(data, "id");
            seriesId = ReadLong(data, "series_id");
        }
        if (id <= 0) id = albumId;

        List<JmChapter> chapters = new();
        if (data.TryGetProperty("series", out JsonElement series) && series.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in series.EnumerateArray())
            {
                long chapterId = ReadLong(element, "id");
                if (chapterId <= 0) continue;
                chapters.Add(new JmChapter
                {
                    Id = chapterId,
                    Name = ReadString(element, "name"),
                    Sort = (int)ReadLong(element, "sort")
                });
            }
        }

        // 按章节 ID 去重（sort 缺失时也不会误删），再按 sort 升序
        chapters = chapters
            .GroupBy(chapter => chapter.Id)
            .Select(group => group.First())
            .OrderBy(chapter => chapter.Sort)
            .ToList();

        string name = ReadString(data, "name");
        if (chapters.Count == 0)
        {
            // 没有 series 时整本算一章
            chapters.Add(new JmChapter { Id = id, Name = name, Sort = 1 });
        }

        // 相关/系列作品：优先 works，退回 related
        List<string> works = ReadStringArray(data, "works");
        if (works.Count == 0) works = ReadStringArray(data, "related");

        return new JmAlbum
        {
            Id = id,
            Name = name,
            Authors = ReadStringArray(data, "author"),
            Tags = ReadStringArray(data, "tags"),
            Description = ReadString(data, "description"),
            TotalPhotos = ReadLong(data, "total_photos"),
            SeriesId = seriesId,
            Chapters = chapters,
            PubDate = ReadString(data, "pub_date"),
            UpdateDate = ReadString(data, "update_date"),
            Likes = ReadString(data, "likes"),
            Views = ReadString(data, "views"),
            CommentCount = ReadLong(data, "comment_count"),
            ScrambleId = ReadLong(data, "scramble_id"),
            Works = works
        };
    }

    /// <summary>章节图片文件名列表（顺序即页序）。</summary>
    public async Task<List<string>> GetChapterImagesAsync(long chapterId, CancellationToken ct)
    {
        JsonElement data = await GetJsonAsync("/chapter?id=" + chapterId, ct);
        List<string> images = new();
        if (data.TryGetProperty("images", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in array.EnumerateArray())
            {
                string? name = element.GetString();
                if (!string.IsNullOrWhiteSpace(name)) images.Add(name);
            }
        }
        return images;
    }

    /// <summary>取章节的 scramble_id；失败时兜底 220980。</summary>
    public async Task<long> GetScrambleIdAsync(long chapterId, CancellationToken ct)
    {
        string path = "/chapter_view_template?id=" + chapterId
                      + "&mode=vertical&page=0&app_img_shunt=1&express=off&v={ts}";
        try
        {
            // 这个接口返回 HTML，不是加密 JSON；并且必须用 scramble 专用密钥签名
            string html = await GetRawTextAsync(path, SecretScramble, ct);
            Match match = Regex.Match(html, @"var\s+scramble_id\s*=\s*(\d+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out long value) && value > 0)
            {
                return value;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("JM 获取 scramble_id 失败：{Message}", ex.Message);
        }
        return 220980;
    }

    #endregion

    #region 账号与会话

    /// <summary>
    /// 用配置里的账号密码登录并把会话落盘。成功返回 true；未配置账号、被风控或需要验证码时返回 false 并给出原因。
    /// 登录走 POST /login（表单 username/password），成功后从 Set-Cookie 取出 jsessionid 与 remember_id。
    /// </summary>
    public async Task<(bool Ok, string Message)> LoginAsync(CancellationToken ct)
    {
        string username = (config.Username ?? "").Trim();
        string password = config.Password ?? "";
        if (username.Length == 0 || password.Length == 0)
        {
            return (false, "未配置 JM 账号或密码，请先在 JM漫画 设置里填写「JM 账号」「JM 密码」。");
        }

        try
        {
            Dictionary<string, string> form = new()
            {
                ["username"] = username,
                ["password"] = password
            };
            (string body, Dictionary<string, string> setCookies) =
                await PostFormAsync("/login", SecretNormal, form, ct);

            string jsessionId = PickCookie(setCookies, "jsessionid");
            string rememberId = PickCookie(setCookies, "remember_id");
            string nickname = "";
            long code = 0;
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                code = ReadLong(root, "code");
                if (root.TryGetProperty("data", out JsonElement payload) && payload.ValueKind == JsonValueKind.Object)
                {
                    nickname = ReadString(payload, "username");
                    if (nickname.Length == 0) nickname = ReadString(payload, "nickname");
                }
            }
            catch
            {
                // 返回不是 JSON 时用 Cookie 是否拿到来判断
            }

            if (jsessionId.Length == 0)
            {
                string hint = code > 0 && code != 200
                    ? "JM 登录被拒绝（返回码 " + code + "），请检查账号密码，或是否触发了验证码。"
                    : "JM 登录未拿到会话 Cookie，可能是域名被劫持或账号密码错误。";
                return (false, hint);
            }

            session = new JmSessionState
            {
                Username = username,
                Nickname = nickname,
                JsessionId = jsessionId,
                RememberId = rememberId,
                SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };
            JmStateStore.Save("session", session);
            ApplySessionCookies();

            logger.LogInformation("JM 账号 {User} 登录成功", username);
            return (true, "登录成功" + (nickname.Length > 0 ? "，昵称 " + nickname : "")
                         + "；会话已保存，重启后自动复用。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "JM 登录失败");
            return (false, "登录请求失败：" + ex.Message);
        }
    }

    /// <summary>清除本地会话（尽力通知服务端登出，失败也不影响本地清理）。</summary>
    public async Task<string> LogoutAsync(CancellationToken ct)
    {
        string user = session.Username;
        try
        {
            if (session.IsUsable)
            {
                await PostFormAsync("/logout", SecretNormal, new Dictionary<string, string>(), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("JM 登出请求失败（忽略）：{Message}", ex.Message);
        }

        session = new JmSessionState();
        JmStateStore.Save("session", session);
        lock (cookieLock)
        {
            rawCookies.RemoveAll(item =>
                item.StartsWith("jsessionid=", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("remember_id=", StringComparison.OrdinalIgnoreCase));
        }
        return user.Length > 0 ? "已退出 JM 账号 " + user + "。" : "当前没有已登录的 JM 账号。";
    }

    /// <summary>
    /// 尽力把本子加入 JM 账号的收藏夹（folder 默认 "0" 即「我的收藏」）。
    /// 未登录或接口不可用时返回 false，调用方只做本地收藏，不影响主流程。
    /// </summary>
    public async Task<bool> TryAddRemoteFavoriteAsync(long albumId, string folderId, CancellationToken ct)
    {
        if (!session.IsUsable) return false;
        try
        {
            Dictionary<string, string> form = new()
            {
                ["aid"] = albumId.ToString(CultureInfo.InvariantCulture),
                ["folder_id"] = string.IsNullOrWhiteSpace(folderId) ? "0" : folderId.Trim()
            };
            (string body, _) = await PostFormAsync("/favorite", SecretNormal, form, ct);
            using JsonDocument document = JsonDocument.Parse(body);
            bool ok = ReadLong(document.RootElement, "code") == 200;
            if (!ok) logger.LogInformation("JM 远端收藏未成功（本子 {Album}）：{Body}", albumId, Shorten(body));
            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("JM 远端收藏失败（本子 {Album}）：{Message}", albumId, ex.Message);
            return false;
        }
    }

    static string Shorten(string text)
        => text.Length <= 200 ? text : text[..200];

    /// <summary>从 Set-Cookie 字典里按名取值（忽略大小写）。</summary>
    static string PickCookie(Dictionary<string, string> cookies, string name)
        => cookies.TryGetValue(name, out string? value) ? value : "";

    /// <summary>带签名头发一个表单 POST，返回响应体与解析后的 Set-Cookie。</summary>
    async Task<(string Body, Dictionary<string, string> Cookies)> PostFormAsync(
        string path, string secret, Dictionary<string, string> form, CancellationToken ct)
    {
        int attempts = Math.Max(1, config.Retry);
        Exception? last = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            // 同 SendRawAsync：模块被卸载/停止时 token 已取消，继续轮换没有意义，
            // 而且要给出能辨认的原因，不能伪装成普通失败。
            if (ct.IsCancellationRequested) throw new Exception("JM 请求被取消（插件重载或模块停止）");

            string domain = CurrentDomain;
            try
            {
                await EnsureCookieAsync(domain, ct);

                string ts = Timestamp();
                string token = Md5Hex(ts + secret);

                using HttpRequestMessage request = new(HttpMethod.Post, "https://" + domain + path);
                request.Headers.TryAddWithoutValidation("user-agent", UserAgent);
                request.Headers.TryAddWithoutValidation("token", token);
                request.Headers.TryAddWithoutValidation("tokenparam", ts + "," + AppVersion);
                request.Content = new FormUrlEncodedContent(form);

                using HttpResponseMessage response = await apiHttp.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, ct);
                string body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        throw new JmTransientException("HTTP " + (int)response.StatusCode);
                    }
                    throw new Exception("JM 接口失败（HTTP " + (int)response.StatusCode + "）");
                }

                return (body, ReadSetCookies(response));
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                throw new Exception("JM 请求被取消（插件重载或模块停止）", ex);
            }
            catch (Exception ex) when (ex is JmTransientException or HttpRequestException or IOException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                last = ex;
                logger.LogWarning("JM 表单接口 {Path} 第 {Attempt} 次失败：{Message}", path, attempt + 1, ex.Message);
                RotateDomain();
                // 同 SendRawAsync：退避不传 ct，取消由循环顶部的检查统一处理
                await Task.Delay(Random.Shared.Next(300, 800));
            }
        }

        throw new Exception("JM 接口请求失败：" + (last?.Message ?? "未知错误"));
    }

    static Dictionary<string, string> ReadSetCookies(HttpResponseMessage response)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)) return result;

        foreach (string value in values)
        {
            string pair = value.Split(';')[0].Trim();
            int equal = pair.IndexOf('=');
            if (equal <= 0) continue;
            string name = pair[..equal].Trim();
            string cookieValue = pair[(equal + 1)..].Trim();
            if (name.Length > 0 && cookieValue.Length > 0) result[name] = cookieValue;
        }
        return result;
    }

    #endregion

    #region 图片

    /// <summary>下载本子封面（https://{domain}/media/albums/{id}.jpg）。</summary>
    public async Task<byte[]> DownloadCoverAsync(long albumId, CancellationToken ct)
    {
        Exception? last = null;
        int attempts = Math.Max(1, config.Retry);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            string url = "https://" + CurrentDomain + "/media/albums/" + albumId + ".jpg";
            try
            {
                return await GetImageBytesAsync(url, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                RotateDomain();
                await Task.Delay(Random.Shared.Next(300, 800), ct);
            }
        }
        throw new Exception("封面下载失败：" + (last?.Message ?? "未知错误"));
    }

    /// <summary>下载章节里的某一页，失败时换 CDN。</summary>
    public async Task<byte[]> DownloadPageAsync(long chapterId, string fileName, CancellationToken ct)
    {
        Exception? last = null;
        int attempts = Math.Max(1, config.Retry);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            string cdn = NextCdn();
            string url = "https://" + cdn + "/media/photos/" + chapterId + "/" + Uri.EscapeDataString(fileName);
            try
            {
                return await GetImageBytesAsync(url, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                await Task.Delay(Random.Shared.Next(300, 800), ct);
            }
        }
        throw new Exception("图片 " + fileName + " 下载失败：" + (last?.Message ?? "未知错误"));
    }

    string NextCdn()
    {
        int index = Interlocked.Increment(ref cdnCursor);
        return CdnHosts[Math.Abs(index) % CdnHosts.Length];
    }

    async Task<byte[]> GetImageBytesAsync(string url, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("user-agent", UserAgent);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "com.JMComic3.app");
        request.Headers.TryAddWithoutValidation("Referer", "https://" + CurrentDomain);
        request.Headers.TryAddWithoutValidation("Accept", "image/*");
        string cookie = CookieHeader();
        if (cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);

        using HttpResponseMessage response = await imageHttp.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception("HTTP " + (int)response.StatusCode);
        }
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new Exception("响应内容为空");
        return bytes;
    }

    string CookieHeader()
    {
        lock (cookieLock)
        {
            return string.Join("; ", rawCookies);
        }
    }

    #endregion

    #region 请求与解密

    async Task<string> GetRawTextAsync(string path, string secret, CancellationToken ct)
    {
        (string body, _) = await SendRawAsync(path, secret, expectJson: false, ct);
        return body;
    }

    async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        (string body, string ts) = await SendRawAsync(path, SecretNormal, expectJson: true, ct);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        long code = ReadLong(root, "code");
        if (code != 200) throw new Exception("JM 接口返回错误码 " + code);
        if (!root.TryGetProperty("data", out JsonElement data)) throw new Exception("JM 接口响应缺少 data");

        // data 已是对象（未加密）时直接使用
        if (data.ValueKind == JsonValueKind.Object) return data.Clone();
        if (data.ValueKind != JsonValueKind.String) throw new Exception("JM 接口 data 格式异常");

        string json = AesDecrypt(data.GetString() ?? "", SecretNormal, ts);
        using JsonDocument payload = JsonDocument.Parse(json);
        return payload.RootElement.Clone();
    }

    async Task<(string Body, string Ts)> SendRawAsync(string path, string secret, bool expectJson, CancellationToken ct)
    {
        int attempts = Math.Max(1, config.Retry);
        Exception? last = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            // 模块被卸载/停止（插件重载、客户端退出）时 token 会被取消。这不是域名或网络问题，
            // 继续轮换没有意义；而且必须给出能辨认的原因，不能伪装成「下载失败」把人带偏。
            if (ct.IsCancellationRequested) throw new Exception("JM 请求被取消（插件重载或模块停止）");

            string domain = CurrentDomain;
            try
            {
                await EnsureCookieAsync(domain, ct);

                string ts = Timestamp();
                string token = Md5Hex(ts + secret);
                string url = "https://" + domain + path.Replace("{ts}", ts);

                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("user-agent", UserAgent);
                request.Headers.TryAddWithoutValidation("token", token);
                request.Headers.TryAddWithoutValidation("tokenparam", ts + "," + AppVersion);

                using HttpResponseMessage response = await apiHttp.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, ct);
                string body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    // 5xx 与 403 Restricted Access! 视为可重试
                    if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        throw new JmTransientException("HTTP " + (int)response.StatusCode);
                    }
                    throw new Exception("JM 接口失败（HTTP " + (int)response.StatusCode + "）");
                }

                string trimmed = body.TrimStart();
                bool looksJson = trimmed.StartsWith("{", StringComparison.Ordinal) ||
                                 trimmed.StartsWith("[", StringComparison.Ordinal);
                if (expectJson && !looksJson)
                {
                    throw new JmTransientException("返回了网页而不是 JSON，可能被域名劫持");
                }
                if (trimmed.Length == 0) throw new JmTransientException("响应为空");

                return (body, ts);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                throw new Exception("JM 请求被取消（插件重载或模块停止）", ex);
            }
            catch (JmTransientException ex)
            {
                last = ex;
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                last = new Exception("请求超时", ex);
            }
            catch (IOException ex)
            {
                last = ex;
            }

            logger.LogWarning("JM 接口 {Path} 第 {Attempt} 次请求失败：{Message}", path, attempt + 1, last?.Message);
            RotateDomain();
            // 退避刻意不传 ct：取消交给下一轮循环顶部的检查统一处理，否则 Task.Delay
            // 会直接抛 TaskCanceledException 穿出重试循环，又变成一条误人的失败日志。
            await Task.Delay(Random.Shared.Next(300, 800));
        }

        throw new Exception("JM 接口请求失败：" + (last?.Message ?? "未知错误"));
    }

    /// <summary>首次访问某个域名时先请求 /setting 建立 Cookie。</summary>
    async Task EnsureCookieAsync(string domain, CancellationToken ct)
    {
        if (!cookieReady.TryAdd(domain, true)) return;

        try
        {
            string ts = Timestamp();
            string token = Md5Hex(ts + SecretNormal);
            using HttpRequestMessage request = new(HttpMethod.Get, "https://" + domain + "/setting");
            request.Headers.TryAddWithoutValidation("user-agent", UserAgent);
            request.Headers.TryAddWithoutValidation("token", token);
            request.Headers.TryAddWithoutValidation("tokenparam", ts + "," + AppVersion);

            using HttpResponseMessage response = await apiHttp.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> values))
            {
                lock (cookieLock)
                {
                    foreach (string value in values)
                    {
                        string pair = value.Split(';')[0].Trim();
                        if (pair.Length > 0 && !rawCookies.Contains(pair)) rawCookies.Add(pair);
                    }
                }
            }
            // 读完响应，确保 CookieContainer 收到 Set-Cookie
            await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 失败必须撤掉「已就绪」标记：否则这个域名以后会被直接跳过建 Cookie，
            // 轮到它重试时带着空 Cookie 发请求，等于白轮换一轮。
            cookieReady.TryRemove(domain, out _);
            logger.LogWarning("JM 建立 Cookie 失败（{Domain}）：{Message}", domain, ex.Message);
        }
    }

    static string Timestamp() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    static string Md5Hex(string text)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>AES-256-ECB：key 为 md5(ts + secret) 的 32 位小写十六进制字符串的 ASCII 字节。</summary>
    static string AesDecrypt(string base64, string secret, string ts)
    {
        if (string.IsNullOrWhiteSpace(base64)) return "{}";
        int remainder = base64.Length % 4;
        if (remainder != 0) base64 = base64.PadRight(base64.Length + (4 - remainder), '=');

        byte[] key = Encoding.ASCII.GetBytes(Md5Hex(ts + secret));
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        byte[] cipher = Convert.FromBase64String(base64);
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        byte[] plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(RemovePkcs7(plain));
    }

    /// <summary>PaddingMode.None 解完后手动去掉 PKCS7 填充。</summary>
    static byte[] RemovePkcs7(byte[] data)
    {
        if (data.Length == 0) return data;
        int pad = data[^1];
        if (pad is <= 0 or > 16 || pad > data.Length) return data;
        for (int i = data.Length - pad; i < data.Length; i++)
        {
            if (data[i] != pad) return data;
        }
        return data[..^pad];
    }

    #endregion

    #region JSON 读取工具

    static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    static string ReadNested(JsonElement element, string name, string child)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            return "";
        return ReadString(value, child);
    }

    static long ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out long number) ? number : (long)value.GetDouble();
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : 0;
        }
        return 0;
    }

    static List<string> ReadStringArray(JsonElement element, string name)
    {
        List<string> result = new();
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return result;
        foreach (JsonElement item in value.EnumerateArray())
        {
            string? text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
        }
        return result;
    }

    #endregion

    public void Dispose()
    {
        apiHttp.Dispose();
        imageHttp.Dispose();
    }
}
