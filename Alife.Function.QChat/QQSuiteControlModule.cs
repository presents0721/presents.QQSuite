using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace QQSuite.Core;

/// <summary>套件总控配置：显示偏好 + 本角色启用的功能清单。</summary>
public record QQSuiteConfig
{
    [DisplayName("显示函数数量")] [Description("在功能卡片上显示该模块提供的 AI 函数数量")]
    public bool ShowFunctionCount { get; set; } = true;

    [DisplayName("显示使用提示")] [Description("在页面底部显示依赖关系与使用提示")]
    public bool ShowHints { get; set; } = true;

    [DisplayName("已启用功能")] [Description("由面板开关自动维护，无需手动编辑")]
    public List<string>? EnabledFeatures { get; set; }
}

public sealed record QQSuiteFeature(
    Type ModuleType,
    string Name,
    string ShortName,
    string Description,
    string[] Highlights,
    string Accent,
    int FunctionCount);

/// <summary>
/// QQ 全能套件总控：整个套件唯一的模块入口，各功能由它按开关动态加载 / 卸载。
/// </summary>
[Module("QQ 全能套件",
    """
    一站式 QQ 功能集合：消息收发、群聊管家、群文件、QQ 工具箱、QQ 增强、QQ 空间、分享卡片、视频解析、Pixiv 图片、JM 漫画与群聊日报。
    统一复用 QQ聊天 的一条 OneBot 连接，适配 NapCat / SnowLuma / LLOneBot 等 OneBot v11 协议端。
    在面板里逐项开关功能，每个功能的详细设置也可以在面板里展开。
    """,
    defaultCategory: "QQ 全能套件",
    editorUI: typeof(QQSuiteUI))]
