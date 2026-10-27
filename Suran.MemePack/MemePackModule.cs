using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using Alife.Function.Vision;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using QQSuite.Core;

namespace QQSuite.MemePack;

/// <summary>表情包库存配置。</summary>
public class MemePackConfig
{
    [DisplayName("启用表情包库存")]
    [Description("开启后 AI 可以收藏表情包，并按含义自主发送")]
    public bool Enabled { get; set; } = true;

    [DisplayName("收藏后自动打标")]
    [Description("收藏时调用视觉模型生成语义描述，AI 才能按「含义」检索；未配置视觉模型时退回用备注或文件名作描述")]
    public bool AutoDescribe { get; set; } = true;

    [DisplayName("提示词注入条数")]
    [Description("启动时把多少个表情包的语义清单注入给 AI，默认 20；0 表示不注入（仍可用 MemeList 主动查询）")]
    public int InjectLimit { get; set; } = 20;

    [DisplayName("库存上限")]
    [Description("最多保存多少个表情包，超出后拒绝新收藏，默认 500；0 表示不限")]
    public int MaxEntries { get; set; } = 500;

    [DisplayName("单张最大 MB")]
    [Description("收藏的图片超过该大小直接拒绝，默认 8 MB")]
    public int MaxImageMB { get; set; } = 8;

    [DisplayName("下载超时(秒)")]
    [Description("从网址收藏时单次下载的超时时间，默认 30 秒")]
    public int DownloadTimeout { get; set; } = 30;

    [DisplayName("启用原生表情包发送")]
    [Description("来自 QQ 表情包（mface）的条目优先按原生表情包发送，失败自动回退成图片；关闭则一律发图片")]
    public bool PreferMface { get; set; } = true;

    [DisplayName("动图按多帧理解")]
    [Description("开启后收藏动图（GIF）会把多帧采样拼成一张图再交给视觉模型，避免只看到第一帧导致标签跑偏；关闭则和静图一样只发单帧")]
    public bool DescribeAnimation { get; set; } = true;

    [DisplayName("动图采样帧数")]
    [Description("收藏动图时抽取多少帧拼成一张图，范围 1-16，默认 6；静图不受影响")]
    public int AnimationFrames { get; set; } = 6;

    [DisplayName("动图单帧边长")]
    [Description("拼图里每一帧的长边像素，默认 400，范围 64-1024；太大只会拖慢识别")]
    public int AnimationMaxEdge { get; set; } = 400;
}

