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
        "注意！如果回复QQ消息，必须保持极简的文本（0-20字）来保证自然感。同时群聊消息要选择性忽略，避免刷屏。此外注意分清语境，群聊环境人声嘈杂，不要回复与自己无关的内容，回复别人直接说话即可，不要用 @。";

    [DisplayName("私聊附加提示词")]
    [Description("收到私聊消息时额外附加给 AI 的提示词")]
    public string AppendPrivateChatPrompt { get; set; } = "(回复请保持1-20字)";

    [DisplayName("群聊附加提示词")]
    [Description("收到群聊消息时额外附加给 AI 的提示词")]
    public string AppendGroupChatPrompt { get; set; } = "(群聊：1-20字，只回@你或喊你的人，别插话别回错人别刷屏)";

    [DisplayName("群聊提示词概率(%)")]
    [Description("上面那段群聊提示词每次推送时附加的概率，0=从不附加，100=每次都附加。记忆插件已经能记住这些规则时设 0 即可；文案本身保留，随时调回来")]
    public int GroupChatPromptProbability { get; set; } = 0;

    [DisplayName("语音转文字")]
    [Description("开启后用本机 whisper.cpp 把语音转成文字再交给 AI。全程本地运行、不出网；默认关闭")]
    public bool VoiceToTextEnabled { get; set; }

    [DisplayName("whisper-cli 路径")]
    [Description("whisper.cpp 命令行程序（whisper-cli.exe）的绝对路径，例如 <whisper.cpp 解压目录>\\whisper-cli.exe。留空则语音转文字不可用")]
    public string WhisperCliPath { get; set; } = "";

    [DisplayName("whisper 模型路径")]
    [Description("ggml 模型文件的绝对路径，例如 <whisper.cpp 解压目录>\\models\\ggml-tiny.bin。tiny 约 74MB 精度一般；base/small 更准但更慢")]
    public string WhisperModelPath { get; set; } = "";

    [DisplayName("语音识别语言")]
    [Description("传给 whisper 的 -l 参数。默认 auto 自动判断语种，纯英文语音不会被强行当中文解码；固定填 zh 会省约 0.5 秒，但英文语音会整句失败")]
    public string WhisperLanguage { get; set; } = "auto";

    [DisplayName("语音识别提示词")]
    [Description("传给 whisper 的 --prompt。实测对简体化没有稳定效果，还会改变个别词的拼写，建议留空；后续可用来放专有名词热词表")]
    public string WhisperPrompt { get; set; } = "";

    [DisplayName("语音识别线程数")]
    [Description("whisper 使用的 CPU 线程数，太高会和 QQ 抢 CPU")]
    public int WhisperThreads { get; set; } = 4;

    [DisplayName("语音识别超时(秒)")]
    [Description("单条语音最长等多久，超时就放弃识别并保留 [语音] 占位符")]
    public int VoiceToTextTimeoutSeconds { get; set; } = 30;

    [DisplayName("动图抽帧拼图")]
    [Description("收到 GIF 时在本地均匀抽帧拼成一张静图，并把图片路径写进消息（AI 用 LoadImage 打开）。多模态上游只会看到第一帧，不开这个等于只看开头。关闭则保持原占位符")]
    public bool GifSheetEnabled { get; set; } = true;

    [DisplayName("动图抽帧数量")]
    [Description("每张 GIF 均匀抽取多少帧拼成网格图，3-16。9~12 一般够用，越大越费 token")]
    public int GifSheetSamples { get; set; } = 12;

    [DisplayName("动图大小上限(MB)")]
    [Description("超过这个大小的 GIF 不下载不处理，回退成普通图片占位符")]
    public int GifSheetMaxMb { get; set; } = 8;

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
