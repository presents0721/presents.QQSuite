using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using QQSuite.Core;

namespace QQSuite.QQForward;

/// <summary>跨群转发配置：白名单、转发形态、限额与自主巡逻。</summary>
public class ForwardConfig
{
    [DisplayName("启用跨群转发")]
    [Description("关闭后所有转发函数与自主巡逻都会停用")]
    public bool Enabled { get; set; } = true;

    [DisplayName("来源群白名单")]
    [Description("允许被读取、被转发的群号，逗号分隔；留空表示不允许从任何群转发")]
    public string AllowedSourceGroups { get; set; } = "";

    [DisplayName("目标群白名单")]
    [Description("允许接收转发内容的群号，逗号分隔；留空表示不允许转发到任何群")]
    public string AllowedTargetGroups { get; set; } = "";

    [DisplayName("默认转发形态")]
    [Description("merge=组合成聊天记录（合并转发卡片）；onebyone=逐条转发；single=只转第一条")]
    public string DefaultMode { get; set; } = "merge";

    [DisplayName("每日转发上限")]
    [Description("每天最多执行多少次转发（一次转发算一次，与包含几条消息无关）；0 表示不限")]
    public int DailyLimit { get; set; } = 20;

    [DisplayName("启用自主巡逻")]
    [Description("按间隔扫描来源群，把值得分享的消息清单交给 AI 自主挑选并转发；需要先配置来源群与目标群白名单")]
    public bool EnablePatrol { get; set; } = true;

    [DisplayName("巡逻间隔(分钟)")]
    [Description("两次自主巡逻之间的最小间隔（5-1440 分钟），默认 15 分钟")]
    public int PatrolInterval { get; set; } = 15;

    [DisplayName("巡逻扫描条数")]
    [Description("每个来源群每轮读取多少条最近消息（5-100），默认 30 条")]
    public int PatrolScanCount { get; set; } = 30;

    [DisplayName("每轮最多转发")]
    [Description("自主巡逻时每轮最多允许转发几条消息（1-20），默认 2 条，避免刷屏")]
    public int PatrolMaxPerRound { get; set; } = 2;

    [DisplayName("纯文本最短字数")]
    [Description("自主巡逻时，纯文字消息少于该字数不参与挑选（图片/视频/文件等不受限），默认 6 字；0 表示不限")]
    public int PatrolMinChars { get; set; } = 6;

    [DisplayName("巡逻默认目标群")]
    [Description("自主巡逻固定转发到的群号，必须也在目标群白名单里；留空则由 AI 从目标群白名单里自行选择")]
    public string PatrolDefaultTarget { get; set; } = "";

    [DisplayName("静默时段")]
    [Description("该时段内不巡逻、不自动转发，格式 HH:mm-HH:mm，可逗号分隔多段，例如 00:00-08:00；留空表示全天可巡逻")]
    public string SilentHours { get; set; } = "00:00-08:00";

    [DisplayName("兴趣偏好")]
    [Description("会附加到自主巡逻指令里，告诉 AI 该挑什么样的内容分享，例如「技术干货、搞笑、有共鸣的生活片段」")]
    public string InterestHint { get; set; } = "有趣、有信息量、能引起共鸣或让人眼前一亮的内容";
}

