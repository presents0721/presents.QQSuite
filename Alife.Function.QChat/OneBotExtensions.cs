using System.Collections.Generic;
using System.Threading.Tasks;

namespace QQSuite.Core;

/// <summary>
/// 基础 API 扩展，提供最常用的消息发送与文件处理功能。
/// </summary>
public static class OneBotExtensions
{
    public static async Task SendPrivateMessage(this OneBotClient client, long userId, string message)
    {
        await client.CallActionAsync<object>("send_private_msg", new { user_id = userId, message });
    }
    public static async Task SendGroupMessage(this OneBotClient client, long groupId, string message)
    {
        await client.CallActionAsync<object>("send_group_msg", new { group_id = groupId, message });
    }
    public static async Task UploadPrivateFile(this OneBotClient client, long userId, string filePath, string name)
    {
        await client.CallActionAsync<object>("upload_private_file", new UploadFileParams { UserId = userId, File = filePath, Name = name });
    }
    public static async Task UploadGroupFile(this OneBotClient client, long groupId, string filePath, string name)
    {
        await client.CallActionAsync<object>("upload_group_file", new UploadFileParams { GroupId = groupId, File = filePath, Name = name });
    }

    /// <summary>
    /// 获取私聊文件下载链接。
    /// </summary>
    public static async Task<OneBotFile?> GetPrivateFileUrl(this OneBotClient client, string fileId)
    {
        return await client.CallActionAsync<OneBotFile>("get_private_file_url", new { file_id = fileId });
    }
    /// <summary>
    /// 获取群文件下载链接。
    /// </summary>
    public static async Task<OneBotFile?> GetGroupFileUrl(this OneBotClient client, long groupId, string fileId)
    {
        return await client.CallActionAsync<OneBotFile>("get_group_file_url", new { group_id = groupId, file_id = fileId });
    }
    /// <summary>
    /// 根据消息 ID 获取消息详情。
    /// </summary>
    public static async Task<OneBotMessageEvent?> GetMessage(this OneBotClient client, long messageId)
    {
        return await client.CallActionAsync<OneBotMessageEvent>("get_msg", new { message_id = messageId });
    }
    /// <summary>
    /// 获取合并转发消息详情。
    /// </summary>
    public static async Task<List<OneBotForwardMessage>?> GetForwardMessage(this OneBotClient client, string forwardId)
    {
        OneBotForwardData? data = await client.CallActionAsync<OneBotForwardData>("get_forward_msg", new { id = forwardId });
        return data?.Messages;
    }
    /// <summary>
    /// 获取群聊天记录（LLOneBot 扩展 API）。
    /// </summary>
    public static async Task<List<OneBotMessageEvent>?> GetGroupMsgHistory(this OneBotClient client, long groupId, long? messageSeq = null, int count = 20)
    {
        // message_seq 传 null 会被 NapCat 判成 schema 错误（retcode 1400 Schema compilation error: Expected string），
        // 想从最新一条开始拉就必须整个键都不出现；给了值时按数字传即可
        object parameters = messageSeq is null
            ? new { group_id = groupId, count }
            : new { group_id = groupId, message_seq = messageSeq.Value, count };
        OneBotGroupHistoryData? data = await client.CallActionAsync<OneBotGroupHistoryData>("get_group_msg_history", parameters);
        return data?.Messages;
    }
}
