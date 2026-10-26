using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using QQSuite.Core;

namespace QQSuite.JmShare;

/// <summary>JM 漫画分享配置。</summary>
public class JmShareConfig
{
    [DisplayName("HTTP 代理")]
    [Description("访问 JM 需要代理时填写，例如 http://127.0.0.1:7890；留空直连")]
    public string Proxy { get; set; } = "";

    [DisplayName("自定义域名")]
    [Description("逗号分隔的 API 域名，按填写顺序依次尝试（越靠前越优先），留空使用内置域名池（已按实测可用度排序）；填写后覆盖内置池")]
    public string Domain { get; set; } = "";

    [DisplayName("AVS 登录态")]
    [Description("访问敏感题材需要的 avs Cookie 值，从 JM 网页登录后复制；可填 avs=xxx 或只填取值，留空表示不携带")]
    public string AvsCookie { get; set; } = "";

    [DisplayName("下载目录")]
    [Description("留空使用 Storage/Plugins/presents.QQSuite/jm；填写绝对路径或相对 Storage 的路径")]
    public string DownloadDir { get; set; } = "";

    [DisplayName("允许私聊")]
    [Description("关闭后私聊请求会被拒绝；默认开启")]
    public bool AllowPrivate { get; set; } = true;

    [DisplayName("允许群号")]
    [Description("逗号分隔的群号，只有列表里的群才能使用下载功能；留空表示群聊一律拒绝")]
    public string AllowedGroups { get; set; } = "";

    [DisplayName("发送封面")]
    [Description("JmInfo / JmDownload 在发送目标时会同时发送本子封面")]
    public bool SendCover { get; set; } = true;

    [DisplayName("发送后删除文件")]
    [Description("ZIP 发送成功后删除本次下载的临时目录；关闭则保留图片与压缩包")]
    public bool DeleteAfterSend { get; set; } = true;

    [DisplayName("单本最大页数")]
    [Description("超过该页数直接拒绝下载，0 表示不限制")]
    public int MaxPages { get; set; }

    [DisplayName("压缩包最大 MB")]
    [Description("压缩包超过该大小时拒绝发送，默认 200 MB")]
    public int MaxZipMB { get; set; } = 200;

    [DisplayName("并发下载数")]
    [Description("同时下载的图片数量（1-8），过大容易被限流")]
    public int ConcurrentDownloads { get; set; } = 3;

    [DisplayName("请求超时(秒)")]
    [Description("单次 HTTP 请求超时时间（10-300 秒）")]
    public int Timeout { get; set; } = 60;

    [DisplayName("失败重试")]
    [Description("请求失败后重试次数（含换域名），默认 3 次")]
    public int Retry { get; set; } = 3;

    [DisplayName("搜索结果条数")]
    [Description("JmSearch 最多返回几条结果（1-20），默认 5")]
    public int SearchLimit { get; set; } = 5;

    [DisplayName("结果渲染成图片")]
    [Description("把搜索结果与详情渲染成图片卡片；关闭或无头浏览器不可用时改用纯文字")]
    public bool RenderAsImage { get; set; } = true;

    [DisplayName("用合并转发发送")]
    [Description("渲染出的图片改用合并转发（聊天记录卡片）发送，避免多张图连续刷屏；关闭则直接发单张图片")]
    public bool UseForward { get; set; } = true;

    [DisplayName("复用已下载章节")]
    [Description("同一章之前下载过时直接复用缓存的图片，不重复下载；关闭则每次都重新下载")]
    public bool ReuseDownloads { get; set; } = true;

    [DisplayName("定时清理下载目录")]
    [Description("每天在指定时间清理下载目录里过期的子目录，避免磁盘被占满")]
    public bool EnableAutoClean { get; set; } = true;

    [DisplayName("清理时间")]
    [Description("每天执行清理的时间，格式 HH:mm，默认 03:00")]
    public string CleanTime { get; set; } = "03:00";

    [DisplayName("保留天数")]
    [Description("下载目录里超过该天数的子目录会被清理，默认 3 天；0 表示不按时间清理")]
    public int KeepDays { get; set; } = 3;

    [DisplayName("JM 账号")]
    [Description("JM 网站用户名，与「JM 密码」一起填写后可用 JmAccount 登录；只保存在本机配置里")]
    public string Username { get; set; } = "";

    [DisplayName("JM 密码")]
    [Description("JM 网站密码；登录成功后会话保存在 Storage/Plugins/presents.QQSuite/jm/state，重启自动复用")]
    public string Password { get; set; } = "";

    [DisplayName("收藏夹 ID")]
    [Description("登录后同步远端收藏用的收藏夹 ID，默认 0 表示「我的收藏」；收藏始终会先保存在本机")]
    public string FavoriteFolder { get; set; } = "0";

    [DisplayName("输出格式")]
    [Description("zip=ZIP 压缩包（默认）；longimage=纵向拼成一张长图；pdf=PDF 文件；后两种依赖系统 Edge/Chrome，不可用时自动回退成 ZIP")]
    public string OutputFormat { get; set; } = "zip";

    [DisplayName("压缩包密码")]
    [Description("填写后 ZIP 使用传统 ZipCrypto 加密（WinRAR / 7-Zip / 手机解压 App 可打开，Windows 资源管理器不支持）；留空不加密")]
    public string ZipPassword { get; set; } = "";

    [DisplayName("启用每日配额")]
    [Description("限制每个群、每个人每天的下载次数，跨自然日自动归零；关闭则不限次数")]
    public bool EnableDailyQuota { get; set; } = true;

    [DisplayName("每群每日次数")]
    [Description("同一个群每天最多下载几次，默认 20；0 表示不限")]
    public int GroupDailyLimit { get; set; } = 20;

    [DisplayName("每人每日次数")]
    [Description("同一个人每天最多下载几次，默认 5；私聊按对方 QQ，群聊按发起人；0 表示不限")]
    public int UserDailyLimit { get; set; } = 5;

    [DisplayName("启用订阅推送")]
    [Description("定时检查已订阅漫画是否出新章节，有更新时推送到指定群 / 私聊")]
    public bool EnableSubscribe { get; set; } = true;

    [DisplayName("订阅检查间隔(分钟)")]
    [Description("每隔多少分钟检查一次订阅更新，默认 30；建议不低于 10 分钟")]
    public int SubscribeInterval { get; set; } = 30;
}

