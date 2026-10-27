using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using QQSuite.Core;
using Microsoft.Extensions.Logging;

namespace QQSuite.QQTools;

public class QQToolsConfig
{
    [DisplayName("OneBot WebSocket地址")]
    [Description("连接OneBot v11的WebSocket地址")]
    public string Url { get; set; } = "ws://127.0.0.1:3001";

    [DisplayName("认证Token")]
    [Description("可选，如果OneBot配置了Token需要填写")]
    public string Token { get; set; } = "";

    [DisplayName("Cookie刷新间隔(分钟)")]
    [Description("定期刷新QQ空间Cookie的间隔时间")]
    public int CookieRefreshMinutes { get; set; } = 120;

    [DisplayName("静默重连模式")]
    [Description("开启后连接OneBot失败时不输出错误日志，仅后台静默重试（适用于无OneBot环境）")]
    public bool SilentReconnect { get; set; } = false;

    [DisplayName("禁用OneBot连接")]
    [Description("开启后完全不尝试连接OneBot，用于不使用QQ机器人功能的场景")]
    public bool DisableConnection { get; set; } = false;

    [DisplayName("最大重连次数")]
    [Description("连接失败后最大自动重试次数，达到次数后停止自动重连（0=不限制，默认不限制）")]
    public int MaxReconnectAttempts { get; set; } = 0;
}

public class QQToolsModule : ChatBehaviour, IConfigurable<QQToolsConfig>, IConfigurable
{
    private readonly XmlFunctionCaller _functionCaller;
    private readonly ILogger<QQToolsModule> _logger;
    private readonly Interactor<QQToolsModule> _interactor;
    private readonly QChatService _qChatService;

    private long _botId;
    private string _lastSentMessageId = "";
    private string? _cachedCookies;
    private DateTime _cookieFetchTime = DateTime.MinValue;
    private readonly SemaphoreSlim _cookieLock = new(1, 1);

    // 输入指示相关
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _typingCts = new();
    private Action<OneBotBaseEvent>? _sharedEventHandler;

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public QQToolsConfig Configuration { get; set; }

    public QQToolsModule(XmlFunctionCaller functionCaller, ILogger<QQToolsModule> logger, Interactor<QQToolsModule> interactor, QChatService qChatService)
    {
        _functionCaller = functionCaller;
        _logger = logger;
        _interactor = interactor;
        _qChatService = qChatService;
    }

    protected override Task OnAwake()
    {
        _logger.LogInformation("QQToolsModule 已启动");
        if (Configuration?.DisableConnection == true)
        {
            _logger.LogInformation("QQTools已禁用OneBot连接（DisableConnection=true），QQ工具箱相关功能不可用");
            return Task.CompletedTask;
        }
        SubscribeSharedEvents();

        var discovered = new XmlHandler(this);
        // QQ增强接管重叠功能时（默认 PreferQQEnhance），本模块不再注册这些同名能力的函数，
        // 避免 AI 同一轮把 ReplyRecent 和 SendReplyMessage 都调一遍，造成重复回复/重复点赞/重复戳。
        // 注意：套件按 Features 顺序创建实例，QQ工具箱可能在 QQ增强之前，所以这里只是顺手过滤；
        // 真正保证不重复的是各函数入口的 EnhanceOwns 门控。
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (EnhanceOwns("ReplyEnabled")) hidden.Add("SendReplyMessage");
        if (EnhanceOwns("EmojiReactEnabled")) hidden.Add("SendEmojiLike");
        if (EnhanceOwns("SendLikesEnabled")) hidden.Add("SendLike");
        if (EnhanceOwns("PokeEnabled")) hidden.Add("PokeGroupMemberById");
        if (EnhanceOwns("DeleteMsgEnabled")) hidden.Add("DeleteMessage");
        if (EnhanceOwns(null)) hidden.Add("GetGroupMessageHistory");
        if (hidden.Count > 0)
            _logger.LogInformation("QQ增强已接管重叠功能，QQ工具箱不再注册 {Count} 个重复函数", hidden.Count);
        var handler = new XmlHandler(this)
        {
            Description = "QQ工具箱：点赞、戳一戳、撤回消息、空间说说等",
            Explanation = "提供QQ点赞、戳一戳、撤回消息与QQ空间互动功能",
            Functions = discovered.Functions.Where(function => !hidden.Contains(function.Name)).ToList()
        };
        _functionCaller.RegisterHandler(handler, DocumentMode.Implicit, DestroyCancellationToken);

        _interactor.Prompt($$"""
            你具备「QQ工具箱」能力：点赞、戳一戳、撤回、发说说、点赞说说、贴表情、引用回复等，按用户口头意图直接调用对应函数。

            ## 提供函数
            {{handler.FunctionDocument()}}

            收到私聊消息时的「正在输入…」状态会自动显示，无需调用。
            """);
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        if (_sharedEventHandler != null)
        {
            _qChatService.Client?.EventReceived -= _sharedEventHandler;
            _sharedEventHandler = null;
        }
        foreach (CancellationTokenSource typing in _typingCts.Values)
        {
            try { typing.Cancel(); } catch { }
        }
        _typingCts.Clear();
        return base.OnDestroy();
    }

