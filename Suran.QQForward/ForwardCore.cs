using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alife.Foundation;

namespace QQSuite.QQForward;

/// <summary>一条消息里可以放进合并转发节点的段。</summary>
public sealed record ForwardSegment(string Type, JsonElement Data);

/// <summary>自主巡逻筛出来的一条候选项。</summary>
public sealed record ForwardCandidate(
    long SourceGroupId,
    string GroupName,
    long MessageId,
    long UserId,
    string Nickname,
    string Summary,
    bool HasMedia,
    DateTime Time);

/// <summary>某个来源群最近的历史记录，供候选筛选使用。</summary>
public sealed record ForwardHistory(long GroupId, string GroupName, JsonElement Data);

/// <summary>跨群转发的持久化状态：去重集合、每日计数、上次巡逻时间。</summary>
public sealed class ForwardState
{
    /// <summary>今日日期（yyyy-MM-dd），跨日时清零每日计数。</summary>
    public string DailyDate { get; set; } = "";

    /// <summary>今日已转发次数（每次转发算一次，与包含几条消息无关）。</summary>
    public int DailyCount { get; set; }

    /// <summary>上次自主巡逻时间（yyyy-MM-dd HH:mm:ss）。</summary>
    public string LastPatrol { get; set; } = "";

    /// <summary>已转发过的消息：键为「来源群号:消息ID」，值为转发时间。</summary>
    public Dictionary<string, string> Forwarded { get; set; } = new();

    static string Key(long sourceGroupId, long messageId) => sourceGroupId + ":" + messageId;

    public bool IsForwarded(long sourceGroupId, long messageId) =>
        Forwarded.ContainsKey(Key(sourceGroupId, messageId));

    public void MarkForwarded(long sourceGroupId, long messageId, DateTime now)
    {
        Forwarded[Key(sourceGroupId, messageId)] = now.ToString("yyyy-MM-dd HH:mm:ss");
        Trim(2000);
    }

    /// <summary>日期变了就把每日计数清零；返回是否发生了重置。</summary>
    public bool NormalizeDaily(DateTime now)
    {
        string today = now.ToString("yyyy-MM-dd");
        if (string.Equals(DailyDate, today, StringComparison.Ordinal))
        {
            return false;
        }
        DailyDate = today;
        DailyCount = 0;
        return true;
    }

    /// <summary>记录一次转发并返回今日已用次数。</summary>
    public int CountForward(DateTime now, int dailyLimit)
    {
        NormalizeDaily(now);
        if (dailyLimit > 0 && DailyCount >= dailyLimit)
        {
            return DailyCount;
        }
        DailyCount++;
        return DailyCount;
    }

    public int RemainingToday(int dailyLimit)
    {
        if (dailyLimit <= 0) return int.MaxValue;
        return Math.Max(0, dailyLimit - Math.Max(0, DailyCount));
    }

    /// <summary>只保留最近的记录，避免状态文件无限膨胀。</summary>
    public void Trim(int max)
    {
        if (Forwarded.Count <= max) return;
        foreach (KeyValuePair<string, string> pair in Forwarded
                     .OrderBy(item => item.Value, StringComparer.Ordinal)
                     .Take(Forwarded.Count - max)
                     .ToList())
        {
            Forwarded.Remove(pair.Key);
        }
    }
}

/// <summary>转发状态的原子读写（先写临时文件再替换）。</summary>
public static class ForwardStore
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>状态目录：Storage/Plugins/presents.QQSuite/forward。</summary>
    public static string Directory => Path.Combine(
        AlifePath.StorageFolderPath, "Plugins", "presents.QQSuite", "forward");

    public static ForwardState Load() => LoadFrom(Directory, "state");

    public static void Save(ForwardState state) => SaveTo(state, Directory, "state");

    public static ForwardState LoadFrom(string directory, string name)
    {
        lock (Gate)
        {
            try
            {
                string path = Path.Combine(directory, name + ".json");
                if (!File.Exists(path)) return new ForwardState();
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new ForwardState();
                return JsonSerializer.Deserialize<ForwardState>(json, Options) ?? new ForwardState();
            }
            catch
            {
                return new ForwardState();
            }
        }
    }

    public static void SaveTo(ForwardState state, string directory, string name)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, name + ".json");
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
                File.Move(temp, path, true);
            }
            catch
            {
                // 状态只影响去重与限额记忆，写盘失败不阻断转发主流程
            }
        }
    }
}