/// <summary>
/// JM 漫画：搜索、详情与封面、整本/单章下载，图片还原后打包 ZIP 发送到 QQ。
/// </summary>
public class JmShareModule(
    XmlFunctionCaller functionCaller,
    ILogger<JmShareModule> logger,
    Interactor<JmShareModule> interactor,
    QChatService qChatService
) : ChatBehaviour, IConfigurable<JmShareConfig>
{
    public JmShareConfig Configuration { get; set; } = null!;

    /// <summary>下载进度提示的阈值。</summary>
    static readonly int[] ProgressThresholds = [25, 50, 75, 100];

    /// <summary>上次执行定时清理的日期，避免同一天重复清理。</summary>
    DateTime lastCleanDate = DateTime.MinValue;

    /// <summary>每日下载配额。</summary>
    readonly JmQuotaStore quotaStore = new();

    /// <summary>本机收藏夹。</summary>
    readonly JmFavoriteStore favoriteStore = new();

    /// <summary>订阅清单。</summary>
    readonly JmSubscriptionStore subscribeStore = new();

    /// <summary>上次订阅轮询时间（用于按间隔串行检查）。</summary>
    DateTime lastSubscribeCheck = DateTime.MinValue;

    /// <summary>订阅轮询是否正在跑，避免上一轮没结束又开一轮。</summary>
    bool subscribeRunning;

    #region 对外 AI 函数

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按关键词搜索 JM 漫画，返回若干条结果的 ID、标题、作者与分类；给了群号/QQ 号时同时发送结果图片卡片")]
    public async Task<string> JmSearch(
        [Description("搜索关键词，例如 原神")] string keyword,
        [Description("页码，从 1 开始")] int page = 1,
        [Description("搜索类型：all 综合、work 作品、author 作者、tag 标签、actor 角色")] string type = "all",
        [Description("接收结果图片的群号或 QQ 号；0 表示只返回文字")] long targetId = 0,
        [Description("group 或 private；留空自动判断")] string messageType = "")
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            interactor.Poke("搜索关键词不能为空");
            return "搜索关键词不能为空";
        }

        return await RunAsync(async () =>
        {
            using JmApiClient api = CreateClient();
            JmSearchResult result = await api.SearchAsync(keyword.Trim(), page, type ?? "all", DestroyCancellationToken);

            if (result.RedirectAid > 0)
            {
                JmAlbum album = await api.GetAlbumAsync(result.RedirectAid, DestroyCancellationToken);
                return await SendAlbumCardAsync(api, album, targetId, messageType, "JM 搜索直接命中该本子")
                       ?? (FormatAlbum(album) + "\n可用 JmDownload 下载（albumId=" + album.Id + "）。");
            }

            if (result.Items.Count == 0) return "没有找到与「" + keyword.Trim() + "」相关的漫画。";

            int limit = Math.Clamp(Configuration.SearchLimit, 1, 20);
            List<JmSearchItem> picked = result.Items.Take(limit).ToList();

            StringBuilder builder = new();
            builder.AppendLine("JM 搜索「" + keyword.Trim() + "」共 " + result.Total + " 条，返回前 "
                               + picked.Count + " 条：");
            foreach (JmSearchItem item in picked)
            {
                builder.AppendLine("- ID " + item.Id + "｜" + item.Name
                                   + "｜作者 " + (string.IsNullOrWhiteSpace(item.Author) ? "未知" : item.Author)
                                   + "｜分类 " + (string.IsNullOrWhiteSpace(item.Category) ? "未知" : item.Category)
                                   + (string.IsNullOrWhiteSpace(item.SubCategory) ? "" : " / " + item.SubCategory));
            }
            builder.Append("提示：用 JmInfo 查看详情与章节，用 JmDownload 按整本或章节下载。");

            string text = builder.ToString();
            if (targetId <= 0 || !Configuration.RenderAsImage) return text;

            // 有目标且开启渲染：下载封面 → 渲染卡片 → 合并转发发送
            try
            {
                OneBotClient client = RequireClient();
                bool isGroup = await IsGroupAsync(client, targetId, messageType);
                string? refused = CheckSendPermission(isGroup, targetId);
                if (refused != null) return text + "\n" + refused;

                List<JmCardRenderer.SearchCover> covers = await DownloadSearchCoversAsync(api, picked);
                string? card = await JmCardRenderer.RenderSearchAsync(
                    keyword.Trim(), page, result.Total, covers, logger);

                if (card == null)
                {
                    // 渲染失败：退回「封面 + 文字」，保持至少一张图
                    if (Configuration.SendCover && covers[0].Cover != null)
                    {
                        await SendImageBase64Async(client, isGroup, targetId, covers[0].Cover!,
                            "JM 搜索「" + keyword.Trim() + "」");
                    }
                    return text;
                }

                await SendCardAsync(client, isGroup, targetId, card, text,
                    "JM 搜索「" + keyword.Trim() + "」", "JM 搜索");
                return text + "\n（已把搜索结果渲染成图片发送到 " + targetId + "）";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "JM 搜索结果卡片发送失败");
                return text + "\n（结果卡片发送失败：" + ex.Message + "）";
            }
        }, "JM 搜索失败");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看 JM 漫画详情：标题、作者、标签、页数与章节列表（含章节 ID），可选把封面发送到 QQ")]
    public async Task<string> JmInfo(
        [Description("漫画/本子 ID（albumId）")] long albumId,
        [Description("接收封面的群号或 QQ 号；0 表示不发送")] long targetId = 0,
        [Description("group 或 private；留空自动判断")] string messageType = "")
    {
        if (albumId <= 0)
        {
            interactor.Poke("漫画 ID 不正确");
            return "漫画 ID 不正确";
        }

        return await RunAsync(async () =>
        {
            using JmApiClient api = CreateClient();
            JmAlbum album = await api.GetAlbumAsync(albumId, DestroyCancellationToken);
            string text = FormatAlbum(album);

            string? sent = await SendAlbumCardAsync(api, album, targetId, messageType, null);
            return sent ?? text;
        }, "JM 详情获取失败");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("下载 JM 漫画（整本或指定章节），还原图片后按「输出格式」打包（ZIP / 长图 / PDF）发送到 QQ 群聊或私聊，返回标题、页数与文件路径；受每日配额限制")]
    public async Task<string> JmDownload(
        [Description("漫画/本子 ID（albumId）")] long albumId,
        [Description("章节：留空表示整本；也可填章节序号（从 1 开始）、章节 ID 或章节名的一部分")] string chapter = "",
        [Description("目标群号或对方 QQ 号；留空表示发给当前对话——私聊就发给正在说话的人，群里就发到当前群，不需要再向主人要号")] long targetId = 0,
        [Description("group 或 private，留空时按目标自动判断")] string messageType = "")
    {
        if (albumId <= 0)
        {
            interactor.Poke("漫画 ID 不正确");
            return "漫画 ID 不正确";
        }

        // 主人说「发给我」时 AI 拿不到自己的 QQ 号，只会反问群号；这里直接落到当前对话
        if (targetId <= 0)
        {
            (long id, bool currentIsGroup) = ResolveCurrentTarget();
            if (id <= 0)
            {
                interactor.Poke("不确定要发到哪里");
                return "无法确定发送目标：请给出群号或对方 QQ 号，或在对话里直接说「发给我」。";
            }

            targetId = id;
            if (string.IsNullOrWhiteSpace(messageType)) messageType = currentIsGroup ? "group" : "private";
        }

        return await RunAsync(
            () => DownloadCoreAsync(albumId, chapter, targetId, messageType, skipQuota: false),
            "JM 下载失败");
    }

    /// <summary>
    /// 下载主流程：权限 → 取章节 → 页数校验 → 每日配额 → 还原图片 → 按输出格式打包 → 发送。
    /// skipQuota 供订阅自动下载使用（系统行为不吃用户配额）。
    /// </summary>
    async Task<string> DownloadCoreAsync(long albumId, string chapter, long targetId, string messageType, bool skipQuota)
    {
        {
            OneBotClient client = RequireClient();
            bool isGroup = await IsGroupAsync(client, targetId, messageType);
            if (isGroup && !GroupAllowed(targetId))
            {
                return "未授权：群 " + targetId + " 不在允许群号列表里，请先在 JM漫画 设置里填写「允许群号」。";
            }
            if (!isGroup && !Configuration.AllowPrivate)
            {
                return "未授权：当前配置不允许私聊下载。";
            }

            using JmApiClient api = CreateClient();
            JmAlbum album = await api.GetAlbumAsync(albumId, DestroyCancellationToken);

            List<JmChapter> selected;
            if (string.IsNullOrWhiteSpace(chapter))
            {
                selected = album.Chapters.ToList();
            }
            else
            {
                JmChapter? match = MatchChapter(album.Chapters, chapter);
                if (match == null)
                {
                    return "没有匹配到章节「" + chapter.Trim() + "」。可用章节：\n" + FormatChapterList(album.Chapters);
                }
                selected = [match];
            }
            if (selected.Count == 0) return "这本漫画没有可下载的章节。";

            // 先取各章图片列表，用于页数限制与下载
            List<(JmChapter Chapter, List<string> Images)> chapters = new();
            int totalPages = 0;
            foreach (JmChapter item in selected)
            {
                List<string> images = await api.GetChapterImagesAsync(item.Id, DestroyCancellationToken);
                if (images.Count == 0) return "章节「" + item.Name + "」没有图片（可能已下架）。";
                chapters.Add((item, images));
                totalPages += images.Count;
            }

            int maxPages = Configuration.MaxPages;
            if (maxPages > 0 && totalPages > maxPages)
            {
                return "页数超过限制：本次共 " + totalPages + " 页，上限 " + maxPages + " 页，已拒绝下载。";
            }

            // 每日配额：确认真要下载了才计入（页数超限不消耗次数）
            if (Configuration.EnableDailyQuota && !skipQuota)
            {
                string? groupKey = isGroup ? "g:" + targetId : null;
                long requester = TryGetRequesterId();
                long userKeyId = requester > 0 ? requester : (isGroup ? 0 : targetId);
                string? userKey = userKeyId > 0 ? "u:" + userKeyId : null;

                JmQuotaStore.QuotaResult quota = quotaStore.TryConsume(
                    groupKey, userKey, Configuration.GroupDailyLimit, Configuration.UserDailyLimit);
                if (!quota.Allowed)
                {
                    interactor.Poke("今日下载次数已用完");
                    return quota.Message;
                }
            }

            string workDir = Path.Combine(DownloadRoot(),
                "jm_" + album.Id + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(workDir);

            int downloaded = 0;
            int unrestored = 0;
            int reused = 0;
            int progressIndex = 0;
            object progressLock = new();

            try
            {
                string chapterText = string.IsNullOrWhiteSpace(chapter)
                    ? (chapters.Count > 1 ? "整本（" + chapters.Count + " 章）" : "整本")
                    : "章节 " + chapters[0].Chapter.Name;

                int concurrency = Math.Clamp(Configuration.ConcurrentDownloads, 1, 8);

                foreach ((JmChapter current, List<string> images) in chapters)
                {
                    // 文件名用「本子在系列中的绝对章序 + 页码」，这样单章下载与整本下载复用同一份缓存
                    int order = album.Chapters.FindIndex(item => item.Id == current.Id) + 1;
                    if (order <= 0) order = 1;
                    string chapterTag = order.ToString("D2", CultureInfo.InvariantCulture);
                    string cacheDir = CacheChapterDir(album.Id, current.Id);

                    // C：命中缓存则直接拷贝图片，跳过下载
                    if (Configuration.ReuseDownloads &&
                        TryCopyFromCache(cacheDir, images.Count, chapterTag, workDir, out int cachedFiles))
                    {
                        reused += cachedFiles;
                        int cachedDone = Interlocked.Add(ref downloaded, cachedFiles);
                        string? cachedNotice = null;
                        lock (progressLock)
                        {
                            while (progressIndex < ProgressThresholds.Length &&
                                   cachedDone * 100 >= ProgressThresholds[progressIndex] * totalPages)
                            {
                                cachedNotice = "JM《" + album.Name + "》下载进度 " +
                                               ProgressThresholds[progressIndex] + "%";
                                progressIndex++;
                            }
                        }
                        if (cachedNotice != null)
                            await SendTextSafeAsync(client, isGroup, targetId, cachedNotice);
                        continue;
                    }

                    // 详情里直接给了 scramble_id 就不必再请求一次；否则按章节取
                    long scrambleId = album.ScrambleId > 0
                        ? album.ScrambleId
                        : await api.GetScrambleIdAsync(current.Id, DestroyCancellationToken);

                    using SemaphoreSlim gate = new(concurrency);
                    List<Task> jobs = new();
                    List<string> savedFiles = new();
                    object savedLock = new();

                    for (int i = 0; i < images.Count; i++)
                    {
                        int pageIndex = i;
                        string remoteName = images[i];
                        jobs.Add(Task.Run(async () =>
                        {
                            await gate.WaitAsync(DestroyCancellationToken);
                            try
                            {
                                byte[] data = await api.DownloadPageAsync(current.Id, remoteName, DestroyCancellationToken);
                                string fileBase = Path.GetFileNameWithoutExtension(remoteName);
                                string extension = Path.GetExtension(remoteName);
                                string targetBase = Path.Combine(workDir,
                                    chapterTag + "_" +
                                    (pageIndex + 1).ToString("D4", CultureInfo.InvariantCulture));

                                string saved = await JmImageTool.SaveRestoredAsync(data, targetBase, extension,
                                    current.Id, fileBase, scrambleId);
                                if (!saved.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                                {
                                    Interlocked.Increment(ref unrestored);
                                }
                                lock (savedLock) savedFiles.Add(saved);

                                int done = Interlocked.Increment(ref downloaded);
                                string? notice = null;
                                lock (progressLock)
                                {
                                    while (progressIndex < ProgressThresholds.Length &&
                                           done * 100 >= ProgressThresholds[progressIndex] * totalPages)
                                    {
                                        notice = "JM《" + album.Name + "》下载进度 " +
                                                 ProgressThresholds[progressIndex] + "%";
                                        progressIndex++;
                                    }
                                }
                                if (notice != null) await SendTextSafeAsync(client, isGroup, targetId, notice);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }, DestroyCancellationToken));
                    }
                    await Task.WhenAll(jobs);

                    if (Configuration.ReuseDownloads) SaveChapterCache(cacheDir, savedFiles);
                }

                // 输出格式：zip / longimage / pdf；长图与 PDF 失败时自动回退成 zip
                string baseName = SanitizeFileName("JM_" + album.Id + "_" + album.Name);
                string format = NormalizeOutputFormat(Configuration.OutputFormat);
                List<(string Path, string Name)> artifacts = new();
                string formatNote = "";

                if (format == "longimage")
                {
                    List<string> parts = JmExporter.BuildLongImages(
                        JmPacker.ListPackableFiles(workDir, ""), Path.Combine(workDir, baseName), logger);
                    foreach (string part in parts) artifacts.Add((part, Path.GetFileName(part)));
                    if (artifacts.Count == 0)
                    {
                        logger.LogWarning("JM 长图拼接失败，回退成 ZIP：{Album}", album.Id);
                        format = "zip";
                        formatNote = "\n（长图拼接失败，已回退成 ZIP）";
                    }
                }
                else if (format == "pdf")
                {
                    string pdfPath = Path.Combine(workDir, baseName + ".pdf");
                    bool ok = await JmExporter.BuildPdfAsync(
                        JmPacker.ListPackableFiles(workDir, ""), pdfPath, logger, DestroyCancellationToken);
                    if (ok) artifacts.Add((pdfPath, Path.GetFileName(pdfPath)));
                    else
                    {
                        logger.LogWarning("JM PDF 导出失败，回退成 ZIP：{Album}", album.Id);
                        format = "zip";
                        formatNote = "\n（PDF 导出失败，已回退成 ZIP）";
                    }
                }

                if (format == "zip")
                {
                    string zipName = baseName + ".zip";
                    string zipPath = Path.Combine(workDir, zipName);
                    JmPacker.PackDirectory(workDir, zipPath, Configuration.ZipPassword);
                    artifacts.Clear();
                    artifacts.Add((zipPath, zipName));
                    if (!string.IsNullOrEmpty(Configuration.ZipPassword))
                    {
                        formatNote += "\n（ZIP 已用「" + Configuration.ZipPassword + "」加密，请用 WinRAR / 7-Zip"
                                      + " 或手机解压 App 打开；Windows 资源管理器不支持加密 ZIP）";
                    }
                }

                long totalBytes = artifacts.Sum(item => new FileInfo(item.Path).Length);
                long maxBytes = Math.Max(0, Configuration.MaxZipMB) * 1024L * 1024L;
                if (maxBytes > 0 && totalBytes > maxBytes)
                {
                    TryDeleteDirectory(workDir);
                    return "输出文件超过限制：" + FormatSize(totalBytes) + " > " + Configuration.MaxZipMB
                           + " MB，已取消发送。";
                }

                string formatLabel = format == "longimage" ? "长图" : (format == "pdf" ? "PDF" : "压缩包");
                await SendTextSafeAsync(client, isGroup, targetId,
                    "JM《" + album.Name + "》" + chapterText + " 已下载完成，共 " + totalPages
                    + " 页（" + FormatSize(totalBytes) + "），正在发送" + formatLabel + "…");

                // 详情卡片：合并转发优先，失败降级成单张图片
                if (Configuration.SendCover)
                {
                    try
                    {
                        await SendAlbumDetailAsync(api, client, album, isGroup, targetId);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        logger.LogWarning(ex, "JM 详情卡片发送失败");
                    }
                }

                await SendArtifactsAsync(client, isGroup, targetId, artifacts, format);

                if (Configuration.DeleteAfterSend) TryDeleteDirectory(workDir);

                logger.LogInformation("JM {AlbumId}「{Name}」已发送到 {Target}，{Pages} 页，格式 {Format}",
                    album.Id, album.Name, targetId, totalPages, format);

                string paths = string.Join("\n", artifacts.Select(item => item.Path));
                return "已发送 JM《" + album.Name + "》（" + chapterText + "），共 " + totalPages + " 页。\n"
                       + formatLabel + "：" + paths + formatNote
                       + (reused > 0 ? "\n其中 " + reused + " 张复用了本地缓存，未重新下载。" : "")
                       + (unrestored > 0 ? "\n其中 " + unrestored + " 张格式不支持还原，已原样打包。" : "");
            }
            catch
            {
                TryDeleteDirectory(workDir);
                throw;
            }
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("JM 账号：查看登录状态、用配置里的账号密码登录 JM、退出登录；登录成功后会话保存在本机并自动复用")]
    public async Task<string> JmAccount(
        [Description("操作：status 查看状态、login 登录、logout 退出")] string action = "status")
    {
        return await RunAsync(async () =>
        {
            using JmApiClient api = CreateClient();
            switch ((action ?? "status").Trim().ToLowerInvariant())
            {
                case "login":
                    (bool ok, string message) = await api.LoginAsync(DestroyCancellationToken);
                    return message;
                case "logout":
                    return await api.LogoutAsync(DestroyCancellationToken);
                default:
                    if (api.HasSession) return "JM 账号已登录：" + api.SessionUsername + "。";
                    string configured = (Configuration.Username ?? "").Trim();
                    return configured.Length == 0
                        ? "当前未登录，且未配置「JM 账号 / JM 密码」，请先在 JM漫画 设置里填写。"
                        : "当前未登录，已配置账号 " + configured + "，可调用 JmAccount(action=login) 登录。";
            }
        }, "JM 账号操作失败");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("JM 收藏：把本子加入/移出本机收藏夹，或列出收藏；已登录 JM 账号时加收藏会同时尽力同步到账号收藏夹")]
    public async Task<string> JmFavorite(
        [Description("操作：add 收藏、remove 取消收藏、list 列出收藏")] string action = "list",
        [Description("漫画 ID（albumId）；list 时可忽略")] long albumId = 0,
        [Description("接收结果的群号或 QQ 号；0 表示只返回文字")] long targetId = 0,
        [Description("group 或 private；留空自动判断")] string messageType = "")
    {
        string mode = (action ?? "list").Trim().ToLowerInvariant();
        if (mode != "list" && albumId <= 0)
        {
            interactor.Poke("漫画 ID 不正确");
            return "漫画 ID 不正确";
        }
        if (targetId < 0) targetId = 0;
        _ = messageType;

        return await RunAsync(async () =>
        {
            if (mode == "list")
            {
                List<JmFavoriteItem> items = favoriteStore.List();
                if (items.Count == 0) return "本机收藏夹是空的，可用 JmFavorite(action=add, albumId=...) 收藏漫画。";

                StringBuilder builder = new();
                builder.AppendLine("本机收藏夹共 " + items.Count + " 本：");
                int shown = Math.Min(30, items.Count);
                for (int i = 0; i < shown; i++)
                {
                    JmFavoriteItem item = items[i];
                    builder.AppendLine("- ID " + item.AlbumId
                        + "｜" + (item.Name.Length == 0 ? "（未记录标题）" : item.Name)
                        + "｜" + item.AddedAt
                        + (item.SyncedRemote ? "｜已同步账号" : ""));
                }
                if (items.Count > shown) builder.AppendLine("……共 " + items.Count + " 本");
                return builder.ToString();
            }

            using JmApiClient api = CreateClient();
            if (mode == "remove")
            {
                return favoriteStore.Remove(albumId)
                    ? "已取消本机收藏 " + albumId + "。"
                    : "本机收藏夹里没有 " + albumId + "。";
            }

            string name = "";
            try
            {
                JmAlbum album = await api.GetAlbumAsync(albumId, DestroyCancellationToken);
                name = album.Name;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "JM 收藏时获取详情失败（仅记 ID）");
            }

            bool synced = await api.TryAddRemoteFavoriteAsync(
                albumId, Configuration.FavoriteFolder, DestroyCancellationToken);
            bool added = favoriteStore.Add(albumId, name, synced);
            string title = name.Length == 0 ? ("ID " + albumId) : ("《" + name + "》");
            return (added ? "已收藏 " : "已更新收藏 ") + title + "。"
                   + (synced
                       ? "已同步到 JM 账号收藏夹。"
                       : (api.HasSession
                           ? "远端同步未成功，已保存在本机收藏夹。"
                           : "未登录 JM 账号，仅保存在本机收藏夹。"));
        }, "JM 收藏操作失败");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("JM 订阅：订阅某本漫画的更新并推送到指定群/私聊，或取消订阅、列出订阅；发现新章节时自动提醒")]
    public async Task<string> JmSubscribe(
        [Description("操作：add 订阅、remove 取消、list 列出")] string action = "list",
        [Description("漫画 ID（albumId）")] long albumId = 0,
        [Description("接收更新的群号或 QQ 号；add 必填")] long targetId = 0,
        [Description("group 或 private；留空自动判断")] string messageType = "",
        [Description("发现新章节时是否自动把新章节下载并发过去，默认 false 只提醒")] bool autoDownload = false)
    {
        string mode = (action ?? "list").Trim().ToLowerInvariant();

        if (mode == "list")
        {
            List<JmSubscription> snapshot = subscribeStore.List();
            if (snapshot.Count == 0) return "还没有任何订阅，可用 JmSubscribe(action=add, albumId=..., targetId=...) 订阅漫画更新。";

            StringBuilder builder = new();
            builder.AppendLine("当前订阅 " + snapshot.Count + " 条：");
            foreach (JmSubscription item in snapshot)
            {
                builder.AppendLine("- " + (item.IsGroup ? "群 " : "私聊 ") + item.TargetId
                    + "｜ID " + item.AlbumId
                    + "｜" + (item.Name.Length == 0 ? "（未记录标题）" : item.Name)
                    + "｜" + (item.HasBaseline ? "基线 " + item.LastChapterCount + " 章" : "待首次检查")
                    + (item.AutoDownload ? "｜自动下载" : "")
                    + (item.LastError.Length > 0 ? "｜上次失败：" + item.LastError : ""));
            }
            return builder.ToString();
        }

        if (albumId <= 0 || targetId <= 0)
        {
            interactor.Poke("漫画 ID 与目标不能为空");
            return "漫画 ID 与目标不能为空";
        }

        return await RunAsync(async () =>
        {
            OneBotClient client = RequireClient();
            bool isGroup = await IsGroupAsync(client, targetId, messageType);
            string? refused = CheckSendPermission(isGroup, targetId);
            if (refused != null) return "未授权：群/好友 " + targetId + " " + refused + "。";

            if (mode == "remove")
            {
                return subscribeStore.Remove(albumId, targetId, isGroup)
                    ? "已取消订阅 " + albumId + " → " + (isGroup ? "群 " : "私聊 ") + targetId + "。"
                    : "没有找到对应的订阅。";
            }

            using JmApiClient api = CreateClient();
            JmAlbum album = await api.GetAlbumAsync(albumId, DestroyCancellationToken);
            (JmSubscription subscription, bool added) =
                subscribeStore.AddOrGet(albumId, album.Name, targetId, isGroup, autoDownload);

            // 立刻建立基线，避免第一次检查把整本书当成新更新推出去
            if (!subscription.HasBaseline)
            {
                subscription.HasBaseline = true;
                subscription.LastChapterCount = album.Chapters.Count;
                subscription.LastNewestChapterId = album.Chapters.Count > 0 ? album.Chapters[^1].Id : 0;
                subscription.LastCheck = Stamp();
                subscription.LastError = "";
                subscribeStore.Update(subscription);
            }

            return (added ? "已订阅 " : "订阅已存在，已更新 ") + "《" + album.Name + "》（ID " + album.Id + "）→ "
                   + (isGroup ? "群 " : "私聊 ") + targetId + "，当前基线 " + album.Chapters.Count + " 章。"
                   + (autoDownload ? "发现新章节会自动下载并发过去。" : "发现新章节会发送提醒。");
        }, "JM 订阅操作失败");
    }

    #endregion

    #region 卡片与合并转发

    /// <summary>发送本子详情卡片（合并转发优先，失败降级单图）；未发送时返回 null。</summary>
    async Task<string?> SendAlbumCardAsync(
        JmApiClient api, JmAlbum album, long targetId, string messageType, string? note)
    {
        if (targetId <= 0) return null;

        OneBotClient client = RequireClient();
        bool isGroup = await IsGroupAsync(client, targetId, messageType);
        string? refused = CheckSendPermission(isGroup, targetId);
        if (refused != null) return "（未发送：群/好友 " + targetId + " " + refused + "）";

        if (!Configuration.RenderAsImage || !Configuration.SendCover)
        {
            byte[] plainCover = await api.DownloadCoverAsync(album.Id, DestroyCancellationToken);
            await SendImageBase64Async(client, isGroup, targetId, plainCover, "JM《" + album.Name + "》封面");
            return "（已把封面发送到 " + targetId + "）";
        }

        byte[] cover = await api.DownloadCoverAsync(album.Id, DestroyCancellationToken);
        string? card = await JmCardRenderer.RenderAlbumAsync(album, cover,
            "QQSuite · JM漫画　用 JmDownload 下载（albumId=" + album.Id + "）", logger);

        if (card == null)
        {
            await SendImageBase64Async(client, isGroup, targetId, cover, "JM《" + album.Name + "》封面");
            return "（卡片渲染失败，已改为发送封面到 " + targetId + "）";
        }

        string fallback = (note == null ? "" : note + "\n") + FormatAlbum(album);
        await SendCardAsync(client, isGroup, targetId, card, fallback,
            "JM《" + album.Name + "》详情", "JM 详情");
        return "（已把详情卡片发送到 " + targetId + "）";
    }

    /// <summary>下载搜索结果前几名的封面，供卡片内联使用；单个失败不影响其它。</summary>
    async Task<List<JmCardRenderer.SearchCover>> DownloadSearchCoversAsync(JmApiClient api, List<JmSearchItem> items)
    {
        List<JmCardRenderer.SearchCover> covers = new(items.Count);
        foreach (JmSearchItem item in items)
        {
            byte[]? bytes = null;
            try
            {
                bytes = await api.DownloadCoverAsync(item.Id, DestroyCancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("JM 封面 {Id} 下载失败：{Message}", item.Id, ex.Message);
            }
            covers.Add(new JmCardRenderer.SearchCover(item, bytes));
        }
        return covers;
    }

    /// <summary>详情卡片用：下载封面 + 渲染 + 发送。</summary>
    async Task SendAlbumDetailAsync(
        JmApiClient api, OneBotClient client, JmAlbum album, bool isGroup, long targetId)
    {
        byte[] cover = await api.DownloadCoverAsync(album.Id, DestroyCancellationToken);
        string? card = Configuration.RenderAsImage
            ? await JmCardRenderer.RenderAlbumAsync(album, cover,
                "QQSuite · JM漫画　已下载（albumId=" + album.Id + "）", logger)
            : null;

        if (card == null)
        {
            await SendImageBase64Async(client, isGroup, targetId, cover, "JM《" + album.Name + "》封面");
            return;
        }

        await SendCardAsync(client, isGroup, targetId, card, FormatAlbum(album),
            "JM《" + album.Name + "》详情", "JM 详情");
    }

    /// <summary>按配置发送图片卡片：合并转发优先，失败降级为直接把图片发出去。</summary>
    async Task SendCardAsync(OneBotClient client, bool isGroup, long targetId, string imagePath,
        string fallbackText, string caption, string nodeName)
    {
        if (Configuration.UseForward)
        {
            try
            {
                List<object> nodes = BuildCardNodes(imagePath, fallbackText, nodeName);
                await SendForwardAsync(client, isGroup, targetId, nodes);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "JM 合并转发发送失败，降级为直接发图");
            }
        }

        byte[] bytes = await File.ReadAllBytesAsync(imagePath, DestroyCancellationToken);
        await SendImageBase64Async(client, isGroup, targetId, bytes, caption);
    }

    /// <summary>合并转发节点：正文文字 + 卡片图片，节点内容一律用消息段数组。</summary>
    List<object> BuildCardNodes(string imagePath, string fallbackText, string nodeName)
    {
        List<object> nodes = new();
        if (!string.IsNullOrWhiteSpace(fallbackText)) nodes.Add(BuildTextNode(fallbackText, nodeName));
        nodes.Add(BuildImageNode(imagePath, nodeName));
        return nodes;
    }

    /// <summary>合并转发里的文本节点。</summary>
    object BuildTextNode(string content, string nodeName = "JM漫画")
    {
        return new { type = "node", data = new { name = nodeName, nickname = nodeName, uin = BotUin(), content } };
    }

    /// <summary>合并转发里的单图节点。</summary>
    object BuildImageNode(string imagePath, string nodeName = "JM漫画")
    {
        return new
        {
            type = "node",
            data = new
            {
                name = nodeName,
                nickname = nodeName,
                uin = BotUin(),
                content = new object[] { new { type = "image", data = new { file = "file:///" + imagePath.Replace('\\', '/') } } }
            }
        };
    }

    string BotUin()
    {
        long botId = qChatService.BotId;
        return (botId == 0 ? 10000 : botId).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>把打包好的产物发到目标：长图优先合并转发 / 直发图片，失败或过大时连同 ZIP / PDF 一起走文件上传。</summary>
    async Task SendArtifactsAsync(OneBotClient client, bool isGroup, long targetId,
        List<(string Path, string Name)> artifacts, string format)
    {
        if (artifacts.Count == 0) return;

        if (format == "longimage")
        {
            const long maxInlineBytes = 8L * 1024 * 1024;
            bool tooLarge = artifacts.Any(item => new FileInfo(item.Path).Length > maxInlineBytes);
            if (!tooLarge)
            {
                try
                {
                    if (Configuration.UseForward && artifacts.Count > 1)
                    {
                        List<object> nodes = new() { BuildTextNode("JM 长图共 " + artifacts.Count + " 张") };
                        foreach ((string path, _) in artifacts) nodes.Add(BuildImageNode(path));
                        await SendForwardAsync(client, isGroup, targetId, nodes);
                        return;
                    }

                    foreach ((string path, string name) in artifacts)
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(path, DestroyCancellationToken);
                        await SendImageBase64Async(client, isGroup, targetId, bytes, "JM 长图 " + name);
                    }
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "JM 长图直接发送失败，改用文件上传");
                }
            }
        }

        foreach ((string path, string name) in artifacts)
        {
            try
            {
                if (isGroup) await client.UploadGroupFile(targetId, path, name);
                else await client.UploadPrivateFile(targetId, path, name);
            }
            catch (Exception ex)
            {
                throw new Exception("发送文件 " + name + " 失败：" + ex.Message, ex);
            }
        }
    }

    async Task SendForwardAsync(OneBotClient client, bool isGroup, long targetId, List<object> nodes)
    {
        string action = isGroup ? "send_group_forward_msg" : "send_private_forward_msg";
        object parameters = isGroup
            ? new { group_id = targetId, messages = nodes }
            : new { user_id = targetId, messages = nodes };

        JsonElement raw = await client.CallActionRawAsync(action, parameters, 60);
        string? status = Pick(raw, "status");
        int retCode = (int)PickLong(raw, "retcode");
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) || retCode > 1)
        {
            string message = Pick(raw, "message", "wording") ?? "协议端返回失败";
            throw new Exception(message + "（retcode=" + retCode + "）");
        }
    }

    /// <summary>发送目标是否允许；不允许时返回原因，允许时返回 null。</summary>
    string? CheckSendPermission(bool isGroup, long targetId)
    {
        if (isGroup && !GroupAllowed(targetId))
        {
            return "不在允许群号列表里";
        }
        if (!isGroup && !Configuration.AllowPrivate)
        {
            return "当前配置不允许私聊";
        }
        return null;
    }

    #endregion

    #region 下载缓存

    /// <summary>章节缓存目录：{下载根}/cache/{albumId}/{chapterId}。</summary>
    string CacheChapterDir(long albumId, long chapterId) => Path.Combine(DownloadRoot(), "cache",
        albumId.ToString(CultureInfo.InvariantCulture),
        chapterId.ToString(CultureInfo.InvariantCulture));

    /// <summary>缓存目录里是否存在对应数量的完整图片，存在则按章序拷贝到目标目录。</summary>
    static bool TryCopyFromCache(string cacheDir, int expectedCount, string chapterTag, string workDir,
        out int copied)
    {
        copied = 0;
        if (!Directory.Exists(cacheDir)) return false;

        string[] files;
        try
        {
            files = Directory.GetFiles(cacheDir);
        }
        catch
        {
            return false;
        }
        if (files.Length != expectedCount) return false;

        try
        {
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                string extension = Path.GetExtension(files[i]);
                string target = Path.Combine(workDir,
                    chapterTag + "_" + (i + 1).ToString("D4", CultureInfo.InvariantCulture) + extension);
                File.Copy(files[i], target, overwrite: true);
            }
            copied = files.Length;
            return true;
        }
        catch
        {
            copied = 0;
            return false;
        }
    }

    /// <summary>把本次下载好的图片按页码顺序复制到缓存目录（先写临时目录再替换，避免半成品被复用）。</summary>
    static void SaveChapterCache(string cacheDir, List<string> savedFiles)
    {
        if (savedFiles.Count == 0) return;
        string tempDir = cacheDir + ".tmp";
        try
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            foreach (string file in savedFiles)
            {
                try
                {
                    // 文件名形如 03_0012.jpg，直接沿用即可保持页序
                    File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)), overwrite: true);
                }
                catch
                {
                    // 少数文件复制失败就不缓存这一章，下次重新下载即可
                }
            }

            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
            Directory.Move(tempDir, cacheDir);
        }
        catch
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    #endregion

    #region 执行壳与发送

    async Task<string> RunAsync(Func<Task<string>> action, string errorPrefix)
    {
        try
        {
            string result = await action();
            if (!string.IsNullOrWhiteSpace(result)) interactor.Poke(result);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Prefix}", errorPrefix);
            string message = errorPrefix + "：" + ex.Message;
            interactor.Poke(message);
            return message;
        }
    }

    OneBotClient RequireClient()
    {
        OneBotClient? client = qChatService.Client;
        if (client is not { IsConnected: true })
        {
            throw new Exception(QQSuiteTransport.NotConnectedMessage);
        }
        return client;
    }

    /// <summary>
    /// 从聊天历史里认出当前对话：群聊取群号，私聊取对方 QQ 号；认不出返回 0。
    /// 消息体格式与 Qzone 模块一致：[群聊消息(群号,群名)] / [私聊消息(QQ号)]。
    /// </summary>
    (long Id, bool IsGroup) ResolveCurrentTarget()
    {
        try
        {
            foreach (var content in ChatBot.ChatHistory.Reverse())
            {
                if (content.Role != Microsoft.SemanticKernel.ChatCompletion.AuthorRole.User) continue;
                string text = content.Content ?? "";
                // 戳一戳、定时任务这类不是真实对话，跳过；找到第一条真实消息就按它判定场合
                if (text.StartsWith(ChatBot.PokeMessageTag) || text.StartsWith("[消息来源(")) continue;

                Match group = Regex.Match(text, @"\[群聊消息\((\d+)");
                if (group.Success) return (long.Parse(group.Groups[1].Value), true);

                Match priv = Regex.Match(text, @"\[私聊消息\((\d+)");
                if (priv.Success) return (long.Parse(priv.Groups[1].Value), false);

                return (0, false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "识别当前对话失败，无法自动确定发送目标");
        }

        return (0, false);
    }

    async Task<bool> IsGroupAsync(OneBotClient client, long targetId, string messageType)
    {
        if (string.Equals(messageType, "group", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(messageType, "private", StringComparison.OrdinalIgnoreCase)) return false;
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

    async Task SendTextSafeAsync(OneBotClient client, bool isGroup, long targetId, string text)
    {
        try
        {
            object message = new object[] { new { type = "text", data = new { text } } };
            object parameters = isGroup
                ? new { group_id = targetId, message }
                : new { user_id = targetId, message };
            await client.CallActionRawAsync(isGroup ? "send_group_msg" : "send_private_msg", parameters, 30);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "JM 提示文本发送失败");
        }
    }

    async Task SendImageBase64Async(OneBotClient client, bool isGroup, long targetId, byte[] bytes, string caption)
    {
        object message = new object[]
        {
            new { type = "text", data = new { text = caption } },
            new { type = "image", data = new { file = "base64://" + Convert.ToBase64String(bytes) } }
        };
        object parameters = isGroup
            ? new { group_id = targetId, message }
            : new { user_id = targetId, message };

        JsonElement raw = await client.CallActionRawAsync(
            isGroup ? "send_group_msg" : "send_private_msg", parameters, 120);
        string? status = Pick(raw, "status");
        int retCode = (int)PickLong(raw, "retcode");
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) && retCode > 1)
        {
            throw new Exception(Pick(raw, "message", "wording") ?? ("retcode=" + retCode));
        }
    }

    #endregion

    #region 配置辅助

    JmApiClient CreateClient() => new(Configuration, logger);

    bool GroupAllowed(long groupId)
    {
        string configured = Configuration.AllowedGroups ?? "";
        foreach (string part in configured.Split([',', '，', ' ', ';', '；'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                && value == groupId)
            {
                return true;
            }
        }
        return false;
    }

    string DownloadRoot()
    {
        string configured = (Configuration.DownloadDir ?? "").Trim();
        if (configured.Length == 0)
        {
            return Path.Combine(AlifePath.StorageFolderPath, "Plugins", "presents.QQSuite", "jm");
        }
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AlifePath.StorageFolderPath, configured);
    }

    #endregion

    #region 展示辅助

    static JmChapter? MatchChapter(List<JmChapter> chapters, string selector)
    {
        string value = selector.Trim();
        if (value.Length == 0) return null;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 1 && index <= chapters.Count)
        {
            return chapters[index - 1];
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long chapterId))
        {
            JmChapter? byId = chapters.FirstOrDefault(item => item.Id == chapterId);
            if (byId != null) return byId;
        }
        return chapters.FirstOrDefault(item =>
            item.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    static string FormatAlbum(JmAlbum album)
    {
        StringBuilder builder = new();
        builder.AppendLine("JM 漫画 " + album.Id + "：" + album.Name);
        builder.AppendLine("作者：" + (album.Authors.Count == 0 ? "未知" : string.Join("、", album.Authors)));
        if (album.Tags.Count > 0)
        {
            builder.AppendLine("标签：" + string.Join("、", album.Tags.Take(20)));
        }
        builder.AppendLine("页数：" + album.TotalPhotos + " · 章节：" + album.Chapters.Count);
        if (!string.IsNullOrWhiteSpace(album.Description))
        {
            builder.AppendLine("简介：" + Shorten(album.Description, 120));
        }

        int shown = Math.Min(8, album.Chapters.Count);
        builder.Append("章节（前 " + shown + " 个）：");
        for (int i = 0; i < shown; i++)
        {
            builder.Append("\n  " + (i + 1) + ". " + album.Chapters[i].Name
                           + "（ID " + album.Chapters[i].Id + "）");
        }
        if (album.Chapters.Count > shown)
        {
            builder.Append("\n  ……共 " + album.Chapters.Count + " 章");
        }
        return builder.ToString();
    }

    static string FormatChapterList(List<JmChapter> chapters)
    {
        StringBuilder builder = new();
        int shown = Math.Min(20, chapters.Count);
        for (int i = 0; i < shown; i++)
        {
            builder.AppendLine((i + 1) + ". " + chapters[i].Name + "（ID " + chapters[i].Id + "）");
        }
        if (chapters.Count > shown) builder.AppendLine("……共 " + chapters.Count + " 章");
        return builder.ToString();
    }

    static string Shorten(string text, int length)
    {
        string value = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= length ? value : value[..length] + "…";
    }

    /// <summary>把「输出格式」配置归一化成 zip / longimage / pdf 之一。</summary>
    static string NormalizeOutputFormat(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "长图" or "longimage" or "long" or "jpg" or "image" => "longimage",
            "pdf" => "pdf",
            _ => "zip"
        };
    }

    static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// 从 AI 上下文里推断本次请求的发起人 QQ：
    /// 群聊消息体形如「[群聊消息(群号,群名)] [123456(昵称)]:内容」，私聊形如「[私聊消息(123456)] 内容」。
    /// 推断不出（定时任务、内部调用等）返回 0，此时群聊只按群计配额。
    /// </summary>
    long TryGetRequesterId()
    {
        try
        {
            foreach (var content in ChatBot.ChatHistory.Reverse())
            {
                if (content.Role != Microsoft.SemanticKernel.ChatCompletion.AuthorRole.User) continue;
                string text = content.Content ?? "";
                if (text.StartsWith(ChatBot.PokeMessageTag) || text.StartsWith("[消息来源(")) continue;

                if (text.Contains("[群聊消息("))
                {
                    Match match = Regex.Match(text, @"\]\s*\[(\d{5,})(?:\([^\)]*\))?\]\s*[:：]");
                    return match.Success && long.TryParse(match.Groups[1].Value, out long uid) ? uid : 0;
                }

                Match priv = Regex.Match(text, @"\[私聊消息\((\d+)(?:,[^)\]]*)?\)\]");
                return priv.Success && long.TryParse(priv.Groups[1].Value, out long pid) ? pid : 0;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "JM 解析请求发起人失败");
        }
        return 0;
    }

    static string SanitizeFileName(string name)
    {
        string value = name;
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }
        value = value.Trim().TrimEnd('.');
        return value.Length == 0 ? "jm" : value;
    }

    static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L) return (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        return bytes + " B";
    }

    static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch
        {
            // 文件可能仍被协议端占用，留给用户手动清理
        }
    }

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
        return long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out long value) ? value : 0;
    }

    #endregion

    #region 生命周期

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "JM 漫画：搜索、详情与封面、整本/单章下载（ZIP / 长图 / PDF），另有账号登录、收藏与订阅推送。",
            Explanation = "JmSearch 按关键词搜索并返回结果，给了 targetId 时还会把结果渲染成图片以合并转发发送；"
                          + "JmInfo 查看标题/作者/标签/页数与章节，给了 targetId 时会发送详情卡片；"
                          + "JmDownload 按整本或指定章节下载，还原图片后按「输出格式」打包成 ZIP / 长图 / PDF 发送到群聊/私聊，"
                          + "并在可用时复用本地缓存、按群/按人扣减每日配额；"
                          + "targetId 可以留空：留空就是发给当前对话——私聊里说「发给我」就直接发给说话的人，群里就发到当前群，不要反问主人要群号；"
                          + "JmAccount 查看/登录/退出 JM 账号，JmFavorite 管理本机收藏夹（登录后加收藏会尽力同步到账号），"
                          + "JmSubscribe 订阅漫画更新，发现新章节时按间隔自动推送到指定群/私聊。"
                          + "群聊需要在设置里填写允许的群号，私聊需要开启允许私聊。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);

        interactor.Prompt($$"""
            你具备「JM漫画」能力，可以搜索 JM 漫画、查看详情、打包下载，并管理账号、收藏与订阅。

            ## 提供函数
            {{handler.FunctionDocument()}}

            ## 使用提示
            - JmSearch 返回搜索结果文字；给了 targetId 时会把结果渲染成图片卡片并以合并转发发送
            - JmInfo 输出标题、作者、标签、页数、日期与统计数据；给了 targetId 时会发送详情图片卡片
            - JmDownload 的 chapter 留空表示整本，也可以填章节序号（从 1 开始）、章节 ID 或章节名的一部分；
              输出格式由设置里的「输出格式」决定（zip / longimage / pdf），设置了「压缩包密码」时 ZIP 会加密
            - 下载会扣减每日配额（每群 / 每人各一份，可分别设置上限，0 表示不限）
            - JmAccount 用设置里的「JM 账号 / JM 密码」登录；登录后 JmFavorite 加收藏会尽力同步到账号收藏夹
            - JmFavorite 的收藏始终先保存在本机；JmSubscribe 会在发现新章节时自动提醒，可选自动下载
            - 群聊必须先把群号写进「允许群号」配置，私聊必须开启「允许私聊」
            - 敏感题材需要在设置里填写「AVS 登录态」；渲染图片与导出 PDF 需要系统安装 Edge 或 Chrome，缺失时会自动降级
            """);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        logger.LogInformation("QQ JM漫画 已启动（搜索 / 详情 / 下载打包 ZIP·长图·PDF / 账号 / 收藏 / 订阅）");
        return Task.CompletedTask;
    }

    /// <summary>每分钟被框架调用一次：跨日重置配额、到间隔检查订阅更新、到点执行一次下载目录清理。</summary>
    protected override Task OnUpdate()
    {
        try
        {
            if (quotaStore.ResetIfNewDay()) logger.LogInformation("JM 每日配额已跨日重置");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JM 每日配额跨日重置失败");
        }

        MaybeCheckSubscriptions();

        if (!Configuration.EnableAutoClean) return Task.CompletedTask;
        if (!TimeSpan.TryParseExact((Configuration.CleanTime ?? "").Trim(), "hh\\:mm",
                CultureInfo.InvariantCulture, out TimeSpan scheduledTime))
        {
            return Task.CompletedTask;
        }

        DateTime now = DateTime.Now;
        DateTime fireTime = now.Date + scheduledTime;
        // 到点后 30 分钟内触发一次，避免刚好错过整分
        if (now < fireTime || now > fireTime.AddMinutes(30) || lastCleanDate == now.Date)
        {
            return Task.CompletedTask;
        }
        lastCleanDate = now.Date;

        try
        {
            int removed = CleanupOldDirectories();
            if (removed > 0) logger.LogInformation("JM 定时清理完成，删除 {Count} 个过期目录", removed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JM 定时清理失败");
        }
        return Task.CompletedTask;
    }

    #region 订阅轮询

    /// <summary>到了检查间隔就起一个后台任务跑一轮订阅检查，不阻塞 OnUpdate。</summary>
    void MaybeCheckSubscriptions()
    {
        try
        {
            if (!Configuration.EnableSubscribe || subscribeRunning) return;

            int interval = Math.Clamp(Configuration.SubscribeInterval, 5, 24 * 60);
            if (DateTime.Now - lastSubscribeCheck < TimeSpan.FromMinutes(interval)) return;

            // 没有订阅时也推进时间戳，避免每分钟都去读一次状态文件
            if (subscribeStore.List().Count == 0)
            {
                lastSubscribeCheck = DateTime.Now;
                return;
            }

            lastSubscribeCheck = DateTime.Now;
            subscribeRunning = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckSubscriptionsAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "JM 订阅轮询异常");
                }
                finally
                {
                    subscribeRunning = false;
                }
            });
        }
        catch (Exception ex)
        {
            subscribeRunning = false;
            logger.LogWarning(ex, "JM 启动订阅轮询失败");
        }
    }

    async Task CheckSubscriptionsAsync()
    {
        foreach (JmSubscription subscription in subscribeStore.SnapshotEnabled())
        {
            if (DestroyCancellationToken.IsCancellationRequested) return;
            try
            {
                await CheckOneSubscriptionAsync(subscription);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "JM 订阅检查失败（{Album} → {Target}）",
                    subscription.AlbumId, subscription.TargetId);
                subscription.LastError = ex.Message;
                subscription.LastCheck = Stamp();
                subscribeStore.Update(subscription);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), DestroyCancellationToken);
        }
    }

    /// <summary>检查一条订阅：拉详情 → 比较章节数 / 最新章节 → 有更新则推送并可选自动下载。</summary>
    async Task CheckOneSubscriptionAsync(JmSubscription subscription)
    {
        using JmApiClient api = CreateClient();
        JmAlbum album = await api.GetAlbumAsync(subscription.AlbumId, DestroyCancellationToken);

        subscription.LastCheck = Stamp();
        subscription.LastError = "";
        if (!string.IsNullOrWhiteSpace(album.Name)) subscription.Name = album.Name;

        // 首次检查只登记基线，不推送
        if (!subscription.HasBaseline)
        {
            subscription.HasBaseline = true;
            subscription.LastChapterCount = album.Chapters.Count;
            subscription.LastNewestChapterId = album.Chapters.Count > 0 ? album.Chapters[^1].Id : 0;
            subscribeStore.Update(subscription);
            return;
        }

        List<JmChapter> fresh = album.Chapters.Count > subscription.LastChapterCount
            ? album.Chapters.Skip(subscription.LastChapterCount).ToList()
            : new List<JmChapter>();

        // 章节数没变但最新章节 ID 变了，也当作更新（原章节被替换）
        if (fresh.Count == 0 && album.Chapters.Count == subscription.LastChapterCount
            && album.Chapters.Count > 0 && album.Chapters[^1].Id != subscription.LastNewestChapterId)
        {
            fresh = [album.Chapters[^1]];
        }

        if (fresh.Count == 0)
        {
            subscribeStore.Update(subscription);
            return;
        }

        // 未连接协议端时不推进基线，下次再推
        OneBotClient client = RequireClient();

        StringBuilder builder = new();
        builder.AppendLine("JM《" + subscription.Name + "》有更新，新增 " + fresh.Count + " 个章节：");
        int shown = Math.Min(10, fresh.Count);
        for (int i = 0; i < shown; i++) builder.AppendLine("  · " + fresh[i].Name);
        if (fresh.Count > shown) builder.AppendLine("  ……");
        builder.Append("可在群里让我下载查看（albumId=" + subscription.AlbumId + "）。");

        await SendTextSafeAsync(client, subscription.IsGroup, subscription.TargetId, builder.ToString());

        subscription.LastChapterCount = album.Chapters.Count;
        subscription.LastNewestChapterId = album.Chapters.Count > 0 ? album.Chapters[^1].Id : 0;
        subscribeStore.Update(subscription);

        if (!subscription.AutoDownload) return;

        // 自动下载最多 3 个新章节，避免一次刷屏
        foreach (JmChapter chapter in fresh.Take(3))
        {
            await DownloadCoreAsync(subscription.AlbumId,
                chapter.Id.ToString(CultureInfo.InvariantCulture),
                subscription.TargetId,
                subscription.IsGroup ? "group" : "private",
                skipQuota: true);
        }
    }

    #endregion

    /// <summary>
    /// 清理下载根目录下过期的临时下载目录与章节缓存；保留天数配置为 0 时只清理临时目录。
    /// 返回删除的目录数量。
    /// </summary>
    int CleanupOldDirectories()
    {
        string root = DownloadRoot();
        if (!Directory.Exists(root)) return 0;

        int keepDays = Math.Clamp(Configuration.KeepDays, 0, 365);
        DateTime now = DateTime.Now;
        int removed = 0;

        // 1) 下载过程中产生的临时/结果目录 jm_*：超过 1 天未更新即视为残留
        foreach (string directory in SafeGetDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (!name.StartsWith("jm_", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsOlderThan(directory, now.AddDays(-1)) && DeleteDirectoryTree(directory)) removed++;
        }

        // 2) 章节缓存 cache/{albumId}/{chapterId}：超过保留天数未使用即清理
        if (keepDays > 0)
        {
            string cacheRoot = Path.Combine(root, "cache");
            foreach (string albumDir in SafeGetDirectories(cacheRoot))
            {
                DateTime? newest = NewestWriteTime(albumDir);
                if (newest != null && newest.Value < now.AddDays(-keepDays) && DeleteDirectoryTree(albumDir))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    static IEnumerable<string> SafeGetDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    static bool IsOlderThan(string directory, DateTime threshold)
    {
        try
        {
            return Directory.GetLastWriteTime(directory) < threshold;
        }
        catch
        {
            return false;
        }
    }

    static DateTime? NewestWriteTime(string directory)
    {
        try
        {
            DateTime newest = Directory.GetLastWriteTime(directory);
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                DateTime time = File.GetLastWriteTime(file);
                if (time > newest) newest = time;
            }
            return newest;
        }
        catch
        {
            return null;
        }
    }

    static bool DeleteDirectoryTree(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
                return true;
            }
        }
        catch
        {
            // 文件仍被占用时留给下次清理
        }
        return false;
    }

    #endregion
}