/// <summary>
/// 跨群转发：把来源群里选中的消息组合成聊天记录或逐条转发到目标群，
/// 并按兴趣定时巡逻、由 AI 自主挑选值得分享的内容。
/// </summary>
public class ForwardModule(
    XmlFunctionCaller functionCaller,
    ILogger<ForwardModule> logger,
    Interactor<ForwardModule> interactor,
    QChatService qChatService
) : ChatBehaviour, IConfigurable<ForwardConfig>
{
    public ForwardConfig Configuration { get; set; } = null!;

    const int MaxMessagesPerSend = 20;

    /// <summary>运行时开关（ForwardAuto 的 on/off），不写回配置。</summary>
    bool? patrolOverride;

    bool patrolRunning;
    bool lastPatrolLoaded;
    DateTime lastPatrol = DateTime.MinValue;
    bool missingWhitelistLogged;

    #region 对外 AI 函数

    [XmlFunction(FunctionMode.OneShot)]
    [Description("读取来源群最近的聊天记录，列出可多选转发的消息（带 message_id），只读不发送")]
    public async Task<string> ForwardScan(
        [Description("来源群号，必须在来源群白名单里")] long sourceGroupId,
        [Description("读取多少条最近消息（1-100），默认 30")] int count = 30,
        [Description("可选：按关键词过滤消息摘要，留空列出全部")] string keyword = "")
    {
        if (!Configuration.Enabled) return "跨群转发未启用。";
        if (sourceGroupId <= 0) return "来源群号不正确。";

        List<long> sources = ForwardText.ParseGroupIds(Configuration.AllowedSourceGroups);
        if (!sources.Contains(sourceGroupId))
        {
            return "来源群 " + sourceGroupId + " 不在来源群白名单里，不能读取。";
        }

        return await RunAsync(async () =>
        {
            OneBotClient client = RequireClient();
            JsonElement data = await FetchHistoryAsync(client, sourceGroupId, Math.Clamp(count, 1, 100));
            if (data.ValueKind != JsonValueKind.Object)
            {
                return "没有取到群 " + sourceGroupId + " 的聊天记录。";
            }

            long botId = qChatService.BotId;
            ForwardState state = ForwardStore.Load();
            string groupName = await GetGroupNameAsync(client, sourceGroupId);
            string key = keyword.Trim();
            StringBuilder builder = new();
            int total = 0;

            foreach (JsonElement item in ReadMessages(data))
            {
                long messageId = ForwardText.GetLong(item, "message_id");
                long userId = ForwardText.GetLong(item, "user_id");
                if (messageId <= 0 || userId == botId) continue;

                string raw = ForwardText.GetString(item, "raw_message");
                string summary = ForwardText.Summarize(raw);
                if (key.Length > 0 && !summary.Contains(key, StringComparison.OrdinalIgnoreCase)) continue;

                total++;
                builder.AppendLine("- #" + messageId
                                   + "｜" + Nickname(item)
                                   + "｜" + FormatTime(item)
                                   + "｜" + summary
                                   + (state.IsForwarded(sourceGroupId, messageId) ? "（已转发过）" : ""));
            }

            if (total == 0)
            {
                return key.Length == 0
                    ? "群 " + sourceGroupId + " 最近没有可转发的消息。"
                    : "群 " + sourceGroupId + " 最近没有匹配「" + key + "」的消息。";
            }

            string header = "来源群" + DisplayGroup(sourceGroupId, groupName) + "最近 " + total + " 条可转发消息：";
            return header + "\n" + builder
                + "提示：用 ForwardSend 传入本条消息的 #ID（可多选，逗号分隔），mode 选 merge / onebyone / single。";
        }, "扫描来源群失败");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("把来源群里选中的多条消息转发到目标群：merge 组合成聊天记录，onebyone 逐条转发，single 只转第一条")]
    public async Task<string> ForwardSend(
        [Description("来源群号，必须在来源群白名单里")] long sourceGroupId,
        [Description("要转发的消息 message_id，多个用逗号/空格分隔（最多 20 条）")] string messageIds,
        [Description("目标群号，必须在目标群白名单里")] long targetGroupId,
        [Description("merge=组合成聊天记录（默认）；onebyone=逐条转发；single=只转第一条")] string mode = "",
        [Description("可选：转发后附一句简短的感想或推荐理由，留空不发")] string comment = "")
    {
        if (!Configuration.Enabled) return "跨群转发未启用。";
        if (sourceGroupId <= 0 || targetGroupId <= 0) return "群号不正确。";

        List<long> sources = ForwardText.ParseGroupIds(Configuration.AllowedSourceGroups);
        if (!sources.Contains(sourceGroupId))
        {
            return "来源群 " + sourceGroupId + " 不在来源群白名单里，不能转发。";
        }
        List<long> targets = ForwardText.ParseGroupIds(Configuration.AllowedTargetGroups);
        if (!targets.Contains(targetGroupId))
        {
            return "目标群 " + targetGroupId + " 不在目标群白名单里，不能转发。";
        }

        List<long> ids = ParseMessageIds(messageIds);
        if (ids.Count == 0) return "请给出要转发的 message_id（可先用 ForwardScan 查看）。";
        if (ids.Count > MaxMessagesPerSend) return "一次最多转发 " + MaxMessagesPerSend + " 条，请减少一些再试。";

        return await RunAsync(async () =>
        {
            DateTime now = DateTime.Now;
            ForwardState state = ForwardStore.Load();
            state.NormalizeDaily(now);

            int dailyLimit = Configuration.DailyLimit;
            if (dailyLimit > 0 && state.DailyCount >= dailyLimit)
            {
                return "今天的转发次数已用完（" + state.DailyCount + " / " + dailyLimit + "），明天再分享吧。";
            }

            OneBotClient client = RequireClient();
            List<MessageDetail> details = new();
            List<string> skipped = new();

            foreach (long id in ids)
            {
                if (state.IsForwarded(sourceGroupId, id))
                {
                    skipped.Add("#" + id + " 已经转发过");
                    continue;
                }
                MessageDetail? detail = await TryGetDetailAsync(client, id, sourceGroupId);
                if (detail == null)
                {
                    skipped.Add("#" + id + " 无法读取或不属于该来源群");
                    continue;
                }
                details.Add(detail);
            }

            if (details.Count == 0)
            {
                return "没有可转发的消息：" + string.Join("；", skipped);
            }

            string resolvedMode = ForwardText.NormalizeMode(
                string.IsNullOrWhiteSpace(mode) ? Configuration.DefaultMode : mode);
            if (resolvedMode == "single" && details.Count > 1)
            {
                details = details.Take(1).ToList();
            }

            string targetName = await GetGroupNameAsync(client, targetGroupId);
            (List<MessageDetail> sentList, string note) = await SendAsync(client, targetGroupId, details, resolvedMode);

            if (sentList.Count == 0)
            {
                return "转发失败：" + note;
            }

            state.CountForward(now, dailyLimit);
            foreach (MessageDetail detail in sentList)
            {
                state.MarkForwarded(sourceGroupId, detail.MessageId, now);
            }
            ForwardStore.Save(state);

            if (!string.IsNullOrWhiteSpace(comment))
            {
                await TrySendCommentAsync(client, targetGroupId, comment);
            }

            StringBuilder result = new();
            result.Append("已把 ").Append(sentList.Count).Append(" 条消息以「")
                .Append(ForwardText.ModeDescription(resolvedMode)).Append("」发到")
                .Append(DisplayGroup(targetGroupId, targetName)).Append("。");
            if (note.Length > 0) result.Append("\n").Append(note);
            if (skipped.Count > 0) result.Append("\n跳过：").Append(string.Join("；", skipped));
            result.Append("\n今日已用 ").Append(state.DailyCount);
            if (dailyLimit > 0) result.Append(" / ").Append(dailyLimit);
            result.Append(" 次。");
            return result.ToString();
        }, "转发消息失败");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看或切换跨群转发的自主巡逻：status 查看状态，on 开启，off 关闭，once 立即巡逻一轮")]
    public async Task<string> ForwardAuto(
        [Description("status / on / off / once，默认 status")] string action = "status")
    {
        if (!Configuration.Enabled) return "跨群转发未启用。";

        string act = (action ?? "").Trim().ToLowerInvariant();
        switch (act)
        {
            case "on" or "开启" or "enable" or "enabled":
                patrolOverride = true;
                lastPatrolLoaded = true;
                lastPatrol = DateTime.MinValue;
                return "自主巡逻已开启（间隔 " + Math.Clamp(Configuration.PatrolInterval, 5, 1440)
                       + " 分钟）。\n" + StatusText();

            case "off" or "关闭" or "disable" or "disabled":
                patrolOverride = false;
                return "自主巡逻已关闭。\n" + StatusText();

            case "once" or "now" or "立即" or "现在" or "run":
                return await PatrolAsync();

            default:
                return StatusText();
        }
    }

    #endregion

    #region 发送

    async Task<(List<MessageDetail> sent, string note)> SendAsync(
        OneBotClient client, long targetGroupId, List<MessageDetail> details, string mode)
    {
        if (mode == "merge")
        {
            // 首选：按 message_id 引用。协议端会自己填充内容，
            // 因此聊天记录、图片、表情都能原样搬运，且保留原作者。
            JsonElement idRaw = await client.CallActionRawAsync("send_group_forward_msg",
                new { group_id = targetGroupId, messages = ForwardText.BuildIdNodes(details.Select(d => d.MessageId)) }, 60);
            if (IsOk(idRaw))
            {
                return (details, "");
            }
            logger.LogWarning("按 message_id 合并转发失败 retcode={Code} {Message}，改用内容节点",
                ForwardText.GetLong(idRaw, "retcode"), ForwardText.GetString(idRaw, "message"));

            // 次选：自己拼内容节点。但聊天记录塞不进节点 content，只能跳过。
            List<object> nodes = new();
            List<MessageDetail> inNodes = new();
            foreach (MessageDetail detail in details)
            {
                if (detail.Segments.Count == 0) continue;
                inNodes.Add(detail);
                nodes.Add(new
                {
                    type = "node",
                    data = new
                    {
                        uin = detail.UserId.ToString(),
                        name = string.IsNullOrWhiteSpace(detail.Nickname) ? "QQ用户" : detail.Nickname,
                        content = detail.Segments.Select(segment => (object)new
                        {
                            type = segment.Type,
                            data = segment.Data
                        }).ToArray()
                    }
                });
            }

            int dropped = details.Count - inNodes.Count;

            if (nodes.Count > 0)
            {
                JsonElement raw = await client.CallActionRawAsync("send_group_forward_msg",
                    new { group_id = targetGroupId, messages = nodes }, 60);
                if (IsOk(raw))
                {
                    return (inNodes, dropped > 0
                        ? "有 " + dropped + " 条消息（多为聊天记录）不适合放进节点，已跳过。"
                        : "");
                }

                logger.LogWarning("内容节点合并转发失败 retcode={Code} {Message}，改用逐条转发",
                    ForwardText.GetLong(raw, "retcode"), ForwardText.GetString(raw, "message"));
            }

            // 兜底：逐条转发
            List<MessageDetail> fallbackSent = await SendOneByOneAsync(client, targetGroupId, details);
            return (fallbackSent, "合并转发不可用，已改用逐条转发，成功 " + fallbackSent.Count
                                  + " / " + details.Count + " 条。");
        }

        if (mode == "single")
        {
            MessageDetail detail = details[0];
            JsonElement raw = await client.CallActionRawAsync("forward_group_single_msg",
                new { group_id = targetGroupId, message_id = detail.MessageId }, 60);
            return IsOk(raw)
                ? (new List<MessageDetail> { detail }, "")
                : (new List<MessageDetail>(), "协议端返回：" + ForwardText.GetString(raw, "message"));
        }

        List<MessageDetail> oneByOne = await SendOneByOneAsync(client, targetGroupId, details);
        return (oneByOne, oneByOne.Count < details.Count
            ? "有 " + (details.Count - oneByOne.Count) + " 条转发失败。"
            : "");
    }

    async Task<List<MessageDetail>> SendOneByOneAsync(OneBotClient client, long targetGroupId, List<MessageDetail> details)
    {
        List<MessageDetail> sent = new();
        foreach (MessageDetail detail in details)
        {
            try
            {
                JsonElement raw = await client.CallActionRawAsync("forward_group_single_msg",
                    new { group_id = targetGroupId, message_id = detail.MessageId }, 60);
                if (IsOk(raw))
                {
                    sent.Add(detail);
                }
                else
                {
                    logger.LogWarning("逐条转发 #{Id} 失败 retcode={Code} {Message}",
                        detail.MessageId, ForwardText.GetLong(raw, "retcode"), ForwardText.GetString(raw, "message"));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "逐条转发 #{Id} 失败", detail.MessageId);
            }
            await Task.Delay(600);
        }
        return sent;
    }

    async Task TrySendCommentAsync(OneBotClient client, long targetGroupId, string comment)
    {
        try
        {
            string text = StripCqCodes(comment.Trim());
            if (text.Length == 0) return;
            if (text.Length > 300) text = text[..300] + "…";
            await client.CallActionAsync<JsonElement>("send_group_msg", new { group_id = targetGroupId, message = text });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "发送转发感想失败");
        }
    }

    static string StripCqCodes(string text) => Regex.Replace(text, @"\[CQ:[^\]]*\]", "").Trim();

    /// <summary>协议端返回里必须真的带 retcode 才能判定成功，缺字段一律视为失败。</summary>
    static bool IsOk(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return false;
        if (!raw.TryGetProperty("retcode", out JsonElement code)) return false;
        if (code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out long number)) return number is 0 or 1;
        if (code.ValueKind == JsonValueKind.String
            && long.TryParse(code.GetString(), out long parsed)) return parsed is 0 or 1;
        return false;
    }

    #endregion

    #region 消息读取

    async Task<JsonElement> FetchHistoryAsync(OneBotClient client, long groupId, int count)
    {
        JsonElement raw = await client.CallActionRawAsync("get_group_msg_history",
            new { group_id = groupId, count }, 30);
        if (!raw.TryGetProperty("data", out JsonElement data)) return default;
        return data;
    }

    static IEnumerable<JsonElement> ReadMessages(JsonElement data) => ForwardText.ReadHistoryMessages(data);

    static string Nickname(JsonElement item) => ForwardText.ReadNickname(item);

    static string FormatTime(JsonElement item) => ForwardText.FormatClock(item);

    async Task<MessageDetail?> TryGetDetailAsync(OneBotClient client, long messageId, long sourceGroupId)
    {
        try
        {
            JsonElement raw = await client.CallActionRawAsync("get_msg", new { message_id = messageId }, 20);
            if (!IsOk(raw)) return null;
            if (!raw.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // 安全校验：确认这条消息确实属于来源群，避免用别的群的消息 ID 越权转发
            long groupId = ForwardText.GetLong(data, "group_id");
            if (groupId != 0 && groupId != sourceGroupId) return null;

            JsonElement content = data.TryGetProperty("message", out JsonElement message)
                ? message
                : default;
            JsonElement sender = data.TryGetProperty("sender", out JsonElement senderElement)
                ? senderElement
                : default;
            string nickname = ForwardText.GetString(sender, "card");
            if (string.IsNullOrWhiteSpace(nickname)) nickname = ForwardText.GetString(sender, "nickname");

            return new MessageDetail
            {
                MessageId = messageId,
                UserId = ForwardText.GetLong(data, "user_id"),
                Nickname = nickname,
                Segments = ForwardText.BuildNodeSegments(content)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取消息 #{Id} 失败", messageId);
            return null;
        }
    }

    static List<long> ParseMessageIds(string? text)
    {
        List<long> ids = new();
        if (string.IsNullOrWhiteSpace(text)) return ids;
        foreach (string part in text.Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string trimmed = part.TrimStart('#');
            if (long.TryParse(trimmed, out long id) && id > 0 && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    static string DisplayGroup(long groupId, string? name) =>
        string.IsNullOrWhiteSpace(name) ? "群 " + groupId : "群「" + name + "」" + groupId;

    async Task<string> GetGroupNameAsync(OneBotClient client, long groupId)
    {
        try
        {
            JsonElement data = await client.CallActionAsync<JsonElement>("get_group_info", new { group_id = groupId });
            return ForwardText.GetString(data, "group_name");
        }
        catch
        {
            return "";
        }
    }

    #endregion

    #region 自主巡逻

    bool PatrolEnabled => patrolOverride ?? Configuration.EnablePatrol;

    void MaybePatrol()
    {
        if (!Configuration.Enabled || !PatrolEnabled || patrolRunning) return;

        List<long> sources = ForwardText.ParseGroupIds(Configuration.AllowedSourceGroups);
        List<long> targets = ForwardText.ParseGroupIds(Configuration.AllowedTargetGroups);
        if (sources.Count == 0 || targets.Count == 0)
        {
            if (!missingWhitelistLogged)
            {
                missingWhitelistLogged = true;
                logger.LogInformation("跨群转发自主巡逻已启用，但来源群或目标群白名单为空，暂不巡逻。");
            }
            return;
        }

        DateTime now = DateTime.Now;
        if (ForwardText.IsInSilentHours(now, Configuration.SilentHours)) return;

        int interval = Math.Clamp(Configuration.PatrolInterval, 5, 1440);
        if (!lastPatrolLoaded)
        {
            lastPatrolLoaded = true;
            lastPatrol = ParseTimestamp(ForwardStore.Load().LastPatrol) ?? DateTime.MinValue;
        }
        if ((now - lastPatrol).TotalMinutes < interval) return;

        ForwardState state = ForwardStore.Load();
        state.NormalizeDaily(now);
        if (state.RemainingToday(Configuration.DailyLimit) <= 0) return;

        lastPatrol = now;
        patrolRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                string result = await PatrolAsync();
                logger.LogInformation("跨群转发巡逻：{Result}", result);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "跨群转发自主巡逻失败");
            }
            finally
            {
                patrolRunning = false;
            }
        });
    }

    async Task<string> PatrolAsync()
    {
        if (!Configuration.Enabled) return "跨群转发未启用。";

        OneBotClient? client = qChatService.Client;
        if (client is not { IsConnected: true }) return "QQ 未连接，跳过本轮巡逻。";

        List<long> sources = ForwardText.ParseGroupIds(Configuration.AllowedSourceGroups);
        List<long> targets = ForwardText.ParseGroupIds(Configuration.AllowedTargetGroups);
        if (sources.Count == 0) return "未配置来源群白名单，跳过巡逻。";
        if (targets.Count == 0) return "未配置目标群白名单，跳过巡逻。";

        DateTime now = DateTime.Now;
        if (ForwardText.IsInSilentHours(now, Configuration.SilentHours))
        {
            return "当前处于静默时段（" + Configuration.SilentHours + "），不巡逻。";
        }

        ForwardState state = ForwardStore.Load();
        state.NormalizeDaily(now);
        int dailyLimit = Configuration.DailyLimit;
        if (state.RemainingToday(dailyLimit) <= 0)
        {
            return "今日转发次数已用完，不再巡逻。";
        }

        int scan = Math.Clamp(Configuration.PatrolScanCount, 5, 100);
        int maxPerRound = Math.Clamp(Configuration.PatrolMaxPerRound, 1, MaxMessagesPerSend);
        int minChars = Math.Max(0, Configuration.PatrolMinChars);
        int windowMinutes = Math.Clamp(Configuration.PatrolInterval, 5, 1440) * 2 + 30;
        long botId = qChatService.BotId;

        List<ForwardHistory> histories = new();
        foreach (long sourceGroupId in sources)
        {
            try
            {
                JsonElement data = await FetchHistoryAsync(client, sourceGroupId, scan);
                histories.Add(new ForwardHistory(sourceGroupId, await GetGroupNameAsync(client, sourceGroupId), data));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "巡逻读取群 {Group} 失败", sourceGroupId);
            }
        }

        List<ForwardCandidate> candidates = ForwardText.CollectCandidates(
            histories, botId, now, windowMinutes, minChars, state.IsForwarded);

        // 群发太多会像刷屏：同一来源群只留最新的几条，再整体取最近的一批
        candidates = candidates
            .GroupBy(candidate => candidate.SourceGroupId)
            .SelectMany(group => group.OrderByDescending(candidate => candidate.Time).Take(maxPerRound * 3))
            .OrderBy(candidate => candidate.Time)
            .ToList();

        if (candidates.Count == 0)
        {
            state.LastPatrol = now.ToString("yyyy-MM-dd HH:mm:ss");
            ForwardStore.Save(state);
            return "本轮没有新的候选消息。";
        }

        string targetHint = BuildTargetHint(targets);
        StringBuilder instruction = new();
        instruction.AppendLine("【定时巡逻｜跨群转发】浏览下面的候选消息，挑出最值得分享的 0~" + maxPerRound + " 条，用 ForwardSend 转发到" + targetHint + "。");
        instruction.AppendLine("- 只转发确实有趣、有信息量、能引起共鸣的内容；没有合适的就不转发，不要硬凑。");
        instruction.AppendLine("- 不要转发广告、刷屏、争吵、隐私或负面情绪的内容；也不要转发自己的消息。");
        instruction.AppendLine("- sourceGroupId 用候选行里的群号，messageIds 填选中的 #ID（逗号分隔）；同一来源的消息优先合并成一条聊天记录。");
        instruction.AppendLine("- 转发形态用「" + ForwardText.ModeDescription(ForwardText.NormalizeMode(Configuration.DefaultMode)) + "」。");
        instruction.AppendLine("- 可以给转发配一句简短自然、不重复的推荐语（comment），也可以不配。");
        instruction.AppendLine("候选消息：");
        foreach (ForwardCandidate candidate in candidates)
        {
            instruction.Append("- [群 ").Append(candidate.SourceGroupId)
                .Append(string.IsNullOrWhiteSpace(candidate.GroupName) ? "" : "「" + candidate.GroupName + "」")
                .Append("] #").Append(candidate.MessageId)
                .Append("｜").Append(candidate.Nickname)
                .Append("｜").Append(candidate.Time.ToString("HH:mm"))
                .Append("｜").Append(candidate.Summary)
                .Append(candidate.HasMedia ? "（含媒体）" : "")
                .AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(Configuration.InterestHint))
        {
            instruction.AppendLine("偏好：" + Configuration.InterestHint.Trim());
        }
        instruction.Append("（静默任务：只调用 ForwardSend 完成分享，不要向任何群聊发送闲聊回复。）");

        interactor.Poke(instruction.ToString());

        state.LastPatrol = now.ToString("yyyy-MM-dd HH:mm:ss");
        ForwardStore.Save(state);
        logger.LogInformation("巡逻产出 {Count} 条候选，已交给 AI 挑选", candidates.Count);
        return "巡逻完成：扫描 " + sources.Count + " 个来源群，候选 " + candidates.Count
               + " 条，已交给 AI 挑选（每轮最多 " + maxPerRound + " 条）。";
    }

    string BuildTargetHint(List<long> targets)
    {
        long preferred = ForwardText.ParseGroupIds(Configuration.PatrolDefaultTarget).FirstOrDefault();
        if (preferred > 0 && targets.Contains(preferred))
        {
            return "群 " + preferred;
        }
        return "目标群白名单里的任意一个（" + string.Join("、", targets) + "，目标群号填其中一个）";
    }

    string StatusText()
    {
        ForwardState state = ForwardStore.Load();
        state.NormalizeDaily(DateTime.Now);
        List<long> sources = ForwardText.ParseGroupIds(Configuration.AllowedSourceGroups);
        List<long> targets = ForwardText.ParseGroupIds(Configuration.AllowedTargetGroups);

        StringBuilder builder = new();
        builder.AppendLine("跨群转发：" + (Configuration.Enabled ? "已启用" : "已停用"));
        builder.AppendLine("自主巡逻：" + (PatrolEnabled ? "开启" : "关闭")
                           + "，间隔 " + Math.Clamp(Configuration.PatrolInterval, 5, 1440)
                           + " 分钟，每轮最多 " + Math.Clamp(Configuration.PatrolMaxPerRound, 1, MaxMessagesPerSend) + " 条");
        builder.AppendLine("来源群：" + (sources.Count == 0 ? "未配置（不会巡逻）" : string.Join("、", sources)));
        builder.AppendLine("目标群：" + (targets.Count == 0 ? "未配置（不能转发）" : string.Join("、", targets)));
        builder.AppendLine("默认形态：" + ForwardText.ModeDescription(ForwardText.NormalizeMode(Configuration.DefaultMode)));
        builder.AppendLine("静默时段：" + (string.IsNullOrWhiteSpace(Configuration.SilentHours) ? "不限" : Configuration.SilentHours.Trim()));
        builder.AppendLine("今日已转发：" + state.DailyCount
                           + (Configuration.DailyLimit > 0 ? " / " + Configuration.DailyLimit : "（不限）") + " 次");
        builder.AppendLine("累计去重记录：" + state.Forwarded.Count + " 条");
        builder.Append("上次巡逻：" + (string.IsNullOrWhiteSpace(state.LastPatrol) ? "尚未巡逻" : state.LastPatrol));
        return builder.ToString();
    }

    static DateTime? ParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParse(text, out DateTime value) ? value : null;
    }

    #endregion

    #region 辅助

    async Task<string> RunAsync(Func<Task<string>> action, string errorPrefix)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Prefix}", errorPrefix);
            return errorPrefix + "：" + ex.Message;
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

    sealed class MessageDetail
    {
        public long MessageId { get; init; }
        public long UserId { get; init; }
        public string Nickname { get; init; } = "";
        public List<ForwardSegment> Segments { get; init; } = new();
    }

    #endregion

    #region 生命周期

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "跨群转发：把来源群里有意思的消息多选转发到目标群，可组合成聊天记录或逐条发送，并支持定时自主巡逻。",
            Explanation = "ForwardScan 读取来源群最近的聊天记录并列出可转发的消息（带 message_id）；"
                          + "ForwardSend 把选中的多条消息转发到目标群，mode=merge 组合成聊天记录、onebyone 逐条发送、single 只转第一条。"
                          + "merge 与 single 都按 message_id 引用原消息，所以文字、图片、表情、语音、文件乃至别人发的整张聊天记录都能原样转发，作者与昵称保持原样。"
                          + "comment 会作为一条独立的文字消息发在卡片之后，不会跑进卡片里。"
                          + "ForwardAuto 查看/开关自主巡逻，或立即巡逻一轮。"
                          + "来源群与目标群都必须在设置里填写白名单；自主巡逻会按间隔把候选清单交给 AI，按兴趣偏好自主挑选。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);

        interactor.Prompt($$"""
            你具备「跨群转发」能力，把来源群有趣的内容分享到目标群。

            ## 提供函数
            {{handler.FunctionDocument()}}

            ## 使用提示
            - 先 ForwardScan 看最近消息，再用 ForwardSend 传 #ID：merge=聊天记录卡片，onebyone=逐条，single=只发第一条
            - 附 comment 一句自然、不重复的感想；来源群/目标群必须在白名单，否则会被拒
            - 自主巡逻会定时把候选交给你挑；受每日限额约束，不值得就别转
            """);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        logger.LogInformation("QQ 跨群转发 已启动（多选合并转发 / 逐条转发 / 白名单 / 自主巡逻）");
        return Task.CompletedTask;
    }

    /// <summary>框架每分钟调用一次：到间隔且有候选时触发一轮自主巡逻。</summary>
    protected override Task OnUpdate()
    {
        try
        {
            MaybePatrol();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "跨群转发巡逻调度失败");
        }
        return Task.CompletedTask;
    }

    #endregion
}