/// <summary>转发相关的纯逻辑：群号解析、消息摘要、节点裁剪、静默时段。</summary>
public static class ForwardText
{
    static readonly Regex CqCode = new(@"\[CQ:([a-zA-Z_]+)(?:,[^\]]*)?\]", RegexOptions.Compiled);
    static readonly Regex CqType = new(@"\[CQ:([a-zA-Z_]+)", RegexOptions.Compiled);

    /// <summary>可放进合并转发节点的消息段类型；其余（at/reply/forward 等）一律丢弃。</summary>
    static readonly HashSet<string> CopyableSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "image", "mface", "face", "video", "record", "file"
    };

    /// <summary>解析逗号/空格/分号/换行分隔的群号，去重并保持顺序。</summary>
    public static List<long> ParseGroupIds(string? text)
    {
        List<long> result = new();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (string part in text.Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0)
            {
                if (!result.Contains(id)) result.Add(id);
            }
        }
        return result;
    }

    /// <summary>把 mode 归一化成 merge / onebyone / single。</summary>
    public static string NormalizeMode(string? mode)
    {
        string value = (mode ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "onebyone" or "one" or "single_each" or "逐条" => "onebyone",
            "single" or "one_only" or "单条" => "single",
            _ => "merge"
        };
    }

    public static string ModeDescription(string mode) => mode switch
    {
        "onebyone" => "逐条转发",
        "single" => "单条转发",
        _ => "组合成聊天记录"
    };

    /// <summary>至少包含图片/语音/视频/文件/卡片等非纯文本内容。</summary>
    public static bool HasMedia(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        Match match = CqType.Match(raw);
        while (match.Success)
        {
            string type = match.Groups[1].Value;
            if (type is "image" or "mface" or "face" or "video" or "record" or "file"
                or "forward" or "json" or "xml")
            {
                return true;
            }
            match = match.NextMatch();
        }
        return false;
    }

    /// <summary>把 raw_message 里的 CQ 码换成人能读的标记，用于候选清单。</summary>
    public static string Summarize(string? raw, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "[消息]";
        bool sticker = raw.Contains("动画表情", StringComparison.Ordinal);
        string text = CqCode.Replace(raw, match =>
        {
            string type = match.Groups[1].Value;
            return type switch
            {
                "image" => sticker ? "[表情]" : "[图片]",
                "mface" => "[表情]",
                "face" => "[表情]",
                "video" => "[视频]",
                "record" => "[语音]",
                "file" => "[文件]",
                "forward" => "[聊天记录]",
                "json" or "xml" => "[卡片]",
                "at" => "",
                "reply" => "",
                _ => ""
            };
        });
        text = text.Replace("&#91;", "[").Replace("&#93;", "]")
            .Replace("&amp;", "&").Replace("&#44;", ",")
            .Replace("\r", " ").Replace("\n", " ");
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        if (text.Length == 0) return "[消息]";
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>
    /// 只保留可复制的消息段；text 为空时丢弃。
    /// 注意：聊天记录（forward 段）不能塞进节点 content —— 协议端会直接丢掉或报错，
    /// 这类消息必须整条用 message_id 引用转发，见 BuildIdNodes。
    /// </summary>
    public static List<ForwardSegment> BuildNodeSegments(JsonElement message)
    {
        List<ForwardSegment> segments = new();
        if (message.ValueKind != JsonValueKind.Array) return segments;

        foreach (JsonElement item in message.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("type", out JsonElement typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            string type = typeElement.GetString() ?? "";
            if (!CopyableSegments.Contains(type)) continue;
            if (!item.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (type == "text")
            {
                string text = GetString(data, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;
            }
            segments.Add(new ForwardSegment(type, data.Clone()));
        }
        return segments;
    }

    /// <summary>这条消息本身是不是一张聊天记录（合并转发）。</summary>
    public static bool IsChatRecord(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement item in message.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("type", out JsonElement typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && string.Equals(typeElement.GetString(), "forward", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 按 message_id 生成节点（协议端会自己把原消息的内容填进去）。
    /// 这是唯一能原样搬运「聊天记录」的形态，普通文字/图片/表情同样适用。
    /// </summary>
    public static List<object> BuildIdNodes(IEnumerable<long> messageIds) =>
        messageIds.Where(id => id > 0)
            .Select(id => (object)new { type = "node", data = new { id = id.ToString() } })
            .ToList();

    /// <summary>判断此刻是否落在静默时段，支持 "00:00-08:00" 或逗号分隔的多个区间。</summary>
    public static bool IsInSilentHours(DateTime now, string? ranges)
    {
        if (string.IsNullOrWhiteSpace(ranges)) return false;
        TimeSpan current = now.TimeOfDay;
        foreach (string part in ranges.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length != 2) continue;
            if (!TimeSpan.TryParseExact(bounds[0], @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan start)
                || !TimeSpan.TryParseExact(bounds[1], @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan end))
            {
                continue;
            }
            if (start == end) continue;
            bool hit = start < end
                ? current >= start && current < end
                : current >= start || current < end;
            if (hit) return true;
        }
        return false;
    }

    /// <summary>读取对象里的字符串字段，缺省返回空串。</summary>
    public static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }
        return "";
    }

    /// <summary>读取对象里的数字字段，缺省返回 0。</summary>
    public static long GetLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }
        return 0;
    }

    /// <summary>判断历史记录里某条消息是不是机器人自己发的。</summary>
    public static bool IsSelfMessage(JsonElement item, long botId) =>
        botId != 0 && GetLong(item, "user_id") == botId;

    /// <summary>
    /// 从各来源群的历史记录里筛出可以交给 AI 挑选的候选项：
    /// 只保留时间窗口内的、不是自己发的、没转发过的，且含媒体或文字长度达标的。
    /// </summary>
    public static List<ForwardCandidate> CollectCandidates(
        IEnumerable<ForwardHistory> histories,
        long botId,
        DateTime now,
        int windowMinutes,
        int minChars,
        Func<long, long, bool> alreadyForwarded)
    {
        List<ForwardCandidate> candidates = new();
        int floor = Math.Max(0, minChars);

        foreach (ForwardHistory history in histories)
        {
            foreach (JsonElement item in ReadHistoryMessages(history.Data))
            {
                long messageId = GetLong(item, "message_id");
                if (messageId <= 0 || IsSelfMessage(item, botId)) continue;
                if (alreadyForwarded(history.GroupId, messageId)) continue;

                long seconds = GetLong(item, "time");
                DateTime time = seconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
                    : now;
                if ((now - time).TotalMinutes > windowMinutes) continue;

                string raw = GetString(item, "raw_message");
                bool media = HasMedia(raw);
                string summary = Summarize(raw);
                if (!media && summary.Length < floor) continue;

                candidates.Add(new ForwardCandidate(
                    history.GroupId,
                    history.GroupName,
                    messageId,
                    GetLong(item, "user_id"),
                    ReadNickname(item),
                    summary,
                    media,
                    time));
            }
        }

        return candidates;
    }

    /// <summary>遍历 OneBot 历史记录里的 messages 数组。</summary>
    public static IEnumerable<JsonElement> ReadHistoryMessages(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) yield break;
        if (!data.TryGetProperty("messages", out JsonElement messages)) yield break;
        if (messages.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement item in messages.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
    }

    /// <summary>优先取群名片，其次昵称，最后退化成 QQ 号。</summary>
    public static string ReadNickname(JsonElement item)
    {
        JsonElement sender = item.ValueKind == JsonValueKind.Object
                             && item.TryGetProperty("sender", out JsonElement senderElement)
            ? senderElement
            : default;
        string card = GetString(sender, "card");
        if (!string.IsNullOrWhiteSpace(card)) return card;
        string nickname = GetString(sender, "nickname");
        return string.IsNullOrWhiteSpace(nickname) ? GetLong(item, "user_id").ToString() : nickname;
    }

    /// <summary>把消息时间戳格式化成 HH:mm；读不到时返回 --:--。</summary>
    public static string FormatClock(JsonElement item)
    {
        long seconds = GetLong(item, "time");
        if (seconds <= 0) return "--:--";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("HH:mm");
        }
        catch
        {
            return "--:--";
        }
    }
}