    private static string GetElementString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? "";
        if (element.ValueKind == JsonValueKind.Number)
            return element.GetInt64().ToString();
        return "";
    }


    private void SubscribeSharedEvents()
    {
        OneBotClient? client = _qChatService.Client;
        if (client == null || _sharedEventHandler != null)
        {
            return;
        }
        _botId = _qChatService.BotId;
        _sharedEventHandler = oneBotEvent =>
        {
            if (!string.IsNullOrEmpty(oneBotEvent.RawJson))
            {
                // 单个事件解析失败不能打断共享接收循环
                try
                {
                    HandleSharedEvent(oneBotEvent.RawJson!);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("处理共享事件异常：{Message}", ex.Message);
                }
            }
        };
        client.EventReceived += _sharedEventHandler;
    }

    /// <summary>读取 QQ增强的配置（存在且可读时返回 true）。判定与 QQ增强的 ShouldDelegate 保持一致：
    /// PreferQQEnhance / Off 由增强执行；Auto / PreferYuYang 让位给本模块。</summary>
    private bool TryGetEnhanceConfig(out string mode, out bool typingEnabled, out object? config)
    {
        mode = "";
        typingEnabled = false;
        config = null;
        try
        {
            if (ChatActivity?.Container.Instances is not { } instances) return false;
            object? enhance = null;
            foreach (object instance in instances)
            {
                if (instance.GetType().FullName == "QQSuite.QQEnhance.QQEnhanceModule")
                {
                    enhance = instance;
                    break;
                }
            }
            if (enhance == null) return false;
            config = enhance.GetType().GetProperty("Configuration")?.GetValue(enhance);
            if (config == null) return false;
            mode = config.GetType().GetProperty("CompatibilityMode")?.GetValue(config) as string ?? "PreferQQEnhance";
            typingEnabled = config.GetType().GetProperty("TypingIndicatorEnabled")?.GetValue(config) as bool? ?? false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>QQ增强是否负责“输入中”状态（是则本模块不再重复发 set_input_status）</summary>
    private bool EnhanceHandlesTyping() =>
        TryGetEnhanceConfig(out string mode, out bool typing, out _) &&
        typing && (mode is "PreferQQEnhance" or "Off");

    /// <summary>QQ增强是否接管某个重叠能力：switchProperty 是 QQ增强里的对应开关，null 表示没有单独开关（QGetMessages）。
    /// 增强未启用或配置让位（Auto / PreferYuYang）时返回 false，本模块照常执行。</summary>
    private bool EnhanceOwns(string? switchProperty)
    {
        if (!TryGetEnhanceConfig(out string mode, out _, out object? config)) return false;
        if (!(mode is "PreferQQEnhance" or "Off")) return false;
        if (switchProperty == null) return true;
        try
        {
            return config?.GetType().GetProperty(switchProperty)?.GetValue(config) as bool? ?? true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>重叠能力门控：QQ增强接管时直接把请求重定向到增强的函数，避免两边各执行一遍</summary>
    private string? RedirectIfEnhanceOwns(string? switchProperty, string enhancedFunction)
    {
        if (!EnhanceOwns(switchProperty)) return null;
        string hint = $"该能力已由 QQ增强 接管，请改用 {enhancedFunction}";
        _interactor.Poke(hint);
        return hint;
    }

    private void HandleSharedEvent(string rawJson)
    {
        using JsonDocument doc = JsonDocument.Parse(rawJson);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("post_type", out var postTypeElement))
        {
            return;
        }

        string postType = GetElementString(postTypeElement);
        if (root.TryGetProperty("self_id", out var selfIdElement))
        {
            _botId = selfIdElement.GetInt64();
        }
        _logger.LogInformation("收到OneBot事件: type={Type}", postType);

        if ((postType == "message" || postType == "message_sent") &&
            root.TryGetProperty("message_id", out var msgIdElement))
        {
            string msgId = GetElementString(msgIdElement);
            string rawMsg = root.TryGetProperty("raw_message", out var rawMsgElement)
                ? rawMsgElement.GetString() ?? ""
                : "";
            bool isSelfSent = postType == "message_sent" ||
                (root.TryGetProperty("user_id", out var userIdElement) && userIdElement.GetInt64() == _botId);
            // 不在这里记录 _lastSentMessageId：message_sent 事件覆盖所有模块（视频转发、Pixiv 发图等），
            // 会让 GetLastMessageId 指向别的模块刚发的消息；该字段只由本模块 CallActionAsync 发送成功时更新

            // 私聊消息时显示"正在输入..."状态
            if (!isSelfSent && postType == "message")
            {
                long senderId = root.TryGetProperty("user_id", out var senderElement) ? senderElement.GetInt64() : 0;
                string messageType = root.TryGetProperty("message_type", out var typeElement) ? typeElement.GetString() ?? "" : "";
                if (messageType == "private" && senderId > 0 && senderId != _botId && !EnhanceHandlesTyping())
                {
                    _ = Task.Run(() => SendTypingIndicator(senderId));
                }
            }

            _logger.LogInformation("QQTools捕获消息: id={MsgId} self={IsSelf} content={Content}", msgId, isSelfSent, rawMsg);
        }

        if (postType == "request")
        {
            _ = Task.Run(() => HandleRequestAsync(root.Clone()));
        }
    }

    private async Task HandleRequestAsync(JsonElement req)
    {
        try
        {
            string requestType = req.TryGetProperty("request_type", out var rtElement)
                ? rtElement.GetString() ?? "" : "";
            if (requestType == "friend")
            {
                string flag = req.TryGetProperty("flag", out var flagElement)
                    ? flagElement.GetString() ?? "" : "";
                long userId = req.TryGetProperty("user_id", out var uidElement)
                    ? uidElement.GetInt64() : 0;
                string comment = req.TryGetProperty("comment", out var commentElement)
                    ? commentElement.GetString() ?? "" : "";
                _logger.LogInformation("收到好友申请: QQ={UserId}, 验证信息={Comment}", userId, comment);

                await CallActionAsync<JsonElement>("set_friend_add_request", new
                {
                    flag = flag,
                    approve = true,
                    remark = "猫娘幼央"
                });
                _logger.LogInformation("已自动同意好友申请: QQ={UserId}, 验证信息={Comment}", userId, comment);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理请求事件失败");
        }
    }

    private async Task<T?> CallActionAsync<T>(string action, object? @params = null)
    {
        OneBotClient? client = _qChatService.Client;
        if (client is not { IsConnected: true })
        {
            throw new InvalidOperationException(QQSuiteTransport.NotConnectedMessage);
        }

        JsonElement response = await client.CallActionRawAsync(action, @params, 10);
        if (response.TryGetProperty("retcode", out var retcodeElement) && retcodeElement.GetInt32() != 0)
        {
            string errorMsg = response.TryGetProperty("message", out var msgElement)
                ? msgElement.GetString() ?? "" : "";
            throw new Exception($"调用失败: {errorMsg}");
        }

        // 从发送类接口的返回值中捕获真实消息ID
        if ((action is "send_msg" or "send_group_msg" or "send_private_msg") && response.TryGetProperty("data", out var sendData))
        {
            if (sendData.TryGetProperty("message_id", out var sentMsgIdElement))
            {
                string sentMsgId = GetElementString(sentMsgIdElement);
                if (!string.IsNullOrEmpty(sentMsgId))
                {
                    _lastSentMessageId = sentMsgId;
                    _logger.LogInformation("捕获自己发送的消息ID: {MsgId}", sentMsgId);
                }
            }
        }

        return response.TryGetProperty("data", out var dataElement)
            ? dataElement.Deserialize<T>()
            : default;
    }

    #region ===== QQ点赞/戳一戳/消息 =====

    [XmlFunction(FunctionMode.OneShot)]
    [Description("给指定QQ用户点赞，返回点赞结果（1-10次）")]
    public async Task<string> SendLike(long userId, int times = 1)
    {
        if (RedirectIfEnhanceOwns("SendLikesEnabled", "SendQQLikes") is { } likeHint) return likeHint;
        try
        {
            times = Math.Clamp(times, 1, 10);
            await CallActionAsync<JsonElement>("send_like", new { user_id = userId, times });
            _interactor.Poke($"成功给 {userId} 点了{times}次赞喵！");
            return $"成功给 {userId} 点了{times}次赞喵！";
        }
        catch (Exception ex)
        {
            _interactor.Poke("点赞失败喵: " + ex.Message);
            return "点赞失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按QQ号在群聊中戳一戳指定成员（兼容别名；推荐用 QQ增强 的 PokeGroupMember）")]
    public async Task<string> PokeGroupMemberById(long groupId, long userId)
    {
        if (RedirectIfEnhanceOwns("PokeEnabled", "PokeGroupMember") is { } pokeHint) return pokeHint;
        try
        {
            await CallActionAsync<JsonElement>("group_poke", new { group_id = groupId, user_id = userId });
            _interactor.Poke($"成功戳了戳 {userId} 喵！");
            return $"成功戳了戳 {userId} 喵！";
        }
        catch (Exception ex)
        {
            _interactor.Poke("戳一戳失败喵: " + ex.Message);
            return "戳一戳失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取QQ工具箱最近一次自己发送的消息ID（仅本模块发送的消息，用于测试撤回）")]
    public async Task<string> GetLastMessageId()
    {
        string result = string.IsNullOrEmpty(_lastSentMessageId) ? "还没有记录到消息ID喵" : $"最近消息ID: {_lastSentMessageId}";
        _interactor.Poke(result);
        return result;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("测试发送消息并获取返回的ID（用于验证send_msg返回值捕获），可指定私聊或群聊")]
    public async Task<string> TestSendMessage(string message, string messageType = "group", long userId = 0)
    {
        try
        {
            object @params;
            if (messageType == "private" && userId != 0)
            {
                @params = new
                {
                    message_type = "private",
                    user_id = userId,
                    message = message
                };
            }
            else
            {
                @params = new
                {
                    message_type = "group",
                    group_id = 427674145L,
                    message = message
                };
            }

            var response = await CallActionAsync<JsonElement>("send_msg", @params);
            string msgId = response.TryGetProperty("message_id", out var idElement) ? GetElementString(idElement) : "未获取到";
            string result = $"发送成功喵！消息ID: {msgId}";
            _interactor.Poke(result);
            return result;
        }
        catch (Exception ex)
        {
            return "发送失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("引用回复消息（需提供消息ID、回复内容、消息类型和目标ID），返回发送结果")]
    public async Task<string> SendReplyMessage(string message, long replyToId, string messageType = "group", long targetId = 0)
    {
        if (RedirectIfEnhanceOwns("ReplyEnabled", "ReplyRecent") is { } replyHint) return replyHint;
        try
        {
            // targetId 缺省时不能猜：私聊会变成 group_id=0 的无效群消息，群聊也会落到 0 号群
            if (targetId == 0)
            {
                string hint = messageType == "private"
                    ? "引用回复失败喵: 私聊请传 messageType=private 且 targetId=对方QQ"
                    : "引用回复失败喵: 群聊请传 targetId=群号";
                _interactor.Poke(hint);
                return hint;
            }

            // 引用必须作为 message 段数组里的 reply 段发送；send_msg 的顶层 reply 参数协议端不会渲染成引用
            object messagePayload = new object[]
            {
                new { type = "reply", data = new { id = replyToId.ToString() } },
                new { type = "text", data = new { text = message } }
            };

            object @params;
            string action;
            if (messageType == "private" && targetId != 0)
            {
                action = "send_private_msg";
                @params = new
                {
                    user_id = targetId,
                    message = messagePayload
                };
            }
            else
            {
                action = "send_group_msg";
                @params = new
                {
                    group_id = targetId,
                    message = messagePayload
                };
            }

            var response = await CallActionAsync<JsonElement>(action, @params);
            string msgId = response.TryGetProperty("message_id", out var idElement) ? GetElementString(idElement) : "未获取到";
            string result = $"引用回复发送成功喵！消息ID: {msgId}";
            _interactor.Poke(result);
            return result;
        }
        catch (Exception ex)
        {
            return "引用回复发送失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取群最近消息及其ID，格式：群号:最近N条，返回消息内容与ID列表")]
    public async Task<string> GetGroupMessageHistory(long groupId, int count = 3)
    {
        if (RedirectIfEnhanceOwns(null, "QGetMessages") is { } historyHint) return historyHint;
        try
        {
            var response = await CallActionAsync<JsonElement>("get_group_msg_history", new
            {
                group_id = groupId,
                count = Math.Min(count, 10)
            });
            if (!response.TryGetProperty("messages", out var messagesElement))
            {
                throw new Exception("返回中没有messages字段");
            }

            var lines = new List<string>();
            int index = 0;
            foreach (JsonElement item in messagesElement.EnumerateArray())
            {
                index++;
                string msgId = item.TryGetProperty("message_id", out var msgIdElement) ? GetElementString(msgIdElement) : "";
                string msgSeq = item.TryGetProperty("message_seq", out var seqElement) ? GetElementString(seqElement) : "";
                long userId = item.TryGetProperty("user_id", out var userIdElement) ? userIdElement.GetInt64() : 0;
                string rawMsg = item.TryGetProperty("raw_message", out var rawElement) ? rawElement.GetString() ?? "" : "";
                lines.Add($"{index}. id={msgId} seq={msgSeq} user={userId} content={rawMsg}");
            }

            string result = "群消息历史:\n" + string.Join("\n", lines);
            _interactor.Poke(result);
            return result;
        }
        catch (Exception ex)
        {
            _interactor.Poke("获取群消息失败喵: " + ex.Message);
            return "获取群消息失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("撤回一条消息（需提供消息ID），返回撤回结果")]
    public async Task<string> DeleteMessage(string messageId)
    {
        if (RedirectIfEnhanceOwns("DeleteMsgEnabled", "DeleteMsgRecent") is { } deleteHint) return deleteHint;
        try
        {
            await CallActionAsync<JsonElement>("delete_msg", new { message_id = messageId });
            _interactor.Poke("消息撤回成功喵！");
            return "消息撤回成功喵！";
        }
        catch (Exception ex)
        {
            _interactor.Poke("撤回失败喵: " + ex.Message);
            return "撤回失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("给QQ消息贴表情（需提供消息ID和表情ID），返回贴表情结果")]
    public async Task<string> SendEmojiLike(string messageId, string emojiId)
    {
        if (RedirectIfEnhanceOwns("EmojiReactEnabled", "SetEmojiRecent") is { } emojiHint) return emojiHint;
        try
        {
            // 私聊贴表情在 NapCat 上是假成功（返回 retcode 0 但没有实际效果）：先查消息所属会话，私聊直接拒绝
            try
            {
                JsonElement target = await CallActionAsync<JsonElement>("get_msg", new { message_id = messageId });
                if (target.TryGetProperty("message_type", out JsonElement mt) &&
                    mt.ValueKind == JsonValueKind.String && mt.GetString() == "private")
                {
                    const string privateHint = "私聊不支持贴表情回应（接口会假成功但没有效果），请改用文字/戳一戳回应";
                    _interactor.Poke(privateHint);
                    return privateHint;
                }
            }
            catch
            {
                // 查不到消息就不阻断，按原逻辑尝试
            }

            await CallActionAsync<JsonElement>("set_msg_emoji_like", new { message_id = messageId, emoji_id = emojiId });
            _interactor.Poke($"成功给消息 {messageId} 贴了表情 {emojiId} 喵！");
            return $"成功给消息 {messageId} 贴了表情 {emojiId} 喵！";
        }
        catch (Exception ex)
        {
            _interactor.Poke("贴表情失败喵: " + ex.Message);
            return "贴表情失败喵: " + ex.Message;
        }
    }

    #endregion

    #region ===== QQ空间 =====

    private async Task<string> GetCookiesAsync(bool forceRefresh = false)
    {
        await _cookieLock.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedCookies != null &&
                (DateTime.Now - _cookieFetchTime).TotalMinutes < Configuration.CookieRefreshMinutes)
            {
                return _cachedCookies;
            }

            var response = await CallActionAsync<JsonElement>("get_cookies", new { domain = "user.qzone.qq.com" });
            string cookies = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("cookies", out var cookieElement)
                ? cookieElement.GetString() ?? ""
                : response.ValueKind == JsonValueKind.String
                    ? response.GetString() ?? ""
                    : "";
            if (string.IsNullOrEmpty(cookies))
            {
                throw new Exception("获取QQ空间Cookie失败：返回为空");
            }

            _cachedCookies = cookies;
            _cookieFetchTime = DateTime.Now;
            _logger.LogInformation("已刷新QQ空间Cookie");
            return cookies;
        }
        finally
        {
            _cookieLock.Release();
        }
    }

    private static string CalcGtk(string key)
    {
        long hash = 5381L;
        foreach (char c in key)
        {
            hash += (hash << 5) + c;
        }
        return ((int)(hash & 0x7FFFFFFF)).ToString();
    }

    private async Task<(string cookies, string gtk, long uin)> GetContextAsync()
    {
        string cookies = await GetCookiesAsync();
        var dict = ParseCookies(cookies);
        long uin = long.TryParse(dict.GetValueOrDefault("uin", "").TrimStart('o'), out var parsedUin) ? parsedUin : 0;
        string skey = dict.GetValueOrDefault("skey", "");
        string pSkey = dict.GetValueOrDefault("p_skey", "");
        string gtk = CalcGtk(!string.IsNullOrEmpty(pSkey) ? pSkey : skey);
        return (cookies, gtk, uin);
    }

    private static Dictionary<string, string> ParseCookies(string cookieStr)
    {
        var dict = new Dictionary<string, string>();
        foreach (string part in cookieStr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eqIndex = part.IndexOf('=');
            if (eqIndex > 0)
            {
                string key = part[..eqIndex].Trim();
                string value = part[(eqIndex + 1)..].Trim();
                dict[key] = value;
            }
        }
        return dict;
    }

    private HttpRequestMessage BuildRequest(string url, Dictionary<string, string> formData, string cookies, string gtk, string referer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url + "?g_tk=" + gtk);
        request.Headers.TryAddWithoutValidation("Cookie", cookies);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("Origin", "https://user.qzone.qq.com");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Content = new FormUrlEncodedContent(formData);
        return request;
    }

    private async Task<string> GetQzoneTokenAsync(string cookies, long uin)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://user.qzone.qq.com/{uin}");
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", $"https://user.qzone.qq.com/{uin}");

            using var response = await Http.SendAsync(request);
            string html = await response.Content.ReadAsStringAsync();

            string token = "";
            int index = html.IndexOf("\"qzonetoken\"", StringComparison.Ordinal);
            if (index >= 0)
            {
                int colonIndex = html.IndexOf(':', index) + 1;
                while (colonIndex < html.Length && (html[colonIndex] == ' ' || html[colonIndex] == '\t')) colonIndex++;
                if (colonIndex < html.Length && html[colonIndex] == '"')
                {
                    int start = colonIndex + 1;
                    int end = html.IndexOf('"', start);
                    if (end > start)
                    {
                        token = html[start..end];
                    }
                }
            }

            if (string.IsNullOrEmpty(token))
            {
                int eqIndex = html.IndexOf("qzonetoken=", StringComparison.Ordinal);
                if (eqIndex >= 0)
                {
                    int start = eqIndex + "qzonetoken=".Length;
                    int end = start;
                    while (end < html.Length && html[end] != '"' && html[end] != '\'' && html[end] != '&' && html[end] != '<') end++;
                    if (end > start)
                    {
                        token = html[start..end];
                    }
                }
            }

            _logger.LogInformation("QZone qzonetoken: {Status}", string.IsNullOrEmpty(token) ? "(空)" : "获取成功");
            return token;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取QZone qzonetoken失败");
            return "";
        }
    }

    private static async Task<string> SendAndCheckAsync(HttpRequestMessage request)
    {
        using var response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"HTTP {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        int code = doc.RootElement.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -1;
        if (code != 0)
        {
            string msg = doc.RootElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() ?? "" : "";
            throw new Exception($"QZone接口错误: code={code} msg={msg}");
        }
        return body;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("发表QQ空间说说（纯文字），返回发布结果")]
    public async Task<string> PublishMood(string content)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                _interactor.Poke("内容不能为空喵！");
                return "内容不能为空喵！";
            }

            var (cookies, gtk, uin) = await GetContextAsync();
            string url = "https://user.qzone.qq.com/proxy/domain/taotao.qzone.qq.com/cgi-bin/emotion_cgi_publish_v6";
            var form = new Dictionary<string, string>
            {
                ["syn_tweet_verson"] = "1",
                ["paramstr"] = "1",
                ["who"] = "1",
                ["con"] = content,
                ["feedversion"] = "1",
                ["ver"] = "1",
                ["ugc_right"] = "1",
                ["to_sign"] = "0",
                ["hostuin"] = uin.ToString(),
                ["code_version"] = "1",
                ["format"] = "json",
                ["qzreferrer"] = $"https://user.qzone.qq.com/{uin}"
            };
            await SendAndCheckAsync(BuildRequest(url, form, cookies, gtk, $"https://user.qzone.qq.com/{uin}"));
            _logger.LogInformation("QZone发说说成功: {Content}", content);
            _interactor.Poke("说说发布成功喵！✨");
            return "说说发布成功喵！✨";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QZone发说说失败");
            _interactor.Poke("发说说失败喵: " + ex.Message);
            return "发说说失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("点赞QQ空间说说（需提供作者QQ号和说说tid），返回点赞结果")]
    public async Task<string> LikeMood(long authorUin, string tid)
    {
        try
        {
            var (cookies, gtk, uin) = await GetContextAsync();
            string unikey = $"http://user.qzone.qq.com/{authorUin}/mood/{tid}.1";
            string url = "https://user.qzone.qq.com/proxy/domain/w.qzone.qq.com/cgi-bin/likes/internal_dolike_app";
            var form = new Dictionary<string, string>
            {
                ["qzreferrer"] = $"https://user.qzone.qq.com/{authorUin}",
                ["opuin"] = authorUin.ToString(),
                ["unikey"] = unikey,
                ["curkey"] = unikey,
                ["from"] = "-100",
                ["fupdate"] = "1",
                ["face"] = "0",
                ["format"] = "json"
            };
            await SendAndCheckAsync(BuildRequest(url, form, cookies, gtk, $"https://user.qzone.qq.com/{authorUin}"));
            _logger.LogInformation("QZone点赞成功: tid={Tid}", tid);
            _interactor.Poke("点赞成功喵！❤️");
            return "点赞成功喵！❤️";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QZone点赞失败: tid={Tid}", tid);
            _interactor.Poke("点赞失败喵: " + ex.Message);
            return "点赞失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("取消点赞QQ空间说说（需提供作者QQ号和说说tid），返回取消结果")]
    public async Task<string> UnlikeMood(long authorUin, string tid)
    {
        try
        {
            var (cookies, gtk, uin) = await GetContextAsync();
            string unikey = $"http://user.qzone.qq.com/{authorUin}/mood/{tid}.1";
            string url = "https://user.qzone.qq.com/proxy/domain/w.qzone.qq.com/cgi-bin/likes/internal_unlike_app";
            var form = new Dictionary<string, string>
            {
                ["qzreferrer"] = $"https://user.qzone.qq.com/{authorUin}",
                ["opuin"] = authorUin.ToString(),
                ["unikey"] = unikey,
                ["curkey"] = unikey,
                ["from"] = "-100",
                ["fupdate"] = "1",
                ["face"] = "0",
                ["format"] = "json"
            };
            await SendAndCheckAsync(BuildRequest(url, form, cookies, gtk, $"https://user.qzone.qq.com/{authorUin}"));
            _logger.LogInformation("QZone取消点赞成功: tid={Tid}", tid);
            _interactor.Poke("取消点赞成功喵");
            return "取消点赞成功喵";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QZone取消点赞失败: tid={Tid}", tid);
            _interactor.Poke("取消点赞失败喵: " + ex.Message);
            return "取消点赞失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取QQ空间最新说说列表（默认自己的，可指定他人QQ号），返回最近几条说说的内容")]
    public async Task<string> GetMoodList(long uin = 0L)
    {
        try
        {
            var (cookies, gtk, myUin) = await GetContextAsync();
            long targetUin = uin > 0 ? uin : myUin;
            string qzonetoken = await GetQzoneTokenAsync(cookies, targetUin);

            string url = "https://h5.qzone.qq.com/proxy/domain/taotao.qq.com/cgi-bin/emotion_cgi_msglist_v6";
            var queryParams = new Dictionary<string, string>
            {
                ["sort"] = "0",
                ["start"] = "0",
                ["num"] = "10",
                ["cgi_host"] = "http://taotao.qq.com/cgi-bin/emotion_cgi_msglist_v6",
                ["replynum"] = "100",
                ["callback"] = "_preloadCallback",
                ["code_version"] = "1",
                ["inCharset"] = "utf-8",
                ["outCharset"] = "utf-8",
                ["notice"] = "0",
                ["format"] = "jsonp",
                ["need_private_comment"] = "1",
                ["g_tk"] = gtk,
                ["qzonetoken"] = qzonetoken,
                ["uin"] = targetUin.ToString(),
                ["pos"] = "0"
            };
            var queryString = new StringBuilder();
            foreach (var (key, value) in queryParams)
            {
                if (queryString.Length > 0) queryString.Append('&');
                queryString.Append(key).Append('=').Append(Uri.EscapeDataString(value));
            }

            var request = new HttpRequestMessage(HttpMethod.Get, url + "?" + queryString);
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", $"https://user.qzone.qq.com/{targetUin}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.TryAddWithoutValidation("Origin", "https://user.qzone.qq.com");

            using var response = await Http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            // 解析JSONP
            string json = body;
            if (body.Contains("_preloadCallback("))
            {
                int start = body.IndexOf('(') + 1;
                int end = body.LastIndexOf(')');
                if (start > 0 && end > start)
                {
                    json = body[start..end];
                }
            }

            using var doc = JsonDocument.Parse(json);
            int code = doc.RootElement.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -1;
            if (code != 0)
            {
                string msg = doc.RootElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() ?? "" : "";
                _interactor.Poke($"获取说说列表失败喵: code={code} msg={msg}");
                return $"获取说说列表失败喵: code={code} msg={msg}";
            }
            if (!doc.RootElement.TryGetProperty("msglist", out var msgList))
            {
                _interactor.Poke("没有获取到说说喵");
                return "没有获取到说说喵";
            }

            var result = new StringBuilder();
            int count = 0;
            foreach (JsonElement item in msgList.EnumerateArray())
            {
                if (count >= 10) break;
                string tid = item.TryGetProperty("tid", out var tidElement) ? tidElement.GetString() ?? "" : "";
                string content = item.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? "" : "";
                string createTime = item.TryGetProperty("createTime", out var timeElement) ? timeElement.GetString() ?? "" : "";
                string name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                if (content.Length > 80)
                {
                    content = content[..80] + "...";
                }
                if (count > 0) result.AppendLine();
                result.Append($"{count + 1}. [{name}] {content} ({createTime}) tid={tid}");
                count++;
            }

            _logger.LogInformation("QZone获取说说列表成功: {Count}条", count);
            string output = $"获取到{count}条说说喵：\n{result}";
            _interactor.Poke(output);
            return output;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QZone获取说说列表失败");
            _interactor.Poke("获取说说列表失败喵: " + ex.Message);
            return "获取说说列表失败喵: " + ex.Message;
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("删除QQ空间说说（需提供tid）")]
    public async Task<string> DeleteMood(string tid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tid))
            {
                _interactor.Poke("tid不能为空喵！");
                return "tid不能为空喵！";
            }

            var (cookies, gtk, uin) = await GetContextAsync();
            string url = "https://user.qzone.qq.com/proxy/domain/taotao.qzone.qq.com/cgi-bin/emotion_cgi_delete_v6";
            var form = new Dictionary<string, string>
            {
                ["hostuin"] = uin.ToString(),
                ["tid"] = tid,
                ["code_version"] = "1",
                ["format"] = "json",
                ["qzreferrer"] = $"https://user.qzone.qq.com/{uin}"
            };
            await SendAndCheckAsync(BuildRequest(url, form, cookies, gtk, $"https://user.qzone.qq.com/{uin}"));
            _logger.LogInformation("QZone删除说说成功: tid={Tid}", tid);
            _interactor.Poke("删除说说成功喵！tid=" + tid);
            return "删除说说成功喵！tid=" + tid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QZone删除说说失败: tid={Tid}", tid);
            _interactor.Poke("删除说说失败喵: " + ex.Message);
            return "删除说说失败喵: " + ex.Message;
        }
    }

    #endregion

    #region ===== 私聊输入指示 =====

    private async Task SendTypingIndicator(long userId)
    {
        try
        {
            // 如果已有活跃的输入指示任务，取消旧的
            // 注意：不立即Dispose旧的CTS，因为旧任务可能还在使用它
            // 只Cancel让旧任务自然退出，等待GC回收，避免ObjectDisposedException
            if (_typingCts.TryRemove(userId, out var oldCts))
            {
                oldCts.Cancel();
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            _typingCts.TryAdd(userId, cts);

            // 延迟2秒后开始发送"正在输入..."（模拟思考时间）
            await Task.Delay(2000, cts.Token);

            // 每隔2秒发送一次，直到超时或被取消
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await CallActionAsync<JsonElement>("set_input_status", new { user_id = userId, event_type = 1 });
                    _logger.LogDebug("已发送输入指示给 {UserId}", userId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("发送输入指示失败: {Error}", ex.Message);
                    break;
                }
                await Task.Delay(2000, cts.Token);
            }
        }
        catch (TaskCanceledException)
        {
            // 任务被取消，正常结束
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "输入指示任务异常");
        }
        finally
        {
            _typingCts.TryRemove(userId, out _);
        }
    }

    #endregion
}
