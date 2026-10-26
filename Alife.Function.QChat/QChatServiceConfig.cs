using System.ComponentModel;

namespace QQSuite.Core;

public record QChatServiceConfig
{
    [DisplayName("机器人QQ号")]
    [Description("OneBot 协议端登录的机器人 QQ 号，用于识别 @ 与戳一戳")]
    public long BotId { get; set; }

    [DisplayName("主人QQ号")]
    [Description("最高权限账号，部分管理功能会校验")]
    public long OwnerId { get; set; }

    [DisplayName("连接地址")]
    [Description("OneBot v11 的 WebSocket 地址，例如 ws://127.0.0.1:3001")]
    public string Url { get; set; } = "ws://127.0.0.1:3001";

    [DisplayName("连接令牌")]
    [Description("协议端配置了 Access Token 时填写，没有就留空")]
    public string Token { get; set; } = "";

    [DisplayName("自动重连间隔(秒)")]
    [Description("连接断开后每隔多少秒尝试重连")]
    public int AutoReconnectSeconds { get; set; } = 60;

    [DisplayName("通用附加提示词")]
    [Description("所有 QQ 消息都会附加给 AI 的提示词，适合写人设和回复风格")]
    public string AppendDocumentPrompt { get; set; } =
        "注意！如果回复QQ消息，必须保持极简的文本（0-20字）来保证自然感。同时群聊消息要选择性忽略，避免刷屏。此外注意分清语境，群聊环境人声嘈杂，不要回复与自己无关的内容，回复时请使用CQ码功能指定回复人。";

    [DisplayName("私聊附加提示词")]
    [Description("收到私聊消息时额外附加给 AI 的提示词")]
    public string AppendPrivateChatPrompt { get; set; } = "(回复请保持1-20字)";

    [DisplayName("群聊附加提示词")]
    [Description("收到群聊消息时额外附加给 AI 的提示词")]
    public string AppendGroupChatPrompt { get; set; } = "(回复请保持1-20字。注意分清群聊场合，不要随便插话，避免刷屏)";

    [DisplayName("屏蔽群号")]
    [Description("完全屏蔽这些群的任何消息，多个群号用逗号分隔")]
    public string IgnoredGroup { get; set; } = "";

    [DisplayName("唤醒词")]
    [Description("群消息包含这些词时激活群监听，多个词用逗号分隔；真正 @ 机器人也会唤醒")]
    public string WakingWords { get; set; } = "";

    [DisplayName("自动参与概率")]
    [Description("群未激活时，每条群消息自动激活监听的随机概率（0-1）")]
    public float ProactiveChatProbability { get; set; }

    [DisplayName("未激活缓存条数")]
    [Description("群未激活时也始终缓存的消息条数，激活时会一并送给 AI 作为聊天背景")]
    public int PerBufferSize { get; set; } = 5;

    [DisplayName("激活缓存上限")]
    [Description("群激活状态下最多暂存多少条消息，溢出立即推送；-1 表示不限")]
    public int MaxBufferMessages { get; set; } = -1;

    [DisplayName("推送间隔(秒)")]
    [Description("群监听激活后，隔多少秒把暂存的消息推送给 AI")]
    public float FlushInterval { get; set; } = 12f;

    [DisplayName("消息防抖")]
    [Description("开启后每收到新消息就重置推送倒计时，等安静下来再推送")]
    public bool DebounceEnabled { get; set; }

    [DisplayName("唤醒时立即推送")]
    [Description("遇到唤醒词或 @ 时立即推送缓存消息，不必等推送间隔")]
    public bool PushOnWakeUp { get; set; }

    [DisplayName("回复后关闭监听")]
    [Description("AI 回复后立即关闭该群的群消息监听")]
    public bool CloseGroupAfterReply { get; set; }

    [DisplayName("自动关闭监听(分钟)")]
    [Description("长时间没有触发唤醒条件时，自动关闭群消息监听的时间")]
    public float AutoCloseMinutes { get; set; } = 4f;

    [DisplayName("私聊防抖(秒)")]
    [Description("私聊时等待多少秒再推送，避免用户连续发多条消息被打断")]
    public float PrivateDebounceTime { get; set; }

    [DisplayName("展开合并转发内容")]
    [Description("收到合并转发消息时自动读取其中的聊天记录，让 AI 直接看到每条内容和发送者，无需额外调用 QForward")]
    public bool ExpandForwardMessages { get; set; } = true;
}
