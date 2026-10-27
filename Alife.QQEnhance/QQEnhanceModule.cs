using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.MessageFilter;
using QQSuite.Core;
using Microsoft.Extensions.Logging;

namespace QQSuite.QQEnhance;

public class QQEnhanceConfig
{
    [DisplayName("贴表情")]
    [Description("启用给QQ消息贴表情功能")]
    public bool EmojiReactEnabled { get; set; } = true;

    [DisplayName("点赞")]
    [Description("启用给QQ用户资料卡点赞功能（好友与陌生人均可，平台日上限50次/人）")]
    public bool SendLikesEnabled { get; set; } = true;

    [DisplayName("点赞成功回执")]
    [Description("资料卡点赞成功后是否通知AI确认（点赞不像发言会刷屏，确认回执不会造成多余互动，默认开启）")]
    public bool LikeConfirmEnabled { get; set; } = true;

    [DisplayName("撤回")]
    [Description("启用撤回QQ消息功能")]
    public bool DeleteMsgEnabled { get; set; } = true;

    [DisplayName("撤回成功回执")]
    [Description("开启后撤回请求被接受会推送确认消息；默认关闭=成功静默，只有 delete_msg 直接报错才提示（NapCat 平台不给真实撤回回执，本配置只是接受确认）")]
    public bool RecallConfirmEnabled { get; set; } = false;

    [DisplayName("列表快照有效期(秒)")]
    [Description("list=true 候选列表序号的有效时间：期间 index 严格按该列表解析，不受新消息影响；过期后自动回退实时解析。0=禁用快照始终实时。默认10秒（Alife响应快通常足够），范围0~600")]
    public int ListSnapshotSeconds { get; set; } = 10;

    [DisplayName("撤回核验延迟(秒)-已废弃")]
    [Description("已废弃：历史接口读NapCat本地库无法核验撤回结果，后台核验已移除，此配置不再生效")]
    public double RecallVerifyDelaySeconds { get; set; } = 1.0;

    [DisplayName("禁言")]
    [Description("启用禁言QQ群成员功能")]
    public bool GroupBanEnabled { get; set; } = true;

    [DisplayName("音乐卡片")]
    [Description("启用发送音乐卡片功能（默认样式见下方「音乐卡片样式」）")]
    public bool MusicCardEnabled { get; set; } = true;

    [DisplayName("音乐卡片样式")]
    [Description("custom=自定义音乐段（协议端本地拼卡，默认）；163=平台原生音乐卡片；record=直接发语音条（网易云直链保底，任何端可播）；json=已废弃，自动按163发送。注意：custom/record 需要网易云歌曲ID与直链，只对 platform=search/163 生效；qq/kugou/migu/kuwo 一律按原生卡片原样透传")]
    public string MusicCardStyle { get; set; } = "custom";

    [DisplayName("音乐签名服务地址")]
    [Description("可选。填入后由插件直接完成卡片签名再发送（协议端不再处理 music 段）。留空则交给协议端：NapCat 有内置默认签名服务，可直接发送；LLBot 未配置 musicSignUrl 时会直接报「音乐卡片签名地址未配置」，必须自行填写。常用公共签名：https://ss.xingzhige.com/music_card/card（实测可用）。注意：网易云 VIP/版权受限歌曲可能因平台限制无法解析（表现为签名服务返回「无法准确获取歌曲信息」），此时会自动降级为自定义卡片或语音条")]
    public string MusicSignUrl { get; set; } = "";

    [DisplayName("签名服务自动兜底")]
    [Description("开启后，当协议端明确拒绝当前卡片（典型：LLBot 未配置 musicSignUrl 直接报错）时，自动改用公共签名服务重签为 json 段重发；成功后会一直沿用它（无感），直到某次失败再切回原通道。默认开启，不想依赖第三方服务可关闭")]
    public bool MusicSignAutoFallback { get; set; } = true;

    [DisplayName("公共签名服务地址")]
    [Description("自动兜底使用的签名服务地址，默认网易云卡片签名服务（POST {type,id}，返回卡片JSON）。可换成自建地址")]
    public string MusicSignFallbackUrl { get; set; } = "https://ss.xingzhige.com/music_card/card";

    [DisplayName("第三方ARK签名通道")]
    [Description("开启后音乐卡片走第三方 ARK 签名服务（自建卡片、不依赖协议端签名，兼容性最好）。\n【怎么用】1) 打开 https://apii.xianyuw.cn 注册账号；2) 在个人中心复制你的 API Key（一串密钥）；3) 把它填到下方「ARK服务Token」；4) 下方「ARK服务地址」保持默认即可。\n默认关闭：不填 Token 时即使开启也不会生效。开启后若签名失败会自动切回默认通道。")]
    public bool MusicArkEnabled { get; set; } = false;

    [DisplayName("ARK服务地址")]
    [Description("第三方 ARK 签名接口地址（默认公共站，可换自建）。仅在开启「第三方ARK签名通道」时使用")]
    public string MusicArkUrl { get; set; } = "https://apii.xianyuw.cn/api/v1/qq-musicArk";

    [DisplayName("ARK服务Token")]
    [Description("从 https://apii.xianyuw.cn 注册后复制的 API Key。留空则「第三方ARK签名通道」不会生效（自动用默认通道）")]
    public string MusicArkToken { get; set; } = "";

    [DisplayName("功能文档常驻（推荐）")]
    [Description("开启：本插件的功能文档常驻在系统提示词里，AI 始终可见 —— 不再出现「调用入口才加载文档」「文档滑出上下文后反复重新注入」的往返（QQ 属高频场景，反复重注入更费 token 也更慢）。" +
        "关闭：改回渐进式加载（AI 每次调用函数都要先输出 <QQEnhanceModule/> 读文档，文档滑出后还会再注入一次）。切换后需重载插件生效")]
    public bool DocumentAlwaysResident { get; set; } = true;

    [DisplayName("B站卡片走签名（推荐）")]
    [Description("开启：B站卡片走第三方签名通路后发送——QQ 只渲染已签名的卡，这样卡片才显示得出来；" +
        "代价是卡片标签会显示成“QQ音乐”（签名服务固定行为，与 QQSuite.ShareArk 一致）。" +
        "关闭：改用自建 news 卡（appid/标签正确，但无签名，实测群里可能显示不出来）")]
    public bool BiliCardUseSign { get; set; } = true;

    [DisplayName("通用分享卡")]
    [Description("允许 AI 用 SendShareCard 把任意网页链接做成QQ分享卡发送（标题/描述/预览图可留空自动抓取）。走与音乐卡相同的签名服务")]
    public bool ShareCardEnabled { get; set; } = true;

    [DisplayName("B站卡片")]
    [Description("启用发送B站视频卡片（platform=bilibili 传 BV 号）。使用 QQ 通用图文卡（news）自行构造，不依赖任何签名服务与协议端白名单；如需关闭可在此禁用")]
    public bool BiliCardEnabled { get; set; } = true;

    [DisplayName("互动提示")]
    [Description("收到QQ消息时在消息末尾附加互动提示（类似官方消息过滤的注入机制），提醒AI可以随手贴表情/引用/戳一戳/点赞")]
    public bool InteractionHintEnabled { get; set; } = true;

    [DisplayName("互动提示概率(%)")]
    [Description("每条QQ消息附加互动提示的概率，0-100，默认100（每次都提示）")]
    public int InteractionHintProbability { get; set; } = 100;

    [DisplayName("互动提示文本")]
    [Description("附加在消息末尾的提示内容，可自定义。支持占位符：{scope}=群号或对方QQ、{type}=group或private、{uin}=发言人QQ、{nick}=发言人昵称、{poke}=戳一戳函数名（自动区分群聊/私聊）、{pokeargs}=戳一戳参数（自动区分群聊/私聊）")]
    public string InteractionHintText { get; set; } =
        "(可随手互动：ReplyRecent targetid={scope} messagetype={type} target={uin} 引用{nick}这条 / SetEmojiRecent target={uin} 贴表情 / {poke} {pokeargs} 戳一戳 / SendQQLikes qq={uin} 点赞——都是完整回应，无需说明)";

    [DisplayName("被引用/被@回引提示")]
    [Description("当有人引用bot的消息或@bot时，在提示末尾追加一句回引建议（不含消息内容，省token），引导bot用引用回复回应")]
    public bool QuoteBackHintEnabled { get; set; } = true;

    [DisplayName("戳一戳")]
    [Description("启用戳一戳功能（群聊/私聊）")]
    public bool PokeEnabled { get; set; } = true;

    [DisplayName("戳回决策")]
    [Description("收到别人的戳一戳后注入决策提示，让模型顺带决定是否回戳（PokeBack），不影响正常回复构建")]
    public bool PokeDecideEnabled { get; set; } = true;

    [DisplayName("提示冷却时间(秒)-已废弃")]
    [Description("已废弃：4.9.23 起各类事件提示冷却已拆分，请用下方四个独立配置项，此项不再生效（仅为兼容旧配置保留）")]
    public double NoticeCooldownSeconds { get; set; } = 10;

    [DisplayName("戳一戳提示冷却(秒)")]
    [Description("同一时间内最多提示一次被戳（超出静默忽略，不排队不合并），默认10秒，最小1秒")]
    public double PokeCooldownSeconds { get; set; } = 10;

    [DisplayName("被赞提示冷却(秒)")]
    [Description("同一时间内最多提示一次资料卡被赞，默认10秒，最小1秒")]
    public double ProfileLikeCooldownSeconds { get; set; } = 10;

    [DisplayName("被贴表情提示冷却(秒)")]
    [Description("他人给「你的消息」贴表情时的提示冷却，默认10秒，最小1秒")]
    public double EmojiLikeCooldownSeconds { get; set; } = 10;

    [DisplayName("他人贴他人表情提示冷却(秒)")]
    [Description("他人给「别人的消息」贴表情时的提示冷却（需开启上方开关），默认30秒——这类互动较频繁，间隔放宽避免刷屏，最小1秒")]
    public double OthersEmojiLikeCooldownSeconds { get; set; } = 30;

    [DisplayName("戳一戳防刷限次")]
    [Description("同一人同一窗口内最多受理的戳一戳次数，超出后静默忽略（防双AI互戳/连戳脚本无限循环；0=不限制）。注意：不影响正常玩闹，仅作保险丝")]
    public int PokeBackFloodLimitCount { get; set; } = 10;

    [DisplayName("戳一戳防刷窗口秒数")]
    [Description("配合防刷限次的滑动窗口时长（秒），默认60")]
    public int PokeBackFloodWindowSeconds { get; set; } = 60;

    [DisplayName("回戳回执抑制秒数")]
    [Description("主动戳人（含回戳）后，N秒内收到同一人的戳通知视为我方动作的回执回声而忽略（防NapCat私聊通知方向字段不规范时把回执当成新戳造成回圈；0=不抑制）")]
    public int PokeEchoSuppressSeconds { get; set; } = 3;

    [DisplayName("戳回执不触发对话（私聊）")]
    [Description("自己戳人后，协议端会回推一条戳回执，官方会把它当成一条消息——主人私聊还会立即开一轮对话。" +
        "开启后把这条回执从官方私聊缓冲里摘掉，缓冲区为空则官方直接跳过，从而不再因自己的动作多触发一轮。" +
        "摘除失败会自动退化为「仅剔除消息内容」（不会出现方向错误的脏内容）。群聊不处理（群聊渲染带发起者，语义本来就对）")]
    public bool PokeEchoDropTurnEnabled { get; set; } = true;

    [DisplayName("被赞感知")]
    [Description("感知资料卡被点赞并提示AI可回赞（走官方连接事件，无需额外上报），默认关闭")]
    public bool PerceiveProfileLike { get; set; } = false;

    [DisplayName("被贴表情感知")]
    [Description("感知群消息被贴表情并提示AI（走官方连接事件，无需额外上报），默认关闭")]
    public bool PerceiveEmojiLike { get; set; } = false;

    [DisplayName("他人消息贴表情感知")]
    [Description("感知他人消息被贴表情时也推送（需开启「被贴表情感知」）。注意：部分协议端只上报「回应自己消息」的表情通知，此时本开关不会产生额外提示；无法判定被贴消息归属时按未缓存处理，默认开启")]
    public bool PerceiveOthersEmojiLike { get; set; } = true;

    [DisplayName("引用回复")]
    [Description("启用引用回复消息功能")]
    public bool ReplyEnabled { get; set; } = true;

    [DisplayName("合并转发")]
    [Description("启用合并转发消息功能（群聊/私聊，转发已有/构造新转发/转发最近消息）")]
    public bool ForwardEnabled { get; set; } = true;

    [DisplayName("兼容模式")]
    [Description("与QQSuite.QQTools（幼央工具箱）的协作模式：PreferQQEnhance=优先本插件直接执行引用/贴表情/点赞（推荐，稳定）；Auto=检测到幼央自动让位重叠功能（AI 需二次调用幼央函数）；PreferYuYang=重叠功能一律让位（幼央未装时自动回退自持）；Off=不检测全功能注册")]
    public string CompatibilityMode { get; set; } = "PreferQQEnhance";

    [DisplayName("感知群禁言")]
    [Description("感知自己被禁言/解除禁言并通知AI")]
    public bool PerceiveGroupBan { get; set; } = true;

    [DisplayName("感知成员进群")]
    [Description("感知新成员进群并通知AI")]
    public bool PerceiveGroupIncrease { get; set; } = true;

    [DisplayName("社交风格提示词")]
    [Description("常驻注入的社交行为引导（留空则不注入）。决定AI多主动/自然地使用贴表情、引用、戳一戳、点赞等功能，可自行改写语气与偏好")]
    public string SocialPrompt { get; set; } =
        "你活跃在QQ群聊和私聊中，像真人一样社交：\n" +
        "- 不是每条互动都要打字——贴表情(SetEmojiRecent)、戳一戳(PokeGroupMember/PokePrivateMember)、点赞(SendQQLikes) 都是完整的回应，看到有趣/赞同/暖心的内容随手就用\n" +
        "- 回应某人优先用引用(ReplyRecent)而不是只@；自己说错了马上撤回(DeleteMsgRecent)\n" +
        "- 被戳一戳/被点赞时回敬一下；这些动作可穿插在日常聊天里随手使用，无需请示，也不用特意说明";

    [DisplayName("输入中状态")]
    [Description("私聊时发送输入中状态")]
    public bool TypingIndicatorEnabled { get; set; } = true;

    [DisplayName("输入中延迟(秒)")]
    [Description("收到消息后延迟多久开始发送输入中状态")]
    public double TypingDelaySeconds { get; set; } = 2.0;

    [DisplayName("输入中间隔(秒)")]
    [Description("输入中状态刷新间隔")]
    public double TypingIntervalSeconds { get; set; } = 2.0;

    [DisplayName("输入中最大时长(秒)")]
    [Description("输入中状态最大持续时长")]
    public double TypingMaxSeconds { get; set; } = 60.0;

    [DisplayName("实时消息捕获(已废弃)")]
    [Description("已废弃：4.9.11起不再开第二条WS，所有功能建立在官方连接+历史回拉之上。此开关无效，仅为兼容旧配置保留")]
    public bool LiveCaptureEnabled { get; set; } = true;

    [DisplayName("实时消息缓存大小")]
    [Description("缓存最近N条消息的消息ID/内容，用于QGetMessages/ReplyRecent等定位")]
    public int LiveCacheSize { get; set; } = 500;

    [DisplayName("捕获连接地址(已废弃)")]
    [Description("已废弃：4.9.11起不再使用，仅为兼容旧配置保留")]
    public string CaptureUrl { get; set; } = "";

    [DisplayName("捕获连接Token(已废弃)")]
    [Description("已废弃：4.9.11起不再使用，仅为兼容旧配置保留")]
    public string CaptureToken { get; set; } = "";
}

