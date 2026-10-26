using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using QQSuite.Core;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace QQSuite.ShareArk;

public class ShareArkConfig
{
    [DisplayName("第三方接口基地址")]
    [Description("填一个第三方签名服务基地址就行，音乐卡/分享卡/歌曲搜索接口地址会自动拼接，如 https://apii.xianyuw.cn/api/v1")]
    public string BaseUrl { get; set; } = "";

    [DisplayName("接口Token")]
    [Description("第三方接口凭证key，可留空；咸鱼API到 https://apii.xianyuw.cn 注册后在个人中心获取")]
    public string ApiKey { get; set; } = "";

    [DisplayName("协议端地址")]
    [Description("OneBot正向WebSocket地址")]
    public string WsUrl { get; set; } = "ws://127.0.0.1:3001";

    [DisplayName("音乐卡签名接口地址")]
    [Description("完整地址，可留空；留空时用基地址自动探测后缀拼出")]
    public string MusicArkUrl { get; set; } = "";

    [DisplayName("分享卡签名接口地址")]
    [Description("完整地址，可留空；留空时用基地址自动探测后缀拼出")]
    public string ShareArkUrl { get; set; } = "";

    [DisplayName("歌曲搜索接口地址")]
    [Description("完整地址，可留空；留空时用基地址自动探测后缀拼出。可含{pf}占位符，会被替换为163/qq/kugou")]
    public string SearchUrl { get; set; } = "";

    [DisplayName("B站搜索接口地址")]
    [Description("B站视频搜索接口完整地址，可留空，留空走B站官方接口。第三方返回需为 {code,data:[{id,title}]} 结构")]
    public string BiliSearchUrl { get; set; } = "";

    [DisplayName("容灾·启用官方接口回退")]
    [Description("第三方接口调用失败时自动改用官方公开接口（音乐卡走官方音乐段、B站系列走B站web接口、搜索走平台公开接口）")]
    public bool EnableOfficialFallback { get; set; } = true;

    // ── 功能开关：关闭后对应函数对AI不可用（默认全开，老配置缺省即开） ──
    [DisplayName("开关·搜歌")] [Description("关闭后 SearchMusic 不可用")]
    public bool EnableSearchMusic { get; set; } = true;
    [DisplayName("开关·B站搜索")] [Description("关闭后 SearchBili 不可用")]
    public bool EnableSearchBili { get; set; } = true;
    [DisplayName("开关·发音乐/视频卡")] [Description("关闭后 SendArkCard 不可用")]
    public bool EnableSendArkCard { get; set; } = true;
    [DisplayName("开关·发通用分享卡")] [Description("关闭后 SendArkShareCard 不可用")]
    public bool EnableSendShareCard { get; set; } = true;
    [DisplayName("开关·B站链接解析")] [Description("关闭后 BiliParse 不可用")]
    public bool EnableBiliParse { get; set; } = true;
    [DisplayName("开关·B站用户查询")] [Description("关闭后 BiliUserInfo 不可用")]
    public bool EnableBiliUserInfo { get; set; } = true;
    [DisplayName("开关·UP主更新查询")] [Description("关闭后 BiliUpdates 不可用")]
    public bool EnableBiliUpdates { get; set; } = true;
    [DisplayName("开关·B站热门/相关")] [Description("关闭后 BiliHot 不可用")]
    public bool EnableBiliHot { get; set; } = true;
    [DisplayName("开关·B站扫码登录")] [Description("关闭后 BiliQrLogin 不可用")]
    public bool EnableBiliQrLogin { get; set; } = true;
    [DisplayName("开关·Cookie刷新")] [Description("关闭后 BiliCookieRefresh 不可用")]
    public bool EnableBiliCookieRefresh { get; set; } = true;
    [DisplayName("开关·Cookie管理")] [Description("关闭后 BiliCookie 不可用")]
    public bool EnableBiliCookie { get; set; } = true;
    [DisplayName("开关·IP查询")] [Description("关闭后 IpLookup 不可用")]
    public bool EnableIpLookup { get; set; } = true;
    [DisplayName("开关·探测接口")] [Description("关闭后 ProbeBase 不可用")]
    public bool EnableProbeBase { get; set; } = true;
    [DisplayName("开关·列出全部接口")] [Description("关闭后 ProbeAll 不可用")]
    public bool EnableProbeAll { get; set; } = true;
}