public class QQSuiteControlModule(
    ILogger<QQSuiteControlModule> logger,
    CharacterSystem characterSystem,
    ConfigurationSystem configurationSystem
) : ChatBehaviour, IConfigurable<QQSuiteConfig>
{
    public QQSuiteConfig Configuration { get; set; } = null!;

    public static readonly IReadOnlyList<QQSuiteFeature> Features =
    [
        new(typeof(QChatService), "QQ聊天", "聊天",
            "OneBot v11 连接与 QQ 消息收发，是其它所有功能的基础。",
            ["私聊/群聊收发", "图片/语音/文件", "群消息缓存与唤醒", "自动重连"],
            "#1677ff", 5),
        new(typeof(QQSuite.GroupManager.GroupManagerModule), "QQ群聊管家", "群管",
            "入群审核、欢迎、关键词、撤回、禁言、公告、精华、群名片与活跃统计。",
            ["入群审核/欢迎", "关键词与撤回", "禁言/踢人", "公告/精华/词云"],
            "#13c2c2", 18),
        new(typeof(QQSuite.GroupFileManager.GroupFileManagerModule), "QQ群文件管家", "文件",
            "搜索、上传、下载、移动、删除群文件与文件夹，并统计存储占用。",
            ["群文件搜索", "上传/下载", "移动/删除/重命名", "容量统计"],
            "#fa8c16", 11),
        new(typeof(QQSuite.QQTools.QQToolsModule), "QQ工具箱", "工具",
            "点赞、戳一戳、撤回消息、空间说说、表情回应、引用回复与输入状态。",
            ["名片赞/戳一戳", "撤回消息", "说说与表情回应", "引用回复"],
            "#eb2f96", 13),
        new(typeof(QQSuite.QQEnhance.QQEnhanceModule), "QQ增强", "增强",
            "更细的群管理与互动：禁言、名片、拍一拍、分享卡片、精华、群文件等。",
            ["精细禁言/名片", "拍一拍", "分享卡片", "群精华/文件"],
            "#722ed1", 14),
        new(typeof(QQSuite.Qzone.QzoneModule), "QQ空间", "空间",
            "发布、删除、点赞说说，查看访客与相册，支持图片和视频。",
            ["发/删/赞说说", "评论与转发", "访客/相册", "图片/视频"],
            "#f5222d", 10),
        new(typeof(QQSuite.ShareArk.ShareArkModule), "QQ分享卡片", "卡片",
            "音乐与 B 站搜索、B 站解析、IP 归属查询和通用 Ark 分享卡。",
            ["音乐/B站搜索", "B站解析与更新", "IP 归属", "通用 Ark 卡片"],
            "#2f54eb", 14),
        new(typeof(QQSuite.VideoShare.VideoShareModule), "QQ视频解析", "视频",
            "自动识别抖音/B站视频链接，解析标题、封面、作者与直链，并以合并转发（聊天记录卡片）发送。",
            ["抖音/B站解析", "合并转发发送", "自动识别链接", "聊天记录读取"],
            "#08979c", 2),
        new(typeof(QQSuite.PixivShare.PixivShareModule), "Pixiv图片", "Pixiv",
            "按作品 PID 或关键词从 Pixiv 拉取图片，下载后直接发送到 QQ 群聊/私聊。",
            ["PID 精确拉图", "关键词搜索", "R-18 过滤", "本地缓存"],
            "#ff5a5f", 2),
        new(typeof(QQSuite.DailyReport.GroupDailyReportModule), "群聊日报", "日报",
            "按日统计群活跃并生成日报图片，支持定时推送。",
            ["每日定时统计", "活跃榜/消息量", "日报图片", "自动推送"],
            "#52c41a", 1),
        new(typeof(QQSuite.JmShare.JmShareModule), "JM漫画", "JM",
            "搜索、查看详情并按本子/章节下载 JM 漫画，还原图片后打包 ZIP / 长图 / PDF 发送；支持账号登录、收藏与订阅推送。",
            ["关键词搜索", "详情/封面", "整本/单章下载", "ZIP/长图/PDF", "每日配额", "收藏/订阅"],
            "#d4380d", 6),
        new(typeof(QQSuite.MemePack.MemePackModule), "QQ表情包", "表情",
            "一键导入 QQ 收藏的表情，并收藏任意图片自动打语义标签，聊天时按情绪自主发送。",
            ["QQ收藏导入", "AI 主动收藏", "视觉模型打标", "按语义发送", "mface/图片回退"],
            "#fadb14", 5),
        new(typeof(QQSuite.QQForward.ForwardModule), "QQ跨群转发", "转发",
            "把来源群里有意思的消息多选转发到目标群，可组合成聊天记录或逐条发送，并支持定时巡逻、按兴趣自主分享。",
            ["多选合并转发", "逐条/单条转发", "来源·目标双白名单", "每日限额", "自主巡逻分享"],
            "#237804", 3)
    ];

    public string LastMessage { get; private set; } = "";
    public bool LastMessageIsError { get; private set; }
    public bool IsBusy { get; private set; }

    /// <summary>构造失败的功能，短暂停止自动重试，避免日志刷屏。</summary>
    private readonly HashSet<string> failedFeatures = new();

    private DateTime lastReconcileTime = DateTime.MinValue;

    /// <summary>旧版 8 个独立插件注册的模块全名：同时启用会双连接、同名函数重复执行。</summary>
    public static readonly IReadOnlyList<string> LegacyModuleIds =
    [
        "Alife.Function.QChat.QChatService",
        "Suran.GroupManager.GroupManagerModule",
        "Suran.GroupFileManager.GroupFileManagerModule",
        "YuYang.QQTools.QQToolsModule",
        "AinaLife.QQEnhance.QQEnhanceModule",
        "AinaLife.Qzone.QzoneModule",
        "Suran.ShareArk.ShareArkModule",
        "Suran.DailyReport.GroupDailyReportModule"
    ];

    public IReadOnlyList<string> EnabledLegacyModuleIds =>
        LegacyModuleIds.Where(id => Character.Modules.Contains(id)).ToList();

    public bool HasLegacyModules => EnabledLegacyModuleIds.Count > 0;

    public string LegacyNamesText => string.Join("、", EnabledLegacyModuleIds.Select(LegacyDisplayName));

    static string LegacyDisplayName(string id) => id switch
    {
        "Alife.Function.QChat.QChatService" => "QQ聊天",
        "Suran.GroupManager.GroupManagerModule" => "QQ群聊管家",
        "Suran.GroupFileManager.GroupFileManagerModule" => "QQ群文件管家",
        "YuYang.QQTools.QQToolsModule" => "QQ工具箱",
        "AinaLife.QQEnhance.QQEnhanceModule" => "QQ增强",
        "AinaLife.Qzone.QzoneModule" => "QQ空间",
        "Suran.ShareArk.ShareArkModule" => "QQ分享卡片",
        "Suran.DailyReport.GroupDailyReportModule" => "群聊日报",
        _ => id
    };

    /// <summary>一键关闭旧插件的同名模块，避免两条 OneBot 连接与重复回复。</summary>
    public async Task DisableLegacyModulesAsync()
    {
        IReadOnlyList<string> enabled = EnabledLegacyModuleIds;
        if (enabled.Count == 0)
        {
            LastMessage = "没有检测到旧插件模块，已经是单插件状态。";
            LastMessageIsError = false;
            return;
        }

        IsBusy = true;
        try
        {
            foreach (string id in enabled)
            {
                Character.Modules.Remove(id);
            }
            await characterSystem.SaveCharacter(Character);
            LastMessage = "已关闭旧插件模块：" + string.Join("、", enabled.Select(LegacyDisplayName));
            LastMessageIsError = false;
        }
        catch (Exception ex)
        {
            LastMessage = "关闭旧插件模块失败：" + ex.Message;
            LastMessageIsError = true;
            logger.LogError(ex, "关闭旧插件模块失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>保存面板配置（显示偏好 + 已启用功能）。</summary>
    public void SavePreferences()
    {
        configurationSystem.SetConfiguration(typeof(QQSuiteControlModule), Configuration, Character.StorageKey);
    }

    private List<string> EnabledIds => Configuration.EnabledFeatures ??= new List<string>();

    public bool IsEnabled(Type moduleType) => EnabledIds.Contains(moduleType.FullName!);

    public bool IsChatEnabled => IsEnabled(typeof(QChatService));

    public int EnabledCount => Features.Count(feature => IsEnabled(feature.ModuleType));

    public bool AllEnabled => EnabledCount == Features.Count;

    public bool AnyFeatureEnabled => EnabledCount > 0;

    public object GetFeatureConfiguration(Type moduleType) =>
        configurationSystem.GetConfiguration(moduleType, Character.StorageKey);

    /// <summary>把面板里改过的配置同步给正在运行的功能实例。</summary>
    public void ApplyFeatureConfiguration(Type moduleType, object configuration)
    {
        if (GetFeatureInstance(moduleType) is IConfigurable configurable)
        {
            configurable.Configuration = configuration;
        }
    }

    public QChatServiceConfig GetConnectionConfig()
    {
        object? config = configurationSystem.GetConfiguration(typeof(QChatService), Character.StorageKey);
        return config as QChatServiceConfig ?? new QChatServiceConfig();
    }

    public object? GetFeatureInstance(Type moduleType) =>
        ChatActivity?.Container.Instances.FirstOrDefault(instance => moduleType.IsInstanceOfType(instance));

    public QChatService? GetLiveChatService() => GetFeatureInstance(typeof(QChatService)) as QChatService;

    public bool IsConnected => GetLiveChatService() is { IsConnected: true };

    public string ConnectionStateText => IsConnected ? "已连接" : (IsChatEnabled ? "未连接" : "QQ聊天未启用");

    /// <summary>保存连接设置：写入 QQ聊天 配置，若模块在线则立即重连。</summary>
    public async Task ApplyConnectionAsync(string url, string token)
    {
        IsBusy = true;
        try
        {
            QChatServiceConfig config = GetConnectionConfig();
            config.Url = string.IsNullOrWhiteSpace(url) ? "ws://127.0.0.1:3001" : url.Trim();
            config.Token = (token ?? "").Trim();
            configurationSystem.SetConfiguration(typeof(QChatService), config, Character.StorageKey);

            QChatService? live = GetLiveChatService();
            if (live != null)
            {
                live.Configuration.Url = config.Url;
                live.Configuration.Token = config.Token;
                await live.ReconnectAsync();
                LastMessage = "连接设置已保存，正在按新地址重连。";
                LastMessageIsError = false;
            }
            else
            {
                LastMessage = "连接设置已保存；启用 QQ聊天 后生效。";
                LastMessageIsError = false;
            }
        }
        catch (Exception ex)
        {
            LastMessage = "保存连接设置失败：" + ex.Message;
            LastMessageIsError = true;
            logger.LogError(ex, "保存套件连接设置失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ToggleFeatureAsync(Type moduleType, bool enabled)
    {
        if (moduleType == typeof(QQSuiteControlModule))
        {
            return;
        }

        IsBusy = true;
        try
        {
            string name = Features.FirstOrDefault(f => f.ModuleType == moduleType)?.Name
                ?? TypeUtility.GetReadableName(moduleType);

            List<string> ids = EnabledIds;

            if (enabled && moduleType != typeof(QChatService))
            {
                // QQ聊天 是统一连接的基础：开启任何其它功能时自动带上 QQ聊天
                if (!ids.Contains(typeof(QChatService).FullName!))
                {
                    ids.Add(typeof(QChatService).FullName!);
                }
            }

            // 关闭 QQ聊天 前必须关闭依赖它的功能，避免出现半残状态
            if (!enabled && moduleType == typeof(QChatService))
            {
                List<string> dependents = Features
                    .Where(f => f.ModuleType != typeof(QChatService) && IsEnabled(f.ModuleType))
                    .Select(f => f.Name)
                    .ToList();
                if (dependents.Count > 0)
                {
                    LastMessage = "请先关闭依赖 QQ聊天 的功能：" + string.Join("、", dependents);
                    LastMessageIsError = true;
                    return;
                }
            }

            if (enabled)
            {
                if (!ids.Contains(moduleType.FullName!))
                {
                    ids.Add(moduleType.FullName!);
                }
            }
            else
            {
                ids.Remove(moduleType.FullName!);
            }

            failedFeatures.Remove(moduleType.FullName!);
            SavePreferences();

            if (enabled)
            {
                bool ok = moduleType == typeof(QChatService) || await CreateFeatureAsync(typeof(QChatService));
                ok = ok && await CreateFeatureAsync(moduleType);

                if (ok)
                {
                    LastMessage = $"已开启 {name}";
                    LastMessageIsError = false;
                }
                else
                {
                    // 实例没建起来就回滚配置，避免面板显示「已启用」但实际没运行
                    ids.Remove(moduleType.FullName!);
                    SavePreferences();
                }
            }
            else
            {
                await DestroyFeatureAsync(moduleType);
                LastMessage = $"已关闭 {name}";
                LastMessageIsError = false;
            }
        }
        catch (Exception ex)
        {
            LastMessage = $"切换 {TypeUtility.GetReadableName(moduleType)} 失败：" + ex.Message;
            LastMessageIsError = true;
            logger.LogError(ex, "切换套件功能失败 {@Type}", moduleType.FullName);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetAllFeaturesAsync(bool enabled)
    {
        IsBusy = true;
        try
        {
            if (enabled)
            {
                Configuration.EnabledFeatures = Features.Select(feature => feature.ModuleType.FullName!).ToList();
                failedFeatures.Clear();
                SavePreferences();
                await ReconcileAsync();
                LastMessage = "已一键开启全部 QQ 功能";
            }
            else
            {
                Configuration.EnabledFeatures = new List<string>();
                SavePreferences();
                foreach (QQSuiteFeature feature in Features.Reverse())
                {
                    await DestroyFeatureAsync(feature.ModuleType);
                }
                LastMessage = "已关闭全部 QQ 功能（总控保留）";
            }

            LastMessageIsError = false;
        }
        catch (Exception ex)
        {
            LastMessage = "批量切换失败：" + ex.Message;
            LastMessageIsError = true;
            logger.LogError(ex, "批量切换套件功能失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把配置里启用但当前不在容器中的功能补建出来。</summary>
    private async Task ReconcileAsync()
    {
        foreach (QQSuiteFeature feature in Features)
        {
            if (!IsEnabled(feature.ModuleType))
            {
                continue;
            }
            if (failedFeatures.Contains(feature.ModuleType.FullName!))
            {
                continue;
            }
            if (GetFeatureInstance(feature.ModuleType) != null)
            {
                continue;
            }
            await CreateFeatureAsync(feature.ModuleType);
        }
    }

    private async Task<bool> CreateFeatureAsync(Type moduleType)
    {
        if (GetFeatureInstance(moduleType) != null)
        {
            return true;
        }

        try
        {
            ConstructContainer container = ChatActivity.Container;
            container.RegisterBuilder(moduleType);
            await container.RequireInstance(moduleType);
            return true;
        }
        catch (Exception ex)
        {
            failedFeatures.Add(moduleType.FullName!);
            logger.LogError(ex, "启用套件功能失败 {@Type}", moduleType.FullName);
            LastMessage = $"启用 {TypeUtility.GetReadableName(moduleType)} 失败：" + ex.Message;
            LastMessageIsError = true;
            return false;
        }
    }

    private async Task DestroyFeatureAsync(Type moduleType)
    {
        object? instance = GetFeatureInstance(moduleType);
        if (instance != null)
        {
            await ChatActivity.Container.RemoveInstance(instance);
        }
    }

    /// <summary>把上一版勾在角色模块表里的功能迁移到套件配置，并清掉这些已不存在的模块 id。</summary>
    private void MigrateLegacySelection()
    {
        if (Configuration.EnabledFeatures != null)
        {
            return;
        }

        Configuration.EnabledFeatures = Features
            .Where(feature => Character.Modules.Contains(feature.ModuleType.FullName!))
            .Select(feature => feature.ModuleType.FullName!)
            .ToList();

        foreach (QQSuiteFeature feature in Features)
        {
            Character.Modules.Remove(feature.ModuleType.FullName!);
        }

        SavePreferences();
        logger.LogInformation("已迁移旧勾选：{Count} 项功能", Configuration.EnabledFeatures.Count);
    }

    /// <summary>插件热重载后，清理上一份程序集残留的功能实例，避免出现两套连接。</summary>
    private async Task CleanupStaleAsync()
    {
        try
        {
            foreach (object instance in ChatActivity.Container.Instances.ToList())
            {
                string? fullName = instance.GetType().FullName;
                if (fullName == null)
                {
                    continue;
                }
                if (!Features.Any(feature => feature.ModuleType.FullName == fullName))
                {
                    continue;
                }
                if (Features.Any(feature => feature.ModuleType == instance.GetType()))
                {
                    continue;
                }
                await ChatActivity.Container.RemoveInstance(instance);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理套件旧实例失败");
        }
    }

    protected override async Task OnAwake()
    {
        await CleanupStaleAsync();
        MigrateLegacySelection();
        logger.LogInformation("QQ 全能套件总控已加载，已启用 {Count}/{Total} 项功能", EnabledCount, Features.Count);
        await ReconcileAsync();
    }

    protected override async Task OnUpdate()
    {
        if (IsBusy)
        {
            return;
        }
        if (DateTime.UtcNow - lastReconcileTime < TimeSpan.FromSeconds(2))
        {
            return;
        }
        lastReconcileTime = DateTime.UtcNow;

        // 角色设定保存后框架会重建容器，这里把漏掉的功能补回来
        await ReconcileAsync();
    }
}
