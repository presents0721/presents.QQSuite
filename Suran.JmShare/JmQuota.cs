using System;
using System.Collections.Generic;
using System.Globalization;

namespace QQSuite.JmShare;

/// <summary>
/// 每日下载配额：按「群」与「人」两个维度分别计数，按自然日自动归零，
/// 状态持久化到 state/quota.json，插件重启后当天已用的次数仍然有效。
/// </summary>
public sealed class JmQuotaStore
{
    /// <summary>配额超限时的判定结果。</summary>
    public readonly record struct QuotaResult(bool Allowed, string Message)
    {
        public static QuotaResult Ok => new(true, "");
    }

    sealed class State
    {
        public string Date { get; set; } = "";
        public Dictionary<string, int> Counts { get; set; } = new();
    }

    const string StateName = "quota";

    State state;
    bool loaded;

    public JmQuotaStore() => state = new State();

    void EnsureLoaded()
    {
        if (loaded) return;
        // Load 内部已兜底：文件缺失或损坏时返回全新的 State
        state = JmStateStore.Load(StateName, () => new State());
        if (state.Counts == null) state.Counts = new Dictionary<string, int>();
        loaded = true;
    }

    static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>跨日时重置计数；返回是否发生了重置。</summary>
    public bool ResetIfNewDay()
    {
        EnsureLoaded();
        string today = Today();
        if (state.Date == today) return false;

        state.Date = today;
        state.Counts.Clear();
        JmStateStore.Save(StateName, state);
        return true;
    }

    /// <summary>当天某个 key 已用的次数。</summary>
    public int Used(string key)
    {
        EnsureLoaded();
        ResetIfNewDay();
        return state.Counts.TryGetValue(key, out int value) ? value : 0;
    }

    /// <summary>
    /// 尝试占用一次配额：先检查群上限与人上限，通过后两个计数各加一。
    /// 任一超限时返回 Allowed=false 与提示语；limit 传 0 表示该维度不限。
    /// </summary>
    public QuotaResult TryConsume(string? groupKey, string? userKey, int groupLimit, int userLimit)
    {
        EnsureLoaded();
        ResetIfNewDay();

        int groupUsed = groupKey == null ? 0 : Used(groupKey);
        int userUsed = userKey == null ? 0 : Used(userKey);

        if (groupKey != null && groupLimit > 0 && groupUsed >= groupLimit)
        {
            return new QuotaResult(false,
                "本群今日的下载次数已用完（已用 " + groupUsed + " / " + groupLimit + " 次），请明天再来。");
        }
        if (userKey != null && userLimit > 0 && userUsed >= userLimit)
        {
            return new QuotaResult(false,
                "你今日的下载次数已用完（已用 " + userUsed + " / " + userLimit + " 次），请明天再来。");
        }

        if (groupKey != null) state.Counts[groupKey] = groupUsed + 1;
        if (userKey != null) state.Counts[userKey] = userUsed + 1;
        JmStateStore.Save(StateName, state);
        return QuotaResult.Ok;
    }

    /// <summary>某个 key 今天的剩余次数；limit 为 0 时返回 int.MaxValue 表示不限。</summary>
    public int Remaining(string key, int limit)
    {
        if (limit <= 0) return int.MaxValue;
        int remaining = limit - Used(key);
        return remaining < 0 ? 0 : remaining;
    }
}
