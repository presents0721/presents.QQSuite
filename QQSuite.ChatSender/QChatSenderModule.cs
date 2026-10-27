using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;
using QQSuite.Core;

namespace QQSuite.ChatSender;

/// <summary>单向发送的身份补充；连接地址与 Token 统一走「QQ聊天」。</summary>
public record QChatSenderConfig
{
    [DisplayName("机器人QQ号")]
    [Description("留 0 表示跟随 QQ聊天 的登录身份；仅用于拦截「发给自己」")]
    public long BotId { get; set; }
}

/// <summary>
/// QQ 单向发送：等价旧版 Alife.Function.QChatSender。
/// 复用「QQ聊天」的同一条 OneBot 连接，只发送、不监听，不额外占用协议端。
/// </summary>
public class QChatSenderModule(
    XmlFunctionCaller functionService,
    ILogger<QChatSenderModule> logger,
    Interactor<QChatSenderModule> interactor,
    QChatService qChatService
) : ChatBehaviour, IConfigurable<QChatSenderConfig>
{
    public QChatSenderConfig Configuration { get; set; } = null!;

    long BotId => Configuration.BotId > 0 ? Configuration.BotId : qChatService.BotId;

    OneBotClient Client =>
        qChatService.Client is { IsConnected: true } client
            ? client
            : throw new InvalidOperationException(QQSuiteTransport.NotConnectedMessage);

    #region AI函数调用

    [XmlFunction(FunctionMode.Content)]
    [Description("单向发送文本到QQ（支持CQ码；只发送，不监听群消息）")]
    public async Task QChatSend(XmlExecutorContext ctx, OneBotMessageType type, long targetId)
    {
        if (ctx.CallMode != CallMode.Closing)
        {
            return;
        }

        long botId = BotId;
        if (botId != 0 && targetId == botId)
        {
            throw new Exception("不允许将消息发送给自己");
        }

        string message = ctx.FullContent.Trim();
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        try
        {
            if (type == OneBotMessageType.Group)
            {
                await Client.SendGroupMessage(targetId, message);
            }
            else
            {
                await Client.SendPrivateMessage(targetId, message);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("QQ单向发送失败: {Message}", ex.Message);
            interactor.Poke($"[QQ单向发送失败] {ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("单向发送文件到QQ")]
    public async Task QChatSendFile(OneBotMessageType type, long targetId,
        [Description("本地绝对路径")] string file)
    {
        file = file.Trim();
        if (string.IsNullOrEmpty(file))
        {
            throw new ArgumentNullException(nameof(file));
        }
        if (targetId == 0)
        {
            throw new ArgumentNullException(nameof(targetId));
        }

        long botId = BotId;
        if (botId != 0 && targetId == botId)
        {
            throw new Exception("不允许将文件发送给自己");
        }

        file = file.Replace('\\', '/');
        string fileName = Path.GetFileName(file);
        try
        {
            if (type == OneBotMessageType.Group)
            {
                await Client.UploadGroupFile(targetId, file, fileName);
            }
            else
            {
                await Client.UploadPrivateFile(targetId, file, fileName);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("QQ单向发送文件失败: {Message}", ex.Message);
            interactor.Poke($"[QQ单向发送文件失败] {ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("单向发送图片到QQ（仅支持图片，不支持文件；发送文件请用 QChatSendFile）")]
    public async Task QChatSendImage(OneBotMessageType type, long targetId,
        [Description("支持网址url、表情库名称，或者本地绝对路径")] string image)
    {
        image = image.Trim();
        if (string.IsNullOrEmpty(image))
        {
            throw new ArgumentNullException(nameof(image));
        }
        if (targetId == 0)
        {
            throw new ArgumentNullException(nameof(targetId));
        }

        long botId = BotId;
        if (botId != 0 && targetId == botId)
        {
            throw new Exception("不允许将图片发送给自己");
        }

        // 表情库匹配优先：目录取随机一张，单文件直接用，无后缀时补 .png/.jpg/.jpeg/.gif
        string emoteBase = Path.Combine(AlifePath.StorageFolderPath, "Emotes");
        string emotePath = Path.Combine(emoteBase, image).Replace('\\', '/');

        if (Directory.Exists(emotePath))
        {
            string[] files = Directory.GetFiles(emotePath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(s => s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            s.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            s.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (files.Length > 0)
            {
                image = files[Random.Shared.Next(files.Length)];
            }
        }
        else if (File.Exists(emotePath))
        {
            image = emotePath;
        }
        else
        {
            string[] extensions = [".png", ".jpg", ".jpeg", ".gif"];
            string? foundFile = extensions.Select(ext => emotePath + ext).FirstOrDefault(File.Exists);
            if (foundFile != null)
            {
                image = foundFile;
            }
        }

        if (!image.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(image))
        {
            throw new Exception("图片不存在");
        }

        image = image.Replace('\\', '/');
        try
        {
            if (type == OneBotMessageType.Group)
            {
                await Client.SendGroupMessage(targetId, $"[CQ:image,file={image}]");
            }
            else
            {
                await Client.SendPrivateMessage(targetId, $"[CQ:image,file={image}]");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("QQ单向发送图片失败: {Message}", ex.Message);
            interactor.Poke($"[QQ单向发送图片失败] {ex.Message}");
        }
    }

    #endregion

    protected override Task OnAwake()
    {
        XmlHandler xmlHandler = new(this);
        functionService.RegisterHandler(xmlHandler, DocumentMode.None, DestroyCancellationToken);

        interactor.Prompt($$"""
                           QQ单向发送：复用「QQ聊天」的连接，只发不收，不监听、不改变群消息接收状态。

                           ## 提供函数
                           {{xmlHandler.FunctionDocument()}}

                           文本用 QChatSend（支持 CQ 码），文件用 QChatSendFile，图片用 QChatSendImage；不要发给自己。
                           """);

        return Task.CompletedTask;
    }
}