public class ShareArkModule(
    XmlFunctionCaller functionCaller,
    ILogger<ShareArkModule> logger,
    Interactor<ShareArkModule> interactor,
    QChatService qChatService
) : ChatBehaviour, IConfigurable<ShareArkConfig>
{
    public ShareArkConfig Configuration { get; set; } = null!;

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // 由基地址推导三个接口地址，填一个基地址就能用全部功能
    string Base()
    {
        var b = Configuration.BaseUrl;
        if (string.IsNullOrWhiteSpace(b))
            throw new Exception("第三方接口基地址没填，去模块配置里补上");
        return b.Trim().TrimEnd('/');
    }
    static readonly Dictionary<string, string[]> _cands = new()
    {
        ["ark"] = new[] { "qq-musicArk", "qq_musicark", "musicArk", "music-card" },
        ["share"] = new[] { "qq-shareArk", "qq_shareArk", "qq-shareark", "shareArk", "share-card" },
        ["s-163"] = new[] { "163-music-search", "163_music_search", "163-search", "music-search" },
    };
    static readonly Dictionary<string, string> _sfx = new();
    static bool _discovered; // 首页/openapi 路径发现只跑一次
    string KeyQ() => string.IsNullOrWhiteSpace(Configuration.ApiKey) ? "" : "key=" + Uri.EscapeDataString(Configuration.ApiKey) + "&";
    // 地址已带 ? 时用 & 接参数，支持手动地址自带 query
    static string WithQuery(string url, string query) => url.Contains('?') ? url + "&" + query : url + "?" + query;
    // 报错里附请求URL时把 key 打码
    static string MaskKey(string url) => Regex.Replace(url, "([?&]key=)[^&]+", "$1***", RegexOptions.IgnoreCase);
    // 候选可以是后缀也可以是完整地址（自动发现的文档链接可能带全路径）
    static string JoinUrl(string b, string c) => c.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? c : b + "/" + c;
    string MusicArkUrl() => !string.IsNullOrWhiteSpace(Configuration.MusicArkUrl) ? Configuration.MusicArkUrl.Trim() : JoinUrl(Base(), _sfx.TryGetValue("ark", out var a) ? a : "qq-musicArk");
    string ShareArkUrl() => !string.IsNullOrWhiteSpace(Configuration.ShareArkUrl) ? Configuration.ShareArkUrl.Trim() : JoinUrl(Base(), _sfx.TryGetValue("share", out var s) ? s : "qq-shareArk");
    string SearchUrl(string pf) => !string.IsNullOrWhiteSpace(Configuration.SearchUrl) ? Configuration.SearchUrl.Trim().Replace("{pf}", pf) : JoinUrl(Base(), _sfx.TryGetValue("s-" + pf, out var q) ? q : pf + "-music-search");

    // 填一个基地址就够：默认后缀不通就挨个试候选，试通的记住
    async Task<string?> Fit(string key, string[] cands, string probe)
    {
        string keyQ = KeyQ();
        foreach (var c in cands)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var r = await http.GetAsync(JoinUrl(Base(), c) + "?" + keyQ + probe, cts.Token);
                string b = await r.Content.ReadAsStringAsync();
                using var d = JsonDocument.Parse(b);
                // 只认 code==200：错误路径回的 {code:404,msg:"API不存在"} 也带 code，一律不认
                if (d.RootElement.ValueKind == JsonValueKind.Object
                    && d.RootElement.TryGetProperty("code", out var c0)
                    && c0.ValueKind == JsonValueKind.Number && c0.GetInt32() == 200)
                {
                    _sfx[key] = c;
                    return c;
                }
            }
            catch { }
        }
        return null;
    }

    // 手动地址覆盖了的不再探测
    bool Manual(string key) => (key == "ark" && !string.IsNullOrWhiteSpace(Configuration.MusicArkUrl))
        || (key == "share" && !string.IsNullOrWhiteSpace(Configuration.ShareArkUrl))
        || (key == "s-163" && !string.IsNullOrWhiteSpace(Configuration.SearchUrl));

    // 每个角色的探测参数：必须是完整合法请求，让正确端点回 code==200（错误包也带 code，不能认）
    static string ProbeOf(string key) => key == "ark"
        ? "format=netease&song=t&singer=t&cover=https%3A%2F%2Fexample.com%2Fc.jpg&url=http%3A%2F%2Fmusic.163.com%2Fsong%2Fmedia%2Fouter%2Furl%3Fid%3D1.mp3&jump=http%3A%2F%2Fmusic.163.com%2Fsong%3Fid%3D1"
        : key == "share" ? "title=t&desc=t&url=https%3A%2F%2Fexample.com&preview=https%3A%2F%2Fexample.com%2Fa.jpg&prompt=t"
        : "msg=test";

    // 按命名猜路径是哪个角色：share 优先于 search，musicArk 落到 ark
    static string? RoleOf(string path)
    {
        string p = path.ToLowerInvariant();
        if (p.Contains("share") || p.Contains("link")) return "share";
        if (p.Contains("search") || p.Contains("sou")) return "search";
        if (p.Contains("ark") || p.Contains("music") || p.Contains("card") || p.Contains("sign")) return "ark";
        return null;
    }

    // 首次用到才适配：先试内置候选，缺的角色再去首页/openapi 挖一圈路径补试
    async Task EnsureFit()
    {
        foreach (var kv in _cands)
            if (!Manual(kv.Key) && !_sfx.ContainsKey(kv.Key))
                await Fit(kv.Key, kv.Value, ProbeOf(kv.Key));
        if (_discovered || string.IsNullOrWhiteSpace(Configuration.BaseUrl)) return;
        if (!_cands.Any(kv => !Manual(kv.Key) && !_sfx.ContainsKey(kv.Key))) return;
        _discovered = true;
        var found = await DiscoverPathsAsync();
        foreach (var kv in _cands)
            if (!Manual(kv.Key) && !_sfx.ContainsKey(kv.Key))
            {
                string role = kv.Key == "s-163" ? "search" : kv.Key;
                var extra = found.Where(p => RoleOf(p) == role && !kv.Value.Contains(p)).Take(12).ToArray();
                if (extra.Length > 0) await Fit(kv.Key, extra, ProbeOf(kv.Key));
            }
    }

    // 从基地址首页/域名根文档页与 openapi/swagger 文档里挖接口路径：候选表对不上的第三方也能认出来
    async Task<List<string>> DiscoverPathsAsync()
    {
        var found = new HashSet<string>();
        string root = Base();
        string domainRoot = "";
        try { var u = new Uri(root); domainRoot = u.Scheme + "://" + u.Authority + "/"; }
        catch { }

        // 归一成候选：完整地址整条收；路径在基地址前缀下取余段，不在就取最后一段
        void AddCandidate(string c)
        {
            if (string.IsNullOrWhiteSpace(c)) return;
            if (c.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(c.Split('?')[0].TrimEnd('/'));
                try { AddCandidate(new Uri(c).AbsolutePath); } catch { }
                return;
            }
            string p = c.Split('?')[0].Trim('/');
            if (p.Length == 0) return;
            string basePath = "";
            try { basePath = new Uri(root).AbsolutePath.TrimEnd('/'); } catch { }
            if (basePath.Length > 0 && p.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
                p = p.Substring(basePath.Length + 1);
            else if (p.Contains('/'))
                p = p.Split('/').Last();
            if (RoleOf(p) != null) found.Add(p);
        }

        foreach (var docPath in new[] { "openapi.json", "swagger.json", "swagger/v1/swagger.json" })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                string b = await http.GetStringAsync(root + "/" + docPath, cts.Token);
                using var d = JsonDocument.Parse(b);
                if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("paths", out var ps))
                    foreach (var p in ps.EnumerateObject())
                        if (domainRoot.Length > 0) AddCandidate(domainRoot + p.Name);
            }
            catch { }
        }

        async Task CrawlPage(string pageUrl)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                string html = await http.GetStringAsync(pageUrl, cts.Token);
                foreach (Match m in Regex.Matches(html, @"href=[""']([^""']+?)[""']"))
                {
                    string h = m.Groups[1].Value;
                    if (h.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        try { var u = new Uri(h); if (u.Host != new Uri(root).Host) continue; AddCandidate(u.Scheme + "://" + u.Authority + u.AbsolutePath); }
                        catch { continue; }
                    }
                    else if (h.StartsWith("/") && domainRoot.Length > 0) AddCandidate(domainRoot + h);
                    else AddCandidate(h);
                }
                foreach (Match m in Regex.Matches(html, @"[A-Za-z][A-Za-z0-9_\-]{2,40}"))
                    AddCandidate(m.Value);
            }
            catch { }
        }
        await CrawlPage(root + "/");
        if (domainRoot.Length > 0 && domainRoot != root.TrimEnd('/') + "/") await CrawlPage(domainRoot);
        return found.ToList();
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("探测三个接口通不通（音乐卡/分享卡/歌曲搜索），手动地址和自动后缀都会报出来，换了新第三方接口先跑一次这个")]
    public async Task ProbeBase()
    {
        if (Gate(Configuration.EnableProbeBase, "探测接口")) return;
        try { interactor.Poke(await ProbeBaseReportAsync()); }
        catch (Exception ex) { interactor.Poke("探测失败：" + ex.Message); }
    }

    // 探测三个接口，返回报告文本（UI 按钮与 AI 函数共用）
    public async Task<string> ProbeBaseReportAsync()
    {
        var rep = new List<string> { string.IsNullOrWhiteSpace(Configuration.BaseUrl) ? "基地址未填，只用手动地址" : "基地址 " + Base() };
        async Task Probe(string name, string url)
        {
            try
            {
                using var r = await http.GetAsync(url);
                string b = await r.Content.ReadAsStringAsync();
                bool json = false;
                try
                {
                    using var d = JsonDocument.Parse(b);
                    json = d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("code", out _);
                }
                catch { }
                rep.Add(name + "：" + (json ? "通" : "不通（返回不是签名接口结构 " + (int)r.StatusCode + "）"));
            }
            catch (Exception ex) { rep.Add(name + "：不通（" + ex.Message + "）"); }
        }
        string keyQ = KeyQ();
        await EnsureFit();
        rep.Add(UrlLine("音乐卡", Configuration.MusicArkUrl, "ark"));
        rep.Add(UrlLine("分享卡", Configuration.ShareArkUrl, "share"));
        rep.Add(UrlLine("歌曲搜索", Configuration.SearchUrl, "s-163"));
        await Probe("歌曲搜索", WithQuery(SearchUrl("163"), keyQ + "msg=" + Uri.EscapeDataString("test")));
        await Probe("音乐卡", WithQuery(MusicArkUrl(), keyQ + "platform=163&id=1"));
        await Probe("分享卡", WithQuery(ShareArkUrl(), keyQ + "title=t&desc=t&url=https%3A%2F%2Fexample.com&preview=https%3A%2F%2Fexample.com%2Fa.jpg&prompt=t"));
        if (!string.IsNullOrWhiteSpace(Configuration.BiliSearchUrl))
            await Probe("B站搜索", WithQuery(Configuration.BiliSearchUrl.Trim(), keyQ + "msg=" + Uri.EscapeDataString("test")));
        return string.Join("\n", rep);
    }

    // 报告每个接口最终用的地址来源：手动填的报地址，自动的报试通的后缀
    string UrlLine(string name, string manual, string key)
    {
        if (!string.IsNullOrWhiteSpace(manual)) return name + "地址 -> 手动 " + manual.Trim();
        if (_sfx.TryGetValue(key, out var v)) return v.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? name + "地址 -> 自动发现 " + v : name + "后缀 -> " + v;
        return name + "后缀 -> 未识别，回落默认（可手动填完整地址）";
    }

    // 猜接口用途，纯展示用
    static string TagOf(string url)
    {
        string l = url.ToLowerInvariant();
        if (l.Contains("bili")) return "［B站］";
        if (l.Contains("musicark")) return "［音乐卡］";
        if (l.Contains("shareark")) return "［分享卡］";
        if (l.Contains("search")) return "［搜索］";
        return "";
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出第三方基地址下发现的所有接口（来自openapi/swagger文档和站点首页的接口链接，只列举不调用，不消耗接口次数），并标注插件当前在用哪些")]
    public async Task ProbeAll()
    {
        if (Gate(Configuration.EnableProbeAll, "列出全部接口")) return;
        try { interactor.Poke(await ProbeAllReportAsync()); }
        catch (Exception ex) { interactor.Poke("列举失败：" + ex.Message); }
    }

    // 列出基地址下发现的全部接口，返回报告文本（UI 按钮与 AI 函数共用）
    public async Task<string> ProbeAllReportAsync()
    {
        string root = Base();
            string domainRoot = "";
            try { var u = new Uri(root); domainRoot = u.Scheme + "://" + u.Authority + "/"; } catch { }

            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            // openapi/swagger 文档：基地址和域名根都试
            foreach (var b in new[] { root, domainRoot.TrimEnd('/') }.Distinct())
            {
                if (b.Length == 0) continue;
                foreach (var docPath in new[] { "openapi.json", "swagger.json", "swagger/v1/swagger.json" })
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        string body = await http.GetStringAsync(b + "/" + docPath, cts.Token);
                        using var d = JsonDocument.Parse(body);
                        if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("paths", out var ps))
                            foreach (var p in ps.EnumerateObject())
                                paths.Add(b + "/" + p.Name.Trim('/'));
                    }
                    catch { }
                }
            }

            // 首页链接：同域且路径含 /api/ 或 /series/ 的视为接口线索
            async Task Crawl(string pageUrl)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    string html = await http.GetStringAsync(pageUrl, cts.Token);
                    foreach (Match m in Regex.Matches(html, @"href=[""']([^""']+?)[""']"))
                    {
                        string h = m.Groups[1].Value.Split('?')[0];
                        if (h.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            try { var u = new Uri(h); if (u.Host != new Uri(root).Host) continue; h = u.AbsolutePath; }
                            catch { continue; }
                        }
                        if (h.Length == 0 || !h.StartsWith("/")) continue;
                        if (h.Contains("/api/") || h.StartsWith("/api/") || h.Contains("/series/") || h.StartsWith("/series/"))
                            paths.Add(domainRoot.TrimEnd('/') + h);
                    }
                }
                catch { }
            }
            await Crawl(root + "/");
            if (domainRoot.Length > 0 && domainRoot.TrimEnd('/') != root.TrimEnd('/')) await Crawl(domainRoot);

            var rep = new List<string>();
            if (paths.Count == 0)
                rep.Add("基地址 " + root + " 下没有发现接口清单（没有openapi文档，首页也没有接口链接），只能靠 ProbeBase 探测已知路径");
            else
            {
                rep.Add("基地址 " + root + "，发现 " + paths.Count + " 个接口：");
                foreach (var p in paths) rep.Add("  " + TagOf(p) + p);
            }
            var used = new List<string>();
            if (!string.IsNullOrWhiteSpace(Configuration.MusicArkUrl)) used.Add("音乐卡（手动）");
            else if (_sfx.TryGetValue("ark", out var ua)) used.Add("音乐卡 -> " + ua);
            if (!string.IsNullOrWhiteSpace(Configuration.ShareArkUrl)) used.Add("分享卡（手动）");
            else if (_sfx.TryGetValue("share", out var us)) used.Add("分享卡 -> " + us);
            if (!string.IsNullOrWhiteSpace(Configuration.SearchUrl)) used.Add("歌曲搜索（手动）");
            else if (_sfx.TryGetValue("s-163", out var uq)) used.Add("歌曲搜索 -> " + uq);
            if (!string.IsNullOrWhiteSpace(Configuration.BiliSearchUrl)) used.Add("B站搜索（手动）");
            used.Add("B站系列 -> " + Base() + "/bili-*");
            rep.Add("插件在用：" + string.Join("；", used));
            return string.Join("\n", rep);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("搜索歌曲拿ID，再发卡片。platform取163(网易云)/qq(QQ音乐)/kugou(酷狗)，默认163，不填就搜网易云")]
    public async Task SearchMusic(
        [Description("歌名或关键词")] string keyword,
        [Description("平台：163(网易云)/qq(QQ音乐)/kugou(酷狗)，默认163")] string? platform = null)
    {
        if (Gate(Configuration.EnableSearchMusic, "搜歌")) return;
        string pf = (platform ?? "163").Trim().ToLowerInvariant();
        if (pf == "netease") pf = "163";
        try
        {
            if (pf != "163" && pf != "qq" && pf != "kugou")
                throw new Exception("搜索只支持 163/qq/kugou");
            await EnsureFit();
            string keyQ = KeyQ();
            string u = WithQuery(SearchUrl(pf), keyQ + "msg=" + Uri.EscapeDataString(keyword));
            string body = await http.GetStringAsync(u);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.GetProperty("code").GetInt32() != 200)
                throw new Exception(doc.RootElement.TryGetProperty("msg", out var mm) ? mm.GetString() : "搜索失败");
            var arr = doc.RootElement.GetProperty("data");
            List<string> lines = new();
            int n = 0;
            foreach (var s0 in arr.EnumerateArray())
            {
                if (n >= 5) break;
                string t = s0.TryGetProperty("title", out var tv) ? tv.GetString() ?? "" : "";
                string a = s0.TryGetProperty("author", out var av) ? av.GetString() ?? "" : "";
                string q = s0.TryGetProperty("quality", out var qv) ? qv.GetString() ?? "" : "";
                string idv = s0.TryGetProperty("id", out var iv) ? iv.GetString() ?? "" : "";
                if (q.Length > 0) lines.Add(idv + " | " + t + " - " + a + " [" + q + "]");
                else lines.Add(idv + " | " + t + " - " + a);
                n++;
            }
            if (lines.Count == 0) lines.Add("没搜到");
            interactor.Poke(string.Join("\n", lines));
        }
        catch (Exception thirdPartyError)
        {
            if (Configuration.EnableOfficialFallback == false)
            {
                interactor.Poke("搜索失败：" + thirdPartyError.Message);
                return;
            }
            try
            {
                List<(string Id, string Name, string Singer)> results = await OfficialSearchAsync(pf, keyword);
                List<string> lines = results.Select(r => r.Id + " | " + r.Name + (r.Singer.Length > 0 ? " - " + r.Singer : "")).ToList();
                if (lines.Count == 0) lines.Add("没搜到");
                interactor.Poke("ℹ️ 第三方接口不可用（" + thirdPartyError.Message + "），已切换官方接口。\n" + string.Join("\n", lines));
            }
            catch (Exception officialError)
            {
                interactor.Poke("❌ 搜索：第三方（" + thirdPartyError.Message + "）；官方容灾（" + officialError.Message + "）");
            }
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("搜索B站视频拿BV号，用于再发卡片。配置了B站搜索接口地址就走第三方，否则走B站官方")]
    public async Task SearchBili([Description("视频关键词")] string keyword)
    {
        if (Gate(Configuration.EnableSearchBili, "B站搜索")) return;
        try
        {
            var hits = (await BiliSearchAsync(keyword)).Take(5);
            var lines = hits.Select(h => h.bv + " - " + h.title).ToList();
            if (lines.Count == 0) lines.Add("没搜到");
            interactor.Poke(string.Join("\n", lines));
        }
        catch (Exception ex) { interactor.Poke("搜索失败：" + ex.Message); }
    }

    // B站视频搜索统一入口：配置了第三方接口优先走第三方（失败按容灾配置转官方），否则走B站官方，都归一成 (BV号, 标题) 列表
    async Task<List<(string bv, string title)>> BiliSearchAsync(string keyword)
    {
        var list = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(Configuration.BiliSearchUrl))
        {
            try
            {
                string u = WithQuery(Configuration.BiliSearchUrl.Trim(), KeyQ() + "msg=" + Uri.EscapeDataString(keyword));
                string body = await http.GetStringAsync(u);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.GetProperty("code").GetInt32() != 200)
                    throw new Exception(doc.RootElement.TryGetProperty("msg", out var mm) ? mm.GetString() ?? "" : "搜索失败");
                foreach (var v in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    string bv = v.TryGetProperty("id", out var iv) ? iv.GetString() ?? "" : "";
                    string t = v.TryGetProperty("title", out var tv) ? Regex.Replace(tv.GetString() ?? "", "<[^>]*>", "") : "";
                    if (bv.Length > 0) list.Add((bv, t));
                }
                return list;
            }
            catch (Exception thirdPartyError)
            {
                if (Configuration.EnableOfficialFallback == false)
                {
                    throw;
                }
                // 容灾：第三方失败转B站官方搜索，失败原因得留个痕，不然排查时两头都查不到
                logger.LogWarning("B站第三方搜索失败，转官方搜索：" + thirdPartyError.Message);
            }
        }
        string ou = "https://api.bilibili.com/x/web-interface/search/all/v2?keyword=" + Uri.EscapeDataString(keyword);
        using var req = new HttpRequestMessage(HttpMethod.Get, ou);
        req.Headers.Referrer = new Uri("https://www.bilibili.com/");
        req.Headers.TryAddWithoutValidation("Cookie", "buvid3=1; b_nut=1");
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        string obody = await (await http.SendAsync(req)).Content.ReadAsStringAsync();
        using var odoc = JsonDocument.Parse(obody);
        foreach (var sec in odoc.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
        {
            if (sec.GetProperty("result_type").GetString() != "video") continue;
            foreach (var v in sec.GetProperty("data").EnumerateArray())
                list.Add((v.GetProperty("bvid").GetString() ?? "", Regex.Replace(v.GetProperty("title").GetString() ?? "", "<[^>]*>", "")));
        }
        return list;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("发送音乐/视频卡片到QQ（自签名版，不依赖协议端签名，比QQ增强的点歌更通用）。platform取163(网易云)/qq(QQ音乐)/bilibili(BV号)/kugou；id为歌曲ID或BV号；type不填默认group")]
    public async Task SendArkCard(
        [Description("平台：163/qq/bilibili/kugou")] string platform,
        [Description("歌曲ID或BV号")] string id,
        [Description("目标群号或QQ号")] long? targetId = null,
        [Description("group或private，默认group")] string? type = null,
        [Description("卡片标题，可留空")] string? title = null)
    {
        if (Gate(Configuration.EnableSendArkCard, "发音乐/视频卡片")) return;
        if (targetId == null)
            throw new Exception("没给目标，发群里还是私聊得说一个");
        long target = targetId.Value;
        bool isGroup = !string.Equals(type, "private", StringComparison.OrdinalIgnoreCase);
        string thirdPartyError;
        try
        {
            await EnsureFit();
            string card = await SignAsync(platform, id, title);
            (bool Success, string Message) sent = await SendJsonAsync(target, isGroup, card);
            if (sent.Success)
            {
                interactor.Poke("卡片已发出");
                return;
            }
            thirdPartyError = sent.Message;
        }
        catch (Exception signError)
        {
            thirdPartyError = signError.Message;
        }

        if (Configuration.EnableOfficialFallback == false)
        {
            logger.LogWarning("发卡片失败：" + thirdPartyError);
            interactor.Poke("❌ 发卡片失败：" + thirdPartyError);
            return;
        }
        // 签名成功但协议端不吃 json 卡片时，退到 OneBot 标准音乐段
        try
        {
            JsonObject musicSegment = await BuildOfficialMusicSegmentAsync(platform, id, title);
            JsonArray segments = new();
            segments.Add(musicSegment);
            (bool Success, string Message) official = await SendSegmentsAsync(target, isGroup, segments);
            if (official.Success)
            {
                interactor.Poke("✅ 卡片已发出（OneBot 标准音乐段容灾；json 卡片那步失败：" + thirdPartyError + "）");
                return;
            }
            interactor.Poke("❌ 发卡片失败：json 卡片（" + thirdPartyError + "）；官方音乐段（" + official.Message + "）");
        }
        catch (Exception officialError)
        {
            logger.LogWarning("发卡片失败：第三方 {Third}；官方容灾 {Official}", thirdPartyError, officialError.Message);
            interactor.Poke("❌ 发卡片：第三方（" + thirdPartyError + "）；官方容灾（" + officialError.Message + "）");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("把任意网页链接拼成QQ分享卡发出（通用链接卡，走分享卡签名接口）。title标题，desc描述，url跳转链接，preview预览图可留空，prompt提示文本可留空，type不填默认group")]
    public async Task SendArkShareCard(
        [Description("跳转链接")] string url,
        [Description("卡片标题，可留空，留空自动抓")] string? title = null,
        [Description("卡片描述，可留空，留空自动抓")] string? desc = null,
        [Description("预览图URL，可留空")] string? preview = null,
        [Description("提示文本，可留空")] string? prompt = null,
        [Description("目标群号或QQ号")] long? targetId = null,
        [Description("group或private，默认group")] string? type = null)
    {
        if (Gate(Configuration.EnableSendShareCard, "发通用分享卡")) return;
        if (targetId == null)
            throw new Exception("没给目标，发群里还是私聊得说一个");
        long target = targetId.Value;
        bool isGroup = !string.Equals(type, "private", StringComparison.OrdinalIgnoreCase);
        try
        {
            // 过滤B站链接：B站走专用搜索+音乐卡接口，不走通用分享卡
            await EnsureFit();
            string host = "";
            try { host = new Uri(url).Host.ToLowerInvariant(); } catch { }
                if ((host.Contains("bilibili.com") || host.Contains("b23.tv") || host.Contains("biligame.com")) && !host.StartsWith("live."))
                    throw new Exception("B站视频/主页链接不走自定义卡，用 SearchBili 拿BV号再 SendArkCard，或用 BiliParse 解析这个链接，直播可直接发");

            var meta = await FetchMetaAsync(url);
            if (preview != null && preview.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) preview = null; // data URI 不收，回落自动抓
            if (string.IsNullOrWhiteSpace(title)) title = meta.title;
            if (string.IsNullOrWhiteSpace(desc)) desc = meta.desc;
            if (string.IsNullOrWhiteSpace(preview)) preview = meta.image;
            if (string.IsNullOrWhiteSpace(title)) title = url;
            if (string.IsNullOrWhiteSpace(desc)) desc = title;
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = "[分享]" + title;
            string shareUrl = ShareArkUrl();
            var q = new Dictionary<string, string>
            {
                ["title"] = title,
                ["desc"] = desc,
                ["preview"] = preview,
                ["url"] = url,
                ["prompt"] = prompt
            };
            if (!string.IsNullOrWhiteSpace(Configuration.ApiKey)) q["key"] = Configuration.ApiKey;
            string query = string.Join("&", q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")));
            string full = WithQuery(shareUrl, query);
            string body = await http.GetStringAsync(full);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.GetProperty("code").GetInt32() != 200)
                throw new Exception("签名失败：" + doc.RootElement.GetProperty("msg").GetString() + " | 请求=" + MaskKey(full));
            string card = doc.RootElement.GetProperty("data").GetRawText();
            (bool Success, string Message) sent = await SendJsonAsync(target, isGroup, card);
            if (sent.Success) interactor.Poke("分享卡已发出");
            else interactor.Poke("❌ 分享卡没发出去：" + sent.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning("发分享卡失败：" + ex.Message);
            interactor.Poke("发分享卡失败：" + ex.Message);
        }
    }

    // ---------- B站系列（{BaseUrl}/bili-*，均需 ApiKey，文档见咸鱼API /series/bili） ----------

    // 第三方接口统一调用（B站系列/IP查询等，均在 {BaseUrl} 下）：拼 key 参数，code!=200 时抛出 msg
    async Task<JsonElement> ApiCall(string path, params (string k, string? v)[] ps)
    {
        if (string.IsNullOrWhiteSpace(Configuration.ApiKey))
            throw new Exception("B站系列接口需要 ApiKey，去模块配置里补上");
        var q = new List<string> { "key=" + Uri.EscapeDataString(Configuration.ApiKey) };
        foreach (var (k, v) in ps)
            if (!string.IsNullOrWhiteSpace(v)) q.Add(Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(v));
        string body = await http.GetStringAsync(WithQuery(Base() + "/" + path, string.Join("&", q)));
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.GetProperty("code").GetInt32() != 200)
            throw new Exception(doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "调用失败");
        return doc.RootElement.GetProperty("data").Clone();
    }

    // 字段取值兼容字符串和数字
    static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v)
        ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "")
        : "";

    static string GetStringField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return "";
        }
        return field.ValueKind switch
        {
            JsonValueKind.String => field.GetString() ?? "",
            JsonValueKind.Number => field.GetRawText(),
            _ => ""
        };
    }

    static long GetNumericField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return 0;
        }
        return field.ValueKind switch
        {
            JsonValueKind.Number => field.GetInt64(),
            JsonValueKind.String => long.TryParse(field.GetString(), out long parsed) ? parsed : 0,
            _ => 0
        };
    }

    // 功能开关检查：关闭时向AI说明并返回 true（调用方据此直接返回）
    bool Gate(bool enabled, string name)
    {
        if (enabled) return false;
        interactor.Poke(name + "功能已在插件配置中关闭");
        return true;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("解析B站链接（视频/b23.tv短链/专栏/番剧/直播间），返回类型、ID、标题、UP主等详情；action=play 时额外返回视频播放直链")]
    public async Task BiliParse(
        [Description("B站链接")] string url,
        [Description("id=仅类型和ID；detail=详情（默认）；play=额外返回播放直链")] string? action = null,
        [Description("清晰度80/64/32/16，仅action=play生效，默认80")] int? qn = null)
    {
        if (Gate(Configuration.EnableBiliParse, "B站链接解析")) return;
        try
        {
            interactor.Poke(await BiliParseThirdPartyAsync(url, action, qn));
        }
        catch (Exception thirdPartyError)
        {
            await FallbackOrReportAsync("解析", thirdPartyError, () => OfficialBiliParseAsync(url, action, qn));
        }
    }

    async Task<string> BiliParseThirdPartyAsync(string url, string? action, int? qn)
    {
        var d = await ApiCall("bili-url-parse", ("url", url), ("action", action), ("qn", qn?.ToString()));
        var rep = new List<string> { "类型 " + S(d, "type") + "，ID " + S(d, "id") };
        string title = S(d, "title");
        if (title.Length > 0) rep.Add(title);
        if (d.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.Object)
            rep.Add("UP主 " + S(ow, "name"));
        if (d.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object)
            rep.Add("作者 " + S(au, "name"));
        if (d.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
            rep.Add("播放 " + S(st, "view") + "，点赞 " + S(st, "like") + "，评论 " + S(st, "reply"));
        if (S(d, "duration_text").Length > 0) rep.Add("时长 " + S(d, "duration_text"));
        if (d.TryGetProperty("rating", out var rt) && rt.ValueKind == JsonValueKind.Number) rep.Add("评分 " + rt.GetDouble());
        if (d.TryGetProperty("ep_count", out var ec) && ec.ValueKind == JsonValueKind.Number) rep.Add("共 " + ec.GetInt32() + " 集");
        if (action == "play")
        {
            if (d.TryGetProperty("play", out var play))
            {
                if (play.TryGetProperty("quality_desc", out var qd)) rep.Add("清晰度 " + qd.GetString());
                string? direct = null;
                if (play.TryGetProperty("no_referer_url", out var nru) && nru.ValueKind == JsonValueKind.String) direct = nru.GetString();
                else if (play.TryGetProperty("durl", out var dl) && dl.ValueKind == JsonValueKind.Array && dl.GetArrayLength() > 0) direct = dl[0].GetString();
                else if (play.TryGetProperty("dash", out var dash) && dash.TryGetProperty("video", out var vd) && vd.ValueKind == JsonValueKind.Array && vd.GetArrayLength() > 0)
                    direct = vd[0].TryGetProperty("base_url", out var bu) ? bu.GetString() : null;
                if (!string.IsNullOrEmpty(direct)) rep.Add("直链 " + direct);
            }
        }
        rep.Add("链接 " + S(d, "web_url"));
        return string.Join("\n", rep);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("通过UID查询B站用户信息：昵称、等级、粉丝数、签名、认证、投稿数等")]
    public async Task BiliUserInfo([Description("用户mid")] long uid)
    {
        if (Gate(Configuration.EnableBiliUserInfo, "B站用户查询")) return;
        try
        {
            interactor.Poke(await BiliUserInfoThirdPartyAsync(uid));
        }
        catch (Exception thirdPartyError)
        {
            await FallbackOrReportAsync("用户查询", thirdPartyError, () => OfficialBiliUserInfoAsync(uid));
        }
    }

    async Task<string> BiliUserInfoThirdPartyAsync(long uid)
    {
        var d = await ApiCall("bili-user-info", ("uid", uid.ToString()));
        var rep = new List<string> { S(d, "name") + "（" + S(d, "mid") + "）" };
        string vip = S(d, "vip_label");
        rep.Add("等级 " + S(d, "level") + (vip.Length > 0 ? "，" + vip : ""));
        rep.Add("粉丝 " + S(d, "fans") + "，关注 " + S(d, "following") + "，获赞 " + S(d, "likes"));
        rep.Add("投稿视频 " + S(d, "archive_count") + "，专栏 " + S(d, "article_count"));
        string sign = S(d, "sign");
        if (sign.Length > 0) rep.Add("签名：" + sign);
        if (d.TryGetProperty("official", out var of) && of.ValueKind == JsonValueKind.Object && S(of, "title").Length > 0)
            rep.Add("认证：" + S(of, "title"));
        return string.Join("\n", rep);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查询B站UP主最新动态/视频/专栏更新，可翻页；视频更新带BV号，可直接拿去发卡片")]
    public async Task BiliUpdates(
        [Description("UP主mid")] long uid,
        [Description("类型过滤：all=全部（默认）/video=仅视频/article=仅专栏")] string? type = null,
        [Description("翻页游标，填上一次结果里的 next_offset")] string? offset = null)
    {
        if (Gate(Configuration.EnableBiliUpdates, "UP主更新查询")) return;
        try
        {
            interactor.Poke(await BiliUpdatesThirdPartyAsync(uid, type, offset));
        }
        catch (Exception thirdPartyError)
        {
            await FallbackOrReportAsync("更新查询", thirdPartyError, () => OfficialBiliUpdatesAsync(uid));
        }
    }

    async Task<string> BiliUpdatesThirdPartyAsync(long uid, string? type, string? offset)
    {
        var d = await ApiCall("bili-user-update", ("uid", uid.ToString()), ("type", type ?? "all"), ("offset", offset));
        var rep = new List<string>();
        foreach (var it in d.GetProperty("items").EnumerateArray())
        {
            string line = S(it, "pub_time_text") + " " + (S(it, "title").Length > 0 ? S(it, "title") : S(it, "summary"));
            if (S(it, "bvid").Length > 0) line += "（" + S(it, "bvid") + "）";
            rep.Add(line);
        }
        if (rep.Count == 0) rep.Add("没有更新");
        if (d.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True && S(d, "next_offset").Length > 0)
            rep.Add("还有更多，用 offset=" + S(d, "next_offset") + " 翻页");
        return string.Join("\n", rep);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取B站热门视频榜，或某个视频的相关推荐（related 需传bvid）")]
    public async Task BiliHot(
        [Description("popular=热门榜（默认）/related=相关推荐")] string? action = null,
        [Description("相关推荐时的视频BV号")] string? bvid = null,
        [Description("热门榜页码，默认1，每页100")] int? pn = null)
    {
        if (Gate(Configuration.EnableBiliHot, "B站热门/相关")) return;
        try
        {
            interactor.Poke(await BiliHotThirdPartyAsync(action, bvid, pn));
        }
        catch (Exception thirdPartyError)
        {
            await FallbackOrReportAsync("热门/相关查询", thirdPartyError, () => OfficialBiliHotAsync(action, bvid, pn));
        }
    }

    async Task<string> BiliHotThirdPartyAsync(string? action, string? bvid, int? pn)
    {
        string a = (action ?? "popular").Trim().ToLowerInvariant();
        var d = await ApiCall("bili-hot-recommend", ("action", a), ("bvid", bvid), ("pn", pn?.ToString()));
        var rep = new List<string>();
        int n = 0;
        foreach (var it in d.GetProperty("items").EnumerateArray())
        {
            if (n >= 10) break;
            string line = (it.TryGetProperty("ranking", out var rk) ? rk.GetInt32() + ". " : "") + S(it, "bvid") + " - " + S(it, "title");
            if (it.TryGetProperty("owner", out var ow2) && ow2.ValueKind == JsonValueKind.Object) line += " - " + S(ow2, "name");
            if (S(it, "view_text").Length > 0) line += " [" + S(it, "view_text") + "播放]";
            rep.Add(line);
            n++;
        }
        if (rep.Count == 0) rep.Add("没有内容");
        return string.Join("\n", rep);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("B站扫码登录：action=generate 申请二维码（把返回的二维码链接发出来让人扫），action=poll 用 qrcode_key 查登录状态；登录成功返回 cookie_id")]
    public async Task BiliQrLogin(
        [Description("generate=申请二维码 / poll=轮询登录状态")] string action,
        [Description("action=poll时填 generate 返回的 qrcode_key")] string? qrcodeKey = null)
    {
        if (Gate(Configuration.EnableBiliQrLogin, "B站扫码登录")) return;
        try
        {
            interactor.Poke(await BiliQrLoginThirdPartyAsync(action, qrcodeKey));
        }
        catch (Exception thirdPartyError)
        {
            await FallbackOrReportAsync("扫码登录", thirdPartyError, () => OfficialBiliQrLoginAsync(action, qrcodeKey));
        }
    }

    async Task<string> BiliQrLoginThirdPartyAsync(string action, string? qrcodeKey)
    {
        var d = await ApiCall("bili-qrcode-login", ("action", action), ("qrcode_key", qrcodeKey));
        if (action == "generate")
            return "qrcode_key " + S(d, "qrcode_key") + "\n二维码链接（180秒内有效，打开后扫码）：\n" + S(d, "qrcode_url") + "\n扫完后用 action=poll、qrcode_key 查结果";
        string status = S(d, "status");
        string msg = S(d, "message").Length > 0 ? S(d, "message") : status;
        if (status == "success")
            msg += "\ncookie_id " + S(d, "cookie_id") + "，用户 " + S(d, "uname") + "，有效期至 " + S(d, "expire_at");
        return msg;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("刷新或检查服务器保存的B站登录Cookie，延长有效期；action=check 仅检查是否需要刷新，refresh=执行刷新（默认）")]
    public async Task BiliCookieRefresh(
        [Description("Cookie唯一标识ID")] string cookieId,
        [Description("check=仅检查 / refresh=执行刷新（默认）")] string? action = null)
    {
        if (Gate(Configuration.EnableBiliCookieRefresh, "Cookie刷新")) return;
        try
        {
            interactor.Poke(await BiliCookieRefreshThirdPartyAsync(cookieId, action));
        }
        catch (Exception thirdPartyError)
        {
            await FallbackOrReportAsync("Cookie刷新", thirdPartyError, () =>
                Task.FromResult("官方容灾暂不支持Cookie刷新（B站官方刷新流程复杂），请改用 BiliQrLogin 重新扫码登录，或恢复第三方服务后重试"));
        }
    }

    async Task<string> BiliCookieRefreshThirdPartyAsync(string cookieId, string? action)
    {
        var d = await ApiCall("bili-cookie-refresh", ("cookie_id", cookieId), ("action", action));
        bool refreshed = d.TryGetProperty("refreshed", out var rf) && rf.ValueKind == JsonValueKind.True;
        string extra = refreshed ? "已刷新，新有效期至 " + S(d, "new_expire_at")
            : S(d, "message").Length > 0 ? S(d, "message") : "未刷新";
        return "Cookie " + S(d, "cookie_id") + " " + extra;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("管理服务器保存的B站登录Cookie：list=列出全部（默认，不含敏感字段）/check=验证登录态/delete=删除/save=导入Cookie字符串。故意不提供导出完整Cookie的动作，避免凭据进入聊天记录")]
    public async Task BiliCookie(
        [Description("list=列出/check=验证/delete=删除/save=导入")] string? action = null,
        [Description("Cookie唯一标识ID，check/delete时填")] string? cookieId = null,
        [Description("action=save时必填，格式 k1=v1; k2=v2")] string? cookieString = null)
    {
        if (Gate(Configuration.EnableBiliCookie, "Cookie管理")) return;
        try
        {
            interactor.Poke(await BiliCookieThirdPartyAsync(action, cookieId, cookieString));
        }
        catch (Exception thirdPartyError)
        {
            await FallbackOrReportAsync("Cookie管理", thirdPartyError, () => OfficialBiliCookieAsync(action, cookieString));
        }
    }

    async Task<string> BiliCookieThirdPartyAsync(string? action, string? cookieId, string? cookieString)
    {
        string a = (action ?? "list").Trim().ToLowerInvariant();
        var d = await ApiCall("bili-user-cookie", ("action", a), ("cookie_id", cookieId), ("cookie_string", cookieString));
        switch (a)
        {
            case "list":
                var rep = new List<string>();
                foreach (var it in d.GetProperty("items").EnumerateArray())
                    rep.Add(S(it, "cookie_id") + " " + S(it, "uname") + "（" + S(it, "uid") + "）有效期至 " + S(it, "expire_at") + "，已用 " + S(it, "use_count") + " 次");
                if (rep.Count == 0) rep.Add("没有保存的Cookie");
                return string.Join("\n", rep);
            case "check":
                bool valid = d.TryGetProperty("valid", out var vv) && vv.ValueKind == JsonValueKind.True;
                return "Cookie " + S(d, "cookie_id") + " " + (valid ? "登录态有效：" + S(d, "uname") + "（" + S(d, "uid") + "）" : "无效：" + S(d, "message"));
            case "delete":
                return "Cookie " + S(d, "cookie_id") + (d.TryGetProperty("deleted", out var del) && del.ValueKind == JsonValueKind.True ? " 已删除" : " 删除失败");
            case "save":
                return "已导入，cookie_id " + S(d, "cookie_id") + "，用户 " + S(d, "uname") + "，有效期至 " + S(d, "expire_at");
            default:
                return "不支持的action：" + a + "（可用 list/check/delete/save）";
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查询IP地址的地理位置、运营商、ASN、时区；不填ip则查询本机出口IP")]
    public async Task IpLookup([Description("要查询的IP地址，留空查本机出口IP")] string? ip = null)
    {
        if (Gate(Configuration.EnableIpLookup, "IP查询")) return;
        try
        {
            var d = await ApiCall("ip-lookup", ("ip", ip));
            bool self = d.TryGetProperty("is_self", out var sf) && sf.ValueKind == JsonValueKind.True;
            var rep = new List<string>
            {
                S(d, "ip") + (self ? "（本机出口）" : ""),
                S(d, "country") + " " + S(d, "region") + " " + S(d, "city"),
                "运营商 " + S(d, "isp") + "，组织 " + S(d, "org") + "，ASN " + S(d, "asn"),
                "时区 " + S(d, "timezone") + "，坐标 " + S(d, "latitude") + "," + S(d, "longitude")
            };
            interactor.Poke(string.Join("\n", rep));
        }
        catch (Exception ex) { interactor.Poke("查询失败：" + ex.Message); }
    }

    // ============================================================
    // 官方容灾：第三方失败时自动切换官方公开接口
    // ============================================================

    // 统一容灾路由：未开启容灾时只报第三方错误；开启则执行官方实现，双失败报双原因
    async Task FallbackOrReportAsync(string name, Exception thirdPartyError, Func<Task<string>> officialFallback)
    {
        if (Configuration.EnableOfficialFallback == false)
        {
            interactor.Poke("❌ " + name + "失败：" + thirdPartyError.Message);
            return;
        }
        try
        {
            interactor.Poke("ℹ️ 第三方接口不可用（" + thirdPartyError.Message + "），已切换官方接口。\n" + await officialFallback());
        }
        catch (Exception officialError)
        {
            interactor.Poke("❌ " + name + "：第三方（" + thirdPartyError.Message + "）；官方容灾（" + officialError.Message + "）");
        }
    }

    // ---- 本地B站Cookie（扫码登录成功后保存，供官方接口降低风控） ----
    string BiliCookieFilePath() => Path.Combine(AlifePath.StorageFolderPath, "ShareArk", "bili_cookie.txt");

    string LoadBiliCookie()
    {
        try
        {
            string path = BiliCookieFilePath();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    void SaveBiliCookie(string cookie)
    {
        string path = BiliCookieFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, cookie.Trim());
    }

    // ---- B站官方GET基座 ----
    const string BiliApiBase = "https://api.bilibili.com";
    const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    async Task<JsonElement> BiliOfficialGetAsync(string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://www.bilibili.com/");
        string cookie = LoadBiliCookie();
        request.Headers.TryAddWithoutValidation("Cookie", (cookie.Length > 0 ? cookie + "; " : "") + "buvid3=1; b_nut=1");
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        string body = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
        using JsonDocument parsed = JsonDocument.Parse(body);
        JsonElement root = parsed.RootElement.Clone();
        int code = root.TryGetProperty("code", out JsonElement codeElement) && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : -1;
        if (code != 0)
        {
            string detail = root.TryGetProperty("message", out JsonElement messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? ""
                : "";
            throw new Exception("B站接口返回 " + code + (detail.Length > 0 ? "：" + detail : ""));
        }
        return root.TryGetProperty("data", out JsonElement dataElement) ? dataElement.Clone() : JsonDocument.Parse("null").RootElement.Clone();
    }

    // ---- WBI 签名（用户信息/投稿搜索接口需要） ----
    static readonly int[] WbiMixinTable = { 46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52 };
    string wbiMixinKey = "";
    DateTime wbiKeyFetchedTime = DateTime.MinValue;

    async Task<string> GetWbiMixinKeyAsync()
    {
        if (wbiMixinKey.Length > 0 && (DateTime.Now - wbiKeyFetchedTime).TotalHours < 24)
        {
            return wbiMixinKey;
        }
        JsonElement nav = await BiliOfficialGetAsync(BiliApiBase + "/x/web-interface/nav");
        JsonElement wbiImg = nav.GetProperty("wbi_img");
        string imgKey = GetStringField(wbiImg, "img_url").Split('/').Last().Split('.')[0];
        string subKey = GetStringField(wbiImg, "sub_url").Split('/').Last().Split('.')[0];
        string rawKey = imgKey + subKey;
        char[] mixin = new char[32];
        for (int i = 0; i < 32; i++)
        {
            mixin[i] = rawKey[WbiMixinTable[i]];
        }
        wbiMixinKey = new string(mixin);
        wbiKeyFetchedTime = DateTime.Now;
        return wbiMixinKey;
    }

    static string Md5Hex(string text)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    static string FilterWbiValue(string value)
    {
        return Regex.Replace(value, "[!'()*]", "");
    }

    // 对参数做 WBI 签名，返回带 w_rid/wts 的最终查询串
    async Task<string> BuildWbiQueryAsync(Dictionary<string, string> parameters)
    {
        string mixinKey = await GetWbiMixinKeyAsync();
        parameters["wts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string query = string.Join("&", parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(FilterWbiValue(pair.Value))));
        return query + "&w_rid=" + Md5Hex(query + mixinKey);
    }

    // ---- BiliParse 官方实现：URL解析 + view 详情 + playurl 直链 ----
    async Task<string> OfficialBiliParseAsync(string url, string? action, int? qn)
    {
        string workUrl = url.Trim();
        if (workUrl.Contains("b23.tv"))
        {
            try
            {
                using HttpResponseMessage redirectResponse = await http.GetAsync(workUrl);
                string? finalUrl = redirectResponse.RequestMessage?.RequestUri?.ToString();
                if (string.IsNullOrEmpty(finalUrl) == false)
                {
                    workUrl = finalUrl;
                }
            }
            catch
            {
                // 短链解析失败则按原文解析
            }
        }

        string bvid = Regex.Match(workUrl, "BV[0-9A-Za-z]{10}").Value;
        Match articleMatch = Regex.Match(workUrl, "cv([0-9]+)");
        Match seasonMatch = Regex.Match(workUrl, "ss([0-9]+)");
        Match liveMatch = Regex.Match(workUrl, @"live\.bilibili\.com/([0-9]+)");

        if (bvid.Length == 12)
        {
            var rep = new List<string> { "类型 video，ID " + bvid };
            if (action == "id")
            {
                rep.Add("链接 https://www.bilibili.com/video/" + bvid);
                return string.Join("\n", rep);
            }
            string viewQuery = await BuildWbiQueryAsync(new Dictionary<string, string> { ["bvid"] = bvid });
            JsonElement view = await BiliOfficialGetAsync(BiliApiBase + "/x/web-interface/wbi/view?" + viewQuery);
            rep.Add(S(view, "title"));
            if (view.TryGetProperty("owner", out JsonElement owner) && owner.ValueKind == JsonValueKind.Object)
            {
                rep.Add("UP主 " + S(owner, "name"));
            }
            if (view.TryGetProperty("stat", out JsonElement stat) && stat.ValueKind == JsonValueKind.Object)
            {
                rep.Add("播放 " + S(stat, "view") + "，点赞 " + S(stat, "like") + "，评论 " + S(stat, "reply"));
            }
            rep.Add("时长 " + FormatSeconds(GetNumericField(view, "duration")));
            rep.Add("链接 https://www.bilibili.com/video/" + bvid);
            if (action == "play")
            {
                try
                {
                    long cid = GetNumericField(view, "cid");
                    JsonElement play = await BiliOfficialGetAsync(BiliApiBase + "/x/player/playurl?bvid=" + bvid
                        + "&cid=" + cid + "&qn=" + (qn ?? 80) + "&fnval=16");
                    string? direct = null;
                    if (play.TryGetProperty("durl", out JsonElement durl) && durl.ValueKind == JsonValueKind.Array && durl.GetArrayLength() > 0)
                    {
                        direct = durl[0].TryGetProperty("url", out JsonElement durlUrl) ? durlUrl.GetString() : null;
                    }
                    else if (play.TryGetProperty("dash", out JsonElement dash)
                        && dash.TryGetProperty("video", out JsonElement dashVideo)
                        && dashVideo.ValueKind == JsonValueKind.Array && dashVideo.GetArrayLength() > 0)
                    {
                        direct = dashVideo[0].TryGetProperty("base_url", out JsonElement baseUrl) ? baseUrl.GetString() : null;
                    }
                    rep.Add(string.IsNullOrEmpty(direct) ? "直链获取失败（高清晰度可能需要登录态）" : "直链 " + direct);
                }
                catch (Exception playError)
                {
                    rep.Add("直链获取失败：" + playError.Message);
                }
            }
            return string.Join("\n", rep);
        }
        if (liveMatch.Success)
        {
            return "类型 live，ID " + liveMatch.Groups[1].Value + "\n链接 https://live.bilibili.com/" + liveMatch.Groups[1].Value;
        }
        if (seasonMatch.Success)
        {
            return "类型 bangumi，ID ss" + seasonMatch.Groups[1].Value + "\n链接 https://www.bilibili.com/bangumi/play/ss" + seasonMatch.Groups[1].Value;
        }
        if (articleMatch.Success)
        {
            return "类型 article，ID cv" + articleMatch.Groups[1].Value + "\n链接 https://www.bilibili.com/read/cv" + articleMatch.Groups[1].Value
                + "\n（官方容灾不提供专栏详情）";
        }
        throw new Exception("无法从链接中识别B站资源（支持视频/短链/专栏/番剧/直播间）");
    }

    // ---- BiliUserInfo 官方实现：wbi acc/info + relation/stat ----
    async Task<string> OfficialBiliUserInfoAsync(long uid)
    {
        Dictionary<string, string> parameters = new() { ["mid"] = uid.ToString(CultureInfo.InvariantCulture) };
        string query = await BuildWbiQueryAsync(parameters);
        JsonElement info = await BiliOfficialGetAsync(BiliApiBase + "/x/space/wbi/acc/info?" + query);
        JsonElement relation = await BiliOfficialGetAsync(BiliApiBase + "/x/relation/stat?vmid=" + uid.ToString(CultureInfo.InvariantCulture));
        var rep = new List<string> { S(info, "name") + "（" + S(info, "mid") + "）" };
        rep.Add("等级 " + S(info, "level"));
        rep.Add("粉丝 " + S(relation, "follower"));
        string sign = S(info, "sign");
        if (sign.Length > 0) rep.Add("签名：" + sign);
        if (info.TryGetProperty("official", out JsonElement official) && official.ValueKind == JsonValueKind.Object && S(official, "title").Length > 0)
        {
            rep.Add("认证：" + S(official, "title"));
        }
        return string.Join("\n", rep);
    }

    // ---- BiliUpdates 官方实现：动态接口（视频投稿，无需WBI） ----
    async Task<string> OfficialBiliUpdatesAsync(long uid)
    {
        JsonElement data = await BiliOfficialGetAsync(BiliApiBase + "/x/polymer/web-dynamic/v1/feed/space?host_mid="
            + uid.ToString(CultureInfo.InvariantCulture));
        var rep = new List<string>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (GetStringField(item, "type") != "DYNAMIC_TYPE_AV")
                {
                    continue;
                }
                string authorName = "";
                long pubTs = 0;
                string title = "";
                string bvid = "";
                if (item.TryGetProperty("modules", out JsonElement modules) && modules.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement module in modules.EnumerateArray())
                    {
                        if (module.TryGetProperty("module_author", out JsonElement author) && author.ValueKind == JsonValueKind.Object)
                        {
                            authorName = S(author, "name");
                            pubTs = GetNumericField(author, "pub_ts");
                        }
                        if (module.TryGetProperty("module_dynamic", out JsonElement dynamicModule)
                            && dynamicModule.ValueKind == JsonValueKind.Object
                            && dynamicModule.TryGetProperty("major", out JsonElement major)
                            && major.ValueKind == JsonValueKind.Object
                            && major.TryGetProperty("archive", out JsonElement archive)
                            && archive.ValueKind == JsonValueKind.Object)
                        {
                            title = S(archive, "title");
                            bvid = S(archive, "bvid");
                        }
                    }
                }
                if (bvid.Length == 0)
                {
                    continue;
                }
                string timeText = pubTs > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(pubTs).LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " "
                    : "";
                rep.Add(timeText + title + "（" + bvid + "）" + (authorName.Length > 0 ? " - " + authorName : ""));
            }
        }
        if (rep.Count == 0)
        {
            rep.Add("没有取到视频投稿（官方容灾仅支持视频投稿；动态/专栏请等第三方恢复后查询）");
        }
        return string.Join("\n", rep);
    }

    // ---- BiliHot 官方实现：popular 热门榜 + archive/related 相关推荐 ----
    async Task<string> OfficialBiliHotAsync(string? action, string? bvid, int? pn)
    {
        string normalized = (action ?? "popular").Trim().ToLowerInvariant();
        List<JsonElement> entries = new();
        if (normalized == "related")
        {
            if (string.IsNullOrWhiteSpace(bvid))
            {
                throw new Exception("相关推荐需要传 bvid");
            }
            JsonElement data = await BiliOfficialGetAsync(BiliApiBase + "/x/web-interface/archive/related?bvid="
                + Uri.EscapeDataString(bvid));
            if (data.ValueKind == JsonValueKind.Array)
            {
                entries = data.EnumerateArray().Take(10).Select(item => item.Clone()).ToList();
            }
        }
        else
        {
            JsonElement data = await BiliOfficialGetAsync(BiliApiBase + "/x/web-interface/popular?ps=20&pn="
                + Math.Max(1, pn ?? 1).ToString(CultureInfo.InvariantCulture));
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("list", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                entries = list.EnumerateArray().Take(10).Select(item => item.Clone()).ToList();
            }
        }
        var rep = new List<string>();
        int index = 0;
        foreach (JsonElement item in entries)
        {
            index++;
            string upName = item.TryGetProperty("owner", out JsonElement owner) && owner.ValueKind == JsonValueKind.Object
                ? S(owner, "name")
                : "";
            rep.Add(index + ". " + S(item, "bvid") + " - " + S(item, "title") + (upName.Length > 0 ? " - " + upName : ""));
        }
        if (rep.Count == 0)
        {
            rep.Add("没有内容");
        }
        return string.Join("\n", rep);
    }

    // ---- BiliQrLogin 官方实现：passport 扫码登录（官方网页原生接口） ----
    async Task<string> OfficialBiliQrLoginAsync(string action, string? qrcodeKey)
    {
        string normalized = (action ?? "").Trim().ToLowerInvariant();
        if (normalized == "generate")
        {
            JsonElement data = await BiliOfficialGetAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate");
            // passport 这里回的字段是 url（实测 data 只有 url 与 qrcode_key），写成 qrcode_url 会打出空链接
            return "qrcode_key " + S(data, "qrcode_key") + "\n二维码链接（180秒内有效，打开后扫码）：\n" + S(data, "url")
                + "\n扫完后用 action=poll、qrcode_key 查结果；登录成功后 Cookie 自动存本地";
        }
        if (normalized == "poll")
        {
            if (string.IsNullOrWhiteSpace(qrcodeKey))
            {
                throw new Exception("poll 需要 qrcode_key");
            }
            using HttpRequestMessage request = new(HttpMethod.Get,
                "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(qrcodeKey));
            using HttpResponseMessage response = await http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            using JsonDocument parsed = JsonDocument.Parse(body);
            JsonElement data = parsed.RootElement.GetProperty("data").Clone();
            long innerCode = GetNumericField(data, "code");
            if (innerCode == 0)
            {
                List<string> cookies = new();
                if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookieHeaders))
                {
                    foreach (string cookieHeader in setCookieHeaders)
                    {
                        cookies.Add(cookieHeader.Split(';')[0].Trim());
                    }
                }
                // 成功时 Cookie 也可能只拼在 data.url 的查询串里，两种下发形态都收，
                // 只认一种就会出现「说登录成功了，其实本地存了个空 Cookie」
                foreach (string urlPair in LoginCookiePairsFromUrl(S(data, "url")))
                {
                    if (cookies.Contains(urlPair) == false)
                    {
                        cookies.Add(urlPair);
                    }
                }
                if (cookies.Count == 0)
                {
                    return "B站回扫码成功，但响应里既没有 Set-Cookie 也没带 Cookie 的跳转链接，Cookie 没有保存。"
                        + "把这行发给开发者，用来定位是接口改了哪个字段";
                }
                SaveBiliCookie(string.Join("; ", cookies));
                return "登录成功，Cookie 已保存到本地（官方接口将自动携带）。可用 BiliCookie check 验证。";
            }
            if (innerCode == 86090)
            {
                return "已扫描，等待确认";
            }
            if (innerCode == 86038)
            {
                return "二维码已过期，请重新 generate";
            }
            if (innerCode == 86101)
            {
                return "未扫描";
            }
            return S(data, "message").Length > 0 ? S(data, "message") : "状态码 " + innerCode;
        }
        throw new Exception("action 仅支持 generate/poll");
    }

    // 扫码成功时 B 站也可能把 Cookie 拼在 data.url 的查询串里下发，只取登录相关这几对
    static List<string> LoginCookiePairsFromUrl(string url)
    {
        List<string> pairs = new();
        int mark = url.IndexOf('?');
        if (mark < 0)
        {
            return pairs;
        }
        foreach (string item in url[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = item.Split('=', 2);
            if (parts.Length == 2
                && parts[0] is "SESSDATA" or "bili_jct" or "DedeUserID" or "DedeUserID__ckMd5" or "sid")
            {
                pairs.Add(parts[0] + "=" + Uri.UnescapeDataString(parts[1]));
            }
        }
        return pairs;
    }

    // ---- BiliCookie 官方实现：本地Cookie管理 ----
    async Task<string> OfficialBiliCookieAsync(string? action, string? cookieString)
    {
        string normalized = (action ?? "list").Trim().ToLowerInvariant();
        string path = BiliCookieFilePath();
        switch (normalized)
        {
            case "list":
            {
                string cookie = LoadBiliCookie();
                return cookie.Length == 0
                    ? "本地未保存B站Cookie（可用 BiliQrLogin 扫码生成，或 BiliCookie save 导入）"
                    : "本地Cookie存在（" + cookie.Length + " 字符，不回显内容），保存于 " + path;
            }
            case "check":
            {
                string cookie = LoadBiliCookie();
                if (cookie.Length == 0)
                {
                    return "本地未保存B站Cookie";
                }
                try
                {
                    JsonElement nav = await BiliOfficialGetAsync(BiliApiBase + "/x/web-interface/nav");
                    bool isLogin = nav.TryGetProperty("isLogin", out JsonElement loginElement) && loginElement.ValueKind == JsonValueKind.True;
                    string uname = S(nav, "uname");
                    return isLogin ? "本地Cookie登录态有效：" + uname : "本地Cookie已失效，请重新扫码登录";
                }
                catch (Exception checkError)
                {
                    return "Cookie有效性验证失败：" + checkError.Message;
                }
            }
            case "delete":
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return "本地Cookie已删除";
            case "save":
                if (string.IsNullOrWhiteSpace(cookieString))
                {
                    throw new Exception("save 需要提供 cookieString");
                }
                SaveBiliCookie(cookieString);
                return "Cookie已保存到本地";
            default:
                return "不支持的action：" + normalized + "（可用 list/check/delete/save）";
        }
    }

    // ---- 搜索官方容灾：平台公开接口 ----
    async Task<List<(string Id, string Name, string Singer)>> OfficialSearchAsync(string platform, string keyword)
    {
        List<(string, string, string)> results = new();
        if (platform == "163")
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "https://music.163.com/api/search/get");
            request.Headers.Referrer = new Uri("https://music.163.com/");
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = keyword,
                ["type"] = "1",
                ["limit"] = "5"
            });
            string body = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement songs = doc.RootElement.GetProperty("result").GetProperty("songs").Clone();
            foreach (JsonElement song in songs.EnumerateArray())
            {
                string singer = "";
                if (song.TryGetProperty("artists", out JsonElement artists) && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
                {
                    singer = artists[0].GetProperty("name").GetString() ?? "";
                }
                results.Add((GetNumericField(song, "id").ToString(CultureInfo.InvariantCulture), GetStringField(song, "name"), singer));
            }
        }
        else if (platform == "qq")
        {
            using HttpRequestMessage request = new(HttpMethod.Get,
                "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg?format=json&key=" + Uri.EscapeDataString(keyword));
            request.Headers.Referrer = new Uri("https://y.qq.com/");
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            string body = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement items = doc.RootElement.GetProperty("data").GetProperty("song").GetProperty("itemlist").Clone();
            foreach (JsonElement item in items.EnumerateArray())
            {
                results.Add((GetStringField(item, "songmid"), GetStringField(item, "songname"), GetStringField(item, "singer")));
            }
        }
        else if (platform == "kugou")
        {
            using HttpRequestMessage request = new(HttpMethod.Get,
                "https://mobilecdn.kugou.com/api/v3/search/song?format=json&pagesize=5&page=1&keyword=" + Uri.EscapeDataString(keyword));
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            string body = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement infoList = doc.RootElement.GetProperty("data").GetProperty("info").Clone();
            foreach (JsonElement item in infoList.EnumerateArray())
            {
                results.Add((GetStringField(item, "hash"), GetStringField(item, "songname"), GetStringField(item, "singername")));
            }
        }
        else
        {
            throw new Exception("官方搜索不支持该平台：" + platform);
        }
        return results;
    }

    // ---- 音乐卡官方容灾：构造 OneBot 标准音乐消息段 ----
    async Task<JsonObject> BuildOfficialMusicSegmentAsync(string platform, string id, string? title)
    {
        string normalized = (platform ?? "163").Trim().ToLowerInvariant();
        if (normalized == "netease")
        {
            normalized = "163";
        }
        if (normalized == "163" || normalized == "qq")
        {
            // 官方模板类型：协议端自己生成卡片
            string songKey = id.Trim();
            bool idValid = normalized == "163" ? songKey.All(char.IsDigit) : songKey.Length == 14;
            if (idValid == false)
            {
                List<(string Id, string Name, string Singer)> hits = await OfficialSearchAsync(normalized, id);
                if (hits.Count == 0)
                {
                    throw new Exception("官方搜索没找到歌曲：" + id);
                }
                songKey = hits[0].Id;
            }
            return new JsonObject
            {
                ["type"] = "music",
                ["data"] = new JsonObject
                {
                    ["type"] = normalized,
                    ["id"] = songKey
                }
            };
        }

        // bilibili / kugou：custom 音乐段（官方无模板类型）
        Dictionary<string, string> info = await ResolveInfoAsync(normalized, id, title);
        string audio = "";
        if (normalized == "bilibili")
        {
            try
            {
                Match bvidMatch = Regex.Match(info.GetValueOrDefault("jump") ?? "", "BV[0-9A-Za-z]{10}");
                if (bvidMatch.Success)
                {
                    string viewQuery = await BuildWbiQueryAsync(new Dictionary<string, string> { ["bvid"] = bvidMatch.Value });
                    JsonElement view = await BiliOfficialGetAsync(BiliApiBase + "/x/web-interface/wbi/view?" + viewQuery);
                    long cid = GetNumericField(view, "cid");
                    JsonElement play = await BiliOfficialGetAsync(BiliApiBase + "/x/player/playurl?bvid=" + bvidMatch.Value
                        + "&cid=" + cid + "&qn=64&fnval=16");
                    if (play.TryGetProperty("dash", out JsonElement dash)
                        && dash.TryGetProperty("audio", out JsonElement dashAudio)
                        && dashAudio.ValueKind == JsonValueKind.Array && dashAudio.GetArrayLength() > 0)
                    {
                        audio = dashAudio[0].TryGetProperty("base_url", out JsonElement audioUrl) ? audioUrl.GetString() ?? "" : "";
                    }
                    else if (play.TryGetProperty("durl", out JsonElement playDurl) && playDurl.ValueKind == JsonValueKind.Array && playDurl.GetArrayLength() > 0)
                    {
                        audio = playDurl[0].TryGetProperty("url", out JsonElement playUrl) ? playUrl.GetString() ?? "" : "";
                    }
                }
            }
            catch
            {
                // 直链失败则降级为不可播放卡片
            }
        }
        else
        {
            // kugou 官方无公开直链：降级为跳转卡（audio 指向页面）
            audio = info.GetValueOrDefault("url") ?? "";
        }
        // SnowLuma 校验 custom 音乐段必须有非空 url/audio/title，拿不到播放直链时退回跳转地址，
        // 否则整条消息会被 1400 原子拒绝（NapCat 与 LLOneBot 容忍空 audio）
        string jumpUrl = info.GetValueOrDefault("jump") ?? "";
        if (audio.Length == 0)
        {
            audio = jumpUrl;
        }
        return new JsonObject
        {
            ["type"] = "music",
            ["data"] = new JsonObject
            {
                ["type"] = "custom",
                ["url"] = jumpUrl,
                ["audio"] = audio,
                ["title"] = info.GetValueOrDefault("song") ?? "未知",
                ["content"] = info.GetValueOrDefault("singer") ?? "",
                ["image"] = info.GetValueOrDefault("cover") ?? ""
            }
        };
    }

    static string FormatSeconds(long totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "N/A";
        }
        return (totalSeconds / 60).ToString(CultureInfo.InvariantCulture) + ":"
            + (totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    class PageMeta { public string title = ""; public string desc = ""; public string image = ""; }

    async Task<PageMeta> FetchMetaAsync(string url)
    {
        var m = new PageMeta();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            // 页面抓取只给8秒：国外站连不上时别拖满20秒全局超时
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            string html = await (await http.SendAsync(req, cts.Token)).Content.ReadAsStringAsync(cts.Token);
            m.title = Meta(html, "og:title") ?? Meta(html, "twitter:title") ?? "";
            m.desc = Meta(html, "og:description") ?? Meta(html, "description") ?? "";
            m.image = Meta(html, "og:image") ?? Meta(html, "twitter:image") ?? "";
            if (string.IsNullOrWhiteSpace(m.image))
            {
                Match lm = Regex.Match(html, "<link[^>]+rel=[\"'][^\"']*icon[^\"']*[\"'][^>]*?href=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
                if (!lm.Success) lm = Regex.Match(html, "<link[^>]+href=[\"']([^\"']+)[\"'][^>]*?rel=[\"'][^\"']*icon[^\"']*[\"']", RegexOptions.IgnoreCase);
                if (lm.Success)
                {
                    try { m.image = new Uri(new Uri(url), lm.Groups[1].Value).ToString(); } catch { }
                }
            }
            if (string.IsNullOrWhiteSpace(m.title))
                m.title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        }
        catch (Exception ex) { logger.LogWarning("抓网页meta失败：" + ex.Message); }
        // 页面抓不到（超时/被墙）也必须给非空预览：签名端空值当缺参，直接用站点favicon
        if (string.IsNullOrWhiteSpace(m.image))
        {
            try { m.image = new Uri(new Uri(url), "/favicon.ico").ToString(); } catch { }
        }
        return m;
    }

    static string Meta(string html, string prop)
    {
        var p = Regex.Escape(prop);
        var a = Regex.Match(html, @"<meta[^>]+(?:property|name)=[""']" + p + @"[""'][^>]+content=[""']([^""']*)", RegexOptions.IgnoreCase);
        if (a.Success) return a.Groups[1].Value;
        var b = Regex.Match(html, @"<meta[^>]+content=[""']([^""']*)[""'][^>]+(?:property|name)=[""']" + p + @"[""']", RegexOptions.IgnoreCase);
        return b.Success ? b.Groups[1].Value : "";
    }

    // 传歌名不是ID时统一走搜索接口拿第一条，不再各平台手写一遍自带API
    async Task<(string id, string title, string author, string cover)> SearchFirstAsync(string pf, string kw)
    {
        string keyQ = KeyQ();
        string u = WithQuery(SearchUrl(pf), keyQ + "msg=" + Uri.EscapeDataString(kw));
        string body = await http.GetStringAsync(u);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.GetProperty("code").GetInt32() != 200)
            throw new Exception("搜索失败");
        var d = doc.RootElement.GetProperty("data")[0];
        string G(string k) => d.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";
        return (G("id"), G("title"), G("author"), G("cover"));
    }

    async Task<Dictionary<string, string>> ResolveInfoAsync(string platform, string id, string? title)
    {
        var info = new Dictionary<string, string>();
        platform = (platform ?? "163").Trim().ToLowerInvariant();
        if (platform == "163")
        {
            try
            {
                if (!id.All(char.IsDigit))
                {
                    var r = await SearchFirstAsync("163", id);
                    id = r.id;
                    if (string.IsNullOrWhiteSpace(title)) title = r.title;
                }
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://music.163.com/api/song/detail/?ids=[" + id + "]");
                req.Headers.Referrer = new Uri("https://music.163.com/");
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
                string body = await (await http.SendAsync(req)).Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var s = doc.RootElement.GetProperty("songs")[0];
                info["song"] = s.GetProperty("name").GetString() ?? "未知歌曲";
                info["singer"] = string.Join("/", s.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
                info["cover"] = s.TryGetProperty("album", out var alb) && alb.TryGetProperty("picUrl", out var pu) ? pu.GetString() ?? "" : "";
            }
            catch { info["song"] = "未知歌曲"; info["singer"] = "未知"; info["cover"] = ""; }
            info["format"] = "netease";
            info["url"] = "http://music.163.com/song/media/outer/url?id=" + id + ".mp3";
            info["jump"] = "http://music.163.com/song?id=" + id;
        }
        else if (platform == "bilibili")
        {
            try
            {
                if (!id.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
                {
                    var hits = await BiliSearchAsync(id);
                    string? pick = null;
                    var words = id.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var (bv, vt) in hits)
                    {
                        if (words.Length > 0 && words.All(w => vt.Contains(w))) { pick = bv; break; }
                    }
                    if (pick == null && hits.Count > 0) pick = hits[0].bv;
                    if (pick == null) throw new Exception("没搜到匹配的视频");
                    id = pick;
                }
                string viewQuery = await BuildWbiQueryAsync(new Dictionary<string, string> { ["bvid"] = id });
                JsonElement dd = await BiliOfficialGetAsync(BiliApiBase + "/x/web-interface/wbi/view?" + viewQuery);
                string pic = GetStringField(dd, "pic");
                if (pic.StartsWith("//")) pic = "https:" + pic;
                info["song"] = GetStringField(dd, "title") is { Length: > 0 } viewTitle ? viewTitle : "未知视频";
                info["singer"] = dd.TryGetProperty("owner", out JsonElement owner) ? GetStringField(owner, "name") : "bilibili";
                info["cover"] = pic;
            }
            catch { info["song"] = "未知视频"; info["singer"] = "bilibili"; info["cover"] = ""; }
            info["url"] = "https://www.bilibili.com/video/" + id;
            info["jump"] = "https://www.bilibili.com/video/" + id;
            info["format"] = "bilibili";
        }
        else if (platform == "qq")
        {
            try
            {
                if (id.Length != 14)
                {
                    var r = await SearchFirstAsync("qq", id);
                    if (!string.IsNullOrWhiteSpace(r.id)) id = r.id;
                    if (string.IsNullOrWhiteSpace(title)) title = r.title;
                }
                string u = "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg?songmid=" + id + "&platform=yqq&format=json&inCharset=utf8&outCharset=utf-8";
                using var req = new HttpRequestMessage(HttpMethod.Get, u);
                req.Headers.Referrer = new Uri("https://y.qq.com/");
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
                string body = await (await http.SendAsync(req)).Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var dd = doc.RootElement.GetProperty("data")[0];
                string albid = dd.GetProperty("album").GetProperty("mid").GetString() ?? "";
                info["song"] = dd.GetProperty("title").GetString() ?? "未知歌曲";
                info["singer"] = string.Join("/", dd.GetProperty("singer").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
                info["cover"] = "https://y.qq.com/music/photo_new/T002R300x300M000" + albid + ".jpg";
            }
            catch { info["song"] = "未知歌曲"; info["singer"] = "未知"; info["cover"] = ""; }
            info["url"] = "https://y.qq.com/n/ryqq/songDetail/" + id;
            info["jump"] = "https://y.qq.com/n/ryqq/songDetail/" + id;
            info["format"] = "qq";
        }
        else if (platform == "kugou")
        {
            // 酷狗：列表模式搜索没有 cover，且签名端要求 url 为播放地址；
            // 统一先拿到 hash（传歌名先搜），再用 hash 走详情接口（id 参数）拿直链/封面/播放页
            if (Regex.IsMatch(id, "^[0-9A-Fa-f]{32}$") == false)
            {
                var r = await SearchFirstAsync("kugou", id);
                if (!string.IsNullOrWhiteSpace(r.id)) id = r.id;
            }
            string songName = "酷狗音乐", singerName = "酷狗音乐", kugouCover = "", playUrl = "";
            string pageUrl = "https://www.kugou.com/song/#hash=" + id;
            try
            {
                string du = WithQuery(SearchUrl("kugou"), KeyQ() + "id=" + Uri.EscapeDataString(id));
                string dbody = await http.GetStringAsync(du);
                using var ddoc = JsonDocument.Parse(dbody);
                if (ddoc.RootElement.GetProperty("code").GetInt32() == 200)
                {
                    var dd = ddoc.RootElement.GetProperty("data");
                    string D(string k) => dd.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    if (D("title").Length > 0) songName = D("title");
                    if (D("author").Length > 0) singerName = D("author");
                    if (D("cover").Length > 0) kugouCover = D("cover").Replace("{size}", "480");
                    if (D("url").Length > 0) playUrl = D("url");
                    if (D("link").Length > 0) pageUrl = D("link");
                }
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(title)) songName = title;
            info["song"] = songName;
            info["singer"] = singerName;
            info["cover"] = kugouCover;
            info["url"] = playUrl.Length > 0 ? playUrl : pageUrl;
            info["jump"] = pageUrl;
            info["format"] = "kugou";
        }
        else throw new Exception("不支持的平台：" + platform);

        if (!string.IsNullOrEmpty(title) && platform != "kugou" && platform != "qq" && platform != "bilibili") info["song"] = title;
        return info;
    }

    async Task<string> SignAsync(string platform, string id, string? title)
    {
        var info = await ResolveInfoAsync(platform, id, title);
        // 签名端所有参数必填且空值当缺失：封面为空时回落抓播放页的 og:image
        if (string.IsNullOrWhiteSpace(info.GetValueOrDefault("cover")))
        {
            var meta = await FetchMetaAsync(info.GetValueOrDefault("jump") ?? "");
            if (!string.IsNullOrWhiteSpace(meta.image)) info["cover"] = meta.image;
        }
        if (string.IsNullOrWhiteSpace(info.GetValueOrDefault("song"))) info["song"] = "未知";
        if (string.IsNullOrWhiteSpace(info.GetValueOrDefault("singer"))) info["singer"] = "未知";
        if (!string.IsNullOrWhiteSpace(Configuration.ApiKey)) info["key"] = Configuration.ApiKey;

        for (int attempt = 0; ; attempt++)
        {
            string query = string.Join("&", info.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")));
            string fullUrl = WithQuery(MusicArkUrl(), query);
            string body = await http.GetStringAsync(fullUrl);
            using var doc = JsonDocument.Parse(body);
            int code = doc.RootElement.GetProperty("code").GetInt32();
            if (code == 200)
            {
                JsonNode? node = JsonNode.Parse(doc.RootElement.GetProperty("data").GetRawText());
                string songName = info["song"];
                try { songName = node!["meta"]!["music"]!["title"]!.GetValue<string>(); } catch { }
                node!["prompt"] = "[分享]" + songName;
                node!["ver"] = "0.0.0.1";
                node!["view"] = "music";
                return node.ToJsonString();
            }
            string msg = doc.RootElement.TryGetProperty("msg", out var mm) ? mm.GetString() ?? "" : "";
            // 缓存的接口后缀失效（服务端报 API 不存在）：清掉重探，再试一次
            if (attempt == 0 && msg.Contains("不存在"))
            {
                _sfx.Clear();
                await EnsureFit();
                continue;
            }
            throw new Exception("签名失败：" + msg + " | 请求=" + MaskKey(fullUrl));
        }
    }

    static string providerName = "";
    static bool providerResolved;

    async Task<(bool Success, string Message)> SendJsonAsync(long target, bool isGroup, string cardJson)
    {
        JsonObject jsonSegment = new()
        {
            ["type"] = "json",
            ["data"] = new JsonObject { ["data"] = cardJson }
        };
        JsonArray segments = new();
        segments.Add(jsonSegment);
        return await SendSegmentsAsync(target, isGroup, segments);
    }

    string ProviderLabel() => providerName.Length > 0 ? providerName : "协议端";

    // 三端都回 app_name（NapCat.Onebot / LLOneBot / SnowLuma），报错风格不同所以失败信息要带署名
    async Task ResolveProviderAsync()
    {
        // 共享连接可能比本模块晚就绪：没连上就先不标记，等下一次调用再识别
        if (providerResolved || qChatService.Client is not { IsConnected: true })
        {
            return;
        }
        providerResolved = true;
        try
        {
            (int retcode, string message, JsonElement data) = await SendActionAsync("get_version_info", new JsonObject());
            if (retcode != 0)
            {
                throw new Exception(message);
            }
            providerName = (GetStringField(data, "app_name") + " " + GetStringField(data, "name")
                + " " + GetStringField(data, "vendor")).Trim();
            logger.LogInformation("[ShareArk] 协议端：{Provider} {Version}",
                providerName.Length > 0 ? providerName : "未识别", GetStringField(data, "app_version"));
        }
        catch (Exception versionError)
        {
            logger.LogInformation("[ShareArk] 未能识别协议端：{Message}", versionError.Message);
        }
    }

    // 发一段消息。三端对不认识的消息段处理方式完全不同：
    // NapCat 抛"未知的消息类型"回 1200、SnowLuma 整条原子拒绝回 1400、
    // LLOneBot 直接丢掉该段但照发其余内容并回 retcode 0——所以成功也必须验 message_id。
    async Task<(bool Success, string Message)> SendSegmentsAsync(long target, bool isGroup, JsonArray segments)
    {
        await ResolveProviderAsync();
        string action = isGroup ? "send_group_msg" : "send_private_msg";
        string idKey = isGroup ? "group_id" : "user_id";
        JsonObject parameters = new()
        {
            [idKey] = target,
            ["message"] = segments
        };
        try
        {
            (int retcode, string message, JsonElement data) = await SendActionAsync(action, parameters);
            if (retcode != 0)
            {
                return (false, ProviderLabel() + " 返回 " + retcode + "：" + message + FailureHint(retcode));
            }
            // 三端成功都会带 message_id；回成功却没有它，说明这一段被静默丢弃了
            if (data.ValueKind != JsonValueKind.Object || data.TryGetProperty("message_id", out _) == false)
            {
                return (false, ProviderLabel() + " 回成功但没给 message_id，卡片段很可能被静默丢弃"
                    + "（LLOneBot 对不认识的消息段不报错直接丢；音乐卡片还需在 LLOneBot 里配置签名地址）");
            }
            return (true, "");
        }
        catch (Exception sendError)
        {
            return (false, sendError.Message);
        }
    }

    string FailureHint(int retcode) => retcode switch
    {
        1404 => "（" + ProviderLabel() + " 未实现该接口）",
        // 1400=SnowLuma 的 UNKNOWN_TYPE/BAD_REQUEST，1200=NapCat 的未知消息类型：都是整条被拒
        1400 or 1200 => "（" + ProviderLabel() + " 不认识这个消息段类型，整条消息已退回；卡片段用 json 三端都认）",
        _ => ""
    };

    async Task<(int Retcode, string Message, JsonElement Data)> SendActionAsync(string action, JsonObject parameters)
    {
        // 复用 QQ聊天 的同一条 OneBot 连接
        OneBotClient? client = qChatService.Client;
        if (client is not { IsConnected: true })
        {
            throw new InvalidOperationException(QQSuiteTransport.NotConnectedMessage);
        }
        JsonElement root = await client.CallActionRawAsync(action, parameters, 15);
        int retcode = root.TryGetProperty("retcode", out JsonElement retcodeElement)
            && retcodeElement.ValueKind == JsonValueKind.Number ? retcodeElement.GetInt32() : -1;
        string message = GetStringField(root, "message");
        // SnowLuma 的失败包只填 wording，LLOneBot/NapCat 两个键都填
        if (message.Length == 0)
        {
            message = GetStringField(root, "wording");
        }
        JsonElement data = root.TryGetProperty("data", out JsonElement dataElement)
            ? dataElement.Clone() : JsonDocument.Parse("null").RootElement.Clone();
        return (retcode, message, data);
    }

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "把网页链接或歌曲/视频做成QQ分享卡片并发送，并提供B站系列查询（解析/用户/更新/热门/登录）。",
            Explanation = "搜索用 SearchMusic / SearchBili，发卡用 SendArkCard / SendArkShareCard；B站系列：BiliParse 解析链接、BiliUserInfo 查用户、BiliUpdates 查UP主更新、BiliHot 查热门、BiliQrLogin 扫码登录、BiliCookieRefresh 刷新登录态、BiliCookie 管理登录Cookie；IpLookup 查IP归属。都需要已配置第三方基地址和ApiKey。ProbeBase 探测三个签名/搜索接口通不通，ProbeAll 列出基地址下发现的所有接口。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        // 热重载时清掉探测缓存；连接由 QQ聊天 统一管理
        _sfx.Clear();
        _discovered = false;
        providerName = "";
        providerResolved = false;
        return Task.CompletedTask;
    }
}