/// <summary>
/// 表情包库存：AI 主动收藏图片/QQ 表情包，自动生成语义描述，之后按含义自主发送。
/// </summary>
public class MemePackModule(
    XmlFunctionCaller functionCaller,
    ILogger<MemePackModule> logger,
    Interactor<MemePackModule> interactor,
    QChatService qChatService,
    IVisionModel? visionModel = null
) : ChatBehaviour, IConfigurable<MemePackConfig>
{
    public MemePackConfig Configuration { get; set; } = null!;

    /// <summary>收藏用 HTTP 客户端（自动解压，避免拿到 gzip 乱码）。</summary>
    static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    });

    #region 对外 AI 函数

    [XmlFunction(FunctionMode.OneShot)]
    [Description("收藏一张图片或 QQ 表情包进表情包库存，自动生成语义描述；之后可用 MemeSend 按含义自主发送")]
    public async Task<string> MemeCollect(
        [Description("图片来源：http(s) 网址、本地绝对路径，或完整 CQ 码（含 mface 时会一并保存原生表情包信息）")] string source,
        [Description("可选备注，会并入语义描述，例如「猫猫震惊」")] string remark = "")
    {
        if (!Configuration.Enabled) return "表情包库存未启用。";
        if (string.IsNullOrWhiteSpace(source)) return "图片来源不能为空。";

        return await RunAsync(async () =>
        {
            (string? url, string? localPath, string emojiId, string packageId, string emojiKey, string summary) =
                ParseSource(source.Trim());

            List<MemeEntry> entries = MemeStore.Load();
            int max = Configuration.MaxEntries;
            if (max > 0 && entries.Count >= max)
            {
                return "表情包库存已满（" + entries.Count + " / " + max + "），请先删除一些再收藏。";
            }

            byte[] bytes = await LoadImageAsync(url, localPath);
            int maxBytes = Math.Max(1, Configuration.MaxImageMB) * 1024 * 1024;
            if (bytes.Length == 0) return "没有取到图片内容，收藏失败。";
            if (bytes.Length > maxBytes)
            {
                return "图片过大（" + (bytes.Length / 1024 / 1024) + " MB），超过上限 " + Configuration.MaxImageMB + " MB，收藏失败。";
            }

            string id = MemeStore.Fingerprint(bytes);
            MemeEntry? existing = MemeStore.Find(entries, id);
            if (existing != null)
            {
                // 重复收藏不白跑：之前没标上的，这次顺手补标，省得还要单独调 MemeDescribe
                if (IsUntagged(existing))
                {
                    string retag = await DescribeAsync(Path.Combine(MemeStore.Directory, existing.File), remark);
                    if (!IsUntagged(retag))
                    {
                        existing.Description = retag;
                        MemeStore.Save(entries);
                        return "这张表情包已经在库存里了（" + existing.Id + "），之前没标注，这次补上了：" + retag + "。";
                    }
                    return "这张表情包已经在库存里了（" + existing.Id + "，仍未生成标签，可查日志确认视觉模型是否可用）。";
                }

                return "这张表情包已经在库存里了（" + existing.Id + "，" + existing.Description + "）。";
            }

            string file = MemeStore.WriteImage(id, bytes, url ?? localPath);

            string description = await DescribeAsync(Path.Combine(MemeStore.Directory, file), remark);
            entries.Add(new MemeEntry
            {
                Id = id,
                File = file,
                Description = description,
                AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Source = Shorten(url ?? localPath ?? source, 300),
                EmojiId = emojiId,
                EmojiPackageId = packageId,
                EmojiKey = emojiKey,
                Summary = summary
            });
            MemeStore.Save(entries);

            string mfaceNote = !string.IsNullOrWhiteSpace(emojiId) && !string.IsNullOrWhiteSpace(packageId)
                ? "（含原生表情包信息，发送时会优先走 mface）"
                : "";
            return "已收藏表情包 " + id + "｜" + description + mfaceNote
                   + "。库存共 " + entries.Count + " 个。";
        }, "收藏表情包失败");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按含义查找并发送一个表情包到群聊或私聊；优先按原生表情包发送，失败自动回退成图片")]
    public async Task<string> MemeSend(
        [Description("接收的群号或 QQ 号")] long targetId,
        [Description("group 或 private；留空自动判断")] string messageType = "",
        [Description("想表达的含义，例如「无语」「猫猫震惊」；留空则随机挑一个")] string query = "",
        [Description("直接指定表情包 ID；填写后忽略 query")] string id = "")
    {
        if (!Configuration.Enabled) return "表情包库存未启用。";
        if (targetId <= 0) return "接收目标不正确。";

        return await RunAsync(async () =>
        {
            List<MemeEntry> entries = MemeStore.Load();
            if (entries.Count == 0) return "表情包库存还是空的，可以先用 MemeCollect 收藏几张。";

            MemeEntry? picked = string.IsNullOrWhiteSpace(id)
                ? PickByQuery(entries, query)
                : MemeStore.Find(entries, id.Trim());
            if (picked == null)
            {
                return string.IsNullOrWhiteSpace(query)
                    ? "没有可用的表情包。"
                    : "库存里没有跟「" + query.Trim() + "」对得上的表情包，可以先用 MemeList 看看有哪些。";
            }

            OneBotClient client = RequireClient();
            bool isGroup = await IsGroupAsync(client, targetId, messageType);
            string way = await SendEntryAsync(client, isGroup, targetId, picked);

            picked.UseCount++;
            picked.LastUsed = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            MemeStore.Save(entries);

            string label = string.IsNullOrWhiteSpace(picked.Description) ? "未标注" : picked.Description;
            return "已发送表情包 " + picked.Id + "｜" + label + "（" + (way == "mface" ? "原生表情包" : "图片") + "）。";
        }, "发送表情包失败");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出表情包库存里的条目及其语义描述，可按关键词过滤")]
    public Task<string> MemeList(
        [Description("关键词，匹配语义描述或 ID；留空列出全部")] string keyword = "",
        [Description("最多返回几条，默认 30")] int limit = 30)
    {
        if (!Configuration.Enabled) return Task.FromResult("表情包库存未启用。");

        List<MemeEntry> entries = MemeStore.Load();
        if (entries.Count == 0) return Task.FromResult("表情包库存还是空的，可以先用 MemeCollect 收藏几张。");

        IEnumerable<MemeEntry> filtered = entries;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            string key = keyword.Trim();
            filtered = entries.Where(e =>
                (e.Description ?? "").Contains(key, StringComparison.OrdinalIgnoreCase)
                || (e.Summary ?? "").Contains(key, StringComparison.OrdinalIgnoreCase)
                || e.Id.Contains(key, StringComparison.OrdinalIgnoreCase));
        }

        List<MemeEntry> shown = filtered
            .OrderByDescending(e => e.UseCount)
            .ThenByDescending(e => e.AddedAt, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();

        if (shown.Count == 0) return Task.FromResult("没有找到匹配「" + keyword.Trim() + "」的表情包。");

        StringBuilder builder = new();
        builder.AppendLine("表情包库存共 " + entries.Count + " 个，本次列出 " + shown.Count + " 个：");
        foreach (MemeEntry entry in shown)
        {
            builder.AppendLine("- " + entry.Id + "｜" + (string.IsNullOrWhiteSpace(entry.Description) ? "未标注" : entry.Description)
                               + "｜已用 " + entry.UseCount + " 次"
                               + (entry.HasMface ? "｜原生表情包" : ""));
        }
        builder.Append("提示：MemeSend 传 id 可指定发送，传 query 会按语义挑一个。");
        return Task.FromResult(builder.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("从表情包库存里删除条目（按 ID 或关键词匹配），可一次删除多条")]
    public Task<string> MemeForget(
        [Description("要删除的表情包 ID 或关键词")] string keyword,
        [Description("最多删除几条，默认 1")] int limit = 1)
    {
        if (!Configuration.Enabled) return Task.FromResult("表情包库存未启用。");
        if (string.IsNullOrWhiteSpace(keyword)) return Task.FromResult("请给出要删除的表情包 ID 或关键词。");

        List<MemeEntry> entries = MemeStore.Load();
        string key = keyword.Trim();
        List<MemeEntry> targets = entries
            .Where(e => e.Id.Equals(key, StringComparison.OrdinalIgnoreCase)
                        || (e.Description ?? "").Contains(key, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();

        if (targets.Count == 0) return Task.FromResult("没有找到匹配「" + key + "」的表情包。");

        foreach (MemeEntry entry in targets)
        {
            try
            {
                string path = MemeStore.FullPath(entry);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "删除表情包文件失败：{File}", entry.File);
            }
            entries.Remove(entry);
        }
        MemeStore.Save(entries);

        return Task.FromResult("已删除 " + targets.Count + " 个表情包（"
                               + string.Join("、", targets.Select(t => t.Id)) + "）。库存还剩 " + entries.Count + " 个。");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("给库存里已有的表情包（重新）生成「含义/情绪/场合」语义标签，用来补标之前没标上的、或标签不准的条目")]
    public Task<string> MemeDescribe(
        [Description("要处理的表情包 ID 或关键词；留空表示处理全部没标注的条目")] string target = "",
        [Description("true 时连已经标注过的也重新打标，默认 false 只补没标注的")] bool force = false,
        [Description("本次最多处理几条，默认 30")] int limit = 30)
    {
        if (!Configuration.Enabled) return Task.FromResult("表情包库存未启用。");

        return RunAsync(async () =>
        {
            // 先查视觉模型是否可用：不可用时逐张去调只会拿到 30 条「未标注」，白等一场
            if (!Configuration.AutoDescribe || visionModel == null)
            {
                return "打标没跑（" + DescribeHint() + "）："
                       + (Configuration.AutoDescribe
                           ? "当前角色没有加载视觉模型，先在角色里挂上视觉模块。"
                           : "配置里关闭了「收藏后自动打标」。");
            }

            List<MemeEntry> entries = MemeStore.Load();
            if (entries.Count == 0) return "表情包库存还是空的，可以先用 MemeImportQQ 或 MemeCollect 攒一些。";

            List<MemeEntry> targets = SelectTargets(entries, target, force)
                .Take(Math.Clamp(limit, 1, 200))
                .ToList();

            if (targets.Count == 0)
            {
                return string.IsNullOrWhiteSpace(target)
                    ? "库存里没有需要补标的表情包（都已有标签；要重打全部可传 force=true）。"
                    : "没有找到匹配「" + target.Trim() + "」的表情包。";
            }

            int done = 0;
            int failed = 0;
            int missing = 0;
            foreach (MemeEntry entry in targets)
            {
                string path = MemeStore.FullPath(entry);
                if (!File.Exists(path))
                {
                    missing++;
                    logger.LogWarning("表情包文件丢失，跳过打标：{File}", entry.File);
                    continue;
                }

                string tag = await DescribeAsync(path, "");
                if (IsUntagged(tag)) { failed++; continue; }

                entry.Description = tag;
                done++;
                // 每张都落盘：中途被中断也不会把已经标好的丢掉
                MemeStore.Save(entries);
            }

            StringBuilder builder = new();
            builder.Append("已给 " + done + " 个表情包生成语义标签");
            if (failed > 0) builder.Append("，失败 " + failed + " 个（" + DescribeHint() + "）");
            if (missing > 0) builder.Append("，另有 " + missing + " 个图片文件已丢失");
            builder.Append("；库存共 " + entries.Count + " 个。");
            if (done > 0) builder.Append(" 可用 MemeList 查看标签。");
            return builder.ToString();
        }, "给表情包补标注失败");
    }

    /// <summary>还没打上标（空或占位串）。</summary>
    static bool IsUntagged(MemeEntry entry) => IsUntagged(entry.Description);

    /// <summary>标签是否仍是「没打上」的占位串。</summary>
    static bool IsUntagged(string? description) =>
        string.IsNullOrWhiteSpace(description) || description.Trim() == Untagged;

    /// <summary>挑出这次要打标的条目：给了 target 就按 ID/关键词匹配，否则挑没标注的（force 时全部）。</summary>
    static List<MemeEntry> SelectTargets(List<MemeEntry> entries, string target, bool force)
    {
        if (!string.IsNullOrWhiteSpace(target))
        {
            string key = target.Trim();
            return entries.Where(e =>
                    e.Id.Equals(key, StringComparison.OrdinalIgnoreCase)
                    || (e.Description ?? "").Contains(key, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return force ? [.. entries] : entries.Where(IsUntagged).ToList();
    }

    /// <summary>打标失败时给人一句能直接照着查的原因。</summary>
    string DescribeHint()
    {
        if (!Configuration.AutoDescribe) return "配置里关闭了自动打标";
        if (visionModel == null) return "当前角色没有加载视觉模型";
        return "视觉模型没返回可用内容，详见日志里「表情包语义打标」的告警";
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("把 QQ 账号里「收藏的表情」批量导入表情包库存（依赖 NapCat 的 fetch_custom_face 扩展接口），导入后即可按语义自主发送")]
    public async Task<string> MemeImportQQ(
        [Description("最多导入多少个，默认 30（接口上限 100）")] int count = 30,
        [Description("是否同时生成语义标签；数量多时会逐张调视觉模型，较慢；关掉则只入库，之后可用 MemeDescribe 补标")] bool describe = true)
    {
        if (!Configuration.Enabled) return "表情包库存未启用。";

        return await RunAsync(async () =>
        {
            OneBotClient client = RequireClient();

            JsonElement result;
            try
            {
                result = await client.CallActionRawAsync(
                    "fetch_custom_face", new { count = Math.Clamp(count, 1, 100) }, 60);
            }
            catch (Exception ex)
            {
                return "读取 QQ 收藏表情失败（fetch_custom_face）：" + ex.Message
                       + "。该接口是 NapCat 的扩展，其它协议端不提供；可以改用 MemeCollect 逐张收藏。";
            }

            if (!IsOk(result) || !result.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return "当前协议端没有返回收藏表情列表（fetch_custom_face 不受支持或返回异常），"
                       + "可以改用 MemeCollect 逐张收藏。";
            }

            List<string> urls = data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .Where(url => url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (urls.Count == 0) return "QQ 收藏表情是空的，或者接口没有返回可用图片。";

            List<MemeEntry> entries = MemeStore.Load();
            List<MemeEntry> newEntries = new();
            int max = Configuration.MaxEntries;
            int added = 0;
            int duplicated = 0;
            int failedCount = 0;
            bool hitLimit = false;

            foreach (string url in urls)
            {
                if (max > 0 && entries.Count >= max)
                {
                    hitLimit = true;
                    break;
                }

                try
                {
                    byte[] bytes = await LoadImageAsync(url, null);
                    if (bytes.Length == 0)
                    {
                        failedCount++;
                        continue;
                    }

                    string id = MemeStore.Fingerprint(bytes);
                    if (MemeStore.Find(entries, id) != null)
                    {
                        duplicated++;
                        continue;
                    }

                    string file = MemeStore.WriteImage(id, bytes, url);
                    MemeEntry entry = new()
                    {
                        Id = id,
                        File = file,
                        Description = Untagged,
                        AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        Source = "QQ 收藏表情"
                    };
                    entries.Add(entry);
                    newEntries.Add(entry);
                    added++;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "导入 QQ 收藏表情失败：{Url}", url);
                    failedCount++;
                }
            }

            // 先把图片落盘：打标要逐张调视觉模型，耗时可能很长，先保存一份避免中途中断时白干
            MemeStore.Save(entries);

            int described = 0;
            int tagFailed = 0;
            if (describe && newEntries.Count > 0)
            {
                foreach (MemeEntry entry in newEntries)
                {
                    entry.Description = await DescribeAsync(
                        Path.Combine(MemeStore.Directory, entry.File), "");
                    if (IsUntagged(entry)) tagFailed++; else described++;
                    // 每张都落盘：打标是逐张调模型，中途被打断也不会把已标好的全丢掉
                    MemeStore.Save(entries);
                }
            }

            StringBuilder builder = new();
            builder.Append("已从 QQ 收藏表情导入 " + added + " 个（重复 " + duplicated + " 个，失败 " + failedCount
                           + " 个），库存共 " + entries.Count + " 个。");
            if (describe && added > 0)
            {
                builder.Append(" 已生成 " + described + " 条语义标签");
                // 一张都没标上时必须说清原因，否则只会看到「已生成 0 条」而不知道卡在哪
                if (tagFailed > 0) builder.Append("，另有 " + tagFailed + " 条没标上（" + DescribeHint() + "）");
                builder.Append("。");
            }
            else if (!describe && added > 0)
            {
                builder.Append(" 本次未打标，可稍后用 MemeDescribe 批量补标。");
            }
            if (hitLimit) builder.Append(" 库存已达上限 " + max + "，剩余未导入。");
            return builder.ToString();
        }, "导入 QQ 收藏表情失败");
    }

    #endregion

    #region 来源解析与下载

    /// <summary>
    /// 解析 AI 给的来源：可能是裸网址、本地路径，也可能是一整段 CQ 码。
    /// 返回 (url, 本地路径, emojiId, emojiPackageId, key, summary)。
    /// </summary>
    static (string? Url, string? LocalPath, string EmojiId, string PackageId, string Key, string Summary) ParseSource(string source)
    {
        if (!source.Contains("[CQ:", StringComparison.OrdinalIgnoreCase))
        {
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return (source, null, "", "", "", "");
            return (null, source, "", "", "", "");
        }

        string url = CqValue(source, "url") ?? "";
        string file = CqValue(source, "file") ?? "";
        string emojiId = CqValue(source, "emoji_id") ?? "";
        string packageId = CqValue(source, "emoji_package_id") ?? "";
        string key = CqValue(source, "key") ?? "";
        string summary = CqValue(source, "summary") ?? "";

        // 表情包的 file 字段通常就是图片地址；url 字段优先
        string? media = !string.IsNullOrWhiteSpace(url) ? url
            : !string.IsNullOrWhiteSpace(file) ? file
            : null;
        if (media != null && !media.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(media))
        {
            media = null;
        }

        return (media?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? media : null,
                media?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? null : media,
                emojiId, packageId, key, summary);
    }

    /// <summary>
    /// 从 CQ 码里取值。取值到逗号为止；只有该字段正好是最后一个字段时，
    /// 才剥掉结尾那个属于 CQ 码本身的方括号，这样 summary=[动画表情] 这类自带方括号的值能完整保留。
    /// </summary>
    static string? CqValue(string text, string key)
    {
        Match match = Regex.Match(text, "(?:^|[,[])" + Regex.Escape(key) + "=([^,]*)");
        if (!match.Success) return null;

        string value = match.Groups[1].Value.Trim();
        int end = match.Index + match.Length;
        bool atTail = end >= text.Length || text[end..].Trim().Length == 0;
        if (atTail && value.EndsWith("]", StringComparison.Ordinal)) value = value[..^1];
        return value;
    }

    /// <summary>按来源取图片字节。</summary>
    async Task<byte[]> LoadImageAsync(string? url, string? localPath)
    {
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            if (!File.Exists(localPath)) throw new Exception("本地图片不存在：" + localPath);
            return await File.ReadAllBytesAsync(localPath);
        }
        if (string.IsNullOrWhiteSpace(url)) throw new Exception("无法识别的图片来源。");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(Math.Clamp(Configuration.DownloadTimeout, 5, 300)));
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Linux; Android 9) AppleWebKit/537.36");
        using HttpResponseMessage response = await Http.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cts.Token);
    }

    /// <summary>标签统一用「未标注」表示还没打上，判断时集中在这里。</summary>
    const string Untagged = "未标注";

    /// <summary>静图的打标提示词。画面内容只是判读手段，输出必须是「含义／情绪／场合」三段语义标签。</summary>
    const string StillPrompt =
        "这是聊天里用的表情包，不是一幅需要赏析的画。请判断主人把它发出去时想表达什么。\n"
        + "要求：\n"
        + "1. 先扫一眼画面，确认主体、构图和图中文字（避免误判），但不要把画面描述写进结果。\n"
        + "2. 重点在语义：这张图在聊天里代表什么态度或情绪，适合在什么场合甩出去。\n"
        + "严格按三行输出，不要解释、不要序号、不要任何额外文字：\n"
        + "含义：这张图发出去相当于在说哪句话，一句口语化的中文，不超过20字\n"
        + "情绪：2-3个情绪词，用/分隔\n"
        + "场合：2-4个适合发它的具体聊天场景，用/分隔";

    /// <summary>动图的打标提示词；上面会再拼一段文字头说明采样了哪几帧。</summary>
    const string AnimationPrompt =
        "上面是这张动图的时序信息，配图是它的采样帧拼图。这是聊天里用的表情包，请判断主人把它发出去时想表达什么。\n"
        + "要求：\n"
        + "1. 先逐帧看清每一格里的主体、动作和文字（不要只留意最显眼的运动），但不要把画面描述写进结果。\n"
        + "2. 重点在语义：这段动画在聊天里代表什么态度或情绪，适合在什么场合甩出去。\n"
        + "严格按三行输出，不要解释、不要序号、不要任何额外文字：\n"
        + "含义：这张图发出去相当于在说哪句话，一句口语化的中文，不超过20字\n"
        + "情绪：2-3个情绪词，用/分隔\n"
        + "场合：2-4个适合发它的具体聊天场景，用/分隔";

    /// <summary>打标的输出上限：三行标签三十来字就够，给 300 既能容错又不至于让模型长篇大论。</summary>
    const int DescribeMaxTokens = 300;

    /// <summary>用视觉模型生成语义标签；不可用时退回备注，并把失败原因写进日志。</summary>
    async Task<string> DescribeAsync(string imagePath, string remark)
    {
        string note = (remark ?? "").Trim();
        if (!Configuration.AutoDescribe || visionModel == null)
        {
            // 这条日志很关键：没有它的话，收藏结果只剩备注，根本看不出是「没视觉模型」还是「模型没认出内容」
            logger.LogInformation("表情包未打标（{Reason}），只保留备注：{Note}",
                Configuration.AutoDescribe ? "当前角色没有加载视觉模型" : "配置里关闭了自动打标",
                note.Length > 0 ? note : "（无备注）");
            return note.Length > 0 ? note : Untagged;
        }

        string? sheetPath = null;
        try
        {
            string? header = TryBuildAnimationSheet(imagePath, out sheetPath);
            string prompt = header == null ? StillPrompt : header + "\n\n" + AnimationPrompt;

            string raw = (await visionModel.QueryAsync(sheetPath ?? imagePath, prompt, DescribeMaxTokens) ?? "").Trim();

            // 视觉模型出错时返回的是「【API 错误】…」这类带【】前缀的中文串而不是异常，
            // 不拦下来的话错误文案会被当成标签存进库存（表现为「标签是个报错」）。
            if (raw.StartsWith("【", StringComparison.Ordinal))
            {
                logger.LogWarning("表情包语义打标失败：{Result}", Shorten(raw, 160));
                return note.Length > 0 ? note : Untagged;
            }

            string tag = NormalizeTag(raw);
            if (tag.Length == 0)
            {
                // 以前这里静默返回「未标注」，30 张图一张没标却查不到任何原因，只能靠日志定位
                logger.LogWarning("表情包语义打标没拿到内容（原始返回：{Raw}）",
                    raw.Length == 0 ? "（空）" : Shorten(raw, 160));
                return note.Length > 0 ? note : Untagged;
            }

            return note.Length > 0 && !tag.Contains(note, StringComparison.OrdinalIgnoreCase)
                ? note + "｜" + tag
                : tag;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "表情包语义打标失败，退回备注");
            return note.Length > 0 ? note : Untagged;
        }
        finally
        {
            if (sheetPath != null)
            {
                try { File.Delete(sheetPath); } catch { }
            }
        }
    }

    /// <summary>
    /// 把视觉模型的多行输出压成一行可检索的语义标签（含义｜情绪｜场合）。
    /// 多出来的行、模型自作主张的序号/项目符号/加粗标记都在这里清掉，最多 200 字。
    /// </summary>
    static string NormalizeTag(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        List<string> lines = new();
        foreach (string line in raw.Replace("\r", "").Split('\n'))
        {
            // 去掉行首的项目符号与「1.」「1、」这类序号，模型偶尔会无视「不要序号」
            string text = line.Trim().TrimStart('-', '*', '•', '·').Trim();
            text = Regex.Replace(text, @"^\d+\s*[.、)）]\s*", "").Replace("**", "").Trim();
            if (text.Length == 0) continue;

            lines.Add(text);
            if (lines.Count == 3) break;   // 只要含义/情绪/场合三行，多出来的（如「备注」）丢掉
        }

        string tag = string.Join("｜", lines).Trim();
        return tag.Length <= 200 ? tag : tag[..200];
    }

    /// <summary>
    /// 动图才做：抽若干帧拼成一张带时序标注的图，并给出交给模型的文字头。
    /// 静图、解析不出多帧、拼图失败一律返回 null（此时 sheetPath 也是 null），调用方按单帧走。
    /// </summary>
    string? TryBuildAnimationSheet(string imagePath, out string? sheetPath)
    {
        sheetPath = null;
        if (!Configuration.DescribeAnimation) return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(imagePath);
        }
        catch
        {
            return null;
        }

        GifInfo info = MemeAnimation.Parse(bytes);
        if (!info.Animated) return null;

        List<int> sampled = MemeAnimation.SampleIndices(info.Frames, Configuration.AnimationFrames);
        if (sampled.Count < 2) return null;

        string folder = Path.Combine(Path.GetTempPath(), "presents.QQSuite", "anim");
        string output = Path.Combine(folder, MemeStore.Fingerprint(bytes) + ".png");
        int maxEdge = Math.Clamp(Configuration.AnimationMaxEdge, 64, 1024);

        if (!MemeAnimation.TryComposeSheet(bytes, sampled, MemeAnimation.StartTimes(info.Delays), maxEdge, output, out string error))
        {
            logger.LogDebug("动图抽帧失败，按单帧打标：{Error}", error);
            return null;
        }

        sheetPath = output;
        return MemeAnimation.BuildHeader(info, sampled);
    }

    #endregion

    #region 检索与发送

    /// <summary>按语义挑一个：命中词最多的优先，其次使用次数多的。</summary>
    static MemeEntry? PickByQuery(List<MemeEntry> entries, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return entries[Random.Shared.Next(entries.Count)];
        }

        string[] words = query.Trim()
            .Split([' ', '，', ',', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) words = [query.Trim()];

        return entries
            .Select(entry => new
            {
                Entry = entry,
                Score = words.Count(word => Matches(entry, word))
            })
            .Where(pair => pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Entry.UseCount)
            .Select(pair => pair.Entry)
            .FirstOrDefault();
    }

    static bool Matches(MemeEntry entry, string word) =>
        (entry.Description ?? "").Contains(word, StringComparison.OrdinalIgnoreCase)
        || (entry.Summary ?? "").Contains(word, StringComparison.OrdinalIgnoreCase)
        || entry.Id.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>发送一个条目，返回实际使用的形态（mface / image）。</summary>
    async Task<string> SendEntryAsync(OneBotClient client, bool isGroup, long targetId, MemeEntry entry)
    {
        if (Configuration.PreferMface && entry.HasMface
            && await TrySendMfaceAsync(client, isGroup, targetId, entry))
        {
            return "mface";
        }

        string path = MemeStore.FullPath(entry);
        if (!File.Exists(path)) throw new Exception("表情包文件已丢失：" + entry.File);
        await SendImageAsync(client, isGroup, targetId, path);
        return "image";
    }

    /// <summary>按原生表情包发送；协议端不支持时返回 false，由调用方回退成图片。</summary>
    async Task<bool> TrySendMfaceAsync(OneBotClient client, bool isGroup, long targetId, MemeEntry entry)
    {
        try
        {
            object message = new object[]
            {
                new
                {
                    type = "mface",
                    data = new
                    {
                        emoji_id = entry.EmojiId,
                        emoji_package_id = entry.EmojiPackageId,
                        key = entry.EmojiKey,
                        summary = string.IsNullOrWhiteSpace(entry.Summary) ? "[动画表情]" : entry.Summary
                    }
                }
            };
            object parameters = isGroup
                ? new { group_id = targetId, message }
                : new { user_id = targetId, message };

            JsonElement result = await client.CallActionRawAsync(
                isGroup ? "send_group_msg" : "send_private_msg", parameters, 20);
            if (IsOk(result)) return true;

            logger.LogDebug("表情包 mface 发送未成功，回退图片：{Result}", result.ToString());
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "表情包 mface 发送异常，回退图片");
            return false;
        }
    }

    /// <summary>以图片消息段发送本地文件（比拼 CQ 码更能容忍路径里的逗号、方括号）。</summary>
    async Task SendImageAsync(OneBotClient client, bool isGroup, long targetId, string path)
    {
        object message = new object[]
        {
            new { type = "image", data = new { file = path.Replace('\\', '/') } }
        };
        object parameters = isGroup
            ? new { group_id = targetId, message }
            : new { user_id = targetId, message };

        await client.CallActionRawAsync(isGroup ? "send_group_msg" : "send_private_msg", parameters, 30);
    }

    static bool IsOk(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return false;
        if (result.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String)
        {
            return string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    static string Shorten(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max] + "…";
    }

    #endregion

    #region 执行壳

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

    #endregion

    #region 生命周期

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "表情包库存：可一键导入 QQ 账号里收藏的表情，也能收藏任意图片并自动打「含义/情绪/场合」语义标签，之后按含义自主发送到群聊或私聊。",
            Explanation = "MemeImportQQ 通过 NapCat 的 fetch_custom_face 把 QQ 收藏夹里的表情批量导入库存；"
                          + "MemeCollect 收藏单张图（网址、本地路径或完整 CQ 码），收藏时自动生成语义标签；"
                          + "标签写的是这张图发出去想表达什么意思、适合什么场合，画面内容只用于判读、不会写进标签；"
                          + "动图（GIF）会先采样多帧拼成一张带时序标注的图再交给视觉模型，避免只看到第一帧把标签打偏；"
                          + "MemeDescribe 给已有的（没标注或标签不准的）条目补标或重打，导入时没标上的用它救回来；"
                          + "MemeSend 按含义（query）或 ID 发送一个表情包，来源是 QQ 商城表情包的会优先按 mface 发送，失败自动回退成图片；"
                          + "MemeList 查看库存与语义，MemeForget 删除条目。"
                          + "库存文件在 Storage/Plugins/presents.QQSuite/meme，由插件私有管理。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);

        List<MemeEntry> entries = MemeStore.Load();
        interactor.Prompt($$"""
            你有一个「表情包库存」，聊天中按情绪自主发图比打字更自然。

            ## 提供函数
            {{handler.FunctionDocument()}}

            ## 当前库存
            {{BuildInjectedList(entries)}}

            ## 使用提示
            - 库存为空先用 MemeImportQQ 导入 QQ 收藏；想表达情绪用 MemeSend 传 query（如「无语」），不必先查 ID
            - 看到好图用 MemeCollect 收藏，未标注的用 MemeDescribe 补；没有合适的就不发，别硬凑
            """);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        logger.LogInformation("QQ 表情包库存 已启动（QQ收藏导入 / 收藏打标 / 语义检索 / 自主发送）");
        return Task.CompletedTask;
    }

    /// <summary>把高频表情包连同语义描述注入提示词，超量时按使用次数取前 N。</summary>
    string BuildInjectedList(List<MemeEntry> entries)
    {
        int limit = Configuration.InjectLimit;
        if (limit <= 0) return "（未注入，可调用 MemeList 查看）";

        List<MemeEntry> picked = entries
            .OrderByDescending(e => e.UseCount)
            .ThenByDescending(e => e.AddedAt, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        if (picked.Count == 0) return "（库存为空，可以用 MemeCollect 收藏）";

        StringBuilder builder = new();
        foreach (MemeEntry entry in picked)
        {
            builder.AppendLine("- " + entry.Id + "｜" + (string.IsNullOrWhiteSpace(entry.Description) ? "未标注" : entry.Description));
        }
        return builder.ToString().TrimEnd();
    }

    #endregion
}
