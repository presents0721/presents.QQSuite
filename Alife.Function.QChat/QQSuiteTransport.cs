using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace QQSuite.Core;

/// <summary>
/// 套件内部统一连接适配层：所有子功能复用 QQ聊天 的同一条 OneBot 连接。
/// </summary>
internal static class QQSuiteTransport
{
    public const string NotConnectedMessage = "QQ聊天 尚未连接 OneBot，请先在「QQ 全能套件」中启用 QQ聊天 并检查连接设置。";

    /// <summary>调用 OneBot 接口并返回完整原始响应（status/retcode/data），保留各子模块原有的响应解析方式。</summary>
    public static async Task<string> CallActionAsync(OneBotClient? client, string action, JsonObject parameters, int timeoutSeconds = 30)
    {
        if (client is not { IsConnected: true })
        {
            throw new InvalidOperationException(NotConnectedMessage);
        }

        JsonElement raw = await client.CallActionRawAsync(action, parameters, timeoutSeconds);
        return raw.GetRawText();
    }
}
