using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace QQSuite.JmShare;

/// <summary>一条订阅：把某本漫画的更新推送到指定群 / 私聊。</summary>
public sealed class JmSubscription
{
    public long AlbumId { get; set; }
    public string Name { get; set; } = "";
    public long TargetId { get; set; }
    public bool IsGroup { get; set; } = true;
    public string AddedAt { get; set; } = "";

    /// <summary>上次检查时间（用于间隔控制）。</summary>
    public string LastCheck { get; set; } = "";
    /// <summary>上次见到的章节数，用于判断是否更新。</summary>
    public int LastChapterCount { get; set; }
    /// <summary>上次见到的最新章节 ID（章节数不变但新增章节时用它兜底判断）。</summary>
    public long LastNewestChapterId { get; set; }
    /// <summary>尚未建立基线：首次检查只记基线不推送，避免订阅时把整本当更新推出去。</summary>
    public bool HasBaseline { get; set; }
    /// <summary>发现更新时是否顺带把新章节下载并发过去。</summary>
    public bool AutoDownload { get; set; }
    public bool Enabled { get; set; } = true;
    public string LastError { get; set; } = "";

    /// <summary>同一个目标下的唯一键。</summary>
    public string Key => KeyOf(AlbumId, TargetId, IsGroup);

    public static string KeyOf(long albumId, long targetId, bool isGroup)
        => (isGroup ? "g" : "u") + targetId.ToString(CultureInfo.InvariantCulture)
           + ":" + albumId.ToString(CultureInfo.InvariantCulture);
}

sealed class JmSubscriptionState
{
    public List<JmSubscription> Items { get; set; } = new();
}

/// <summary>
/// JM 订阅清单，落盘在 state/subscriptions.json。
/// 定时检查由模块的 OnUpdate 驱动：到间隔就拉一次详情，比较章节数 / 最新章节 ID 判断是否有更新。
/// </summary>
public sealed class JmSubscriptionStore
{
    const string StateName = "subscriptions";

    readonly object gate = new();
    JmSubscriptionState state = new();
    bool loaded;

    void EnsureLoaded()
    {
        lock (gate)
        {
            if (loaded) return;
            state = JmStateStore.Load(StateName, () => new JmSubscriptionState());
            if (state.Items == null) state.Items = new List<JmSubscription>();
            loaded = true;
        }
    }

    public List<JmSubscription> List()
    {
        EnsureLoaded();
        lock (gate)
        {
            return state.Items.OrderBy(item => item.AddedAt, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>返回快照，供轮询时遍历（避免长时间持锁）。</summary>
    public List<JmSubscription> SnapshotEnabled()
    {
        EnsureLoaded();
        lock (gate) return state.Items.Where(item => item.Enabled).ToList();
    }

    public JmSubscription? Find(long albumId, long targetId, bool isGroup)
    {
        EnsureLoaded();
        string key = JmSubscription.KeyOf(albumId, targetId, isGroup);
        lock (gate) return state.Items.FirstOrDefault(item => item.Key == key);
    }

    /// <summary>新增订阅；已存在时返回既有记录并把返回值 Added 置 false。</summary>
    public (JmSubscription Subscription, bool Added) AddOrGet(long albumId, string name, long targetId, bool isGroup, bool autoDownload)
    {
        EnsureLoaded();
        lock (gate)
        {
            JmSubscription? existing = state.Items.FirstOrDefault(
                item => item.Key == JmSubscription.KeyOf(albumId, targetId, isGroup));
            if (existing != null)
            {
                existing.Enabled = true;
                if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
                existing.AutoDownload = autoDownload;
                JmStateStore.Save(StateName, state);
                return (existing, false);
            }

            JmSubscription created = new()
            {
                AlbumId = albumId,
                Name = name ?? "",
                TargetId = targetId,
                IsGroup = isGroup,
                AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                AutoDownload = autoDownload,
                Enabled = true
            };
            state.Items.Add(created);
            JmStateStore.Save(StateName, state);
            return (created, true);
        }
    }

    public bool Remove(long albumId, long targetId, bool isGroup)
    {
        EnsureLoaded();
        string key = JmSubscription.KeyOf(albumId, targetId, isGroup);
        lock (gate)
        {
            int removed = state.Items.RemoveAll(item => item.Key == key);
            if (removed > 0) JmStateStore.Save(StateName, state);
            return removed > 0;
        }
    }

    /// <summary>把轮询结果写回清单。</summary>
    public void Update(JmSubscription subscription)
    {
        EnsureLoaded();
        lock (gate)
        {
            int index = state.Items.FindIndex(item => item.Key == subscription.Key);
            if (index < 0) return;
            state.Items[index] = subscription;
            JmStateStore.Save(StateName, state);
        }
    }
}