public class QQEnhanceModule(
    XmlFunctionCaller functionCaller,
    ILogger<QQEnhanceModule> logger,
    Interactor<QQEnhanceModule> interactor,
    QChatService qChatService,
    MessageFilterService messageFilterService,
    ModuleSystem moduleSystem) :
    ChatBehaviour,
    IConfigurable<QQEnhanceConfig>
{
    public QQEnhanceConfig Configuration { get; set; } = null!;

    /// <summary>幼央工具箱模块的完整类型名（套件版 + 旧独立版都认，避免重复接管）</summary>
    private const string YuYangModuleId = "QQSuite.QQTools.QQToolsModule";
    private const string LegacyYuYangModuleId = "YuYang.QQTools.QQToolsModule";

    // QChatService 未公开 OneBotClient，通过反射获取（不修改官方代码）
    private OneBotClient? GetClient()
    {
        FieldInfo? field = typeof(QChatService).GetField("oneBotClient",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(qChatService) as OneBotClient;
    }

    private string? _botNickname;

    /// <summary>QGetMessages 防抖：每个会话的查询时间戳</summary>
    private readonly ConcurrentDictionary<string, List<DateTime>> _qgetTimes = new();
    private readonly object _qgetLock = new();

    /// <summary>bot 真实昵称（转发/显示用），获取失败后退回"我"</summary>
    private string SelfName => _botNickname ?? "我";

    private DateTime _botNicknameLastTry = DateTime.MinValue;

    /// <summary>拉取 bot 昵称（get_login_info）。启动时可能 WS 未连接导致失败，所以带 60 秒节流的重试：
    /// 每次转发/查询用到 SelfName 前都会再试一次，直到成功</summary>
    private async Task FetchBotNicknameAsync(OneBotClient client)
    {
        if (_botNickname != null) return;
        if (DateTime.Now - _botNicknameLastTry < TimeSpan.FromSeconds(60)) return;
        _botNicknameLastTry = DateTime.Now;
        try
        {
            var info = await client.CallActionAsync<LoginInfoResult>("get_login_info");
            if (!string.IsNullOrWhiteSpace(info?.Nickname)) _botNickname = info!.Nickname;
        }
        catch { /* 忽略，用"我"兜底，60秒后重试 */ }
    }

    private sealed class LoginInfoResult
    {
        [JsonPropertyName("nickname")]
        public string? Nickname { get; init; }
    }

    private long GetBotId()
    {
        OneBotClient? client = GetClient();
        return client?.BotId ?? 0;
    }

    // ==================== 幼央兼容检测 ====================

    private bool IsYuYangActive()
    {
        try
        {
            // 套件版：QQ工具箱 与 QQ增强 同属一个插件，看是否真的在运行
            if (ChatActivity?.Container.Instances.Any(instance => instance.GetType().FullName == YuYangModuleId) == true)
            {
                return true;
            }

            // 旧独立插件版：仍以角色模块勾选为准
            if (moduleSystem.GetModule(LegacyYuYangModuleId) == null) return false;
            return Character.Modules.Contains(LegacyYuYangModuleId);
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldDelegate()
    {
        return Configuration.CompatibilityMode switch
        {
            "PreferQQEnhance" => false,
            "PreferYuYang" => IsYuYangActive(),
            "Off" => false,
            _ => IsYuYangActive() // Auto
        };
    }

    private static string DelegateHint(string feature, string yuYangFunction)
    {
        return $"{feature}功能由 QQSuite.QQTools（幼央工具箱）接管，请调用幼央的 {yuYangFunction} 函数";
    }

    // ==================== 统一 OneBot 调用（超时/失败友好提示） ====================

    private async Task<string?> CallActionSafeAsync(
        string action,
        object? @params,
        string feature,
        OneBotClient? client)
    {
        if (client == null)
            return $"{feature}失败：QQ客户端不可用";
        try
        {
            await client.CallActionAsync<object>(action, @params);
            return null; // 成功
        }
        catch (TaskCanceledException)
        {
            return $"{feature}请求超时（10秒未收到响应）。操作可能已生效，请稍后用 QGetMessages 检查确认，不要重复操作";
        }
        catch (Exception e)
        {
            return $"{feature}失败：{e.Message}";
        }
    }

    // Typing indicator 状态管理
    private readonly Dictionary<long, CancellationTokenSource> _typingCts = new();
    private readonly object _typingLock = new();

    // ==================== 消息缓存（历史回拉+发送自存，不依赖任何事件上报） ====================
    private sealed class LiveMessage
    {
        public long MessageId { get; init; }
        public long UserId { get; init; }
        public long GroupId { get; init; }
        /// <summary>私聊会话对端QQ（群聊为0）。私聊筛选必须用这个字段而不是UserId（bot自己发的消息UserId=BotId）</summary>
        public long PeerId { get; init; }
        public string Nickname { get; init; } = "";
        public string Raw { get; init; } = "";
        /// <summary>完整原文（含 [CQ:image,file=url] 等完整CQ码），专供合并转发节点用——NapCat 会解析CQ码重发真实图片/语音/表情。AI展示用 Raw（占位符省token）</summary>
        public string FullRaw { get; init; } = "";
        /// <summary>原始消息段数组 JSON，转发时直接作为节点 content；NapCat 不解析节点里的 CQ 字符串媒体，必须用段数组</summary>
        public string Segments { get; init; } = "";
        /// <summary>含文件/嵌套转发/引用/卡片/音乐等无法CQ重建的结构化段——转发时必须用id节点（NapCat按真实ID取原消息，结构原样保留）</summary>
        public bool IdNodeOnly { get; init; }
        /// <summary>已确认被撤回（内容保留在缓存中作存档，列表标注【已撤回】；撤回/贴表情/引用定位跳过，转发降级为内容节点）</summary>
        public bool IsRecalled { get; set; }
        public long Time { get; init; }
        public bool IsSelf { get; init; }
        public long Seq { get; init; }
    }

    private long _liveSeq;
    private readonly ConcurrentQueue<LiveMessage> _liveMessages = new();
    private readonly ConcurrentDictionary<long, LiveMessage> _liveById = new();

    // ==================== 撤回确认跟踪 ====================
    // NapCat 对 delete_msg 可能返回成功（retcode 0）但 QQ 实际拒绝撤回（超2分钟时限等），
    // 本插件不依赖任何事件上报——撤回后经历史记录比对核验，确认成功的在此登记
    /// <summary>已确认撤回的消息ID（mid -> 撤回unix秒），用于缓存存档标记与列表标注</summary>
    private readonly ConcurrentDictionary<long, long> _recalledIds = new();

    /// <summary>登记一条已撤回消息：保留在缓存中作存档但打上标记（列表标注【已撤回】），定时修剪</summary>
    private void MarkRecalled(long mid)
    {
        if (mid == 0) return;
        _recalledIds[mid] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_liveById.TryGetValue(mid, out LiveMessage? known)) known.IsRecalled = true;
        if (_recalledIds.Count > 2000)
        {
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
            foreach (KeyValuePair<long, long> kv in _recalledIds)
                if (kv.Value < cutoff) _recalledIds.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>是否未被撤回（所有缓存查询的统一过滤条件）</summary>
    private bool NotRecalled(LiveMessage m) => !_recalledIds.ContainsKey(m.MessageId);
    private DateTime _lastLikePromptTime = DateTime.MinValue;
    private DateTime _lastEmojiLikePromptTime = DateTime.MinValue;
    /// <summary>签名通道偏好：null=未探测（按配置走协议端）；true=已确认公共签名可用（沿用，无感）；false=已确认不可用（不再尝试）</summary>
    private bool? _preferPluginSign;

    /// <summary>第三方 ARK 通道失败标记（仅"服务不可用"时置位，避免每次多一次超时等待）</summary>
    private bool _arkBroken;

    /// <summary>上一次 ARK 调用是否为"服务不可用"（HTTP 失败/超时），供失败分类使用</summary>
    private bool ArkServiceUnavailable;

    /// <summary>静态方法内可用的日志器引用（供非静态辅助方法使用）</summary>
    private static ILogger<QQEnhanceModule>? loggerStaticForInfo;

    /// <summary>「他人给别人的消息贴表情」独立计时（与「贴我的消息」分开，各自的冷却不同）</summary>
    private DateTime _lastOthersEmojiLikePromptTime = DateTime.MinValue;

    private static TimeSpan Cool(double seconds) => TimeSpan.FromSeconds(Math.Max(1, seconds));
    private TimeSpan PokeCooldown => Cool(Configuration.PokeCooldownSeconds);
    private TimeSpan ProfileLikeCooldown => Cool(Configuration.ProfileLikeCooldownSeconds);
    private TimeSpan EmojiLikeCooldown => Cool(Configuration.EmojiLikeCooldownSeconds);
    private TimeSpan OthersEmojiLikeCooldown => Cool(Configuration.OthersEmojiLikeCooldownSeconds);

    private void AddLiveMessage(LiveMessage msg)
    {
        if (msg.MessageId == 0) return;
        if (_recalledIds.ContainsKey(msg.MessageId)) msg.IsRecalled = true; // 已确认撤回的消息保留入缓存并标注（存档）
        if (!_liveById.TryAdd(msg.MessageId, msg)) return;
        _liveMessages.Enqueue(msg);
        TrimLiveCache();
    }

    private void TrimLiveCache()
    {
        int max = Math.Max(50, Configuration.LiveCacheSize);
        while (_liveMessages.Count > max && _liveMessages.TryDequeue(out LiveMessage? old))
            _liveById.TryRemove(old.MessageId, out _);
    }

    // ==================== JSON 读取小工具 ====================

    private static long ReadLong(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetInt64(out long v) ? v : 0,
        JsonValueKind.String => long.TryParse(e.GetString(), out long v) ? v : 0,
        _ => 0
    };

    private static long ReadPropLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) ? ReadLong(e) : 0;

    private static string ReadPropString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    /// <summary>从事件根元素提取可读文本（raw_message 优先，否则遍历 message 段数组，富媒体给占位符）</summary>
    private static string ExtractRawText(JsonElement root)
    {
        string raw = ReadPropString(root, "raw_message");
        if (!string.IsNullOrEmpty(raw)) return raw;
        if (!root.TryGetProperty("message", out var me)) return "";
        if (me.ValueKind == JsonValueKind.String) return me.GetString() ?? "";
        if (me.ValueKind == JsonValueKind.Array) return SegmentArrayToText(me);
        return "";
    }

    /// <summary>消息段数组 → 可读文本（图片/语音/表情等给占位符，与官方 OneBotSegment 语义对齐）</summary>
    private static string SegmentArrayToText(JsonElement segments)
    {
        var parts = new List<string>();
        foreach (JsonElement seg in segments.EnumerateArray())
        {
            if (seg.ValueKind != JsonValueKind.Object) continue;
            string type = ReadPropString(seg, "type");
            if (!seg.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            switch (type)
            {
                case "text": parts.Add(ReadPropString(data, "text")); break;
                case "at": parts.Add($"[CQ:at,qq={ReadPropString(data, "qq")}]"); break;
                case "image": parts.Add("[图片]"); break;
                case "face": parts.Add("[表情]"); break;
                case "record": parts.Add("[语音]"); break;
                case "video": parts.Add("[视频]"); break;
                case "reply": parts.Add("[引用]"); break;
                case "forward": parts.Add("[合并转发]"); break;
                case "json": parts.Add("[卡片]"); break;
                case "file": parts.Add("[文件]"); break;
                case "music": parts.Add("[音乐]"); break;
            }
        }
        return string.Join("", parts);
    }

    /// <summary>无法用CQ码文本重建、转发必须走id节点的消息段类型</summary>
    private static readonly HashSet<string> _idNodeSegmentTypes = new() { "file", "forward", "reply", "json", "music" };

    /// <summary>提取完整原文：raw_message 本身含完整CQ码直接用；否则遍历消息段数组重建完整CQ码（图片/语音/视频带URL，表情带id），供合并转发节点原样重发富媒体。
    /// idNodeOnly=true 表示含文件/嵌套转发/引用/卡片/音乐段，转发时必须用id节点</summary>
    private static (string text, bool idNodeOnly, string segments) ExtractFullText(JsonElement root)
    {
        bool idOnly = false;
        string segments = "";
        if (root.TryGetProperty("message", out var me) && me.ValueKind == JsonValueKind.Array)
        {
            segments = me.GetRawText();
            foreach (JsonElement seg in me.EnumerateArray())
            {
                if (seg.ValueKind == JsonValueKind.Object && _idNodeSegmentTypes.Contains(ReadPropString(seg, "type")))
                { idOnly = true; break; }
            }
        }
        string raw = ReadPropString(root, "raw_message");
        if (!string.IsNullOrEmpty(raw)) return (raw, idOnly, segments);
        if (me.ValueKind == JsonValueKind.Undefined) return ("", idOnly, segments);
        if (me.ValueKind == JsonValueKind.String) return (me.GetString() ?? "", idOnly, segments);
        return (SegmentArrayToFullText(me), idOnly, segments);
    }

    /// <summary>消息段数组 → 完整CQ码文本（富媒体保留 url/id，NapCat 解析后可原样重发）</summary>
    private static string SegmentArrayToFullText(JsonElement segments)
    {
        var parts = new List<string>();
        foreach (JsonElement seg in segments.EnumerateArray())
        {
            if (seg.ValueKind != JsonValueKind.Object) continue;
            string type = ReadPropString(seg, "type");
            if (!seg.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            // 取媒体地址：url 优先，其次 file（可能是URL或本地路径）
            string MediaUrl() => ReadPropString(data, "url") is { Length: > 0 } u ? u : ReadPropString(data, "file");
            switch (type)
            {
                case "text": parts.Add(ReadPropString(data, "text")); break;
                case "at": parts.Add($"[CQ:at,qq={ReadPropString(data, "qq")}]"); break;
                case "face": parts.Add($"[CQ:face,id={ReadPropString(data, "id")}]"); break;
                case "image": parts.Add($"[CQ:image,file={MediaUrl()}]"); break;
                case "record": parts.Add($"[CQ:record,file={MediaUrl()}]"); break;
                case "video": parts.Add($"[CQ:video,file={MediaUrl()}]"); break;
                case "reply": break; // 转发节点里嵌套引用无意义，跳过
                case "forward": parts.Add("[合并转发]"); break;
                case "json": parts.Add("[卡片]"); break;
                case "file": parts.Add("[文件]"); break;
                case "music": parts.Add("[音乐]"); break;
            }
        }
        return string.Join("", parts);
    }

    // ==================== 发送自存（bot 自己发的消息也进缓存） ====================

    /// <summary>发送类 API 的返回（取 message_id / res_id）</summary>
    private sealed class SendResult
    {
        [JsonPropertyName("message_id")]
        public JsonElement MessageId { get; init; }

        [JsonPropertyName("res_id")]
        public JsonElement ResId { get; init; }
    }

    /// <summary>把本插件发送的消息存入实时缓存（UserId=BotId，私聊记 PeerId=对方），供转发/引用/撤回/查询使用</summary>
    private void RecordSentMessage(long messageId, long groupId, long peerId, string raw)
    {
        if (messageId == 0) return;
        long botId = GetBotId();
        AddLiveMessage(new LiveMessage {
            MessageId = messageId, UserId = botId, GroupId = groupId, PeerId = peerId,
            Nickname = SelfName, Raw = raw, FullRaw = raw, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            IsSelf = true, Seq = Interlocked.Increment(ref _liveSeq)
        });
    }

    /// <summary>从发送结果中提取 id（兼容数字/数字字符串），取不到返回0</summary>
    private static long ExtractId(JsonElement elem) => ReadLong(elem);

    private static long ExtractSentId(SendResult? result) =>
        result == null ? 0 : ReadLong(result.MessageId);

    private static long ExtractResId(SendResult? result) =>
        result == null ? 0 : ReadLong(result.ResId);

    // ==================== 历史消息回拉补齐（缓存不足时自动调用，message_id 为真实可用ID） ====================

    /// <summary>回拉群/私聊历史消息写入缓存。返回新增条数。只取 message_id 字段（不要与分页参数 message_seq 混淆）。
    /// 私聊特殊处理：get_friend_msg_history 不传 message_seq 时实测可能返回陈旧页（最新消息是数天前，
    /// 导致私聊"最近一条"定位到旧消息）—— NapCat 传 message_seq 会返回该条之后（更新）的消息，
    /// 因此私聊在常规拉取后，用缓存中该会话已知最大 message_id 为锚点前向追新，直至追上最新或翻页上限</summary>
    private async Task<(bool ok, int added)> BackfillHistoryAsync(long groupId, long userId, int count)
    {
        OneBotClient? client = GetClient();
        if (client == null) return (false, 0);
        long botId = GetBotId();

        // 解析一页历史消息写入缓存，返回 (新增条数, 页内最大message_id)
        (int added, long maxId) ParsePage(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("messages", out var msgs) ||
                msgs.ValueKind != JsonValueKind.Array)
                return (0, 0);
            int added = 0;
            long maxId = 0;
            foreach (JsonElement m in msgs.EnumerateArray())
            {
                long mid = ReadPropLong(m, "message_id");
                if (mid == 0) continue;
                if (mid > maxId) maxId = mid;
                if (_liveById.ContainsKey(mid)) continue;
                long uid = ReadPropLong(m, "user_id");
                long gid = ReadPropLong(m, "group_id");
                long time = ReadPropLong(m, "time");
                bool isSelf = botId != 0 && uid == botId;
                string nick = "";
                if (m.TryGetProperty("sender", out var se) && se.ValueKind == JsonValueKind.Object)
                {
                    nick = ReadPropString(se, "card");
                    if (string.IsNullOrEmpty(nick)) nick = ReadPropString(se, "nickname");
                }
                long peerId = gid == 0 ? (groupId == 0 ? userId : 0) : 0;
                var (fullRaw, idOnly, segments) = ExtractFullText(m);
                AddLiveMessage(new LiveMessage {
                    MessageId = mid, UserId = uid, GroupId = gid, PeerId = peerId,
                    Nickname = nick, Raw = ExtractRawText(m), FullRaw = fullRaw, Segments = segments, IdNodeOnly = idOnly, Time = time,
                    IsSelf = isSelf, Seq = Interlocked.Increment(ref _liveSeq),
                    IsRecalled = _recalledIds.ContainsKey(mid) // 回拉恢复存档时保留已撤回标记
                });
                added++;
            }
            return (added, maxId);
        }

        try
        {
            int total = 0;
            if (groupId != 0)
            {
                JsonElement data = await client.CallActionAsync<JsonElement>("get_group_msg_history",
                    new { group_id = groupId, count = Math.Clamp(count, 1, 50) });
                total = ParsePage(data).added;
            }
            else
            {
                // 第一拉：不传 seq（部分NapCat版本此调用返回最新页，部分返回陈旧页）
                JsonElement data = await client.CallActionAsync<JsonElement>("get_friend_msg_history",
                    new { user_id = userId, count = Math.Clamp(count, 1, 50) });
                var (added, _) = ParsePage(data);
                total += added;

                // 前向追新：从缓存已知最大 message_id 起，逐页拉"之后"的消息直到追上最新（最多4页防呆）
                for (int page = 0; page < 4; page++)
                {
                    long anchor = _liveMessages
                        .Where(m => m.GroupId == 0 && m.PeerId == userId)
                        .Select(m => m.MessageId).DefaultIfEmpty(0).Max();
                    if (anchor == 0) break;
                    JsonElement fwd = await client.CallActionAsync<JsonElement>("get_friend_msg_history",
                        new { user_id = userId, message_seq = anchor.ToString(), count = 50 });
                    var (fAdded, fMax) = ParsePage(fwd);
                    total += fAdded;
                    if (fAdded > 0)
                        logger.LogDebug("私聊历史前向追新命中：user={UserId} 锚点={Anchor} 新增={Added} 第{Page}页", userId, anchor, fAdded, page + 1);
                    if (fMax <= anchor || fAdded == 0) break; // 没有更新的消息了
                }
            }
            return (true, total);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "回拉历史消息失败 group={GroupId} user={UserId}", groupId, userId);
            return (false, 0);
        }
    }

    // ==================== 戳回决策状态 ====================
    private sealed record PokeRequest(long UserId, long GroupId, bool IsGroup, DateTime Time);
    /// <summary>待回应的戳请求：按会话索引（私聊=对端QQ / 群聊=群号）。
    /// 早期是单槽全局，多会话连续被戳时后到的会覆盖先到的 ⇒ PokeBack 可能戳错人</summary>
    private readonly Dictionary<string, PokeRequest> _pendingPokes = new();
    private static readonly TimeSpan PokeRequestTtl = TimeSpan.FromMinutes(10);
    private DateTime _lastPokePromptTime = DateTime.MinValue;
    private readonly object _pokeLock = new();
    /// <summary>插件近期主动发出的戳一戳（目标QQ, 时间），用于回执回声抑制</summary>
    private readonly List<(long UserId, DateTime Time)> _recentOutgoingPokes = new();
    /// <summary>戳一戳防刷滑动窗口：(用户QQ, 群号) → 受理时间列表</summary>
    private readonly Dictionary<(long UserId, long GroupId), List<DateTime>> _pokeFlood = new();

    /// <summary>我方戳动作的回执“待剔除”条目：官方 QChat 会把 poke 回执渲染成一条会话消息
    /// （私聊还不带发起者，显示成“对端 戳了戳 对端”），需在它进入 AI 上下文前精确剔除</summary>
    private sealed class PokeEcho
    {
        public long PeerId;
        public long TargetId;
        public DateTime Time = DateTime.Now;
        public bool Used;
        public PokeEcho(long peerId, long targetId) { PeerId = peerId; TargetId = targetId; }
    }
    private readonly List<PokeEcho> _pokeEchoStrips = new();
    private static readonly Regex PrivateMsgTagRegex = new(@"\[私聊消息\((\d+)", RegexOptions.Compiled);

    private static string PokeSessionKey(bool isGroup, long scopeId) => (isGroup ? "group:" : "private:") + scopeId;

    /// <summary>“戳一戳由幼央接管”提示。幼央的私聊/群聊函数名与本插件不同，故用泛化措辞，
    /// 避免把 AI 引到本插件自己的函数上（此前 PokeBack 里写死的是 PokeGroupMember，本就指错）</summary>
    private const string PokeDelegateHint =
        "戳一戳功能由 QQSuite.QQTools（幼央工具箱）接管，请按幼央的函数列表选择对应的戳一戳函数（群聊/私聊各不相同），不要调用本插件的 PokeGroupMember/PokePrivateMember";

    private bool IsSelfQQ(long userId)
    {
        long botId = GetBotId();
        return botId != 0 && userId == botId;
    }

    /// <summary>戳一戳防刷：同一人同一会话在滑动窗口内超过限次则静默忽略，返回 true 表示本次应丢弃。</summary>
    bool PokeFloodExceeded(long userId, long groupId)
    {
        int limitCount = Configuration.PokeBackFloodLimitCount;
        int limitWin = Configuration.PokeBackFloodWindowSeconds;
        if (limitCount <= 0 || limitWin <= 0) return false;

        DateTime now = DateTime.Now;
        lock (_pokeLock)
        {
            var key = (userId, groupId);
            // 顺手清掉长期无活动的会话，避免字典随群/人无限增长
            if (_pokeFlood.Count > 256)
            {
                List<(long UserId, long GroupId)> stale = _pokeFlood
                    .Where(p => p.Key != key && (p.Value.Count == 0 || (now - p.Value[^1]).TotalSeconds > limitWin))
                    .Select(p => p.Key)
                    .ToList();
                foreach (var k in stale) _pokeFlood.Remove(k);
            }

            if (!_pokeFlood.TryGetValue(key, out List<DateTime>? list))
            {
                list = new List<DateTime>();
                _pokeFlood[key] = list;
            }
            list.RemoveAll(t => (now - t).TotalSeconds > limitWin);
            if (list.Count >= limitCount) return true;
            list.Add(now);
            return false;
        }
    }

    /// <summary>记录一次插件主动发出的戳一戳（仅在动作成功后调用——失败也记会过度抑制对方真实的戳）</summary>
    private void MarkOutgoingPoke(long userId)
    {
        lock (_pokeLock)
        {
            _recentOutgoingPokes.RemoveAll(p => (DateTime.Now - p.Time).TotalMinutes > 2);
            _recentOutgoingPokes.Add((userId, DateTime.Now));
        }
    }

    /// <summary>登记一条“我方戳动作回执”待剔除条目（peer=会话对端，target=被戳者）。
    /// 只有我方发起的动作才会登记，因此不会误伤对方真实的戳</summary>
    private void MarkPokeEchoForStrip(long peerId, long targetId)
    {
        if (targetId == 0) return;
        lock (_pokeLock)
        {
            _pokeEchoStrips.RemoveAll(e => (DateTime.Now - e.Time).TotalSeconds > 30);
            _pokeEchoStrips.Add(new PokeEcho(peerId, targetId));
        }
    }

    /// <summary>从 RawJson 读取 NapCat 私聊戳的扩展字段 sender_id（框架事件模型未映射它，
    /// 但 RawJson 是官方为“模型未覆盖字段”预留的取数通道）。
    /// 私聊戳的 user_id 恒为会话对端（与谁戳谁无关），方向只能靠 sender_id 判断；返回 0=协议端未提供</summary>
    private static long TryReadSenderId(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("sender_id", out JsonElement v) && v.TryGetInt64(out long s))
                return s;
        }
        catch { /* 报文非 JSON 或结构异常：回落到旧规则 */ }
        return 0;
    }

    /// <summary>尝试把“我方戳动作回执”从官方私聊缓冲里摘掉：缓冲区被摘空后，官方 FlushPrivateMessage 会
    /// 直接 return（Count==0），于是不再因为自己的动作多触发一轮对话。
    /// 只在能精确匹配（整行等于“戳了戳 {被戳者}”）时删一条；任何异常或结构不符都静默放弃——
    /// 放弃时仍有 ChatSend 内容剔除兜底，不会出现方向错误的脏内容</summary>
    private List<System.Collections.IDictionary>? _stateContainers;
    private DateTime _containerWarnAt = DateTime.MinValue;
    private DateTime _dropFailWarnAt = DateTime.MinValue;
    private readonly List<(long Peer, long Target, DateTime Until)> _pendingEchoDrops = new();

    /// <summary>按“结构”而非字段名定位官方会话状态容器（值类型带 MessageBuffer 属性的 Dictionary&lt;long,_&gt;）。
    /// 这样官方改字段名也不会失效；一个都找不到时留一次告警，便于从日志一眼看出是版本不匹配</summary>
    private List<System.Collections.IDictionary> GetStateContainers()
    {
        if (_stateContainers != null) return _stateContainers;
        List<System.Collections.IDictionary> found = new();
        try
        {
            foreach (FieldInfo f in typeof(QChatService).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
            {
                if (!f.FieldType.IsGenericType) continue;
                Type[] args = f.FieldType.GetGenericArguments();
                if (args.Length != 2 || args[0] != typeof(long)) continue;
                if (args[1].GetProperty("MessageBuffer") == null) continue;
                if (f.GetValue(qChatService) is System.Collections.IDictionary dict) found.Add(dict);
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "按结构定位官方会话状态容器失败");
        }

        if (found.Count == 0 && DateTime.Now - _containerWarnAt > TimeSpan.FromMinutes(5))
        {
            _containerWarnAt = DateTime.Now;
            logger.LogWarning("未能定位官方会话状态容器（QChat 版本可能不同）——戳回执摘除不可用，" +
                "本次起会多一轮对话（内容仍由 ChatSend 清理，并已提示 AI 无需回应）");
        }
        _stateContainers = found;
        return found;
    }

    /// <summary>只在整行完全相等时删一条（绝不碰其他消息）</summary>
    private static bool TryRemoveExactLine(object source, string expect)
    {
        if (source.GetType().GetProperty("MessageBuffer")?.GetValue(source) is not System.Collections.IList buffer)
            return false;
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            if (buffer[i] is string line && string.Equals(line.TrimEnd('\r'), expect, StringComparison.Ordinal))
            {
                buffer.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>摘除“我方戳动作回执”。先在“对端键”对应的会话里找；找不到就全容器按内容定位——
    /// 万一协议端的 user_id 语义与预期不同（键对不上）也能命中。
    /// 群聊不受影响：群聊缓冲里的行是“[QQ]:戳了戳 X”，与本函数的整行精确匹配永不相等</summary>
    private bool TryStripPokeEchoFromBuffers(long peerId, long targetId)
    {
        if (!Configuration.PokeEchoDropTurnEnabled || targetId == 0) return false;
        try
        {
            List<System.Collections.IDictionary> containers = GetStateContainers();
            if (containers.Count == 0) return false;
            string expect = "戳了戳 " + targetId;

            foreach (System.Collections.IDictionary dict in containers)
            {
                if (dict[peerId] is object src && TryRemoveExactLine(src, expect))
                {
                    logger.LogInformation("已摘除我方戳回执：本次不再因自己的动作多触发一轮（peer={Peer} target={Target}）",
                        peerId, targetId);
                    return true;
                }
            }
            foreach (System.Collections.IDictionary dict in containers)
            {
                foreach (object? src in dict.Values)
                {
                    if (src != null && TryRemoveExactLine(src, expect))
                    {
                        logger.LogInformation("已摘除我方戳回执（按内容定位，peer={Peer} target={Target}）", peerId, targetId);
                        return true;
                    }
                }
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "摘除戳回执失败（忽略；内容仍由 ChatSend 兜底剔除）");
        }
        return false;
    }

    private void ClearPendingEchoDrop(long peerId, long targetId)
    {
        lock (_pokeLock)
            _pendingEchoDrops.RemoveAll(d => d.Peer == peerId && d.Target == targetId);
    }

    /// <summary>调度“摘除我方戳回执”。先立即试一次（订阅顺序正确时同一轮派发内即可命中）；
    /// 官方 flush 由 0.3 秒的 tick 驱动，所以未命中先按 5ms 密集轮询抢跑，再退避重试；
    /// 同时登记到 _pendingEchoDrops，让 OnUpdate 每个 tick 再兜一次。
    /// 全部未命中也只是退化成“多一轮无脏内容的对话”，且该轮会被明确提示无需回应</summary>
    private void SchedulePokeEchoTurnDrop(long peerId, long targetId)
    {
        if (!Configuration.PokeEchoDropTurnEnabled || targetId == 0) return;
        if (TryStripPokeEchoFromBuffers(peerId, targetId)) return;

        lock (_pokeLock)
        {
            _pendingEchoDrops.RemoveAll(d => DateTime.Now > d.Until);
            _pendingEchoDrops.Add((peerId, targetId, DateTime.Now.AddSeconds(1.5)));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(5);
                    if (TryStripPokeEchoFromBuffers(peerId, targetId)) { ClearPendingEchoDrop(peerId, targetId); return; }
                }
                foreach (int delay in new[] { 40, 100, 200, 350 })
                {
                    await Task.Delay(delay);
                    if (TryStripPokeEchoFromBuffers(peerId, targetId)) { ClearPendingEchoDrop(peerId, targetId); return; }
                }
                if (DateTime.Now - _dropFailWarnAt > TimeSpan.FromSeconds(30))
                {
                    _dropFailWarnAt = DateTime.Now;
                    logger.LogWarning("戳回执未能摘除（官方已抢先 flush）——本次会多一轮对话；内容已由 ChatSend 清理，并已提示 AI 无需回应");
                }
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "延时摘除戳回执失败（忽略）");
            }
        });
    }

    /// <summary>每个 tick 再兜一次（万一本模块的更新恰好早于官方私聊 flush）</summary>
    private void SweepPokeEchoDrops()
    {
        List<(long Peer, long Target, DateTime Until)> todo;
        lock (_pokeLock)
        {
            _pendingEchoDrops.RemoveAll(d => DateTime.Now > d.Until);
            if (_pendingEchoDrops.Count == 0) return;
            todo = _pendingEchoDrops.ToList();
        }
        foreach ((long peer, long target, DateTime _) in todo)
        {
            if (TryStripPokeEchoFromBuffers(peer, target))
                ClearPendingEchoDrop(peer, target);
        }
    }

    /// <summary>取出待回应的戳请求（默认最近一次；target&gt;0 时按对象精确指定）。过期即丢弃</summary>
    private PokeRequest? TakePendingPoke(long target, out string? error)
    {
        error = null;
        List<PokeRequest> all;
        lock (_pokeLock)
        {
            DateTime now = DateTime.Now;
            foreach (string k in _pendingPokes.Where(kv => (now - kv.Value.Time) > PokeRequestTtl).Select(kv => kv.Key).ToList())
                _pendingPokes.Remove(k);
            all = _pendingPokes.Values.ToList();
        }
        if (all.Count == 0) { error = "没有待回应的戳一戳（可能已过期或已处理）"; return null; }

        PokeRequest? req = null;
        if (target > 0)
        {
            req = all.Where(r => r.UserId == target).OrderByDescending(r => r.Time).FirstOrDefault();
            if (req == null) { error = $"没有来自 {target} 的待回应戳一戳（可能已过期/已处理，或该QQ没有戳过你）"; return null; }
        }
        req ??= all.OrderByDescending(r => r.Time).First();
        lock (_pokeLock)
            _pendingPokes.Remove(PokeSessionKey(req.IsGroup, req.IsGroup ? req.GroupId : req.UserId));
        return req;
    }

    /// <summary>清除待回应戳请求（target&gt;0 只清该对象，否则全清）</summary>
    private void ClearPendingPoke(long target)
    {
        lock (_pokeLock)
        {
            if (target <= 0) { _pendingPokes.Clear(); return; }
            foreach (string k in _pendingPokes.Where(kv => kv.Value.UserId == target).Select(kv => kv.Key).ToList())
                _pendingPokes.Remove(k);
        }
    }

    /// <summary>剔除“我方戳动作回执”被官方渲染成的那行（例：私聊里 “戳了戳 主人QQ”）。
    /// 挂在官方 ChatBot.ChatSend 钩子上——消息进入 AI 上下文之前执行，无并发风险。
    /// 三条件同时满足才删：①当前私聊块对端==条目对端 ②整行完全等于“戳了戳 {target}” ③条目未过期且未用过。
    /// 匹配不到就原样返回（行为等同修复前，绝不劣化）</summary>
    private string StripPokeEchoLines(string message)
    {
        if (string.IsNullOrEmpty(message) || message.IndexOf("戳了戳", StringComparison.Ordinal) < 0)
            return message;

        List<PokeEcho> pending;
        lock (_pokeLock)
        {
            _pokeEchoStrips.RemoveAll(e => (DateTime.Now - e.Time).TotalSeconds > 30);
            pending = _pokeEchoStrips.Where(e => !e.Used).ToList();
        }
        if (pending.Count == 0) return message;

        string[] lines = message.Split('\n');
        long currentPeer = 0;
        int removed = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Match tag = PrivateMsgTagRegex.Match(line);
            if (tag.Success)
            {
                currentPeer = long.TryParse(tag.Groups[1].Value, out long pid) ? pid : 0;
                continue;
            }
            // 新的注入块开始且不是私聊标签 ⇒ 其后散行不属于任何私聊块
            if (line.IndexOf("[消息来源(", StringComparison.Ordinal) >= 0) { currentPeer = 0; continue; }
            if (currentPeer == 0) continue;

            string text = line.TrimEnd('\r').Trim();
            foreach (PokeEcho echo in pending)
            {
                if (echo.PeerId != currentPeer) continue;
                if (!string.Equals(text, "戳了戳 " + echo.TargetId, StringComparison.Ordinal)) continue;
                lines[i] = "";
                lock (_pokeLock) echo.Used = true;
                removed++;
                break;
            }
        }
        if (removed == 0) return message;
        logger.LogDebug("已剔除 {Count} 行我方戳一戳回执（官方渲染的会话消息），避免 AI 误判为被戳", removed);
        string result = string.Join("\n", lines);

        // 漏网兜底：若剔完这条消息已没有任何可回应内容（说明摘缓冲没赶上、官方仍开了这一轮），
        // 就明确提示“无需回应”，免得 AI 把空上下文猜成“主人手滑发了个空消息”。
        // 注意：这一条**不跟**「戳回执不触发对话」开关绑定——它属于内容净化，关掉摘除的用户同样需要
        if (!HasRealContent(result))
            result += "\n[System 以上只是你自己戳一戳动作的记录，没有新的可回应内容，请保持沉默不要回复]";

        return result;
    }

    protected override Task OnAwake()
    {
        loggerStaticForInfo ??= logger;

        bool yuYangActive = IsYuYangActive();
        bool delegateToYuYang = ShouldDelegate();
        if (yuYangActive)
            logger.LogInformation("QQ增强：检测到 QQSuite.QQTools 已启用，兼容模式 {Mode}，重叠功能{State}", Configuration.CompatibilityMode, delegateToYuYang ? "让位给幼央" : "由本插件直接执行");

        // 配置自愈：旧默认提示文本里 {poke} target={uin} / SendQQLikes target={uin} 的参数名与实际函数签名不符
        // （XmlHandler 按名取值，取不到会向 AI 抛"缺少参数"），老配置里存的是旧文本，这里就地修正并记日志
        string healedHint = Configuration.InteractionHintText
            .Replace("{poke} target={uin}", "{poke} {pokeargs}")
            .Replace("{poke} groupId={scope} userId={uin}", "{poke} {pokeargs}")
            .Replace("SendQQLikes target={uin}", "SendQQLikes qq={uin}");
        if (!string.Equals(healedHint, Configuration.InteractionHintText, StringComparison.Ordinal))
        {
            Configuration.InteractionHintText = healedHint;
            logger.LogInformation("QQ增强：已自动修正互动提示文本里的函数参数名，避免 AI 照抄后触发「缺少参数」");
        }

        string explanation = delegateToYuYang
            ? """
                使用规则：
                - 已检测到 QQSuite.QQTools（幼央工具箱）接管：戳一戳、引用回复、点赞、贴表情、撤回、输入中 请调用幼央的函数；本插件负责：禁言(GroupBan)、音乐卡片(SendMusicCard)、合并转发(ForwardRecent/SendForwardById/SendForwardNew)、消息ID查询(QGetMessages)、戳回决策(PokeBack)、感知通知。
                - QQ消息ID通常是负数，编造必败；要操作某条消息先用 QGetMessages 获取真实ID。
                """
            : """
                使用规则：
                - 消息ID禁止编造，必须来自 QGetMessages 或 DeleteMsgRecent list=true 列表；贴表情/引用/撤回默认 target+index 一步到位，无需先查ID。
                - 发送类函数（引用回复/合并转发/音乐卡片）成功即已完成发送，不要再用 QChat 发重复确认；音乐卡片发送较慢，超时先 QGetMessages 确认再决定是否重发。
                - 被戳后系统会提示，用 PokeBack 回戳或忽略。
                - 贴表情 emojiId 常用：201=点赞 264=捂脸 182=笑哭 271=吃瓜 179=doge 268=问号脸；完整对照表用 SetEmojiRecent emojiId=0 查看，按需取用别总用同一个。
                """;

        XmlHandler xmlHandler = new(this) {
            Description = "QQ增强：贴表情、资料卡点赞、撤回、禁言、戳一戳、引用回复、合并转发、点歌发音乐卡片、消息ID查询。可随手用 SetEmojiRecent/ReplyRecent/PokeGroupMember/SendQQLikes 轻量互动",
            Explanation = explanation
        };
        // 文档注入模式：常驻（默认）＝始终可见、零重注入；渐进式＝调用入口才加载（旧行为，可配置回退）
        functionCaller.RegisterHandler(xmlHandler,
            Configuration.DocumentAlwaysResident ? DocumentMode.Explicit : DocumentMode.Implicit,
            DestroyCancellationToken);

        // 常驻社交风格提示（可在配置中自定义/清空）
        if (!string.IsNullOrWhiteSpace(Configuration.SocialPrompt))
            interactor.Prompt(Configuration.SocialPrompt);

        OneBotClient? client = GetClient();
        if (client == null)
        {
            logger.LogWarning("无法获取 OneBotClient，QQ增强功能不可用（请确认已启用QQ聊天模块）");
            return Task.CompletedTask;
        }

        // 始终订阅官方连接事件：引用段提取（被引用消息真实ID）、感知通知都走这里——不开第二条WS
        client.EventReceived += OnEventReceived;

        // 输入中状态：幼央接管时自动让位（避免双插件同时发 set_input_status）
        if (Configuration.TypingIndicatorEnabled && !ShouldDelegate())
        {
            ChatBot.ChatSent += OnChatSent;
            ChatBot.ChatOver += OnChatOver;
        }

        // 回执剔除：挂到官方消息过滤钩子（ChatBot.ChatSend）——剔除“我方戳动作回执”被官方渲染出的那行，
        // 必须在互动提示之前执行（先清干净再附加提示）
        ChatBot.ChatSend += StripPokeEchoLines;

        // 互动提示：挂到官方消息过滤同款钩子（ChatBot.ChatSend），收到QQ消息时按概率附加提示
        ChatBot.ChatSend += OnChatSendHint;

        _ = FetchBotNicknameAsync(client);


        return Task.CompletedTask;
    }

    /// <summary>官方QChat纠错规则要求"QQ消息输入必须输出QChat标签"，与QQ增强发送类函数冲突（用ReplyRecent回复后会触发纠错→AI又发一条重复确认）。
    /// 在所有模块Awake后把该规则替换为扩展版：输出含 QChat 或本插件任意函数名都算合规。模块销毁时恢复原规则。</summary>
    private MessageReplyRule? _originalQChatRule;
    /// <summary>本插件添加的扩展版规则（恢复时只移除它，不按名字批量删）</summary>
    private MessageReplyRule? _extendedQChatRule;

    protected override Task OnStart()
    {
        try
        {
            if (messageFilterService.MessageReplyRules is List<MessageReplyRule> rules)
            {
                _originalQChatRule = rules.FirstOrDefault(r =>
                    string.Equals(r.Name, "QChatService", StringComparison.OrdinalIgnoreCase));
                if (_originalQChatRule != null)
                {
                    MessageReplyRule orig = _originalQChatRule;
                    string[] qqEnhanceFunctions = [
                        "ReplyRecent", "ForwardRecent", "SendForwardById", "SendForwardNew",
                        "SendMusicCard", "QGetMessages", "SetEmojiRecent", "DeleteMsgRecent",
                        "SendQQLikes", "PokeGroupMember", "PokePrivateMember", "PokeBack", "GroupBan"
                    ];
                    rules.Remove(orig);
                    _extendedQChatRule = new MessageReplyRule {
                        Name = orig.Name,
                        InputMatching = orig.InputMatching,
                        OutputMatching = output => orig.OutputMatching(output) ||
                            qqEnhanceFunctions.Any(f => output.Contains(f, StringComparison.OrdinalIgnoreCase)),
                        CorrectionMessage = orig.CorrectionMessage
                    };
                    messageFilterService.AddMessageReplyRule(_extendedQChatRule, DestroyCancellationToken);
                    // 自检：规则确实进去了才宣称成功（官方改了内部实现时能一眼看出来，而不是静默失效）
                    bool applied = messageFilterService.MessageReplyRules.Any(r =>
                        string.Equals(r.Name, orig.Name, StringComparison.OrdinalIgnoreCase) &&
                        r.OutputMatching("QQEnhance-SendMusicCard-探测"));
                    if (applied)
                        logger.LogInformation("QQ增强：已扩展QChat回复格式规则，使用QQ增强函数回复不再触发格式纠正（自检通过）");
                    else
                        logger.LogWarning("QQ增强：回复格式规则替换未生效（官方 MessageFilter 实现可能已变化）——" +
                            "使用本插件函数回复时可能仍被纠正，不影响其它功能");
                }
                else
                {
                    logger.LogWarning("QQ增强：未找到官方 QChatService 回复格式规则（官方可能已改名），使用QQ增强函数回复时可能仍会触发格式纠正");
                }
            }
            else
            {
                logger.LogWarning("QQ增强：消息过滤规则列表类型不是预期的 List<MessageReplyRule>，已跳过回复格式规则扩展（不影响其他功能）");
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "扩展QChat回复格式规则失败（不影响其他功能）");
        }

        // 把本插件的事件订阅移到委托链末尾：官方 QChat 的 OnEventReceived 是 async void，且 poke 分支全程同步
        // （无 await），排在它之后才能在同一轮派发里、官方刚把回执写进缓冲后就摘掉它
        try
        {
            OneBotClient? client = GetClient();
            if (client != null)
            {
                client.EventReceived -= OnEventReceived;
                client.EventReceived += OnEventReceived;
                logger.LogDebug("QQ增强：事件订阅已移至委托链末尾（确保晚于官方处理，便于摘除我方戳回执）");
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "调整事件订阅顺序失败（不影响其他功能；戳回执改为延时摘除）");
        }
        return Task.CompletedTask;
    }

    /// <summary>每个 tick 兜一次“摘除戳回执”（见 SweepPokeEchoDrops）</summary>
    protected override Task OnUpdate()
    {
        SweepPokeEchoDrops();
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        OneBotClient? client = GetClient();
        if (client != null)
            client.EventReceived -= OnEventReceived;

        ChatBot.ChatSent -= OnChatSent;
        ChatBot.ChatOver -= OnChatOver;
        ChatBot.ChatSend -= StripPokeEchoLines;
        ChatBot.ChatSend -= OnChatSendHint;

        // 恢复官方QChat纠错规则（OnStart 中替换过）——只移除本插件添加的那条扩展规则，
        // 不按名字批量删除，避免误删官方或其它插件在此期间注册的同名规则
        if (_originalQChatRule != null &&
            messageFilterService.MessageReplyRules is List<MessageReplyRule> restoreRules)
        {
            if (_extendedQChatRule != null)
            {
                if (!restoreRules.Remove(_extendedQChatRule))
                    logger.LogDebug("QQ增强：扩展版QChat回复格式规则已不在列表中（可能已随模块销毁自动注销）");
                _extendedQChatRule = null;
            }
            if (!restoreRules.Any(r => ReferenceEquals(r, _originalQChatRule)))
                restoreRules.Add(_originalQChatRule);
            _originalQChatRule = null;
        }

        lock (_typingLock)
        {
            foreach (var cts in _typingCts.Values)
                cts.Cancel();
            _typingCts.Clear();
        }

        return Task.CompletedTask;
    }

    // ==================== 消息定位（缓存 + 历史回拉兜底） ====================

    /// <summary>在指定会话中定位目标用户最近一条消息。target 为纯数字按QQ号精确匹配，"我"匹配自己，否则按昵称包含匹配</summary>
    /// <summary>全缓存范围定位目标用户最近一条消息（用于 targetId 缺省时推断会话）</summary>
    private LiveMessage? FindFromUser(long scopeId, string target, bool isGroup, int index, bool includeRecalled = false)
    {
        target = target.Trim();
        bool byId = long.TryParse(target, out long targetUin);
        long botId = GetBotId();
        bool self = target is "我" or "自己" || (byId && botId != 0 && targetUin == botId);

        var candidates = _liveMessages
            .Where(m => isGroup ? m.GroupId == scopeId : (m.GroupId == 0 && m.PeerId == scopeId))
            .Where(m => includeRecalled || NotRecalled(m))
            .Where(m => self ? m.IsSelf
                : byId ? m.UserId == targetUin
                : m.Nickname.Contains(target, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Time)
            .ThenByDescending(m => m.Seq)
            .ToList();

        if (!byId && !self && candidates.Select(m => m.UserId).Distinct().Count() > 1)
            return null;
        return candidates.Skip(Math.Max(0, index - 1)).FirstOrDefault();
    }

    private LiveMessage? FindLatestFromUserAnywhere(string target)
    {
        target = target.Trim();
        bool byId = long.TryParse(target, out long targetUin);
        long botId = GetBotId();
        bool self = target is "我" or "自己" || (byId && botId != 0 && targetUin == botId);

        var matches = _liveMessages
            .Where(NotRecalled)
            .Where(m => self ? m.IsSelf
                : byId ? m.UserId == targetUin
                : m.Nickname.Contains(target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // 昵称跨会话匹配多人时不猜会话（与 FindFromUser 同款守卫），返回null让调用方提示显式传 targetId
        if (!byId && !self && matches.Select(m => m.UserId).Distinct().Count() > 1)
            return null;
        return matches
            .OrderByDescending(m => m.Time)
            .ThenByDescending(m => m.Seq)
            .FirstOrDefault();
    }

    /// <summary>判定 targetId 是群还是私聊：显式 messageType 优先；GroupStates/缓存证据次之；
    /// 都没有时群历史探测（群号能拉到=群，报错=按私聊）——解决私聊场景省略 messageType 被误判成群的问题</summary>
    private async Task<bool> DetectIsGroupAsync(long id, string messageType)
    {
        if (messageType == "group") return true;
        if (messageType == "private") return false;
        if (qChatService.GroupStates.ContainsKey(id)) return true;
        if (_liveMessages.Any(m => m.GroupId == id)) return true;
        if (_liveMessages.Any(m => m.GroupId == 0 && m.PeerId == id)) return false;
        var (ok, _) = await BackfillHistoryAsync(id, 0, 1);
        return ok;
    }

    // ==================== 列表快照（list=true 序号绑定，三功能共用） ====================

    private sealed record ListSnapshot(long ScopeId, bool IsGroup, string Target, DateTime Time, long[] Ids);

    /// <summary>按「目标+会话」分别保存快照：同一轮里先列 A 再列 B 时，A 的序号仍然有效，不会被 B 覆盖</summary>
    private readonly ConcurrentDictionary<string, ListSnapshot> _listSnapshots = new();

    /// <summary>找出与本次 target/targetId 匹配的最新快照</summary>
    private ListSnapshot? FindSnapshot(string target, long targetId)
    {
        string nt = NormalizeTarget(target);
        ListSnapshot? best = null;
        foreach (KeyValuePair<string, ListSnapshot> pair in _listSnapshots)
        {
            ListSnapshot snap = pair.Value;
            if (!string.Equals(snap.Target, nt, StringComparison.OrdinalIgnoreCase)) continue;
            if (targetId != 0 && targetId != snap.ScopeId) continue;
            if (best == null || snap.Time > best.Time) best = snap;
        }
        return best;
    }

    private static string NormalizeTarget(string target)
    {
        string t = target.Trim();
        return t is "" or "自己" ? "我" : t;
    }

    /// <summary>快照是否可用于本次解析：有效期内 + 同target + 同会话（targetId缺省时不限定会话）</summary>
    private bool SnapshotUsable(ListSnapshot snap, string target, long targetId)
    {
        int ttl = Configuration.ListSnapshotSeconds;
        if (ttl <= 0) return false;
        if ((DateTime.Now - snap.Time).TotalSeconds > Math.Min(ttl, 600)) return false;
        if (!string.Equals(snap.Target, NormalizeTarget(target), StringComparison.OrdinalIgnoreCase)) return false;
        return targetId == 0 || targetId == snap.ScopeId;
    }

    /// <summary>按 index 解析目标：快照有效则严格按快照序号（不受新消息影响），否则实时解析（跳过已撤回）</summary>
    private async Task<(LiveMessage? msg, string? error)> ResolveByIndexAsync(string target, long targetId, string messageType, int index)
    {
        ListSnapshot? snap = FindSnapshot(target, targetId);
        if (snap != null && SnapshotUsable(snap, target, targetId))
        {
            if (index > snap.Ids.Length)
                return (null, $"快照候选只有 {snap.Ids.Length} 条，没有第 {index} 条；要看最新请重新 list=true");
            if (_liveById.TryGetValue(snap.Ids[index - 1], out LiveMessage? sm))
                return (sm, null);
            return (null, "快照中该条消息已不在缓存，请重新 list=true 获取候选");
        }
        var (msg, _, _, error) = await ResolveTargetMessageAsync(target, targetId, messageType, index, includeRecalled: false);
        return (msg, error);
    }

    /// <summary>渲染候选列表并拍快照（撤回/贴表情/引用三功能共用，序号对所有功能一致）</summary>
    private async Task<string> BuildCandidateListAsync(string target, long targetId, string messageType)
    {
        var (msg, isGroup, scopeId, error) = await ResolveTargetMessageAsync(target, targetId, messageType, 1, includeRecalled: true);
        if (scopeId == 0 && msg == null) return error!;
        bool g = msg != null ? isGroup
            : scopeId != 0 ? await DetectIsGroupAsync(scopeId, messageType)
            : messageType != "private";
        long sc = msg != null ? scopeId : targetId;
        string nt = NormalizeTarget(target);
        bool self = nt == "我";
        long botId = GetBotId();
        bool byIdUin = long.TryParse(nt, out long targetUin);
        var candidates = _liveMessages
            .Where(m => g ? m.GroupId == sc : (m.GroupId == 0 && m.PeerId == sc))
            .Where(m => self ? m.IsSelf
                : byIdUin ? (m.UserId == targetUin || (botId != 0 && targetUin == botId && m.IsSelf))
                : m.Nickname.Contains(nt, StringComparison.OrdinalIgnoreCase))
            // 排序必须与 FindFromUser 定位严格一致（Time→Seq）：QQ时间戳秒级精度，同秒消息靠插入顺序（继承NapCat历史页时序）决胜；
            // 不能用MessageId比大小——QQ平台消息ID可能为负，大小关系不可靠，且与定位不一致会导致列表序号与实际解析错位
            .OrderByDescending(m => m.Time).ThenByDescending(m => m.Seq)
            .Take(10).ToList();
        if (candidates.Count == 0) return $"未找到 {target} 的消息记录";
        // 只保留最近 5 分钟内的快照，避免长时间运行后无界增长
        foreach (KeyValuePair<string, ListSnapshot> p in _listSnapshots)
            if ((DateTime.Now - p.Value.Time).TotalMinutes > 5)
                _listSnapshots.TryRemove(p.Key, out _);
        _listSnapshots[nt + "|" + sc.ToString()] = new ListSnapshot(sc, g, nt, DateTime.Now, candidates.Select(c => c.MessageId).ToArray());
        int ttl = Math.Clamp(Configuration.ListSnapshotSeconds, 0, 600);
        var sb = new StringBuilder();
        sb.AppendLine($"{target} 的最近 {candidates.Count} 条消息（用 index=序号 或真实消息ID 操作；标注【已撤回】的不可再操作。序号在 {ttl} 秒内有效，期间不受新消息影响）：");
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            sb.AppendLine($"{i + 1}. [消息ID:{c.MessageId}]{(c.IsRecalled ? "【已撤回】" : "")} {(c.IsSelf ? SelfName : c.Nickname)}: {FoldText(c.Raw)}");
        }
        return sb.ToString();
    }


    /// <summary>
    /// 私聊会话的对端 QQ（显式 messageType=private 时用）：
    /// 数字目标 ⇒ 它本身就是私聊对端（**不再满库搜索**——这正是"用户刚在群里说过话就被发进群"的根源）；
    /// "我/自己" ⇒ 取最近有自己发言的私聊会话；昵称 ⇒ 只在私聊会话里找，命中多人则返回 0（不猜）。
    /// </summary>
    private long ResolvePrivatePeerId(string target)
    {
        string nt = NormalizeTarget(target);
        long botId = GetBotId();
        if (long.TryParse(nt, out long uin))
        {
            if (botId != 0 && uin == botId)
            {
                LiveMessage? mine = _liveMessages.Where(m => m.GroupId == 0 && m.IsSelf)
                    .OrderByDescending(m => m.Time).ThenByDescending(m => m.Seq).FirstOrDefault();
                return mine?.PeerId ?? 0;
            }
            return uin;
        }
        var dm = _liveMessages.Where(m => m.GroupId == 0)
            .Where(m => m.Nickname.Contains(nt, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dm.Select(m => m.PeerId).Distinct().Count() > 1) return 0;
        return dm.OrderByDescending(m => m.Time).ThenByDescending(m => m.Seq).FirstOrDefault()?.PeerId ?? 0;
    }

    /// <summary>显式 messageType=group 时用：只在**群聊**会话里找该用户最近发言的群号（命中多个群不猜，返回 0）</summary>
    private long ResolveGroupScopeId(string target)
    {
        string nt = NormalizeTarget(target);
        long botId = GetBotId();
        bool self = nt is "我" or "自己" || (long.TryParse(nt, out long u) && botId != 0 && u == botId);
        bool byId = long.TryParse(nt, out long uin);
        var inGroups = _liveMessages.Where(m => m.GroupId != 0)
            .Where(m => self ? m.IsSelf : byId ? m.UserId == uin : m.Nickname.Contains(nt, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (inGroups.Select(m => m.GroupId).Distinct().Count() > 1) return 0;
        return inGroups.OrderByDescending(m => m.Time).ThenByDescending(m => m.Seq).FirstOrDefault()?.GroupId ?? 0;
    }

    /// <summary>定位结果解析：targetId 缺省时自动推断会话；找不到时回拉历史重试一次。
    /// includeRecalled=true 时（仅撤回功能用）候选含已撤回消息，保证与撤回候选列表序号完全一致</summary>
    private async Task<(LiveMessage? msg, bool isGroup, long scopeId, string? error)> ResolveTargetMessageAsync(
        string target, long targetId, string messageType, int index = 1, bool includeRecalled = false)
    {
        long scopeId = targetId;
        bool isGroup;

        if (scopeId != 0)
        {
            // targetId 明确给出但 messageType 省略时自动判定群/私聊（不再一律当群聊）
            isGroup = await DetectIsGroupAsync(scopeId, messageType);
        }
        else if (messageType == "private")
        {
            // ★ 显式要求私聊：会话对端就是该 QQ，直接在私聊会话内定位（不再满库找"最近一条"）
            isGroup = false;
            scopeId = ResolvePrivatePeerId(target);
            if (scopeId == 0)
                return (null, false, 0, $"未能在私聊会话中定位 {target}（多个同名联系人时请改传QQ号，或显式传 targetId=对方QQ）");
        }
        else if (messageType == "group")
        {
            // ★ 显式要求群聊：只在群聊会话里定位该用户的发言
            isGroup = true;
            scopeId = ResolveGroupScopeId(target);
            if (scopeId == 0)
                return (null, true, 0, $"未能在群聊中定位 {target} 的发言（多个群都有此人时请显式传 targetId=群号）");
        }
        else
        {
            // 未指定类型：保留原启发式——"该用户最近发言所在会话"（与函数文档一致）
            isGroup = messageType != "private";

            LiveMessage? any = FindLatestFromUserAnywhere(target);
            // 注意：此处用最近一条定位"会话"，定位后按 index 在该会话内取倒数第N条
            if (any == null)
            {
                // 缓存没有该用户消息（典型：bot自己通过QChat发的消息不在捕获中）——
                // 先对最近活跃的会话回拉历史再查一次，实现真正的"免ID一步撤回"
                var recentScopes = _liveMessages
                    .OrderByDescending(m => m.Time).ThenByDescending(m => m.Seq)
                    .Select(m => (g: m.GroupId, p: m.PeerId))
                    .Distinct()
                    .Take(3)
                    .ToList();
                foreach (var (g, p) in recentScopes)
                    await BackfillHistoryAsync(g, g != 0 ? 0 : p, 20);
                any = FindLatestFromUserAnywhere(target);
            }
            if (any == null)
                return (null, isGroup, 0, $"未找到 {target} 的任何消息记录（缓存+历史回拉均无）。请显式传 targetId（群号或对方QQ）后重试");
            isGroup = any.GroupId != 0;
            scopeId = isGroup ? any.GroupId : any.PeerId;
        }

        // 关键：定位前无条件刷新历史——不依赖任何事件上报（bot经官方QChat发的消息不一定进缓存），
        // 仅靠缓存会拿到陈旧条目（撤回目标其实是几小时前的消息→超2分钟必失败）。历史回拉的 message_id 真实可撤回
        await BackfillHistoryAsync(isGroup ? scopeId : 0, isGroup ? 0 : scopeId, Math.Max(20, index + 10));
        LiveMessage? live = FindFromUser(scopeId, target, isGroup, index, includeRecalled);

        if (live == null)
        {
            string scope = isGroup ? $"群 {scopeId}" : $"与 {scopeId} 的私聊";
            return (null, isGroup, scopeId,
                $"未在{scope}中找到 {target} 的消息（昵称匹配到多人时也会返回此提示，请改用QQ号）。可用 QGetMessages 查列表确认");
        }
        return (live, isGroup, scopeId, null);
    }

    // ==================== 工具函数 ====================

    /// <summary>QQ 表情编号主动表（SetEmojiRecent emojiId=0 查表用，精选常用、不占常驻文档；
    /// 编号与描述均取自 QQ 系统表情全量表 EmojiFullIdTable）</summary>
    private const string EmojiIdTable =
        "66=爱心 104=哈欠 106=委屈 109=左亲亲 111=可怜 116=示爱 118=抱拳 120=拳头 122=爱你 123=NO 124=OK 125=转圈 129=挥手 144=喝彩 147=棒棒糖 171=茶 173=泪奔 174=无奈 175=卖萌 176=小纠结 179=doge 180=惊喜 181=戳一戳 182=笑哭 183=我最美 201=点赞 203=托脸 212=托腮 214=啵啵 219=蹭一蹭 222=抱抱 227=拍手 232=佛系 240=喷脸 243=甩头 262=脑阔疼 264=捂脸 265=辣眼睛 266=哦哟 267=头秃 268=问号脸 269=暗中观察 270=emm 271=吃瓜 272=呵呵哒 273=我酸了 277=汪汪 278=汗 281=无眼笑 282=敬礼 284=面无表情 285=摸鱼 287=哦 289=睁眼 311=打call 424=续标识";

    /// <summary>QQ 系统表情完整编号表（来源：NapCat face_config.json 全量系统表情，329 条）。
    /// 仅用于「识别」：被动感知到被贴表情时显示「编号（描述）」，未收录的只显示编号。
    /// 不参与主动查表输出，避免常驻文档/回复膨胀。</summary>
    private const string EmojiFullIdTable =
        "0=惊讶 1=撇嘴 2=色 3=发呆 4=得意 5=流泪 6=害羞 7=闭嘴 8=睡 9=大哭 10=尴尬 11=发怒 12=调皮 13=呲牙 14=微笑 15=难过 16=酷 18=抓狂 19=吐 20=偷笑 21=可爱 22=白眼 23=傲慢 24=饥饿 25=困 26=惊恐 27=流汗 28=憨笑 29=悠闲 30=奋斗 31=咒骂 32=疑问 33=嘘 34=晕 35=折磨 36=衰 37=骷髅 38=敲打 39=再见 41=发抖 42=爱情 43=跳跳 46=猪头 49=拥抱 53=蛋糕 55=炸弹 56=刀 59=便便 60=咖啡 63=玫瑰 64=凋谢 66=爱心 67=心碎 74=太阳 75=月亮 76=赞 77=踩 78=握手 79=胜利 85=飞吻 86=怄火 89=西瓜 96=冷汗 97=擦汗 98=抠鼻 99=鼓掌 100=糗大了 101=坏笑 102=左哼哼 103=右哼哼 104=哈欠 105=鄙视 106=委屈 107=快哭了 108=阴险 109=左亲亲 110=吓 111=可怜 112=菜刀 114=篮球 116=示爱 118=抱拳 119=勾引 120=拳头 121=差劲 122=爱你 123=NO 124=OK 125=转圈 129=挥手 137=鞭炮 144=喝彩 146=爆筋 147=棒棒糖 148=喝奶 169=手枪 171=茶 172=眨眼睛 173=泪奔 174=无奈 175=卖萌 176=小纠结 177=喷血 178=斜眼笑 179=doge 180=惊喜 181=戳一戳 182=笑哭 183=我最美 185=羊驼 187=幽灵 193=大笑 194=不开心 198=呃 200=求求 201=点赞 202=无聊 203=托脸 204=吃 206=害怕 210=飙泪 211=我不看 212=托腮 214=啵啵 215=糊脸 216=拍头 217=扯一扯 218=舔一舔 219=蹭一蹭 221=顶呱呱 222=抱抱 223=暴击 224=开枪 225=撩一撩 226=拍桌 227=拍手 229=干杯 230=嘲讽 231=哼 232=佛系 233=掐一掐 235=颤抖 237=偷看 238=扇脸 239=原谅 240=喷脸 241=生日快乐 243=甩头 244=扔狗 262=脑阔疼 263=沧桑 264=捂脸 265=辣眼睛 266=哦哟 267=头秃 268=问号脸 269=暗中观察 270=emm 271=吃瓜 272=呵呵哒 273=我酸了 277=汪汪 278=汗 281=无眼笑 282=敬礼 283=狂笑 284=面无表情 285=摸鱼 286=魔鬼笑 287=哦 288=请 289=睁眼 290=敲开心 292=让我康康 293=摸锦鲤 294=期待 295=拿到红包 297=拜谢 298=元宝 299=牛啊 300=胖三斤 301=好闪 302=左拜年 303=右拜年 305=右亲亲 306=牛气冲天 307=喵喵 311=打call 312=变形 314=仔细分析 317=菜汪 318=崇拜 319=比心 320=庆祝 322=拒绝 323=嫌弃 324=吃糖 325=惊吓 326=生气 332=举牌牌 333=烟花 334=虎虎生威 336=豹富 337=花朵脸 338=我想开了 339=舔屏 341=打招呼 342=酸Q 343=我方了 344=大怨种 345=红包多多 346=你真棒棒 347=大展宏兔 348=福萝卜 349=坚强 350=贴贴 351=敲敲 352=咦 353=拜托 354=尊嘟假嘟 355=耶 356=666 357=裂开 358=骰子 359=包剪锤 360=亲亲 361=狗狗笑哭 362=好兄弟 363=狗狗可怜 364=超级赞 365=狗狗生气 366=芒狗 367=狗狗疑问 368=奥特笑哭 369=彩虹 370=祝贺 371=冒泡 372=气呼呼 373=忙 374=波波流泪 375=超级鼓掌 376=跺脚 377=嗨 378=企鹅笑哭 379=企鹅流泪 380=真棒 381=路过 382=emo 383=企鹅爱心 384=晚安 385=太气了 386=呜呜呜 387=太好笑 388=太头疼 389=太赞了 390=太头秃 391=太沧桑 392=龙年快乐 393=新年中龙 394=新年大龙 395=略略略 396=狼狗 397=抛媚眼 398=超级ok 399=tui 400=快乐 401=超级转圈 402=别说话 403=出去玩 404=闪亮登场 405=好运来 406=姐是女王 407=我听听 408=臭美 409=送你花花 410=么么哒 411=一起嗨 412=开心 413=摇起来 415=划龙舟 416=中龙舟 417=大龙舟 419=火车 420=中火车 421=大火车 422=粽于等到你 423=复兴号 424=续标识 425=求放过 426=玩火 427=偷感 428=收到 429=蛇年快乐 430=蛇身 431=蛇尾 432=灵蛇献瑞 450=撇嘴 451=色 452=微笑 453=发呆 454=得意 455=害羞 456=闭嘴 457=睡 458=我吗 459=优雅 460=硬撑 461=宕机 462=无语 463=新年快乐 464=马上到 465=拆红包 466=羞羞哒 467=摇花手 468=失眠 469=坚毅 470=马到成功 472=心动 474=给你一拳 475=干饭 476=不是吧 477=你懂的 478=对的对的 479=不对不对 480=散味儿 481=学习 482=热化了 483=略 484=比爱心";


    /// <summary>表情ID → 描述：全量识别表打底，主动表覆盖（供贴表情通知渲染；未收录的返回空串）</summary>
    private static readonly Dictionary<long, string> EmojiDescriptions = BuildEmojiDescriptions();

    private static Dictionary<long, string> BuildEmojiDescriptions()
    {
        var map = ParseEmojiDescriptions(EmojiFullIdTable);
        foreach (var kv in ParseEmojiDescriptions(EmojiIdTable))
            map[kv.Key] = kv.Value;   // 主动表描述优先（人工精选，覆盖全表）
        return map;
    }

    /// <summary>解析 "201=点赞 264=捂脸 ..." 形式的表情对照表</summary>
    private static Dictionary<long, string> ParseEmojiDescriptions(string table)
    {
        var map = new Dictionary<long, string>();
        foreach (string part in table.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (long.TryParse(part.AsSpan(0, eq), out long id))
                map[id] = part[(eq + 1)..];
        }
        return map;
    }

    /// <summary>渲染表情编号：对照表内能查到描述时显示「编号（描述）」，否则只显示编号</summary>
    private static string DescribeEmojiId(long id, long count = 1)
    {
        string body = EmojiDescriptions.TryGetValue(id, out string? desc) && !string.IsNullOrEmpty(desc)
            ? $"{id}（{desc}）"
            : id.ToString();
        return count > 1 ? $"{body}×{count}" : body;
    }

    /// <summary>从通知原文的 likes 数组渲染贴的表情（如「264（捂脸）」「12345」；多个用、连接，最多5个）</summary>
    private static string RenderLikes(JsonElement root)
    {
        if (!root.TryGetProperty("likes", out var likesEl) || likesEl.ValueKind != JsonValueKind.Array)
            return "";
        var texts = new List<string>();
        foreach (JsonElement like in likesEl.EnumerateArray())
        {
            if (like.ValueKind != JsonValueKind.Object) continue;
            long eid = ReadPropLong(like, "emoji_id");
            if (eid == 0) continue;
            texts.Add(DescribeEmojiId(eid, Math.Max(1, ReadPropLong(like, "count"))));
            if (texts.Count >= 5) break;   // 防刷屏
        }
        return string.Join("、", texts);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("给QQ消息贴表情回应（一步到位，无需先查ID；仅群聊，私聊平台不支持）。看到有趣/赞同/暖心/好笑的消息随手贴一个，这是真人最轻量的互动方式。常用：201=点赞 264=捂脸 182=笑哭 271=吃瓜 270=emm 179=doge 269=暗中观察 273=我酸了 272=呵呵哒 222=抱抱 227=拍手 311=打call 116=示爱 122=爱你 214=啵啵 219=蹭一蹭 111=可怜 106=委屈 173=泪奔 262=脑阔疼 268=问号脸 265=辣眼睛，更多传 emojiId=0 查看完整对照表再选（别总用同一个）。两种用法：1) 默认贴 target 的最近一条（index 指定倒数第N条）；2) 已知真实消息ID时直接传 messageId（必须来自 QGetMessages 或撤回列表，严禁编造）")]
    public async Task SetEmojiRecent(
        [Description("目标用户QQ号或昵称，\"我\"表示自己")] string target = "",
        [Description("表情ID，默认201=点赞；传 0 = 只显示完整表情ID对照表")] int emojiId = 201,
        [Description("贴倒数第几条，默认1=最近一条")] int index = 1,
        [Description("真实消息ID（传入则直接对该消息贴，忽略 target/index/list）")] long messageId = 0,
        [Description("目标群号（可省略，自动推断最近会话）")] long targetId = 0,
        [Description("消息类型：group或private，可省略")] string messageType = "",
        [Description("true=只列出 target 最近10条候选（不贴），再用 index/messageId 贴")] bool list = false)
    {
        if (!Configuration.EmojiReactEnabled) { interactor.Poke("贴表情功能已禁用"); return; }
        if (ShouldDelegate()) { interactor.Poke(DelegateHint("贴表情", "SendEmojiLike")); return; }
        if (emojiId == 0) { interactor.Poke("QQ表情ID对照（选一个重新调用 SetEmojiRecent 并带上该 emojiId）：" + EmojiIdTable); return; }
        index = Math.Clamp(index, 1, 20);

        if (list)
        {
            interactor.Poke(await BuildCandidateListAsync(target, targetId, messageType));
            return;
        }

        // 私聊平台不支持贴表情（接口假成功）——直接如实封堵
        long mid;
        if (messageId != 0)
        {
            if (_liveById.TryGetValue(messageId, out LiveMessage? k))
            {
                if (k.GroupId == 0)
                {
                    interactor.Poke("QQ私聊不支持贴表情回应，可用文字/戳一戳回应");
                    return;
                }
                if (k.IsRecalled)
                {
                    interactor.Poke($"[消息ID:{messageId}]已被撤回，不能贴表情，请选其他消息");
                    return;
                }
            }
            mid = messageId;
        }
        else
        {
            // 私聊只有两个人：判定为私聊时 target 省略自动=对方
            if (string.IsNullOrWhiteSpace(target) && targetId != 0 &&
                !await DetectIsGroupAsync(targetId, messageType))
                target = targetId.ToString();
            if (string.IsNullOrWhiteSpace(target)) { interactor.Poke("请传 target（QQ号或昵称）或 messageId（真实消息ID）"); return; }
            (LiveMessage? msg, string? error) = await ResolveByIndexAsync(target, targetId, messageType, index);
            if (msg == null) { interactor.Poke(error!); return; }
            if (msg.GroupId == 0) { interactor.Poke("QQ私聊不支持贴表情回应，可用文字/戳一戳回应"); return; }
            if (msg.IsRecalled) { interactor.Poke($"第 {index} 条 [消息ID:{msg.MessageId}]已被撤回，不能贴表情，请选其他序号或重新 list=true"); return; }
            mid = msg.MessageId;
        }

        // 自我防护：不贴自己的消息（除非明确指定"我"）
        if (_liveById.TryGetValue(mid, out LiveMessage? known) && known.IsSelf && target is not ("我" or "自己"))
        {
            interactor.Poke("这条消息是你自己发的，不建议给自己的消息贴表情，已跳过。请选别人发的消息");
            return;
        }
        OneBotClient? client = GetClient();
        string? err = await CallActionSafeAsync("set_msg_emoji_like", new { message_id = mid, emoji_id = emojiId.ToString(), set = true }, "贴表情", client);
        if (err != null) interactor.Poke(err);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("给某人资料卡点赞（好友/陌生人）。对方帮了忙、说了让你开心的话时自然回赞。每人每天上限50个（平台限制），到时明确提示，明天可再来")]
    public async Task SendQQLikes(
        [Description("QQ号")] long qq,
        [Description("点赞次数，默认50次（平台每日上限）")] int times = 50)
    {
        if (!Configuration.SendLikesEnabled) { interactor.Poke("点赞功能已禁用"); return; }
        if (ShouldDelegate()) { interactor.Poke(DelegateHint("点赞", "SendLike")); return; }
        OneBotClient? client = GetClient();
        if (client == null) { interactor.Poke("点赞失败：QQ客户端不可用"); return; }
        try
        {
            times = Math.Clamp(times, 1, 50);
            var chunks = new List<int>();
            for (int i = 0; i < times / 10; i++) chunks.Add(10);
            if (times % 10 > 0) chunks.Add(times % 10);

            int count = 0;
            foreach (int chunk in chunks)
            {
                string? err = await CallActionSafeAsync("send_like", new { user_id = qq, times = chunk }, "点赞", client);
                if (err != null)
                {
                    interactor.Poke($"{err}（已成功 {count} 个赞。若提示\"今日同一好友点赞数已达上限\"说明今天已点满，明天再来；不要重复尝试）");
                    return;
                }
                count += chunk;
            }
            if (Configuration.LikeConfirmEnabled) interactor.Poke($"点赞成功，点了 {count} 个赞");
        }
        catch (Exception e)
        {
            interactor.Poke($"点赞失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("撤回消息（一步到位，无需先查ID）。说错话、发错会话、内容有误时立刻用。三种用法：1) 默认撤 target 的倒数第 index 条；2) 已知真实ID时传 messageId 直接撤（必须来自 QGetMessages 或 list 列表，严禁编造）；3) list=true 先出候选列表再用 index/messageId 撤。私聊只能撤自己的；群聊默认撤自己的，是管理员时可撤他人")]
    public async Task DeleteMsgRecent(
        [Description("目标群号或对方QQ（可省略，自动找目标最近发言所在会话）")] long targetId = 0,
        [Description("撤回谁的消息：默认\"我\"，管理员撤群员时填对方QQ号")] string target = "我",
        [Description("消息类型：group或private，可省略，省略时自动判定")] string messageType = "",
        [Description("撤回倒数第几条：默认1=最近一条")] int index = 1,
        [Description("真实消息ID（传入则直接撤该条，忽略 target/index/list）")] long messageId = 0,
        [Description("true=只列出 target 最近10条候选（不撤回），再用 index/messageId 撤")] bool list = false)
    {
        if (!Configuration.DeleteMsgEnabled) { interactor.Poke("撤回功能已禁用"); return; }
        if (ShouldDelegate()) { interactor.Poke(DelegateHint("撤回", "DeleteMessage")); return; }
        index = Math.Clamp(index, 1, 20);

        // 模式2：直接按真实ID撤
        if (messageId != 0)
        {
            if (_liveById.TryGetValue(messageId, out LiveMessage? byId))
            {
                if (byId.IsRecalled)
                {
                    interactor.Poke($"[消息ID:{messageId}]已经是撤回状态（列表中标注【已撤回】），无需再撤，也不要再对它贴表情/引用");
                    return;
                }
                if (!byId.IsSelf && byId.GroupId == 0)
                {
                    interactor.Poke("私聊无法撤回对方的消息（平台限制），只能撤回自己发的");
                    return;
                }
            }
            await RecallByIdAsync(messageId);
            return;
        }

        // 模式3：出候选列表并拍快照（序号在快照有效期内不受新消息影响）
        if (list)
        {
            interactor.Poke(await BuildCandidateListAsync(target, targetId, messageType));
            return;
        }

        // 模式1：撤倒数第 index 条（快照有效则按快照序号；否则实时解析，自动跳过已撤回，支持连撤）
        (LiveMessage? msg, string? error) = await ResolveByIndexAsync(target, targetId, messageType, index);
        if (msg == null) { interactor.Poke(error!); return; }
        if (msg.IsRecalled)
        {
            interactor.Poke($"第 {index} 条 [消息ID:{msg.MessageId}]已经是撤回状态（列表中标注【已撤回】），无需再撤。要撤别的请用快照内其他序号，或重新 list=true 看最新候选");
            return;
        }
        if (!msg.IsSelf && msg.GroupId == 0)
        {
            interactor.Poke("私聊无法撤回对方的消息（平台限制），只能撤回自己发的");
            return;
        }
        await RecallByIdAsync(msg.MessageId);
    }

    /// <summary>按真实ID撤回并回执（含折叠内容摘要）。说明：NapCat 可能对实际失败的 delete_msg 也返回成功回包，
    /// 但历史接口读的是NapCat本地库（撤回后本地记录不删），无法作为核验依据，故不做结果核验——
    /// 真实ID+115秒预拒已保证自己的消息撤回必然成功；delete_msg 报错时如实回报</summary>
    private async Task RecallByIdAsync(long mid)
    {
        OneBotClient? client = GetClient();
        _liveById.TryGetValue(mid, out LiveMessage? known);
        string preview = known != null
            ? $" {(known.IsSelf ? SelfName : known.Nickname)}: {FoldText(known.Raw)}" : "";

        string? err = await CallActionSafeAsync("delete_msg", new { message_id = mid }, "撤回", client);
        if (err != null)
        {
            interactor.Poke(err + "（RetCode 1200 是 NapCat 内部异常的统称，常见原因：消息超过约2分钟撤回时限、非管理员撤回他人消息、目标是卡片/合并转发类消息、或 NapCat 内存中已丢失该消息记录——超时类消息无法撤回属平台限制）");
            return;
        }

        // 撤回请求被接受：乐观标记已撤回存档（真实新鲜ID下自己的消息撤回几乎必然成功），成功默认静默
        MarkRecalled(mid);
        if (Configuration.RecallConfirmEnabled)
            interactor.Poke($"撤回请求已被 NapCat 接受 [消息ID:{mid}]{preview}。撤回已完成，无需再确认（平台无真实回执，超时/无权限等极少数情况可能实际未撤回）");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("禁言QQ群成员（duration 秒，默认600，传 0 解除）。群号取自群消息标签 [群聊消息(群号,群名)]")]
    public async Task GroupBan(
        [Description("群号")] long groupId,
        [Description("QQ号")] long userId,
        [Description("禁言时长(秒)，默认600，0为解除禁言")] int duration = 600)
    {
        if (!Configuration.GroupBanEnabled) { interactor.Poke("禁言功能已禁用"); return; }
        OneBotClient? client = GetClient();
        string? err = await CallActionSafeAsync("set_group_ban", new { group_id = groupId, user_id = userId, duration }, "禁言", client);
        if (err != null) interactor.Poke(err);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("戳一戳群成员。想引起注意、打招呼、催回复、表达'我来啦/我赞同'时随手戳，比打字轻快。userId 可传自己（自戳）")]
    public async Task PokeGroupMember(
        [Description("群号")] long groupId,
        [Description("QQ号")] long userId)
    {
        if (!Configuration.PokeEnabled) { interactor.Poke("戳一戳功能已禁用"); return; }
        if (ShouldDelegate()) { interactor.Poke(PokeDelegateHint); return; }
        if (groupId <= 0) { interactor.Poke("请传有效的群号（戳群成员需要 groupId）"); return; }
        if (userId <= 0) { interactor.Poke("请传有效的QQ号"); return; }
        OneBotClient? client = GetClient();
        string? err = await CallActionSafeAsync("group_poke", new { group_id = groupId, user_id = userId }, "戳一戳", client);
        if (err != null) { interactor.Poke(err); return; }
        MarkOutgoingPoke(userId); // 成功后记录（失败也记会过度抑制对方真实的戳）
        if (IsSelfQQ(userId))
            logger.LogDebug("群 {Group} 自戳动作完成（自戳允许；其回执由 sender_id 判定，不会触发被戳流程）", groupId);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("私聊戳一戳指定用户。打招呼、提醒看消息时随手用。userId 传对方QQ；传自己的QQ 即为自戳")]
    public async Task PokePrivateMember(
        [Description("QQ号")] long userId)
    {
        if (!Configuration.PokeEnabled) { interactor.Poke("戳一戳功能已禁用"); return; }
        if (ShouldDelegate()) { interactor.Poke(PokeDelegateHint); return; }
        if (userId <= 0) { interactor.Poke("请传有效的QQ号"); return; }
        OneBotClient? client = GetClient();
        string? err = await CallActionSafeAsync("friend_poke", new { user_id = userId }, "私聊戳一戳", client);
        if (err != null) { interactor.Poke(err); return; }
        MarkOutgoingPoke(userId);
        MarkPokeEchoForStrip(userId, userId); // 兜底：协议端不给 sender_id 时也能认出这条回执
        if (IsSelfQQ(userId))
            logger.LogDebug("私聊自戳动作完成（自戳允许；其回执会被剔除，不会触发被戳流程）");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("回应最近一次戳你的人：decide=\"yes\" 回戳 / \"no\" 忽略（被戳时系统会提示）。多个会话同时被戳时用 target 指定对象。主动戳人请用 PokeGroupMember/PokePrivateMember")]
    public async Task PokeBack(
        [Description("yes=回戳，no=忽略")] string decide = "yes",
        [Description("可选：指定回戳对象QQ（多个会话同时被戳时用它精确指定；缺省取最近一次）")] long target = 0)
    {
        if (!Configuration.PokeDecideEnabled || !Configuration.PokeEnabled) { interactor.Poke("戳回功能已禁用"); return; }
        if (ShouldDelegate()) { interactor.Poke(PokeDelegateHint); return; }

        if (decide != "yes")
        {
            ClearPendingPoke(target);
            // 成功静默
            return;
        }

        PokeRequest? req = TakePendingPoke(target, out string? takeErr);
        if (req == null) { interactor.Poke(takeErr!); return; }

        // 防御兜底：待回应请求永不来自自戳（sender_id 判定已挡住），此处防任何路径造成自我回戳回环
        if (IsSelfQQ(req.UserId))
        {
            interactor.Poke("这条戳是你自己发起的（自戳），不需要回戳");
            return;
        }

        OneBotClient? client = GetClient();
        string? err;
        if (req.IsGroup)
            err = await CallActionSafeAsync("group_poke", new { group_id = req.GroupId, user_id = req.UserId }, "戳一戳", client);
        else
            err = await CallActionSafeAsync("friend_poke", new { user_id = req.UserId }, "私聊戳一戳", client);
        if (err != null) { interactor.Poke(err); return; }
        MarkOutgoingPoke(req.UserId);
        if (!req.IsGroup) MarkPokeEchoForStrip(req.UserId, req.UserId);
    }

    // ==================== 引用回复 ====================

    /// <summary>引用回复核心：消息段数组 [{reply},{text}]（NapCat 原生支持，send_msg 无顶层 reply 参数）</summary>
    private async Task<string?> SendReplyCoreAsync(bool isGroup, long scopeId, long replyToId, string message)  // 成功返回null（静默），仅失败/超时返回提示
    {
        OneBotClient? client = GetClient();
        if (client == null) return "引用回复失败：QQ客户端不可用";

        object[] msgArr = [
            new { type = "reply", data = new { id = replyToId.ToString() } },
            new { type = "text", data = new { text = message } }
        ];
        try
        {
            SendResult? sent = isGroup
                ? await client.CallActionAsync<SendResult>("send_group_msg", new { group_id = scopeId, message = msgArr })
                : await client.CallActionAsync<SendResult>("send_private_msg", new { user_id = scopeId, message = msgArr });
            long sentId = ExtractSentId(sent);
            if (sentId != 0)
                RecordSentMessage(sentId, isGroup ? scopeId : 0, isGroup ? 0 : scopeId, message);
            return null;  // 成功静默：不触发AI新一轮，避免多余的确认回复
        }
        catch (TaskCanceledException)
        {
            return "引用回复请求超时（10秒），可能已发送成功，请用 QGetMessages 确认，不要重复发送";
        }
        catch (Exception e)
        {
            return $"引用回复失败：{e.Message}";
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("引用回复（一步到位，无需先查ID）。群聊里回应特定某人时优先用它（比@更清楚）；私聊引用对方原话再回更自然。两种用法：1) 默认引用 target 的最近一条（index 指定倒数第N条）；2) 已知真实ID时传 replyToId 直接引用（必须来自 QGetMessages 或 list 列表，严禁编造）；list=true 可先列候选")]
    public async Task ReplyRecent(
        [Description("回复内容")] string message,
        [Description("目标用户QQ号或昵称，\"我\"表示自己")] string target = "",
        [Description("引用倒数第几条，默认1=最近一条")] int index = 1,
        [Description("被回复消息的真实ID（传入则直接引用该条，忽略 target/index）")] long replyToId = 0,
        [Description("目标群号或对方QQ（可省略，自动推断该用户最近发言所在会话）")] long targetId = 0,
        [Description("消息类型：group或private，可省略")] string messageType = "",
        [Description("true=只列出 target 最近10条候选（不引用），再用 index/replyToId 引用")] bool list = false)
    {
        if (!Configuration.ReplyEnabled) { interactor.Poke("引用回复功能已禁用"); return; }
        if (ShouldDelegate()) { interactor.Poke(DelegateHint("引用回复", "SendReplyMessage")); return; }
        index = Math.Clamp(index, 1, 20);

        if (list)
        {
            interactor.Poke(await BuildCandidateListAsync(target, targetId, messageType));
            return;
        }

        long mid; bool isGroup; long scopeId;
        if (replyToId != 0)
        {
            mid = replyToId;
            scopeId = targetId;
            if (scopeId != 0)
            {
                isGroup = await DetectIsGroupAsync(scopeId, messageType);
            }
            else
            {
                isGroup = messageType != "private";
            }
            if (_liveById.TryGetValue(replyToId, out LiveMessage? knownMsg) && knownMsg.IsRecalled)
            {
                interactor.Poke($"[消息ID:{replyToId}]已被撤回，无法引用，请选择其他消息");
                return;
            }
            if (scopeId == 0)
            {
                if (_liveById.TryGetValue(replyToId, out LiveMessage? known))
                {
                    isGroup = known.GroupId != 0;
                    scopeId = isGroup ? known.GroupId : known.PeerId;
                }
                else
                {
                    interactor.Poke("该消息ID不在缓存中，无法推断会话。请显式传 targetId（群号或对方QQ）与 messageType");
                    return;
                }
            }
        }
        else
        {
            // 私聊只有两个人：判定为私聊时 target 省略自动=对方
            if (string.IsNullOrWhiteSpace(target) && targetId != 0 &&
                !await DetectIsGroupAsync(targetId, messageType))
                target = targetId.ToString();
            if (string.IsNullOrWhiteSpace(target)) { interactor.Poke("请传 target（QQ号或昵称）或 replyToId（真实消息ID）"); return; }
            (LiveMessage? msg, string? error) = await ResolveByIndexAsync(target, targetId, messageType, index);
            if (msg == null) { interactor.Poke(error!); return; }
            if (msg.IsRecalled) { interactor.Poke($"第 {index} 条 [消息ID:{msg.MessageId}]已被撤回，不能引用，请选其他序号或重新 list=true"); return; }
            mid = msg.MessageId; isGroup = msg.GroupId != 0; scopeId = isGroup ? msg.GroupId : msg.PeerId;
        }

        string? result = await SendReplyCoreAsync(isGroup, scopeId, mid, message);
        if (result != null) interactor.Poke(result);
    }

    // ==================== 合并转发 ====================

    /// <summary>发送合并转发核心，返回 (成功?, 提示)</summary>
    private async Task<(bool ok, string text)> SendForwardCoreAsync(bool isGroup, long scopeId, List<object> nodes)
    {
        OneBotClient? client = GetClient();
        if (client == null) return (false, "合并转发失败：QQ客户端不可用");
        try
        {
            SendResult? sent = isGroup
                ? await client.CallActionAsync<SendResult>("send_group_forward_msg", new { group_id = scopeId, messages = nodes })
                : await client.CallActionAsync<SendResult>("send_private_forward_msg", new { user_id = scopeId, messages = nodes });
            long sentId = ExtractSentId(sent);
            long resId = ExtractResId(sent);
            if (sentId != 0)
                RecordSentMessage(sentId, isGroup ? scopeId : 0, isGroup ? 0 : scopeId,
                    resId != 0 ? $"[CQ:forward,id={resId}]" : "[合并转发]");
            return (true, $"合并转发发送成功（{nodes.Count} 个节点{(resId != 0 ? $"，res_id={resId}" : "")}）。消息已实际发出，不要再用 QChat 重复确认");
        }
        catch (TaskCanceledException)
        {
            return (false, "合并转发请求超时（10秒），可能已发送成功，请稍后确认，不要重复发送");
        }
        catch (Exception e)
        {
            return (false, $"合并转发失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("把某群/私聊最近N条消息合并转发（免ID，一步到位）。文本/图片/语音/视频/表情原样转发（图片按原始URL重发为真实图片）；文件/嵌套转发/引用/卡片/音乐按真实消息ID节点保留结构；含bot自己的消息，发送者显示真实QQ昵称；缓存不足自动回拉补齐")]
    public async Task ForwardRecent(
        [Description("目标群号或对方QQ")] long targetId,
        [Description("转发条数，1-50，默认5")] int count = 5,
        [Description("消息类型：group或private。**私聊场景务必传 private**（省略时会按该用户最近发言所在会话推断，可能落错会话）")] string messageType = "")
    {
        if (!Configuration.ForwardEnabled) { interactor.Poke("合并转发功能已禁用"); return; }
        if (targetId == 0) { interactor.Poke("targetId不能为0"); return; }

        bool isGroup = await DetectIsGroupAsync(targetId, messageType);
        count = Math.Clamp(count, 1, 50);

        List<LiveMessage> Query() => _liveMessages
            .Where(m => isGroup ? m.GroupId == targetId : (m.GroupId == 0 && m.PeerId == targetId))
            .Where(NotRecalled)
            .OrderByDescending(m => m.Time)
            .ThenByDescending(m => m.Seq)
            .Take(count)
            .OrderBy(m => m.Time)
            .ThenBy(m => m.Seq)
            .ToList();

        // 转发前无条件刷新历史，保证转发的是此刻最新消息（不依赖任何事件上报）
        await BackfillHistoryAsync(isGroup ? targetId : 0, isGroup ? 0 : targetId, count);
        var matches = Query();

        if (matches.Count == 0)
        {
            interactor.Poke($"{(isGroup ? $"群 {targetId}" : $"与 {targetId} 的私聊")}暂无可转发的消息记录");
            return;
        }

        // 含自己的消息且昵称未知时，先补拉 bot 昵称（启动时WS未连接会导致首次拉取失败）
        if (_botNickname == null && matches.Any(m => m.IsSelf))
        {
            OneBotClient? c0 = GetClient();
            if (c0 != null) await FetchBotNicknameAsync(c0);
        }

        // 混合节点（全自动，无需LLM决策）：
        // - 文本/图片/语音/视频/表情 → 内容节点：FullRaw 含完整 [CQ:image,file=原始URL] 等CQ码，
        //   NapCat 解析后按原始URL重新下载并发出真实媒体，不依赖服务端消息缓存，bot自己发的、历史补拉的都不会丢；
        // - 文件/嵌套转发/引用/卡片/音乐（IdNodeOnly）→ id节点：NapCat 按真实消息ID服务端取原消息，结构原样保留；
        // - 已撤回消息服务端已取不到，强制降级为内容节点（媒体仍可CQ重发，结构化段退化为占位文字）。
        var nodes = matches.Select(m =>
        {
            string nick = m.IsSelf ? SelfName : (string.IsNullOrEmpty(m.Nickname) ? m.UserId.ToString() : m.Nickname);
            if (m.IdNodeOnly && !m.IsRecalled)
                return (object)new { type = "node", data = new { id = m.MessageId.ToString(), nickname = nick, uin = m.UserId.ToString(), content = m.Raw } };
            return (object)new { type = "node", data = new { name = nick, nickname = nick, uin = m.UserId.ToString(), content = BuildForwardContent(m) } };
        }).ToList();
        var (ok, text) = await SendForwardCoreAsync(isGroup, targetId, nodes);
        if (!ok) interactor.Poke(text);  // 成功静默
    }

    /// <summary>构造转发节点内容：有原始消息段数组就原样使用（图片/视频/表情才能在聊天记录里正常渲染），否则退回 CQ 文本</summary>
    private static object BuildForwardContent(LiveMessage m)
    {
        if (!string.IsNullOrEmpty(m.Segments))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(m.Segments);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return doc.RootElement.Clone();
            }
            catch
            {
                // 段数组解析失败时退回文本，不能因此中断整条转发
            }
        }
        return string.IsNullOrEmpty(m.FullRaw) ? m.Raw : m.FullRaw;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("转发一条已有的合并转发消息到群聊/私聊。forwardId 取自 QGetMessages 返回的 [消息ID:xxx]（可为负数）")]
    public async Task SendForwardById(
        [Description("目标群号或对方QQ")] long targetId,
        [Description("合并转发消息的消息ID（来自QGetMessages，可为负）")] long forwardId,
        [Description("消息类型：group或private。**私聊场景务必传 private**（省略时会按该用户最近发言所在会话推断，可能落错会话）")] string messageType = "")
    {
        if (!Configuration.ForwardEnabled) { interactor.Poke("合并转发功能已禁用"); return; }
        // NapCat node schema 强制要求 nickname/content 必填，缺失直接 RetCode 1400；id 有效时忽略这两个字段
        var nodes = new List<object> { new { type = "node", data = new { id = forwardId.ToString(), nickname = "QQ用户", content = "" } } };
        var (okFwd, text) = await SendForwardCoreAsync(await DetectIsGroupAsync(targetId, messageType), targetId, nodes);
        if (!okFwd) interactor.Poke(text);  // 成功静默
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("构造并发送新的合并转发消息。nodesJson 为 JSON 数组，节点两种格式：{\"name\":\"昵称\",\"uin\":QQ号,\"content\":\"内容\"} 或 {\"id\":真实消息ID}（id 必须来自 QGetMessages）。⚠必须传完整合法的 JSON 数组：最外层 [] 包裹，不要漏收尾括号")]
    public async Task SendForwardNew(
        [Description("目标群号或对方QQ")] long targetId,
        [Description("节点JSON数组（必须是完整合法的JSON，[]闭合）")] string nodesJson,
        [Description("消息类型：group或private。**私聊场景务必传 private**（省略时会按该用户最近发言所在会话推断，可能落错会话）")] string messageType = "")
    {
        if (!Configuration.ForwardEnabled) { interactor.Poke("合并转发功能已禁用"); return; }
        try
        {
            // 容错：去掉首尾多余空白；若AI漏了收尾括号，尝试补全（最多补一层 ]）
            string json = nodesJson.Trim();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new Exception("nodesJson必须为JSON数组");
            }
            catch (JsonException)
            {
                string? repaired = RepairJsonArray(json);
                if (repaired == null)
                    throw new JsonException("nodesJson不是合法JSON数组（检查是否漏了收尾括号或引号未闭合）。正确示例：[{\"name\":\"昵称\",\"uin\":123456,\"content\":\"内容\"},{\"id\":-1234567890}]");
                json = repaired;
            }

            using var doc2 = JsonDocument.Parse(json);
            if (doc2.RootElement.ValueKind != JsonValueKind.Array)
                throw new Exception("nodesJson必须为JSON数组");

            var nodes = new List<object>();
            var missingIds = new List<long>();
            foreach (JsonElement node in doc2.RootElement.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                    throw new Exception("每个节点必须是JSON对象，请检查是否漏了花括号");

                bool hasId = node.TryGetProperty("id", out var idElem) &&
                             (idElem.ValueKind == JsonValueKind.Number ||
                              (idElem.ValueKind == JsonValueKind.String && long.TryParse(idElem.GetString(), out _)));
                if (hasId)
                {
                    long id = idElem.ValueKind == JsonValueKind.Number ? idElem.GetInt64()
                        : long.Parse(idElem.GetString()!);
                    // 校验：id 必须在缓存中存在（编造/过期ID会被NapCat静默跳过，全部跳过则整条转发失败）
                    if (!_liveById.TryGetValue(id, out LiveMessage? idMsg))
                    {
                        missingIds.Add(id);
                        continue;
                    }
                    // schema 要求 nickname/content 必填（缺失直接 RetCode 1400），id 有效时这两个字段被忽略
                    string idNick = idMsg.IsSelf ? SelfName : (string.IsNullOrEmpty(idMsg.Nickname) ? idMsg.UserId.ToString() : idMsg.Nickname);
                    nodes.Add(new { type = "node", data = new { id = id.ToString(), nickname = idNick, uin = idMsg.UserId.ToString(), content = idMsg.Raw } });
                }
                else
                {
                    string name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    long uin = node.TryGetProperty("uin", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64()
                        : (node.TryGetProperty("uin", out var u2) && u2.ValueKind == JsonValueKind.String && long.TryParse(u2.GetString(), out var u3) ? u3 : 0);
                    string content = node.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    nodes.Add(new { type = "node", data = new { name, nickname = name, uin = uin.ToString(), content } });
                }
            }
            if (nodes.Count == 0)
                throw new Exception("节点列表为空");
            if (missingIds.Count > 0)
            {
                string miss = string.Join(",", missingIds.Take(5));
                interactor.Poke($"警告：{missingIds.Count} 个id节点不在消息缓存中（{miss}{(missingIds.Count > 5 ? "..." : "")}），NapCat 会跳过这些节点。请改用 QGetMessages 取真实ID，或对该节点改用 name/uin/content 自定义内容格式");
                return;
            }

            var (okNew, text) = await SendForwardCoreAsync(await DetectIsGroupAsync(targetId, messageType), targetId, nodes);
            if (!okNew) interactor.Poke(text);  // 成功静默
        }
        catch (Exception e)
        {
            interactor.Poke($"合并转发失败：{e.Message}");
        }
    }

    /// <summary>修复AI生成的残缺JSON数组：只允许补全缺失的收尾括号，不允许修改内容。返回修复后的JSON，无法修复时返回null</summary>
    private static string? RepairJsonArray(string json)
    {
        string s = json.Trim();
        if (string.IsNullOrEmpty(s) || s[0] != '[') return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '[': case '{': depth++; break;
                case ']': case '}': depth--; break;
            }
        }
        if (inString || depth != 1) return null;

        string candidate = s + "]";
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? candidate : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ==================== 音乐卡片：默认网易云官方卡片（免签名全端可渲染） ====================

    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>卡片 JSON 序列化选项：中文不转义（日志可读，且避免个别实现对 \uXXXX 处理不当）</summary>
    private static readonly JsonSerializerOptions CardJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>CQ码转义（custom音乐卡片字段用）</summary>
    /// <summary>折叠长文本：>15字符时显示首5...尾5（省token，AI可识别）</summary>
    private static string FoldText(string s) => s.Length <= 15 ? s : s[..5] + "..." + s[^5..];

    private static string CqEscape(string s) =>
        s.Replace("&", "&amp;").Replace("[", "&#91;").Replace("]", "&#93;").Replace(",", "&#44;");


    [XmlFunction(FunctionMode.OneShot)]
    [Description("把任意网页链接做成QQ分享卡发送（标题/描述/预览图可留空，会自动抓取网页 og 信息）。用于分享链接、文章、视频页等；卡片点击即跳转该链接")]
    public async Task SendShareCard(
        [Description("跳转链接（http/https）")] string url,
        [Description("目标群号或对方QQ")] long targetId,
        [Description("卡片标题（可留空自动抓取）")] string? title = null,
        [Description("卡片描述（可留空自动抓取）")] string? desc = null,
        [Description("预览图URL（可留空自动抓取）")] string? preview = null,
        [Description("消息类型：group或private（可省略，自动判定）")] string messageType = "")
    {
        if (!Configuration.ShareCardEnabled) { interactor.Poke("通用分享卡功能已禁用"); return; }
        if (!Regex.IsMatch((url ?? "").Trim(), @"^https?://"))
        {
            interactor.Poke("请传以 http/https 开头的链接");
            return;
        }
        OneBotClient? client = GetClient();
        if (client == null) { interactor.Poke("分享卡发送失败：QQ客户端不可用"); return; }

        try
        {
            string link = (url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(desc) || string.IsNullOrWhiteSpace(preview))
            {
                var (ot, od, oi) = await FetchLinkMetaAsync(link);
                if (string.IsNullOrWhiteSpace(title)) title = ot;
                if (string.IsNullOrWhiteSpace(desc)) desc = od;
                if (string.IsNullOrWhiteSpace(preview)) preview = oi;
            }
            string host = Uri.TryCreate(link, UriKind.Absolute, out Uri? u) ? u.Host : link;
            string t = string.IsNullOrWhiteSpace(title) ? host : title!;

            // 复用签名通路：同一服务的 custom 形式可签任意链接卡（公共免key兜底默认可用）
            string? cardJson = null;
            foreach (string signUrl in new[]
                     {
                         Configuration.MusicSignUrl?.Trim() ?? "",
                         Configuration.MusicSignFallbackUrl?.Trim() ?? ""
                     })
            {
                if (signUrl.Length == 0) continue;
                cardJson = await SignNcmCardAsync(signUrl, link, t, desc ?? "", preview ?? "", "", platformType: "link");
                if (cardJson != null) break;
            }
            if (cardJson == null)
            {
                interactor.Poke("分享卡签名失败（第三方签名服务不可用），未发送");
                return;
            }

            bool isGroup = await DetectIsGroupAsync(targetId, messageType);
            object message = new object[] { new { type = "json", data = new { data = cardJson } } };
            object sendParams = isGroup ? new { group_id = targetId, message } : new { user_id = targetId, message };
            SendResult? sent = await client.CallActionAsync<SendResult>("send_msg", sendParams);
            long sentId = ExtractSentId(sent);
            if (sentId != 0)
                RecordSentMessage(sentId, isGroup ? targetId : 0, isGroup ? 0 : targetId, $"[分享卡 {host}]");
            // 成功静默：不触发新一轮确认回复
        }
        catch (TaskCanceledException)
        {
            interactor.Poke("分享卡发送超时（10秒未收到协议端响应）。卡片可能已发出，请先用 QGetMessages 确认，不要重复发送");
        }
        catch (Exception e)
        {
            interactor.Poke($"分享卡发送失败：{e.Message}");
        }
    }


    /// <summary>
    /// 外链抓取安全闸：只允许 http/https，且主机必须是**公网地址**。
    /// 目的：AI 可能被聊天内容诱导去请求 127.0.0.1:3001（NapCat 本机 API）/ 内网设备 / 云元数据地址，
    /// 抓到的内容还会被写进卡片发到群里（等于把内网信息读出来外发）。这里把这类地址全部挡在门外。
    /// 域名会做一次 DNS 解析并逐个校验解析结果（防「域名指向内网」）。
    /// </summary>
    private static async Task<bool> IsPublicHttpUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        try
        {
            IPAddress[] addrs;
            if (IPAddress.TryParse(u.Host, out IPAddress? literal))
                addrs = new[] { literal };
            else
                addrs = await Dns.GetHostAddressesAsync(u.Host);
            if (addrs.Length == 0) return false;
            foreach (IPAddress ip in addrs)
                if (IsPrivateAddress(ip)) return false;
            return true;
        }
        catch
        {
            return false;   // 解析不了就当不可信
        }
    }

    /// <summary>回环 / 私有 / 链路本地 / 组播 / 未指定 / 云元数据（169.254.169.254）一律算内网</summary>
    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any);
        byte[] b = ip.GetAddressBytes();
        if (b.Length != 4) return true;
        return b[0] switch
        {
            0 or 10 or 127 => true,                              // 0.0.0.0/8、10/8、127/8
            169 when b[1] == 254 => true,                        // 169.254/16（含云元数据）
            172 when b[1] >= 16 && b[1] <= 31 => true,           // 172.16/12
            192 when b[1] == 168 => true,                        // 192.168/16
            100 when b[1] >= 64 && b[1] <= 127 => true,          // 100.64/10（CGNAT）
            >= 224 => true,                                      // 组播/保留
            _ => false
        };
    }

    /// <summary>跟随跳转后再校验一次最终落点（防「公网 URL 302 到内网」）</summary>
    private static bool FinalHopIsPublic(HttpResponseMessage resp)
    {
        Uri? final = resp.RequestMessage?.RequestUri;
        return final != null && !IsHostLikelyPrivate(final.Host);
    }

    /// <summary>同步版主机初筛（仅字面量判断，用于跳转落点这种无法再做 DNS 的场合）</summary>
    private static bool IsHostLikelyPrivate(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        if (IPAddress.TryParse(host, out IPAddress? ip)) return IsPrivateAddress(ip);
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.');
    }

    /// <summary>抓取网页 og:title/og:description/og:image（取不到回落 &lt;title&gt;），用于分享卡自动补全</summary>
    private async Task<(string title, string desc, string image)> FetchLinkMetaAsync(string url)
    {
        try
        {
            // 安全闸①：只抓公网 http/https（AI 可能被聊天内容诱导去读内网/NapCat 本机接口）
            if (!await IsPublicHttpUrlAsync(url))
            {
                logger.LogWarning("分享卡：已拦截非公网地址的抓取请求（防 SSRF）");
                return ("", "", "");
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
            using HttpResponseMessage resp = await _http.SendAsync(req);
            // 安全闸②：跟随跳转后的最终落点也要是公网（否则可能 302 到内网）
            if (!FinalHopIsPublic(resp))
            {
                logger.LogWarning("分享卡：跳转落点非公网，已放弃抓取（防重定向型 SSRF）");
                return ("", "", "");
            }
            if (!resp.IsSuccessStatusCode) return ("", "", "");
            long? len = resp.Content.Headers.ContentLength;
            if (len is > 2_000_000) return ("", "", "");            // 超大页面不抓
            string html = await resp.Content.ReadAsStringAsync();
            if (html.Length > 200_000) html = html[..200_000];      // 解析量上限

            static string Unescape(string v) => v.Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&#39;", "'").Trim();
            string Og(string prop)
            {
                Match m = Regex.Match(html,
                    "<meta[^>]+(?:property|name)=[\"']" + prop + "[\"'][^>]*content=[\"']([^\"']*)[\"']",
                    RegexOptions.IgnoreCase);
                if (!m.Success)
                    m = Regex.Match(html,
                        "<meta[^>]+content=[\"']([^\"']*)[\"'][^>]*(?:property|name)=[\"']" + prop + "[\"']",
                        RegexOptions.IgnoreCase);
                return m.Success ? Unescape(m.Groups[1].Value) : "";
            }
            string title = Og("og:title");
            if (title.Length == 0)
            {
                Match mt = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (mt.Success) title = Unescape(mt.Groups[1].Value);
            }
            string desc = Og("og:description");
            if (desc.Length == 0) desc = Og("description");
            string image = Og("og:image");
            if (image.StartsWith("//")) image = "https:" + image;
            return (title, desc, image);
        }
        catch
        {
            return ("", "", "");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("点歌/发卡到QQ。platform=search 时 musicId 传歌名关键词（推荐）；platform=163/qq/kugou/migu/kuwo 时传该平台原生ID（原样透传）；platform=bilibili 时传 BV号或视频链接（如 BV15wUXYAEci）。style 显式填写则按其发送（163=原生卡/custom=自定义卡/record=语音条，仅网易云生效），留空由配置决定；被协议端拒绝会尝试签名兜底，仍不行按 163→custom→record 降级一次；超时不重发（卡片可能已发出，先用 QGetMessages 确认）")]
    public async Task SendMusicCard(
        [Description("目标群号或对方QQ")] long targetId,
        [Description("消息类型：private或group（可省略，自动判定）")] string type = "",
        [Description("音乐平台：search=关键词搜索网易云（推荐）/163=网易云歌曲ID/qq/kugou/migu/kuwo=对应平台原生ID/bilibili=B站视频（传 BV号 或 视频链接）")] string platform = "search",
        [Description("歌曲关键词（platform=search）或平台音乐ID/BV号（其它platform原样透传）")] string musicId = "",
        [Description("卡片样式：163=原生卡片 / custom=自定义卡片 / record=语音条；显式填写则按其发送（仅网易云生效），留空由配置决定")] string style = "")
    {
        if (!Configuration.MusicCardEnabled) { interactor.Poke("音乐卡片功能已禁用"); return; }
        if (targetId == 0) { interactor.Poke("targetId不能为0"); return; }
        if (string.IsNullOrWhiteSpace(musicId)) { interactor.Poke("请传 musicId（歌名关键词或平台音乐ID）"); return; }
        OneBotClient? clientOrNull = GetClient();
        if (clientOrNull == null) { interactor.Poke("音乐卡片发送失败：QQ客户端不可用"); return; }
        OneBotClient client = clientOrNull;

        // 平台归一化（只做便利化，不改变调用者意图）：
        //  1) 别名/大小写/空格统一：网易云|netease|wy|wyy 视为网易云系；qq音乐→qq、酷狗→kugou、酷我→kuwo、咪咕→migu
        //  2) 网易云系按 musicId 形态细分：纯数字视为歌曲ID直用，否则按关键词搜索（与 ResolveNcmIdAsync 既有口径一致）
        //  3) 完全无法识别时同样按网易云处理，避免把非法 type 丢给协议端报错
        string rawPlatform = platform.Trim().ToLowerInvariant();
        string pf = rawPlatform switch
        {
            "qq" or "qq音乐" or "qqmusic" => "qq",
            "kugou" or "酷狗" => "kugou",
            "kuwo" or "酷我" => "kuwo",
            "migu" or "咪咕" => "migu",
            "bilibili" or "b站" or "哔哩哔哩" or "bili" or "bv" => "bilibili",
            _ => long.TryParse(musicId.Trim(), out _) ? "163" : "search",
        };
        if (rawPlatform is not ("" or "search" or "163" or "网易云" or "netease" or "wy" or "wyy"
            or "qq" or "qq音乐" or "qqmusic" or "kugou" or "酷狗" or "kuwo" or "酷我" or "migu" or "咪咕"
            or "bilibili" or "b站" or "哔哩哔哩" or "bili" or "bv"))
            logger.LogDebug("未识别的音乐平台 {Platform}，按网易云处理（musicId 为纯数字则视为歌曲ID，否则按关键词搜索）", platform);
        // 只有网易云系平台能解析出歌曲ID与直链
        bool ncmFamily = pf is "search" or "163";

        // 样式归一化：只认 163/custom/record（含已废弃的 json 一律按 163）；custom/record 需要网易云歌曲ID与直链，
        // 非网易云平台一律走原生卡片，ID 原样透传
        // bot 是否显式指定了样式（显式指定时尊重 bot 的选择，不强制走 ARK —— 保证 bot 的自主权）
        bool styleExplicit = !string.IsNullOrWhiteSpace(style);
        const bool arkForced = false;   // 预留：如需"无视 bot 选择强制 ARK"可改为 true
        string cfgStyle = (styleExplicit ? style : Configuration.MusicCardStyle).Trim().ToLowerInvariant();
        if (cfgStyle is not ("163" or "custom" or "record")) cfgStyle = "163";
        // 非网易云平台：custom/record 需要音频直链，若该平台能解析出直链则允许使用；
        // 解析不到时降级为原生卡片（不静默改样式，会记日志说明原因）
        if (!ncmFamily && cfgStyle != "163")
        {
            logger.LogDebug("平台 {Platform} 请求了样式 {Style}，将尝试解析该平台的音频直链", pf, cfgStyle);
        }


        bool isGroup = await DetectIsGroupAsync(targetId, type);

        // 歌曲信息变量（所有分支共用；在分支前统一填充）
        long ncmId = 0;
        string songTitle = "", songArtist = "", songCover = "", playUrl = "";
        string otherJumpUrl = "";   // 非网易云平台的跳转链接（供 ARK 使用）
        bool resolved = false;

        // ===== 统一先解析歌曲信息（供 ARK / 签名 / custom / record 共用，避免走不同分支时字段缺失）=====
        // platform=search 需要先把关键词转成网易云歌曲ID
        if (pf == "search")
        {
            ncmId = await SearchNetEaseIdAsync(musicId, logger);
            if (ncmId == 0)
            {
                interactor.Poke($"未找到歌曲：{musicId}。可换个更短的关键词重试（只用歌名或只用歌手名），若多次失败说明搜索接口暂时不可用，可稍后再试");
                return;
            }
        }
        else if (pf == "163")
        {
            _ = long.TryParse(musicId.Trim(), out ncmId);
        }

        if (ncmFamily && ncmId != 0)
        {
            (songTitle, songArtist, songCover) = await GetNcmSongDetailAsync(ncmId);
            string? directUrl = await ResolveNcmUrlAsync(ncmId);
            playUrl = !string.IsNullOrEmpty(directUrl) && await IsPlayableAudioAsync(directUrl)
                ? directUrl
                : NeteaseOuterUrl(ncmId);
        }
        else if (!ncmFamily)
        {
            var (ot, oa, oc, oj, op) = await ResolveOtherPlatformInfoAsync(pf, musicId.Trim());
            songTitle = ot; songArtist = oa; songCover = oc; otherJumpUrl = oj; playUrl = op;
            if (playUrl.Length > 0)
                logger.LogDebug("平台 {Platform} 已取到播放直链", pf);
            else
                logger.LogDebug("平台 {Platform} 未取到播放直链（custom/record 将不可用，直接走原生卡片）", pf);
        }

        // B站：走签名通路（与音乐同一个签名服务，format=bilibili），失败才回退自建 news 卡。
        // 为什么改：QQ **只渲染已签名的卡**；自建 news 卡没有 sign，实测在群里显示不出来（只当链接）。
        // 代价（如实记录）：签名服务对 B站 会按图文卡兜底，卡片标签显示"QQ音乐"、appid=100497308。
        // 这是该服务的固定行为（QQSuite.ShareArk 走同一服务，表现完全一致），换来的是**卡片真的能渲染**。
        // 想要"appid 正确但可能不渲染"的自建卡：把「B站卡片走签名」关掉即可。
        if (pf == "bilibili")
        {
            if (!Configuration.BiliCardEnabled) { interactor.Poke("B站卡片功能已禁用"); return; }
            if (string.IsNullOrWhiteSpace(musicId)) { interactor.Poke("请传 BV 号、视频链接或 b23.tv 短链"); return; }
            await SendBiliCardAsync(client, targetId, isGroup, musicId.Trim());
            return;
        }

        async Task<bool> ResolveSongAsync()
        {
            if (resolved) return ncmId != 0;
            resolved = true;
            if (ncmId == 0) ncmId = await ResolveNcmIdAsync(pf, musicId);
            if (ncmId == 0) return false;
            (songTitle, songArtist, songCover) = await GetNcmSongDetailAsync(ncmId);
            // audio 取「真实可播直链」：outer 外链已失效（实测 302 到 music.163.com/404，返回 HTML 而非音频），
            // 因此优先用第三方解析出的 music.126.net 直链，并做真实可播校验（跟随重定向后必须是音频且足够大）
            string? direct = await ResolveNcmUrlAsync(ncmId);
            playUrl = !string.IsNullOrEmpty(direct) && await IsPlayableAudioAsync(direct)
                ? direct
                : NeteaseOuterUrl(ncmId);   // 拿不到真直链时才退回 outer（VIP 歌会走套壳卡，由签名服务处理音频）
            return true;
        }

        object BuildCustomCard() => new object[] {
            new { type = "music", data = new {
                type = "custom",
                url = ncmId != 0 ? $"https://music.163.com/song?id={ncmId}"
                     : (otherJumpUrl.Length > 0 ? otherJumpUrl : musicId),
                audio = playUrl,
                title = songTitle,
                content = songArtist,
                singer = songArtist,
                image = songCover
            } }
        };
        object BuildRecord() => new object[] { new { type = "record", data = new { file = playUrl } } };
        // 签名通道决策：
        //  - 用户配置了 MusicSignUrl        → 直接用插件侧签名（发 json 段）
        //  - 未配置但已确认公共签名可用      → 沿用公共签名（无感，不再走协议端）
        //  - 未配置且未探测/已确认不可用     → 走协议端原生 music 段
        /// <summary>取"模板卡"：优先用签名服务产出的完整卡结构（含 appid/uin/tagIcon），
        /// 供套壳使用；取不到返回 null（套壳将用内置默认值）。结果缓存，避免每次都请求</summary>
        string? templateCard = null;
        bool templateTried = false;
        async Task<string?> GetTemplateAsync()
        {
            if (templateTried) return templateCard;
            templateTried = true;
            string signUrl = Configuration.MusicSignUrl?.Trim() ?? "";
            if (signUrl.Length == 0 && Configuration.MusicSignAutoFallback)
                signUrl = Configuration.MusicSignFallbackUrl?.Trim() ?? "";
            if (signUrl.Length == 0 || ncmId == 0) return null;
            try
            {
                templateCard = await SignNcmCardAsync(signUrl, ncmId.ToString(), songTitle, songArtist, songCover, playUrl);
                if (templateCard != null) logger.LogDebug("已取到模板卡（用于套壳补全结构）");
            }
            catch { templateCard = null; }
            return templateCard;
        }

        /// <summary>第三方 ARK 通道（统一最高优先级，B站卡除外）。成功返回 json 段，失败/未启用返回 null</summary>
        async Task<object?> TryArkAsync(string platformType, string cardId)
        {
            if (!Configuration.MusicArkEnabled || string.IsNullOrWhiteSpace(Configuration.MusicArkToken)) return null;
            // bot 显式指定样式时不用 ARK（由调用方决定，保证 bot 自主权）
            if (styleExplicit && !arkForced) return null;
            if (_arkBroken)
            {
                logger.LogDebug("ARK 通道此前已失败，本次跳过（如需重试请在配置中关闭再开启该通道）");
                return null;
            }
            string arkUrl = Configuration.MusicArkUrl?.Trim() ?? "";
            if (arkUrl.Length == 0) return null;
            string? arkJson = await SignMusicArkAsync(arkUrl, Configuration.MusicArkToken.Trim(),
                cardId: cardId, platform: platformType,
                title: songTitle, artist: songArtist, cover: songCover,
                playUrl: playUrl, jumpUrl: otherJumpUrl);
            if (arkJson != null)
            {
                // 套壳补全：ARK 返回的卡缺 extra{appid,uin} 与 meta.music 的 appid/app_type/ctime/uin/tagIcon，
                // QQ 侧可能不按官方应用卡片渲染（表现为显示不完整/仅试听）→ 用签名服务的完整卡作模板补齐
                string? shelled = ApplyCardShell(arkJson, await GetTemplateAsync(), GetBotId());
                return new object[] { new { type = "json", data = new { data = shelled ?? arkJson } } };
            }
            // 失败分类：只有"签名服务本身不可用"才永久标记；字段类失败（如信息解析不到）不标记，
            // 否则一次失败会连累后续所有平台的卡片（此前非网易云平台带空字段导致 ARK 被永久禁用）
            if (ArkServiceUnavailable)
            {
                _arkBroken = true;
                logger.LogWarning("第三方 ARK 签名服务不可用，已切回默认通道（如需重试请在配置中关闭再开启该通道）");
            }
            else
            {
                logger.LogWarning("第三方 ARK 签名被拒（可能是歌曲信息不完整），本次改用默认通道，下次仍会尝试");
            }
            return null;
        }

        // 原生卡片（非网易云平台或 163 样式）：ARK → 签名 → 协议端 music 段
        async Task<object> BuildNativeCardAsync(string platformType, string cardId)
        {
            object? ark = await TryArkAsync(platformType, cardId);
            if (ark != null) return ark;

            string configured = Configuration.MusicSignUrl?.Trim() ?? "";
            string? signUrl = configured.Length > 0
                ? configured
                : (_preferPluginSign != false && Configuration.MusicSignAutoFallback
                    ? Configuration.MusicSignFallbackUrl?.Trim()
                    : "");

            if (!string.IsNullOrEmpty(signUrl))
            {
                // 一律用 custom 形式（我们提供字段）——实测签名服务对非网易云平台的 {type,id} 形式不可用
                //（qq 返回"关闭id解析功能"、kugou/kuwo/migu 直接 500），而 custom 形式各平台内容都能签成功
                string? signedJson = await SignNcmCardAsync(signUrl, cardId, songTitle, songArtist, songCover, playUrl,
                    platformType: platformType);
                if (signedJson != null)
                {
                    if (configured.Length == 0 && _preferPluginSign == null)
                        logger.LogInformation("公共签名服务可用，后续卡片将沿用它（无需配置）");
                    _preferPluginSign = true;
                    string? shelled = ApplyCardShell(signedJson, null, GetBotId());
                    return new object[] { new { type = "json", data = new { data = shelled ?? signedJson } } };
                }
                if (configured.Length > 0)
                    logger.LogWarning("插件侧签名服务 {SignUrl} 请求失败，回退为原生 music 段交给协议端处理", signUrl);
                else
                {
                    _preferPluginSign = false;   // 公共签名不可用，本次及后续不再尝试
                    logger.LogWarning("公共签名服务不可用，已切回协议端通道（后续不再尝试）");
                }
            }
            return new object[] { new { type = "music", data = new { type = platformType, id = cardId } } };
        }


        // 发送一次并判定结果：ok=协议端已接受（retcode 0，协议端没回 message_id 也算成功）；
        // rejected=平台明确拒绝（肯定没送达，可安全降级）；reason="timeout" 表示未收到响应（不确定是否已发出）
        async Task<(bool ok, bool rejected, string reason, long attemptAt)> SendAsync(object message)
        {
            object sendParams = isGroup ? new { group_id = targetId, message } : new { user_id = targetId, message };
            long attemptAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();   // 发起时间（超时后核验用）
            try
            {
                SendResult? sent = await client.CallActionAsync<SendResult>("send_msg", sendParams);
                long sentId = ExtractSentId(sent);
                if (sentId != 0)
                    RecordSentMessage(sentId, isGroup ? targetId : 0, isGroup ? 0 : targetId, $"[音乐 {pf}:{musicId}]");
                return (true, false, "", attemptAt);
            }
            catch (TaskCanceledException)
            {
                // 注意：超时只是"没收到 OneBot 回执"。record 需下载音频+转码，回执常迟于实际发送，
                // 因此超时 ≠ 未发出（由调用方核验）
                return (false, false, "timeout", attemptAt);
            }
            catch (Exception e)
            {
                // 框架对 retcode≠0 的固定文案「调用失败 (RetCode: x) - msg」→ 平台明确拒绝，肯定没送达
                bool rejected = e.Message.Contains("调用失败 (RetCode:", StringComparison.Ordinal);
                return (false, rejected, e.Message, attemptAt);
            }
        }

        /// <summary>超时后核验消息是否已实际发出：回拉历史，看本会话在 attemptAt 之后有没有 bot 自己的消息。
        /// 找到即认为已发出（避免误报"没发出来"）；核验只做一轮短等待，不拖慢交互</summary>
        async Task<bool> VerifySentAsync(long attemptAt)
        {
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    await BackfillHistoryAsync(isGroup ? targetId : 0, isGroup ? 0 : targetId, 10);
                    long botId = GetBotId();
                    bool found = _liveMessages.Any(m => m.IsSelf
                        && (isGroup ? m.GroupId == targetId : (m.GroupId == 0 && m.PeerId == targetId))
                        && m.Time >= attemptAt - 3);
                    if (found) return true;
                    if (i == 0) await Task.Delay(2000);   // 给协议端一点时间落库
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "超时后核验发送结果失败（按未确认处理）");
            }
            return false;
        }

        // 超时文案：明确"已发出、只是回执慢"，避免 bot 误判成失败后重复发送或向用户报告失败
        const string timeoutUnverifiedHint = "音乐卡片未在10秒内收到协议端回执（注意：回执慢≠未发出，语音条需要下载转码通常更慢）。消息很可能已发出，请勿重复发送；如需确认可用 QGetMessages 查看";
        const string timeoutVerifiedHint = "音乐卡片已发出（协议端回执较慢，已通过历史核验确认），无需重复发送";

        /// <summary>协议端明确拒绝后：尝试公共签名兜底重发（成功即沿用，失败则记录并切回原通道）。
        /// 返回 true 表示已重发成功，调用方直接结束</summary>
        async Task<bool> TryFallbackSignAsync(string platformType, string cardId, string reason)
        {
            if (!Configuration.MusicSignAutoFallback) return false;
            if ((Configuration.MusicSignUrl?.Trim() ?? "").Length > 0) return false;   // 用户已配置自己的签名服务，不覆盖其意图
            string fallbackUrl = Configuration.MusicSignFallbackUrl?.Trim() ?? "";
            if (fallbackUrl.Length == 0) return false;
            if (_preferPluginSign == false) return false;   // 之前已确认不可用

            string? signedJson = await SignMusicCardAsync(fallbackUrl, platformType, cardId);
            if (signedJson == null)
            {
                _preferPluginSign = false;
                logger.LogWarning("公共签名服务 {Url} 不可用，已切回协议端通道（后续不再尝试）", fallbackUrl);
                return false;
            }
            logger.LogInformation("协议端拒绝原生卡片（{Reason}），公共签名兜底成功，后续将沿用它（无需配置）", reason);
            _preferPluginSign = true;
            var r = await SendAsync(new object[] { new { type = "json", data = new { data = signedJson } } });
            if (r.ok) return true;
            if (r.reason == "timeout")
            {
                // 兜底重发的场景：核验到已发出就静默（避免误报）
                if (await VerifySentAsync(r.attemptAt)) logger.LogInformation("公共签名卡片超时但已核验发出");
                else interactor.Poke(timeoutUnverifiedHint);
                return true;
            }
            logger.LogWarning("公共签名卡片发送失败：{Reason}", r.reason);
            return false;
        }

        // ===== 样式 163 / 所有非网易云平台：原生卡片，ID 原样透传 =====
        if (cfgStyle == "163")
        {
            string cardId = pf == "search" ? ncmId.ToString() : musicId.Trim();

            // 歌曲信息已在分支前统一解析（见上方"统一先解析歌曲信息"）

            var r1 = await SendAsync(await BuildNativeCardAsync(pf == "search" ? "163" : pf, cardId));
            if (r1.ok) return;
            if (r1.reason == "timeout")
            {
                if (await VerifySentAsync(r1.attemptAt)) interactor.Poke(timeoutVerifiedHint);
                else interactor.Poke(timeoutUnverifiedHint);
                return;
            }

            // 先尝试"公共签名兜底"（针对协议端未配置签名服务这类情况）
            if (r1.rejected && await TryFallbackSignAsync(pf == "search" ? "163" : pf, cardId, r1.reason))
                return;

            // 平台明确拒绝才降级；语义不明的异常不重发（可能是已送达但回包异常），避免重复发卡
            if (r1.rejected && ncmFamily)
            {
                // 163 被拒时优先降级 custom（部分协议端对 163 依赖签名服务、直接抛错）
                logger.LogWarning("原生卡片被协议端拒绝（{Reason}），自动降级 custom 重发", r1.reason);
                if (await ResolveSongAsync())
                {
                    var r2 = await SendAsync(BuildCustomCard());
                    if (r2.ok) return;
                    if (r2.reason == "timeout")
                    {
                        if (await VerifySentAsync(r2.attemptAt)) interactor.Poke(timeoutVerifiedHint);
                        else interactor.Poke(timeoutUnverifiedHint);
                        return;
                    }
                    if (r2.rejected)
                    {
                        logger.LogWarning("custom 卡片也被拒绝（{Reason}），降级语音条重发", r2.reason);
                        var r3 = await SendAsync(BuildRecord());
                        if (r3.ok) return;
                        if (r3.reason == "timeout")
                        {
                            if (await VerifySentAsync(r3.attemptAt)) interactor.Poke(timeoutVerifiedHint);
                            else interactor.Poke(timeoutUnverifiedHint);
                            return;
                        }
                        interactor.Poke($"音乐卡片发送失败：{r3.reason}");
                        return;
                    }
                    interactor.Poke($"音乐卡片发送失败：{r2.reason}");
                    return;
                }
            }
            interactor.Poke($"音乐卡片发送失败：{r1.reason}");
            return;
        }

        // ===== 样式 custom / record =====
        // 歌曲信息已在分支前统一解析；这里只检查非网易云是否拿到了可播放直链
        if (ncmFamily)
        {
            if (ncmId == 0)
            {
                interactor.Poke($"未找到歌曲：{musicId}。可换个更短的关键词重试（只用歌名或只用歌手名）");
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(playUrl))
        {
            // 非网易云且拿不到直链：custom/record 都需要音频地址 → 改走原生卡片分支
            logger.LogWarning("平台 {Platform} 未能解析出音频直链，样式 {Style} 无法使用，改用原生卡片", pf, cfgStyle);
            // 只有 bot 显式要求该样式时才告知（配置默认导致的降级不打扰 AI）
            if (styleExplicit)
                interactor.Poke($"该平台（{pf}）暂未取到可播放的音频直链，无法使用 {cfgStyle} 样式，已按原生卡片发送");
            object fallbackCard = await BuildNativeCardAsync(pf, musicId.Trim());
            var rFb = await SendAsync(fallbackCard);
            if (!rFb.ok && rFb.reason != "timeout") interactor.Poke($"音乐卡片发送失败：{rFb.reason}");
            return;
        }
        // ARK 仅在「bot 未显式指定样式」时介入（配置默认走 ARK，保证 ARK 优先）；
        // bot 明确要求 custom/record 时尊重其选择（例如它就是想发语音条）
        object? arkStyle = styleExplicit ? null : await TryArkAsync("163", ncmId.ToString());
        if (styleExplicit)
            logger.LogDebug("bot 显式指定样式 {Style}，跳过 ARK 通道（尊重调用方选择）", cfgStyle);
        var rC = await SendAsync(arkStyle ?? (cfgStyle == "record" ? BuildRecord() : BuildCustomCard()));
        if (rC.ok) return;
        if (rC.reason == "timeout")
        {
            if (await VerifySentAsync(rC.attemptAt)) interactor.Poke(timeoutVerifiedHint);
            else interactor.Poke(timeoutUnverifiedHint);
            return;
        }
        if (rC.rejected && cfgStyle == "custom")
        {
            logger.LogWarning("custom 卡片被协议端拒绝（{Reason}），降级语音条重发", rC.reason);
            var rR = await SendAsync(BuildRecord());
            if (rR.ok) return;
            if (rR.reason == "timeout")
            {
                if (await VerifySentAsync(rR.attemptAt)) interactor.Poke(timeoutVerifiedHint);
                else interactor.Poke(timeoutUnverifiedHint);
                return;
            }
            interactor.Poke($"音乐卡片发送失败：{rR.reason}");
            return;
        }
        interactor.Poke($"音乐卡片发送失败：{rC.reason}");
    }

    /// <summary>解析网易云歌曲ID：platform=163且为数字时直用，否则按关键词搜索</summary>
    private static async Task<long> ResolveNcmIdAsync(string platform, string musicId)
    {
        if (platform == "163" && long.TryParse(musicId.Trim(), out long directId))
            return directId;
        if (long.TryParse(musicId.Trim(), out long numeric) && platform == "search")
            return numeric;
        return await SearchNetEaseIdAsync(musicId);
    }

    /// <summary>关键词 → 网易云歌曲ID（163api 最优先 → 官方web搜索 → meting 三级静默兜底，全失败才报错）</summary>
    /// <summary>请求音乐签名服务（与 NapCat 同一协议：POST {type,id}，返回卡片JSON字符串）。失败返回null</summary>
    private static async Task<string?> SignMusicCardAsync(string signUrl, string type, string id)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { type, id }), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(signUrl, content);
            if (!resp.IsSuccessStatusCode) return null;
            string body = (await resp.Content.ReadAsStringAsync()).Trim();
            // 签名服务应返回卡片JSON；有的实现包了一层 {"data": "..."}，做兼容
            if (body.StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                        return d.GetString();
                }
                catch { /* 按原始字符串处理 */ }
            }
            return EnsureCardFields(body.Length > 10 ? body : null);
        }
        catch { return null; }
    }

    /// <summary>套壳：把 ARK/签名返回的卡片补全成「完整卡片结构」。
    /// 部分签名服务（如 ARK）只返回 meta.music 的基础字段，缺 extra{appid,uin} 与 meta.music 的
    /// appid/app_type/ctime/uin/tagIcon —— QQ 侧可能不按"官方应用卡片"渲染，表现为显示不完整/仅试听。
    /// 这里按 QQ 官方音乐卡结构补齐（已有值不覆盖）。template 为可选的模板卡（如签名服务产出的完整卡）。</summary>
    private static string? ApplyCardShell(string? cardJson, string? template, long botUin)
    {
        if (string.IsNullOrWhiteSpace(cardJson) || !cardJson.TrimStart().StartsWith("{")) return cardJson;
        try
        {
            using var doc = JsonDocument.Parse(cardJson);
            JsonObject node = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();

            // 模板中的 appid / uin（优先用模板，其次默认网易云音乐 appid）
            long appid = 100495085, uin = botUin;
            string tagIcon = "https://p.qpic.cn/qqconnect/0/app_100495085_1626060999/100?max-age=2592000&t=0";
            if (!string.IsNullOrWhiteSpace(template) && template!.TrimStart().StartsWith("{"))
            {
                try
                {
                    using var tdoc = JsonDocument.Parse(template);
                    if (tdoc.RootElement.TryGetProperty("meta", out var tmeta) && tmeta.TryGetProperty("music", out var tmusic))
                    {
                        if (tmusic.TryGetProperty("appid", out var ta) && ta.TryGetInt64(out long taVal) && taVal != 0) appid = taVal;
                        else if (tdoc.RootElement.TryGetProperty("extra", out var tex) &&
                                 tex.TryGetProperty("appid", out var tea) && tea.TryGetInt64(out long teaVal) && teaVal != 0) appid = teaVal;
                        if (tmusic.TryGetProperty("tagIcon", out var tti)) tagIcon = tti.GetString() ?? tagIcon;
                    }
                    if (tdoc.RootElement.TryGetProperty("extra", out var tex2) &&
                        tex2.TryGetProperty("uin", out var tu) && tu.TryGetInt64(out long tuVal) && tuVal != 0) uin = tuVal;
                }
                catch { /* 模板不可用则用默认值 */ }
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // 顶层 extra（QQ 判断"官方应用卡片"的关键节点之一）
            if (!node.ContainsKey("extra"))
            {
                node["extra"] = new JsonObject
                {
                    ["app_type"] = 1,
                    ["appid"] = appid,
                    ["uin"] = uin
                };
            }
            // config 补全
            if (node["config"] is not JsonObject cfg)
            {
                cfg = new JsonObject();
                node["config"] = cfg;
            }
            cfg["app_type"] = 1;
            cfg["appid"] = appid;
            cfg["uin"] = uin;
            if (!cfg.ContainsKey("ctime")) cfg["ctime"] = now;
            if (!cfg.ContainsKey("forward")) cfg["forward"] = 1;
            if (!cfg.ContainsKey("type")) cfg["type"] = "normal";
            if (!cfg.ContainsKey("autosize")) cfg["autosize"] = 1;
            if (!cfg.ContainsKey("token")) cfg["token"] = Guid.NewGuid().ToString("N");

            // meta.music 补全
            if (node["meta"]?["music"] is JsonObject music)
            {
                music["app_type"] = 1;
                if (!music.ContainsKey("appid")) music["appid"] = appid;
                if (!music.ContainsKey("ctime")) music["ctime"] = now;
                if (!music.ContainsKey("uin")) music["uin"] = uin;
                if (!music.ContainsKey("tagIcon")) music["tagIcon"] = tagIcon;
            }
            else if (node["meta"]?["news"] is JsonObject news)
            {
                news["app_type"] = 1;
                if (!news.ContainsKey("appid")) news["appid"] = appid;
                if (!news.ContainsKey("ctime")) news["ctime"] = now;
                if (!news.ContainsKey("uin")) news["uin"] = uin;
                if (!news.ContainsKey("tagIcon")) news["tagIcon"] = tagIcon;
            }

            return EnsureCardFields(node.ToJsonString(CardJsonOptions));
        }
        catch
        {
            return EnsureCardFields(cardJson);   // 补全失败则退回基础补全，不破坏原行为
        }
    }

    /// <summary>签名服务返回的卡片 JSON 兜底补全 prompt/ver/view——
    /// 部分签名实现（尤其是自建/第三方）不返回这三个字段，QQ 端可能不渲染成卡片。已有值不覆盖。</summary>
    private static string? EnsureCardFields(string? cardJson)
    {
        if (string.IsNullOrWhiteSpace(cardJson) || !cardJson.TrimStart().StartsWith("{")) return cardJson;
        try
        {
            using var doc = JsonDocument.Parse(cardJson);
            var node = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
            // 只补缺、不覆盖：签名服务返回的卡通常已带 ver/prompt/view，此时**原样返回**，
            // 避免多余的重序列化影响已签名内容（签名对这些字段的有效性由服务端决定）
            bool changed = false;
            if (!node.ContainsKey("ver")) { node["ver"] = "0.0.0.1"; changed = true; }
            if (!node.ContainsKey("prompt"))
            {
                string title = node["meta"]?["music"]?["title"]?.GetValue<string>() ?? "";
                node["prompt"] = string.IsNullOrEmpty(title) ? "[分享]" : $"[分享]{title}";
                changed = true;
            }
            if (!node.ContainsKey("view"))
            {
                node["view"] = node["meta"]?.AsObject().ContainsKey("music") == true ? "music" : "news";
                changed = true;
            }
            return changed ? node.ToJsonString(CardJsonOptions) : cardJson;
        }
        catch
        {
            return cardJson;   // 解析失败原样返回，不破坏原有行为
        }
    }

    /// <summary>网易云歌曲信息 → 签名服务（custom 形式：我们提供歌名/歌手/封面/直链，签名服务无需再查歌）。
    /// 这样 VIP/版权受限歌曲也能签成功（直接送 {type:"163",id} 会因服务查不到歌曲信息而失败）。
    /// 失败自动兼容回退为 {type,id} 形式，成功返回卡片 JSON</summary>
    private static async Task<string?> SignNcmCardAsync(string signUrl, string cardId, string title, string artist, string cover, string playUrl,
        string platformType = "163")
    {
        string songUrl = platformType switch
        {
            "qq" => $"https://y.qq.com/n/ryqq/songDetail/{cardId}",
            "kugou" => $"https://www.kugou.com/song/#hash={cardId}",
            "kuwo" => $"https://www.kuwo.cn/play_detail/{cardId}",
            "migu" => $"https://music.migu.cn/v3/music/song/{cardId}",
            "bilibili" => $"https://www.bilibili.com/video/{cardId}",
            "link" => cardId,                       // 通用链接卡：cardId 本身就是目标 URL
            _ => $"https://music.163.com/song?id={cardId}",
        };
        // 主路径：custom 形式（带完整字段）
        string? byFields = await PostSignAsync(signUrl, new
        {
            type = "custom",
            url = songUrl,
            audio = playUrl,
            title = string.IsNullOrWhiteSpace(title) ? "未知歌曲" : title,
            singer = string.IsNullOrWhiteSpace(artist) ? "未知" : artist,
            content = string.IsNullOrWhiteSpace(artist) ? "未知" : artist,
            image = string.IsNullOrWhiteSpace(cover)
                ? "https://p1.music.126.net/6y-UleORITEDbvrOLV0Q8A==/5639395138885805.jpg" : cover
        });
        if (byFields != null) return byFields;
        // 兼容回退：仅网易云可用 {type,id} 形式（其它平台该形式必失败，不再尝试）
        return platformType is "163" or "search"
            ? await PostSignAsync(signUrl, new { type = "163", id = cardId })
            : null;
    }

    /// <summary>POST 签名服务并取出卡片 JSON（兼容 {"data":"..."} 包裹与直接返回）</summary>
    private static async Task<string?> PostSignAsync(string signUrl, object payload)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(signUrl, content);
            if (!resp.IsSuccessStatusCode) return null;
            string body = (await resp.Content.ReadAsStringAsync()).Trim();
            if (body.StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                        return EnsureCardFields(d.GetString());
                }
                catch { /* 按原始字符串处理 */ }
            }
            return EnsureCardFields(body.Length > 10 ? body : null);
        }
        catch { return null; }
    }

    /// <summary>第三方 ARK 签名：GET {url}?key=...&amp;format=...&amp;song=...&amp;singer=...&amp;url=...&amp;jump=...&amp;cover=...，
    /// 返回 {code,msg,data:卡片JSON}。成功返回卡片 JSON（已补 prompt/ver/view），失败返回 null</summary>
    private async Task<string?> SignMusicArkAsync(string arkUrl, string token, string platform, string cardId,
        string title, string artist, string cover, string playUrl, string jumpUrl)
    {
        try
        {
            string fmt = platform switch { "163" or "search" => "netease", _ => platform };
            string musicUrl = platform is "163" or "search"
                ? $"http://music.163.com/song/media/outer/url?id={cardId}.mp3"
                : playUrl;
            string jump = platform is "163" or "search" ? $"http://music.163.com/song?id={cardId}" : jumpUrl;
            var q = new List<string>
            {
                "key=" + Uri.EscapeDataString(token),
                "format=" + Uri.EscapeDataString(fmt),
                "song=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(title) ? "未知歌曲" : title),
                "singer=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? "未知" : artist),
                "url=" + Uri.EscapeDataString(musicUrl ?? ""),
                "jump=" + Uri.EscapeDataString(jump ?? ""),
                "cover=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(cover)
                    ? "https://p1.music.126.net/6y-UleORITEDbvrOLV0Q8A==/5639395138885805.jpg" : cover)
            };
            // URL 拼接：剥掉地址里已有的 query（否则会出现两个 ? → 服务端只认第一个，参数全丢）
            // 同时兼容"用户把 key 直接填进地址"的情况（不再重复追加 key）
            string arkBase = arkUrl;
            string arkQuery = "";
            int qi = arkUrl.IndexOf('?');
            if (qi >= 0)
            {
                arkBase = arkUrl[..qi];
                arkQuery = arkUrl[(qi + 1)..].TrimEnd('&');
                // 地址里已带 key=... 时，从中取出作为 token 使用，并从 q 里去掉重复的 key
                if (arkQuery.Contains("key=", StringComparison.OrdinalIgnoreCase))
                {
                    q.RemoveAll(x => x.StartsWith("key=", StringComparison.OrdinalIgnoreCase));
                    loggerStaticForInfo?.LogDebug("检测到 ARK 地址中已包含 key，优先使用地址内的 key");
                }
            }
            var qFinal = new List<string>(q);
            if (arkQuery.Length > 0) qFinal.Insert(0, arkQuery);
            string fullUrl = arkBase + "?" + string.Join("&", qFinal);
            using var resp = await _http.GetAsync(fullUrl);
            if (!resp.IsSuccessStatusCode)
            {
                ArkServiceUnavailable = true;   // HTTP 失败 = 服务不可用，值得永久标记
                return null;
            }
            string body = (await resp.Content.ReadAsStringAsync()).Trim();
            if (!body.StartsWith("{"))
            {
                ArkServiceUnavailable = true;
                return null;
            }
            ArkServiceUnavailable = false;      // 能正常应答 = 服务可用，业务失败不标记
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("code", out var codeEl) || ReadLong(codeEl) != 200)
            {
                string arkMsg = doc.RootElement.TryGetProperty("msg", out var mm) ? mm.GetString() ?? "" : "";
                loggerStaticForInfo?.LogWarning("ARK 签名被拒（业务错误）：{Msg}", arkMsg);
                return null;
            }
            if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return null;
            string cardJson = dataEl.ValueKind == JsonValueKind.String ? dataEl.GetString() ?? "" : dataEl.GetRawText();
            return EnsureCardFields(cardJson);
        }
        catch (Exception ex)
        {
            ArkServiceUnavailable = true;   // 网络异常（连不上/超时）= 服务不可用，值得永久标记
            logger.LogDebug(ex, "ARK 签名失败");
            return null;
        }
    }


    /// <summary>把 BV号 / 视频链接 / b23.tv 短链 统一成 BV 号（解析失败原样返回，由调用方报错）</summary>
    private async Task<string> NormalizeBiliInputAsync(string input)
    {
        string raw = (input ?? "").Trim();
        Match m = Regex.Match(raw, @"BV[0-9A-Za-z]{10}");
        if (m.Success) return m.Value;               // BV号 或 链接里已带 BV
        if (!raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return raw;
        try
        {
            // 安全闸：只解析公网 http/https（防被诱导请求内网地址）
            if (!await IsPublicHttpUrlAsync(raw)) return raw;
            using var req = new HttpRequestMessage(HttpMethod.Get, raw);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
            using HttpResponseMessage resp = await _http.SendAsync(req);   // 跟随跳转
            if (!FinalHopIsPublic(resp)) return raw;
            string finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            Match m2 = Regex.Match(finalUrl, @"BV[0-9A-Za-z]{10}");
            if (m2.Success) return m2.Value;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "B站短链解析失败：{Input}", raw);
        }
        return raw;
    }

    /// <summary>B站卡片签名通路：ARK(xianyuw，需 key) → 自配签名 → 公共免key兜底；全失败返回 null（回退自建卡）</summary>
    private async Task<string?> TrySignBiliCardAsync(string bv, string title, string up, string cover, string jump)
    {
        if (!Configuration.BiliCardUseSign) return null;
        string t = string.IsNullOrWhiteSpace(title) ? bv : title;
        string a = string.IsNullOrWhiteSpace(up) ? "bilibili" : up;

        // 1) ARK 服务（用户配了 key 时优先：字段最完整）
        if (Configuration.MusicArkEnabled && !string.IsNullOrWhiteSpace(Configuration.MusicArkToken))
        {
            try
            {
                string? arkJson = await SignMusicArkAsync(Configuration.MusicArkUrl.Trim(), Configuration.MusicArkToken.Trim(),
                    cardId: bv, platform: "bilibili", title: t, artist: a, cover: cover,
                    playUrl: jump, jumpUrl: jump);
                if (arkJson != null)
                {
                    logger.LogInformation("B站卡片：已通过 ARK 签名服务出卡（{Bv}）", bv);
                    return arkJson;
                }
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "B站 ARK 签名失败，改用下一个通路");
            }
        }

        // 2) 自配签名地址 → 3) 公共免 key 兜底（后者受「自动兜底」开关约束，与音乐通路语义一致：
        //    用户关掉自动兜底 = 不希望走公共签名服务，那就只用自己的）
        var signUrls = new List<string> { Configuration.MusicSignUrl?.Trim() ?? "" };
        if (Configuration.MusicSignAutoFallback)
            signUrls.Add(Configuration.MusicSignFallbackUrl?.Trim() ?? "");
        foreach (string signUrl in signUrls)
        {
            if (signUrl.Length == 0) continue;
            string? card = await SignNcmCardAsync(signUrl, bv, t, a, cover, jump, platformType: "bilibili");
            if (card != null)
            {
                logger.LogInformation("B站卡片：已通过签名服务出卡（{Host}）", Uri.TryCreate(signUrl, UriKind.Absolute, out Uri? u) ? u.Host : "?");
                return card;
            }
        }
        return null;
    }

    /// <summary>B站官方分享卡 appid（QQ 通用图文卡）</summary>
    private const long BiliArkAppId = 100951776;

    /// <summary>发送B站视频卡片：拉取视频信息 → 自建 news 卡 JSON → 以 json 段发送。
    /// news 卡不需要签名（QQ 仅对 music 卡校验签名通道），因此不依赖任何签名服务与协议端平台白名单。</summary>
    private async Task SendBiliCardAsync(OneBotClient client, long targetId, bool isGroup, string bv)
    {
        // 支持三种输入：BV号 / 完整视频链接 / b23.tv 短链（短链先跟随跳转解析出 BV）
        bv = await NormalizeBiliInputAsync(bv);
        if (!Regex.IsMatch(bv, @"^BV[0-9A-Za-z]{10}$"))
        {
            interactor.Poke($"没识别出B站视频号：{bv}。请传 BV 号（如 BV15wUXYAEci）或视频链接");
            return;
        }
        string title = "", up = "", cover = "";
        try
        {
            string url = $"https://api.bilibili.com/x/web-interface/view?bvid={Uri.EscapeDataString(bv)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // 完整浏览器头：缺 Accept/Cookie 时更易被 B站风控（返回 HTML 导致解析失败）
            req.Headers.TryAddWithoutValidation("Referer", $"https://www.bilibili.com/video/{bv}");
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            req.Headers.TryAddWithoutValidation("Cookie", "buvid3=1; b_nut=1");
            // 取真实 buvid3（假 cookie 更易被风控 → -412）。取不到就退回占位值
            string biliCookie = "buvid3=1; b_nut=1";
            try
            {
                using var homeReq = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/");
                homeReq.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
                using var homeResp = await _http.SendAsync(homeReq);
                if (homeResp.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    var parts = new List<string>();
                    foreach (string c in cookies)
                    {
                        string kv = c.Split(';')[0];
                        if (kv.StartsWith("buvid3=") || kv.StartsWith("b_nut=") || kv.StartsWith("buvid4="))
                            parts.Add(kv);
                    }
                    if (parts.Count > 0)
                    {
                        biliCookie = string.Join("; ", parts);
                        logger.LogDebug("B站 cookie 获取成功");
                    }
                }
            }
            catch { /* 取不到就用占位值 */ }
            req.Headers.Remove("Cookie");
            req.Headers.TryAddWithoutValidation("Cookie", biliCookie);

            HttpResponseMessage resp = await _http.SendAsync(req);
            string body;
            using (var ms = new MemoryStream())
            {
                await resp.Content.CopyToAsync(ms);
                body = Encoding.UTF8.GetString(ms.ToArray());
            }
            // -412 = B站风控，退避后重试一次（换用新 cookie）
            if (body.Contains("\"code\":-412"))
            {
                logger.LogWarning("B站接口触发风控(-412)，1.5秒后重试一次");
                await Task.Delay(1500);
                using var retryReq = new HttpRequestMessage(HttpMethod.Get, url);
                retryReq.Headers.TryAddWithoutValidation("Referer", $"https://www.bilibili.com/video/{bv}");
                retryReq.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
                retryReq.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                retryReq.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                retryReq.Headers.TryAddWithoutValidation("Cookie", biliCookie);
                using var retryResp = await _http.SendAsync(retryReq);
                using var rms = new MemoryStream();
                await retryResp.Content.CopyToAsync(rms);
                body = Encoding.UTF8.GetString(rms.ToArray());
            }

            // 解析单独容错：风控/限流时 B站 会返回 HTML 而非 JSON
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                logger.LogWarning("B站接口返回非JSON（可能被风控），前200字符：{Head}", body.Length > 200 ? body[..200] : body);
                interactor.Poke($"B站接口返回异常（可能被风控或限流），请稍后重试；BV号：{bv}");
                return;
            }
            using (doc)
            {
                // 先判 code：B站失败时仍是 HTTP 200，但带 code/message（如 -400 请求错误 / -404 不存在 / -352 风控）
                long code = doc.RootElement.TryGetProperty("code", out var codeEl) ? ReadLong(codeEl) : 0;
                if (code != 0)
                {
                    string msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    string friendly = code switch
                    {
                        -400 => "请求错误（BV号格式不对或参数有误）",
                        -404 => "视频不存在（可能已被删除或BV号有误）",
                        -352 => "触发风控（请稍后重试或降低频率）",
                        _ => msg.Length > 0 ? msg : "接口返回错误"
                    };
                    interactor.Poke($"未找到该B站视频：{bv}（{friendly}，code={code}）");
                    return;
                }
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    interactor.Poke($"B站视频信息不完整（{bv}），请确认BV号是否正确或稍后重试");
                    return;
                }
                title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (data.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
                    up = owner.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                cover = data.TryGetProperty("pic", out var p) ? p.GetString() ?? "" : "";
                if (cover.StartsWith("//")) cover = "https:" + cover;
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "B站视频信息获取失败：{Bv}", bv);
            interactor.Poke($"B站视频信息获取失败：{e.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(title)) title = bv;
        if (string.IsNullOrWhiteSpace(cover))
            cover = "https://p1.music.126.net/6y-UleORITEDbvrOLV0Q8A==/5639395138885805.jpg";

        string jump = $"https://www.bilibili.com/video/{bv}";

        // 签名优先：QQ 只渲染已签名的卡（签名不可用时回退下面的自建 news 卡）
        string? signedCard = await TrySignBiliCardAsync(bv, title, up, cover, jump);
        if (signedCard == null)
        {
            logger.LogInformation("B站卡片：签名通路不可用，回退自建 news 卡（无签名，可能不被渲染）");
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long uin = client.BotId;
        var card = new
        {
            app = "com.tencent.structmsg",
            view = "news",
            ver = "0.0.0.1",
            prompt = $"[分享]{title}",
            config = new { autosize = 1, ctime = now, forward = 1, type = "normal" },
            meta = new
            {
                news = new
                {
                    app_type = 1,
                    appid = BiliArkAppId,
                    ctime = now,
                    desc = up,
                    jumpUrl = jump,
                    preview = cover,
                    tag = "哔哩哔哩",
                    tagIcon = "https://www.bilibili.com/favicon.ico",
                    title,
                    uin
                }
            }
        };
        string cardJson = signedCard ?? JsonSerializer.Serialize(card, CardJsonOptions);
        object message = new object[] { new { type = "json", data = new { data = cardJson } } };

        try
        {
            object sendParams = isGroup ? new { group_id = targetId, message } : new { user_id = targetId, message };
            SendResult? sent = await client.CallActionAsync<SendResult>("send_msg", sendParams);
            long sentId = ExtractSentId(sent);
            if (sentId != 0)
                RecordSentMessage(sentId, isGroup ? targetId : 0, isGroup ? 0 : targetId, $"[B站卡片 {bv}]");
            // 成功静默：不触发AI新一轮确认回复
        }
        catch (TaskCanceledException)
        {
            interactor.Poke("B站卡片请求超时（10秒未收到协议端响应）。卡片可能已发出，请先用 QGetMessages 确认，不要重复发送");
        }
        catch (Exception e)
        {
            interactor.Poke($"B站卡片发送失败：{e.Message}");
        }
    }

    /// <summary>网易云歌曲详情（标题/歌手/封面），失败时回退到关键词与默认封面</summary>
    private static async Task<(string title, string artist, string cover)> GetNcmSongDetailAsync(long ncmId)
    {
        try
        {
            string url = $"https://music.163.com/api/song/detail/?id={ncmId}&ids=%5B{ncmId}%5D";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            using var resp = await _http.SendAsync(req);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement song = doc.RootElement.GetProperty("songs")[0];
            string title = song.GetProperty("name").GetString() ?? ncmId.ToString();
            string artist = song.TryGetProperty("artists", out JsonElement arts) && arts.GetArrayLength() > 0
                ? string.Join("/", arts.EnumerateArray().Select(a => a.GetProperty("name").GetString()))
                : "未知歌手";
            string cover = song.TryGetProperty("album", out JsonElement album) &&
                           album.TryGetProperty("picUrl", out JsonElement pic)
                ? pic.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(cover))
                cover = "https://p1.music.126.net/6y-UleORITEDbvrOLV0Q8A==/5639395138885805.jpg"; // 网易云默认封面
            return (title, artist, cover);
        }
        catch
        {
            return (ncmId.ToString(), "未知歌手", "https://p1.music.126.net/6y-UleORITEDbvrOLV0Q8A==/5639395138885805.jpg");
        }
    }

    private static async Task<long> SearchNetEaseIdAsync(string keyword, ILogger? logger = null)
    {
        // 1. 163api（NCM-Downloader 同款搜索源，最优先：实测稳定、ID有效）
        try
        {
            string url = "https://163api.qijieya.cn/search?keywords=" + Uri.EscapeDataString(keyword);
            using var resp = await _http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("songs", out var songs) &&
                    songs.GetArrayLength() > 0)
                {
                    long id = songs[0].GetProperty("id").GetInt64();
                    logger?.LogDebug("音乐搜索命中 [163api]：{Keyword} → {Id}", keyword, id);
                    return id;
                }
            }
            logger?.LogDebug("音乐搜索 [163api] 无结果：{Keyword}", keyword);
        }
        catch (Exception ex) { logger?.LogDebug("音乐搜索 [163api] 异常：{Msg}", ex.Message); }

        // 2. 网易云官方 web 搜索（裸调不稳定，常返回空/-462，作为兜底）
        try
        {
            string url = "https://music.163.com/api/search/get/web?s=" + Uri.EscapeDataString(keyword) + "&type=1&limit=1&offset=0";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("songs", out var songs) &&
                    songs.GetArrayLength() > 0)
                {
                    long id = songs[0].GetProperty("id").GetInt64();
                    logger?.LogDebug("音乐搜索命中 [官方web搜索]：{Keyword} → {Id}", keyword, id);
                    return id;
                }
            }
            logger?.LogDebug("音乐搜索 [官方web搜索] 无结果：{Keyword}", keyword);
        }
        catch (Exception ex) { logger?.LogDebug("音乐搜索 [官方web搜索] 异常：{Msg}", ex.Message); }

        // 3. meting 搜索兜底（从 url 字段解析 id）
        try
        {
            string url = "https://api.qijieya.cn/meting/?type=search&id=" + Uri.EscapeDataString(keyword) + "&limit=1";
            using var resp = await _http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    string urlField = doc.RootElement[0].TryGetProperty("url", out var uf) ? uf.GetString() ?? "" : "";
                    var m = Regex.Match(urlField, @"id=(\d+)");
                    if (m.Success && long.TryParse(m.Groups[1].Value, out long id))
                    {
                        logger?.LogDebug("音乐搜索命中 [meting]：{Keyword} → {Id}", keyword, id);
                        return id;
                    }
                }
            }
        }
        catch (Exception ex) { logger?.LogDebug("音乐搜索 [meting] 异常：{Msg}", ex.Message); }

        // 三级全部失败才报错
        logger?.LogWarning("音乐搜索三源全部无结果：{Keyword}", keyword);
        return 0;
    }


    /// <summary>非网易云平台歌曲信息（供 ARK / 签名服务使用）：尽量取到歌名/歌手/封面/跳转/直链。
    /// 取不到时用音乐ID兜底，保证 ARK/签名服务不因字段为空而被拒</summary>
    private static async Task<(string title, string artist, string cover, string jump, string playUrl)> ResolveOtherPlatformInfoAsync(string platform, string id)
    {
        string title = id, artist = platform, cover = "", jump = "", play = "";
        try
        {
            if (platform == "qq")
            {
                jump = $"https://y.qq.com/n/ryqq/songDetail/{id}";
                string mid = id;
                if (long.TryParse(id, out _))   // 纯数字ID不认，先搜索拿字符串 mid
                {
                    string su = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w=" + Uri.EscapeDataString(title) + "&format=json&n=1&p=1";
                    using var sreq = new HttpRequestMessage(HttpMethod.Get, su);
                    sreq.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
                    sreq.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
                    using var sresp = await _http.SendAsync(sreq);
                    using var sdoc = JsonDocument.Parse(await sresp.Content.ReadAsStringAsync());
                    mid = sdoc.RootElement.GetProperty("data").GetProperty("song").GetProperty("list")[0]
                        .GetProperty("songmid").GetString() ?? mid;
                    jump = $"https://y.qq.com/n/ryqq/songDetail/{mid}";
                }
                string u = "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg?songmid=" + mid
                    + "&platform=yqq&format=json&inCharset=utf8&outCharset=utf-8";
                using var req = new HttpRequestMessage(HttpMethod.Get, u);
                req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
                req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
                using var resp = await _http.SendAsync(req);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var dd = doc.RootElement.GetProperty("data")[0];
                title = dd.GetProperty("title").GetString() ?? title;
                artist = string.Join("/", dd.GetProperty("singer").EnumerateArray()
                    .Select(a => a.GetProperty("name").GetString()));
                string albid = dd.GetProperty("album").GetProperty("mid").GetString() ?? "";
                if (albid.Length > 0)
                    cover = "https://y.qq.com/music/photo_new/T002R300x300M000" + albid + ".jpg";
                // 播放直链：vkey 接口（此前未取 → ARK/签名报「缺少必要参数 url」）
                play = await ResolveQQPlayUrlAsync(mid);
            }
            else if (platform == "kugou")
            {
                jump = $"https://www.kugou.com/song/#hash={id}";
                // 先取 album_id/mid（部分接口需要），再取播放地址
                string u = "https://wwwapi.kugou.com/yy/index.php?r=play/getdata&hash=" + Uri.EscapeDataString(id);
                using var req = new HttpRequestMessage(HttpMethod.Get, u);
                req.Headers.TryAddWithoutValidation("Referer", "https://www.kugou.com/");
                req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
                req.Headers.TryAddWithoutValidation("Cookie", "kg_mid=1; kg_dfid=1");
                using var resp = await _http.SendAsync(req);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("data", out var dd) && dd.ValueKind == JsonValueKind.Object)
                {
                    title = dd.TryGetProperty("audio_name", out var an) ? an.GetString() ?? title : title;
                    artist = dd.TryGetProperty("author_name", out var au) ? au.GetString() ?? artist : artist;
                    cover = dd.TryGetProperty("img", out var im) ? im.GetString() ?? "" : "";
                    play = dd.TryGetProperty("play_url", out var pu) ? pu.GetString() ?? "" : "";
                }
            }
            else if (platform == "migu")
            {
                jump = $"https://music.migu.cn/v3/music/song/{id}";
            }
            else
            {
                jump = id;
            }
        }
        catch (Exception ex)
        {
            loggerStaticForInfo?.LogDebug(ex, "获取 {Platform} 歌曲信息失败，使用ID兜底", platform);
        }
        if (string.IsNullOrWhiteSpace(title)) title = id;
        if (string.IsNullOrWhiteSpace(artist)) artist = platform;
        return (title, artist, cover, jump, play);
    }

    /// <summary>QQ音乐播放直链（vkey 接口）。返回可播 URL 或空串。
    /// 实测：返回 sip[0] + purl（形如 http://aqqmusic.tc.qq.com/xxx.m4a?vkey=...），内容为 audio/mp4 真实音频</summary>
    private static async Task<string> ResolveQQPlayUrlAsync(string songMid)
    {
        try
        {
            string payload = "{\"req_0\":{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\",\"param\":{"
                + "\"guid\":\"10000\",\"songmid\":[\"" + songMid + "\"],\"songtype\":[0],"
                + "\"uin\":\"0\",\"loginflag\":1,\"platform\":\"20\"}},"
                + "\"comm\":{\"uin\":0,\"format\":\"json\",\"ct\":24,\"cv\":0}}";
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("req_0", out var req0) ||
                !req0.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("sip", out var sip) || sip.GetArrayLength() == 0 ||
                !data.TryGetProperty("midurlinfo", out var midInfo) || midInfo.GetArrayLength() == 0)
                return "";
            string sip0 = sip[0].GetString() ?? "";
            string purl = midInfo[0].TryGetProperty("purl", out var p) ? p.GetString() ?? "" : "";
            if (sip0.Length == 0 || purl.Length == 0) return "";
            string url = sip0 + purl;
            return await IsPlayableAudioAsync(url) ? url : "";
        }
        catch (Exception ex)
        {
            loggerStaticForInfo?.LogDebug(ex, "QQ音乐直链解析失败");
            return "";
        }
    }

    /// <summary>浏览器 UA（B站/腾讯等接口需要，避免被风控）</summary>
    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>校验直链是否真实可播：跟随重定向后必须是 audio/* 且大小可观（避免拿到 404 HTML 页）</summary>
    private static async Task<bool> IsPlayableAudioAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return false;
            string? mediaType = resp.Content.Headers.ContentType?.MediaType;
            long? len = resp.Content.Headers.ContentLength;
            bool audio = mediaType != null && mediaType.StartsWith("audio", StringComparison.OrdinalIgnoreCase);
            return audio && (len == null || len > 100_000);   // 大于 100KB 才认为是有内容的音频
        }
        catch { return false; }
    }

    /// <summary>网易云歌曲ID → 官方 outer 播放外链（302 到实际音频；协议端本地下载，不依赖第三方解析服务）</summary>
    private static string NeteaseOuterUrl(long id) => $"https://music.163.com/song/media/outer/url?id={id}.mp3";

    /// <summary>旧：网易云歌曲ID → 第三方解析直链（已被 outer 外链取代，保留供参考/回退，当前无调用点）</summary>
    [Obsolete("改用 NeteaseOuterUrl（outer 外链更稳）")]
    private static async Task<string?> ResolveNcmUrlAsync(long id)
    {
        try
        {
            string url = $"https://api.qijieya.cn/meting/?server=netease&type=url&id={id}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (resp.IsSuccessStatusCode)
            {
                string? mediaType = resp.Content.Headers.ContentType?.MediaType;
                if (mediaType is "audio/mpeg" or "audio/mp3" or "audio/x-mpeg")
                {
                    string? finalUrl = resp.RequestMessage?.RequestUri?.ToString();
                    if (!string.IsNullOrEmpty(finalUrl) && finalUrl.Contains("music.126.net"))
                        return finalUrl;
                }
            }
        }
        catch { }

        try
        {
            string url = $"https://api.vkeys.cn/v2/music/netease?id={id}&quality=4";
            using var resp = await _http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                {
                    string? s = u.GetString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
        }
        catch { }
        return null;
    }

    // ==================== 消息查询 ====================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("纯查询：获取群聊/私聊最近消息及每条的 [消息ID:xxx]（真实ID，可为负数）。仅用于看上下文或取ID——撤回/贴表情/引用回复请直接用对应函数一步到位。群聊传 groupId；私聊传 userId。缓存不足自动回拉补齐（历史ID同样可用）。15秒内同一会话查询超过2次会被防抖拒绝")]
    public async Task QGetMessages(
        [Description("群号（私聊时传0）")] long groupId = 0,
        [Description("QQ号（仅私聊时需要）")] long userId = 0,
        [Description("获取条数，1-50，默认10")] int count = 10)
    {
        OneBotClient? client = GetClient();
        if (client == null) { interactor.Poke("获取消息失败：QQ客户端不可用"); return; }
        if (groupId == 0 && userId == 0) { interactor.Poke("群聊请传 groupId，私聊请传 userId"); return; }
        // 容错：AI 把对方QQ误传成群号时自动按私聊处理
        if (userId == 0 && groupId != 0 && !await DetectIsGroupAsync(groupId, ""))
        {
            userId = groupId;
            groupId = 0;
        }

        // 防抖保护：同一会话15秒内查询超过2次进入冷却，防止AI递归查询
        string scopeKey = groupId != 0 ? $"g{groupId}" : $"u{userId}";
        lock (_qgetLock)
        {
            DateTime now = DateTime.Now;
            var times = _qgetTimes.GetOrAdd(scopeKey, _ => new List<DateTime>());
            times.RemoveAll(t => (now - t).TotalSeconds > 15);
            if (times.Count >= 2)
            {
                interactor.Poke("查询过于频繁：该会话15秒内已查询2次，请稍后再试。列表中的[消息ID:xxx]短期内不会变化，直接用上次结果里的ID操作即可，无需重复查询");
                return;
            }
            times.Add(now);
        }
        try
        {
            count = Math.Clamp(count, 1, 50);

            // 不过滤已撤回：与撤回候选列表口径一致——已撤回条目保留内容存档并标注【已撤回】，
            // 供 AI 回溯"刚撤的是哪条"；误操作由各功能自身的 IsRecalled 守卫拦下（会明确提示不能操作）
            List<LiveMessage> Query() => _liveMessages
                .Where(m => groupId != 0 ? m.GroupId == groupId : (m.GroupId == 0 && m.PeerId == userId))
                .OrderByDescending(m => m.Time)
                .ThenByDescending(m => m.Seq)
                .Take(count)
                .OrderBy(m => m.Time)
                .ThenBy(m => m.Seq)
                .ToList();

            // 查询前无条件刷新历史，保证拿到的是此刻最新（不依赖任何事件上报）
            await BackfillHistoryAsync(groupId, userId, count);
            var matches = Query();

            if (matches.Count == 0)
            {
                interactor.Poke(groupId != 0
                    ? $"群 {groupId} 暂无消息记录（已尝试历史回拉也为空；请检查OneBot连接与群号是否正确）"
                    : $"与 {userId} 暂无消息记录");
                return;
            }

            var sb = new StringBuilder();
            string target = groupId != 0 ? $"群 {groupId}" : $"与 {userId} 的私聊";
            sb.AppendLine($"{target} 最近 {matches.Count} 条消息（[消息ID:xxx]即真实ID，可为负数，直接用于操作；标注【已撤回】的已不存在于QQ，仅作内容存档，不能再撤回/贴表情/引用）：");
            foreach (var m in matches)
            {
                string nick = m.IsSelf ? SelfName : (string.IsNullOrEmpty(m.Nickname) ? m.UserId.ToString() : m.Nickname);
                DateTime time = DateTimeOffset.FromUnixTimeSeconds(m.Time).LocalDateTime;
                sb.AppendLine($"[{time:HH:mm:ss}] {m.UserId}({nick}) [消息ID:{m.MessageId}]{(m.IsRecalled ? "【已撤回】" : "")} {m.Raw}");
            }
            interactor.Poke(sb.ToString());
        }
        catch (Exception e)
        {
            interactor.Poke($"获取消息失败：{e.Message}");
        }
    }

    // ==================== notice 感知（官方事件链路：禁言/进群/戳一戳） ====================

    private sealed class GroupInfoData
    {
        [JsonPropertyName("group_id")]
        public long GroupId { get; init; }

        [JsonPropertyName("group_name")]
        public string? GroupName { get; init; }
    }

    private async void OnEventReceived(OneBotBaseEvent oneBotEvent)
    {
        try
        {
            if (oneBotEvent is not OneBotNoticeEvent noticeEvent)
                return;

            string? noticeType = noticeEvent.NoticeType;
            if (noticeType == "profile_like" && Configuration.PerceiveProfileLike)
            {
                if (DateTime.Now - _lastLikePromptTime < ProfileLikeCooldown) return;
                _lastLikePromptTime = DateTime.Now;
                long uid = noticeEvent.UserId;
                interactor.Poke($"[System 用户{uid} 赞了你的资料卡。可以回赞（SendQQLikes qq={uid}）或戳一戳回应，也可以忽略]");
            }
            else if (noticeType == "group_msg_emoji_like" && Configuration.PerceiveEmojiLike)
            {
                long uid = noticeEvent.UserId;
                // 自己贴的表情不提示自己（与 poke 分支"自己发起的一律无视"对齐）
                if (uid == noticeEvent.SelfId) return;
                // 部分协议端（如 SnowLuma）额外用 sub_type 区分添加/移除表情回应，NapCat 不发该字段（恒视为添加）。
                // 取消回应不是新互动，不注入提示、也不占用冷却
                if (string.Equals(noticeEvent.SubType, "remove", StringComparison.OrdinalIgnoreCase)) return;

                // 被贴消息的归属只查一轮：缓存命中即用；未命中用 get_msg 回查一次；再失败就记一条日志放弃（不重试）
                long messageId = 0;
                long likeId = 0;          // 第一个表情的编号（提示 AI 可贴回同一个；取不到时用默认201）
                string likeText = "";
                string? rawJson = noticeEvent.RawJson;
                if (!string.IsNullOrEmpty(rawJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
                        if (doc.RootElement.TryGetProperty("message_id", out var msgIdEl))
                            messageId = ReadLong(msgIdEl);
                        likeText = RenderLikes(doc.RootElement);
                        if (doc.RootElement.TryGetProperty("likes", out var likesEl2) && likesEl2.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (JsonElement lk in likesEl2.EnumerateArray())
                            {
                                long first = ReadPropLong(lk, "emoji_id");
                                if (first != 0) { likeId = first; break; }
                            }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "贴表情通知 RawJson 解析失败");
                    }
                }

                long targetUid = 0;
                string targetName = "";
                if (messageId != 0)
                {
                    if (_liveById.TryGetValue(messageId, out LiveMessage? lm))
                    {
                        targetUid = lm.UserId;
                        targetName = lm.Nickname;
                    }
                    else
                    {
                        OneBotClient? client0 = GetClient();
                        if (client0 != null)
                        {
                            try
                            {
                                var je = await client0.CallActionAsync<System.Text.Json.JsonElement?>("get_msg", new { message_id = messageId });
                                if (je.HasValue && je.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                    je.Value.TryGetProperty("sender", out var sender) && sender.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    targetUid = ReadPropLong(sender, "user_id");
                                    targetName = ReadPropString(sender, "nickname");
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "贴表情通知 get_msg 回查失败 message_id={MessageId}", messageId);
                            }
                        }
                    }
                }

                // 分类：Own=被贴的是自己的消息 / Other=能确证是他人的消息 / 无法判定=查不到作者，静默记日志
                if (targetUid == 0)
                {
                    logger.LogDebug("贴表情通知无法判定被贴消息归属，已忽略：message_id={MessageId} user_id={User}", messageId, uid);
                    return;
                }
                bool isOwnMessage = targetUid == noticeEvent.SelfId;
                if (!isOwnMessage && !Configuration.PerceiveOthersEmojiLike) return;

                // 冷却按类型分开计：给「我的消息」贴 / 给「别人的消息」贴 各自独立时长
                DateTime lastPrompt = isOwnMessage ? _lastEmojiLikePromptTime : _lastOthersEmojiLikePromptTime;
                TimeSpan cooldown = isOwnMessage ? EmojiLikeCooldown : OthersEmojiLikeCooldown;
                if (DateTime.Now - lastPrompt < cooldown) return;

                // 通过全部过滤后才占用冷却，避免被抑制的事件把冷却槽吃掉
                if (isOwnMessage) _lastEmojiLikePromptTime = DateTime.Now;
                else _lastOthersEmojiLikePromptTime = DateTime.Now;

                string operatorName = uid == 0 ? "" : await GetQQUserName(uid, noticeEvent.GroupId);
                string opText = uid == 0 ? "某位用户" : (string.IsNullOrEmpty(operatorName) ? $"用户{uid}" : $"用户{uid}({operatorName})");
                string targetText = isOwnMessage
                    ? $"我的消息(我,{noticeEvent.SelfId})"
                    : $"用户{targetUid}({targetName})的消息";
                string likePart = likeText.Length > 0 ? $"贴了表情：{likeText}" : "贴了表情";
                // likeId=0 时不能传 emojiId（SetEmojiRecent 里 emojiId=0 是"查对照表"的语义）；
                // 措辞不指定"贴回同一个"：只给出可用参数并提示可以换个 emojiId，具体怎么回应交给 AI
                string backArgs = likeId != 0
                    ? $"SetEmojiRecent target={uid} targetId={noticeEvent.GroupId} emojiId={likeId}，或换个别的 emojiId"
                    : $"SetEmojiRecent target={uid} targetId={noticeEvent.GroupId}，或换个别的 emojiId";
                interactor.Poke($"[System {opText} 在群 {noticeEvent.GroupId} 给{targetText}{likePart}。想回应的话可以贴回表情（{backArgs}）、说句话，也可以忽略]");
            }
            else if (noticeType == "group_ban" && Configuration.PerceiveGroupBan)
            {
                if (noticeEvent.SelfId == noticeEvent.UserId)
                {
                    string subType = noticeEvent.SubType ?? "";
                    string groupInfo = await GetGroupInfoText(noticeEvent.GroupId);
                    if (subType == "ban")
                        interactor.Poke($"[System 你被禁言了（{groupInfo}）]");
                    else if (subType == "lift_ban")
                        interactor.Poke($"[System 你被解除禁言了（{groupInfo}）]");
                }
            }
            else if (noticeType == "group_increase" && Configuration.PerceiveGroupIncrease)
            {
                long userId = noticeEvent.UserId;
                string userName = await GetQQUserName(userId, noticeEvent.GroupId);
                string userText = string.IsNullOrEmpty(userName)
                    ? $"用户{userId}"
                    : $"用户{userId}({userName})";
                string groupInfo = await GetGroupInfoText(noticeEvent.GroupId);
                interactor.Poke($"[System {userText}加入了群聊（{groupInfo}）]");
            }
            else if (noticeType == "notify" && noticeEvent.SubType == "poke" && Configuration.PokeDecideEnabled)
            {
                long targetId = 0;
                if (oneBotEvent is OneBotPokeEvent pokeEvent)
                    targetId = pokeEvent.TargetId;

                // NapCat 私聊戳的 user_id 恒为“会话对端”（源码 api/msg.ts 传 msg.peerUid），与谁戳谁无关；
                // 方向信息只在扩展字段 sender_id 里（框架事件模型未映射，从 RawJson 读）
                long senderId = TryReadSenderId(noticeEvent.RawJson);

                logger.LogDebug("poke通知原文：user_id={User} sender_id={Sender} target_id={Target} self_id={Self} group_id={Group}",
                    noticeEvent.UserId, senderId, targetId, noticeEvent.SelfId, noticeEvent.GroupId);

                // 第一层：我方发起的戳（主动戳 / 回戳 / 自戳）——含其回执，一律不进“被戳”流程，防无限回圈。
                // 优先用 sender_id（唯一可靠的方向字段）；协议端未提供时回落旧规则（user_id==self，覆盖群聊）
                bool fromSelf = senderId != 0
                    ? senderId == noticeEvent.SelfId
                    : noticeEvent.UserId == noticeEvent.SelfId;
                if (fromSelf)
                {
                    // 官方 QChat 会把这条回执渲染成一条会话消息（私聊还不带发起者，显示成“对端 戳了戳 对端”），
                    // 这里登记待剔除条目，由 ChatSend 钩子在消息进入上下文前精确剔除——
                    // 否则 AI 会读到一条方向错误的消息并被错误触发
                    MarkPokeEchoForStrip(noticeEvent.UserId, targetId);
                    // 私聊再多做一步：把回执行从官方缓冲里摘掉，连那一轮对话也不触发。
                    // 群聊不处理——群聊渲染带发起者（“[自己QQ]:戳了戳 X”），语义本来就对
                    if (noticeEvent.GroupId == 0)
                        SchedulePokeEchoTurnDrop(noticeEvent.UserId, targetId);
                    else if (Configuration.PokeEchoDropTurnEnabled)
                        MarkOutgoingPoke(targetId); // 群聊不摘缓冲，仅保留回声抑制记录
                    logger.LogDebug("我方发起的poke（含自戳/回戳回执）：sender={Sender} peer={Peer} target={Target}，已登记剔除，不触发被戳流程",
                        senderId, noticeEvent.UserId, targetId);
                    return;
                }

                // 只处理自己被戳。
                // 修复误判：target_id==0 时无法确认被戳的是谁——群聊场景其他人互戳也会上报此事件，
                // 放行会把戳别人的误判为戳bot。协议端（LLBot/NapCat）正常都带 target_id，
                // 为0视为异常报文，忽略并留日志排查，不再放行
                if (targetId == 0)
                {
                    logger.LogWarning("poke通知缺少 target_id（user_id={User}），无法确认被戳对象，已忽略以避免误判。若此日志频繁出现，说明协议端上报不完整", noticeEvent.UserId);
                    return;
                }
                if (targetId != noticeEvent.SelfId) return;

                // 第二层：回执回声抑制——刚主动戳过此人，短时间内同一人的戳通知视为我方动作的回执而非对方新戳
                int echoSec = Configuration.PokeEchoSuppressSeconds;
                if (echoSec > 0)
                {
                    lock (_pokeLock)
                    {
                        _recentOutgoingPokes.RemoveAll(p => (DateTime.Now - p.Time).TotalMinutes > 2);
                        // 双键匹配：群聊回执 user_id=自己、私聊回执 user_id=对端，但两者的 target 都是我戳的人；
                        // 协议端不提供 sender_id 时，这层就是唯一的方向兜底
                        if (_recentOutgoingPokes.Any(p => (p.UserId == noticeEvent.UserId || p.UserId == targetId) &&
                                                         (DateTime.Now - p.Time).TotalSeconds <= echoSec))
                        {
                            logger.LogDebug("忽略疑似回戳回执回声：user_id={User} target={Target}（{Sec}秒内我方刚戳过）",
                                noticeEvent.UserId, targetId, echoSec);
                            return;
                        }
                    }
                }

                // 第三层：防刷限次（滑动窗口，超限静默——AI无感知）——防双AI互戳等双方逻辑都正确的回圈
                if (PokeFloodExceeded(noticeEvent.UserId, noticeEvent.GroupId)) return;

                bool isGroup = noticeEvent.GroupId != 0;
                // 私聊的会话对象就是对端（user_id），群聊是群号——按会话存档，避免多会话互相覆盖导致回戳错人
                long scopeId = isGroup ? noticeEvent.GroupId : noticeEvent.UserId;
                lock (_pokeLock)
                    _pendingPokes[PokeSessionKey(isGroup, scopeId)] =
                        new PokeRequest(noticeEvent.UserId, noticeEvent.GroupId, isGroup, DateTime.Now);

                // 冷却期内不重复注入，避免连续戳一戳刷屏上下文
                if (DateTime.Now - _lastPokePromptTime < PokeCooldown) return;
                _lastPokePromptTime = DateTime.Now;

                string userName = await GetQQUserName(noticeEvent.UserId, noticeEvent.GroupId);
                string userText = string.IsNullOrEmpty(userName)
                    ? $"用户{noticeEvent.UserId}"
                    : $"用户{noticeEvent.UserId}({userName})";
                string where = isGroup ? $"在群 {noticeEvent.GroupId} 戳了戳你" : "私聊戳了戳你";
                interactor.Poke($"[System {userText} {where}。你可以输出 <PokeBack decide=\"yes\"/> 回戳（多个会话同时被戳时加 target={noticeEvent.UserId} 指定），或 <PokeBack decide=\"no\"/> 忽略]");
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "感知notice事件失败");
        }
    }

    private async Task<string> GetGroupInfoText(long groupId)
    {
        if (groupId == 0) return "群号:未知";
        string name = await GetGroupNameAsync(groupId);
        return string.IsNullOrEmpty(name) ? $"群号:{groupId}" : $"群号:{groupId} 群名:{name}";
    }

    private async Task<string> GetGroupNameAsync(long groupId)
    {
        if (qChatService.GroupStates.TryGetValue(groupId, out var state) && !string.IsNullOrEmpty(state.Name))
            return state.Name!;

        OneBotClient? client = GetClient();
        if (client == null) return "";
        try
        {
            var info = await client.CallActionAsync<GroupInfoData>("get_group_info", new { group_id = groupId, no_cache = false });
            return info?.GroupName ?? "";
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "获取群名失败: {GroupId}", groupId);
            return "";
        }
    }

    private async Task<string> GetQQUserName(long userId, long groupId = 0)
    {
        OneBotClient? client = GetClient();
        if (client == null) return "";
        try
        {
            if (groupId != 0)
            {
                try
                {
                    var sender = await client.CallActionAsync<OneBotSender>(
                        "get_group_member_info",
                        new { group_id = groupId, user_id = userId, no_cache = false });
                    if (sender != null)
                    {
                        if (!string.IsNullOrEmpty(sender.Card)) return sender.Card;
                        if (!string.IsNullOrEmpty(sender.Nickname)) return sender.Nickname;
                    }
                }
                catch (Exception ex)
                {
                    // 协议端可能未实现/不返回群成员信息（如部分轻量协议端），继续尝试陌生人信息
                    logger.LogDebug(ex, "获取群成员信息失败，尝试陌生人信息: {UserId}@{GroupId}", userId, groupId);
                }
            }

            var stranger = await client.CallActionAsync<OneBotSender>(
                "get_stranger_info",
                new { user_id = userId });
            return stranger?.Nickname ?? "";
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "获取QQ用户名失败: {UserId}", userId);
            return "";
        }
    }

    /// <summary>一整行是否只由来源标签 [..] / QChat 附加提示 (..) 组成（即没有实际内容）</summary>
    private static bool IsNonContentLine(string line)
    {
        string left = Regex.Replace(line, @"\[[^\]]*\]", "").Trim();
        if (left.Length == 0) return true;
        // 整行就是一个括号包裹的附加提示，例如“(回复请保持1-20字)”
        if (left.Length >= 2 && left[0] == '(' && left[left.Length - 1] == ')') return true;
        return false;
    }

    /// <summary>消息里是否存在“可回应的实际内容”。
    /// 注意群聊行是“[群聊消息(群号,群名)] [QQ(昵称)]: 内容”这种**同一行**格式，
    /// 所以必须剥掉标签后看剩下什么，不能只看行首字符</summary>
    private static bool HasRealContent(string message)
    {
        foreach (string raw in message.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (!IsNonContentLine(line)) return true;
        }
        return false;
    }

    // ==================== 互动提示（官方消息过滤同款 ChatSend 钩子） ====================

    /// <summary>收到QQ消息时按概率在消息末尾附加互动提示（动态填入发言人参数，AI照抄即可调用，无需查ID）</summary>
    private string OnChatSendHint(string message)
    {
        if (!Configuration.InteractionHintEnabled) return message;
        // 没有可回应内容（例如只剩来源标签的戳回执）时不附加互动提示——否则等于催 AI 再去戳一次
        if (!HasRealContent(message)) return message;
        if (string.IsNullOrWhiteSpace(Configuration.InteractionHintText)) return message;
        // 只附加在 QQ 来源的消息上（群聊/私聊标签），不影响其他模块的消息
        bool isGroupMsg = message.Contains("[群聊消息(");
        if (!isGroupMsg && !message.Contains("[私聊消息(")) return message;
        int prob = Math.Clamp(Configuration.InteractionHintProbability, 0, 100);
        if (prob < 100 && Random.Shared.Next(100) >= prob) return message;

        // 解析会话：[群聊消息(群号,群名)] / [私聊消息(QQ,昵称)]
        Match scopeMatch = Regex.Match(message, isGroupMsg ? @"\[群聊消息\((?<id>\d+)" : @"\[私聊消息\((?<id>\d+)");
        string scope = scopeMatch.Success ? scopeMatch.Groups["id"].Value : "";
        // 解析最后一位发言人：[QQ(昵称)]:（批量消息取最后一条的发言人，AI 最可能要回应的就是TA）
        Match? speakerMatch = Regex.Matches(message, @"\[(?<uin>\d+)\((?<nick>[^)]*)\)\]:")
            .Cast<Match>().LastOrDefault();
        string uin = speakerMatch?.Groups["uin"].Value ?? scope;
        string nick = speakerMatch?.Groups["nick"].Value ?? "对方";

        string hint = Configuration.InteractionHintText
            .Replace("{scope}", scope)
            .Replace("{type}", isGroupMsg ? "group" : "private")
            .Replace("{uin}", uin)
            .Replace("{nick}", nick)
            // {pokeargs} 必须先于 {poke} 替换（虽然 {poke} 带右花括号不会误匹配，但顺序固定更稳）
            .Replace("{pokeargs}", isGroupMsg ? $"groupId={scope} userId={uin}" : $"userId={uin}")
            .Replace("{poke}", isGroupMsg ? "PokeGroupMember" : "PokePrivateMember");

        // 被引用/被@时追加回引建议（不含消息内容，省token）
        if (Configuration.QuoteBackHintEnabled)
        {
            long botId = GetClient()?.BotId ?? 0;
            if (botId != 0 && !string.IsNullOrEmpty(uin) && uin != botId.ToString() &&
                (message.Contains($"的回复]@{botId}") || message.Contains($"@{botId}") ||
                 message.Contains($"对\"{botId}：")))
            {
                bool quoted = message.Contains($"的回复]@{botId}") || message.Contains($"对\"{botId}：");
                string reason = quoted ? "引用了你的消息" : "@了你";
                hint += $"（{nick}{reason}，回应时可用上面的 ReplyRecent 参数引用TA这条）";
            }
        }
        return message + "\n" + hint;
    }

    // ==================== Typing Indicator ====================

    private void OnChatSent(string message)
    {
        var match = Regex.Match(message, @"\[私聊消息\((\d+)");
        if (!match.Success) return;

        long userId = long.Parse(match.Groups[1].Value);
        StartTyping(userId);
    }

    private void OnChatOver()
    {
        StopAllTyping();
    }

    private void StartTyping(long userId)
    {
        lock (_typingLock)
        {
            if (_typingCts.TryGetValue(userId, out var existing))
            {
                existing.Cancel();
                _typingCts.Remove(userId);
            }

            var cts = new CancellationTokenSource();
            _typingCts[userId] = cts;
            _ = RunTypingLoopAsync(userId, cts);
        }
    }

    private void StopAllTyping()
    {
        lock (_typingLock)
        {
            foreach (var cts in _typingCts.Values)
                cts.Cancel();
            _typingCts.Clear();
        }
    }

    private async Task RunTypingLoopAsync(long userId, CancellationTokenSource cts)
    {
        OneBotClient? client = GetClient();
        if (client == null) return;
        CancellationToken ct = cts.Token;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Configuration.TypingDelaySeconds), ct);

            var startTime = DateTime.Now;
            while (!ct.IsCancellationRequested)
            {
                await client.CallActionAsync<object>("set_input_status", new { user_id = userId, event_type = 1 });

                if ((DateTime.Now - startTime).TotalSeconds >= Configuration.TypingMaxSeconds)
                    break;

                await Task.Delay(TimeSpan.FromSeconds(Configuration.TypingIntervalSeconds), ct);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception e)
        {
            logger.LogDebug(e, "Typing indicator 发送失败");
        }
        finally
        {
            lock (_typingLock)
            {
                if (_typingCts.TryGetValue(userId, out CancellationTokenSource? cur) && cur == cts)
                    _typingCts.Remove(userId);
            }
        }
    }
}
