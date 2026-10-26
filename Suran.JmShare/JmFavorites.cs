using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace QQSuite.JmShare;

/// <summary>一条收藏记录。</summary>
public sealed class JmFavoriteItem
{
    public long AlbumId { get; set; }
    public string Name { get; set; } = "";
    public string AddedAt { get; set; } = "";
    /// <summary>远端同步是否成功（未登录时恒为 false，只保存在本地）。</summary>
    public bool SyncedRemote { get; set; }
}

sealed class JmFavoriteState
{
    public List<JmFavoriteItem> Items { get; set; } = new();
}

/// <summary>
/// JM 收藏（本地清单 + 登录后尽力同步到远端）。
/// 清单落盘在 state/favorites.json，插件重启后仍可用；未登录时表现为「本机收藏夹」。
/// </summary>
public sealed class JmFavoriteStore
{
    const string StateName = "favorites";

    readonly object gate = new();
    JmFavoriteState state = new();
    bool loaded;

    void EnsureLoaded()
    {
        lock (gate)
        {
            if (loaded) return;
            state = JmStateStore.Load(StateName, () => new JmFavoriteState());
            if (state.Items == null) state.Items = new List<JmFavoriteItem>();
            loaded = true;
        }
    }

    /// <summary>按加入时间倒序返回全部收藏。</summary>
    public List<JmFavoriteItem> List()
    {
        EnsureLoaded();
        lock (gate)
        {
            return state.Items.OrderByDescending(item => item.AddedAt, StringComparer.Ordinal).ToList();
        }
    }

    public int Count
    {
        get
        {
            EnsureLoaded();
            lock (gate) return state.Items.Count;
        }
    }

    public bool Contains(long albumId)
    {
        EnsureLoaded();
        lock (gate) return state.Items.Any(item => item.AlbumId == albumId);
    }

    /// <summary>新增收藏；已存在时只更新标题。返回是否是新增。</summary>
    public bool Add(long albumId, string name, bool syncedRemote)
    {
        EnsureLoaded();
        lock (gate)
        {
            JmFavoriteItem? existing = state.Items.FirstOrDefault(item => item.AlbumId == albumId);
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
                existing.SyncedRemote = existing.SyncedRemote || syncedRemote;
                JmStateStore.Save(StateName, state);
                return false;
            }

            state.Items.Add(new JmFavoriteItem
            {
                AlbumId = albumId,
                Name = name ?? "",
                AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                SyncedRemote = syncedRemote
            });
            JmStateStore.Save(StateName, state);
            return true;
        }
    }

    /// <summary>删除收藏；返回是否确实删掉了一条。</summary>
    public bool Remove(long albumId)
    {
        EnsureLoaded();
        lock (gate)
        {
            int removed = state.Items.RemoveAll(item => item.AlbumId == albumId);
            if (removed > 0) JmStateStore.Save(StateName, state);
            return removed > 0;
        }
    }

    /// <summary>记住标题（详情接口拿到名称后回填，收藏列表就不必再逐条请求）。</summary>
    public void UpdateName(long albumId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        EnsureLoaded();
        lock (gate)
        {
            JmFavoriteItem? existing = state.Items.FirstOrDefault(item => item.AlbumId == albumId);
            if (existing == null || existing.Name == name) return;
            existing.Name = name;
            JmStateStore.Save(StateName, state);
        }
    }
}
