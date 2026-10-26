using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using QQSuite.Core;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace QQSuite.GroupFileManager;

public class GroupFileManagerConfig
{
    // ---- 基础设置 ----
    [DisplayName("基础·协议端地址")]
    [Description("OneBot正向WebSocket地址（NapCat/LLOneBot等），默认 ws://127.0.0.1:3001")]
    public string OneBotWsUrl { get; set; } = "ws://127.0.0.1:3001";

    [DisplayName("基础·列表最大条数")]
    [Description("列出/搜索文件时最多显示的条目数量")]
    public int MaxFilesList { get; set; } = 20;

    [DisplayName("基础·调试模式")]
    [Description("开启后日志输出API调用、搜索、缓存等详细过程")]
    public bool DebugMode { get; set; } = false;

    [DisplayName("基础·协议端类型")]
    [Description("auto=自动探测（默认）/ napcat / llonebot / snowluma。影响移动文件接口的参数格式：NapCat 用 current/target_parent_directory，LLOneBot 与 SnowLuma 用 parent/target_directory")]
    public string ApiProvider { get; set; } = "auto";

    // ---- 下载设置 ----
    [DisplayName("下载·保存目录")]
    [Description("相对存储目录的相对路径（禁止..和盘符），默认 files，即 {存储目录}/files")]
    public string DownloadPath { get; set; } = "files";

    [DisplayName("下载·大小上限(MB)")]
    [Description("允许下载的最大文件大小，0为不限制")]
    public int MaxFileSizeMb { get; set; } = 100;

    [DisplayName("下载·扩展名白名单")]
    [Description("允许下载的扩展名，英文逗号分隔（如 pdf,jpg,zip），留空允许所有类型")]
    public string AllowedExtensions { get; set; } = "";

    [DisplayName("下载·超时(秒)")]
    [Description("下载超时时间")]
    public int DownloadTimeoutSeconds { get; set; } = 60;

    // ---- 权限设置 ----
    [DisplayName("权限·权限模式")]
    [Description("deny_list=黑名单（默认，所有群可用按需禁用）/ allow_list=白名单（仅允许名单内的群）")]
    public string PermissionMode { get; set; } = "deny_list";

    [DisplayName("权限·白名单群号")]
    [Description("白名单模式下允许使用群文件功能的群号，英文逗号分隔；留空等于全部禁用")]
    public string EnabledGroups { get; set; } = "";

    [DisplayName("权限·禁用创建文件夹的群")]
    [Description("群号英文逗号分隔，填 all 全局禁用，留空不限制")]
    public string DisabledCreateFolder { get; set; } = "";

    [DisplayName("权限·禁用删除文件夹的群")]
    [Description("群号英文逗号分隔，填 all 全局禁用，留空不限制")]
    public string DisabledDeleteFolder { get; set; } = "";

    [DisplayName("权限·禁用删除文件的群")]
    [Description("群号英文逗号分隔，填 all 全局禁用，留空不限制")]
    public string DisabledDeleteFile { get; set; } = "";

    [DisplayName("权限·禁用移动文件的群")]
    [Description("群号英文逗号分隔，填 all 全局禁用，留空不限制")]
    public string DisabledMoveFile { get; set; } = "";

    [DisplayName("权限·禁用重命名文件的群")]
    [Description("群号英文逗号分隔，填 all 全局禁用，留空不限制")]
    public string DisabledRenameFile { get; set; } = "";

    [DisplayName("权限·禁用重命名文件夹的群")]
    [Description("群号英文逗号分隔，填 all 全局禁用，留空不限制")]
    public string DisabledRenameFolder { get; set; } = "";

    [DisplayName("权限·禁用下载文件的群")]
    [Description("群号英文逗号分隔，填 all 全局禁用，留空不限制")]
    public string DisabledDownloadFile { get; set; } = "";

    [DisplayName("权限·禁用查看列表的群")]
    [Description("群号英文逗号分隔，填 all 全局禁用；同时控制搜索功能")]
    public string DisabledListFiles { get; set; } = "";
}

public class GroupFileManagerModule(
    XmlFunctionCaller functionCaller,
    ILogger<GroupFileManagerModule> logger,
    Interactor<GroupFileManagerModule> interactor,
    QChatService qChatService
) : ChatBehaviour, IConfigurable<GroupFileManagerConfig>
{
    public GroupFileManagerConfig Configuration { get; set; } = null!;

    // 单次向协议端拉取的文件数量（get_group_*files 的 file_count 参数）
    const int SearchFileCount = 200;

    // ---- 连接层：复用 QQ聊天 的 OneBot 连接 ----
    static readonly HttpClient sharedDownloadClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    // ---- 文件夹内容缓存（5秒有效）与下载任务 ----
    readonly Dictionary<string, (DateTime CachedTime, List<JsonElement> Files)> folderCache = new();
    readonly ConcurrentDictionary<string, DownloadTaskRecord> pendingDownloadTasks = new();
    string downloadDirectory = "";
    List<string> allowedExtensions = new();
    long maxFileSizeBytes;
    // 协议端类型："auto" / "napcat" / "llonebot" / "snowluma"，来自配置
    string apiProvider = "auto";
    // 移动接口实测生效的参数格式：""=未试出来，napcat / onebot（LLOneBot 与 SnowLuma 同格式）
    string moveParameterStyle = "";
    // get_version_info 识别出的协议端署名，只用于日志与错误提示
    static string providerName = "";

    // ============================================================
    // 生命周期
    // ============================================================

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "QQ群文件管理工具集：查看/搜索/重命名/创建删除文件夹/删除移动文件/异步下载群文件。",
            Explanation = "删除与移动等写操作需要Bot是群管理员。搜索结果带 file_id，可直接用于删除/移动/重命名。按文件名操作时同名文件多处存在会被拒绝并列出位置，防止误删。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        apiProvider = Configuration.ApiProvider.Trim().ToLowerInvariant();
        if (apiProvider != "napcat" && apiProvider != "llonebot" && apiProvider != "snowluma")
        {
            apiProvider = "auto";
        }

        // 下载目录：仅允许相对路径，挂在存储目录下防止越权
        string downloadPath = (Configuration.DownloadPath ?? "files").Trim().Trim('/', '\\');
        if (downloadPath.Length == 0 || downloadPath.Contains("..") || downloadPath.Contains(':'))
        {
            logger.LogWarning("DownloadPath 配置非法（仅支持相对路径），已回退为 files");
            downloadPath = "files";
        }
        downloadDirectory = Path.Combine(AlifePath.StorageFolderPath, downloadPath);
        Directory.CreateDirectory(downloadDirectory);

        // 扩展名白名单：去空格去点转小写，空列表允许全部
        allowedExtensions = Configuration.AllowedExtensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.Trim().TrimStart('.').ToLowerInvariant())
            .ToList();

        long maxFileSizeMb = Math.Max(0, Configuration.MaxFileSizeMb);
        maxFileSizeBytes = maxFileSizeMb * 1024 * 1024;

        if (Configuration.DebugMode)
        {
            logger.LogInformation("[文件管家] 🔧 调试模式已开启，将显示详细日志");
        }
        LogDebug("下载目录: " + downloadDirectory);
        if (Configuration.PermissionMode == "allow_list")
        {
            logger.LogInformation("[文件管家] 白名单模式，允许的群: {Groups}", Configuration.EnabledGroups);
        }
        StartProviderCheck();
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        providerName = "";
        logger.LogInformation("[文件管家] 👋 已卸载");
        return Task.CompletedTask;
    }

    // ============================================================
    // OneBot 连接层：直接复用 QQ聊天 的连接，不再自建 WebSocket
    // ============================================================

    async Task<string> CallActionAsync(string action, JsonObject parameters)
    {
        return await QQSuiteTransport.CallActionAsync(qChatService.Client, action, parameters, 30);
    }

    // 解析动作响应：失败抛出带协议端信息的异常
    static JsonElement ParseActionResponse(string responseText, string operationName)
    {
        using JsonDocument parsedResponse = JsonDocument.Parse(responseText);
        JsonElement root = parsedResponse.RootElement.Clone();
        string status = root.TryGetProperty("status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString() ?? ""
            : "";
        int retcode = root.TryGetProperty("retcode", out JsonElement retcodeElement) && retcodeElement.ValueKind == JsonValueKind.Number
            ? retcodeElement.GetInt32()
            : -1;
        bool succeeded = status == "ok" || status == "async" || retcode == 0;
        if (succeeded == false)
        {
            // SnowLuma 的失败包只填 wording（LLOneBot/NapCat 两个键都填同一个值），只读 message 在 SnowLuma 下永远是「未知错误」
            string detail = GetStringField(root, "message");
            if (detail.Length == 0)
            {
                detail = GetStringField(root, "wording");
            }
            if (detail.Length == 0)
            {
                detail = "未知错误";
            }
            // 1404 = 动作名在该协议端不存在（三端都有的接口不多，文件夹重命名只有 LLOneBot/SnowLuma 有）
            if (retcode == 1404)
            {
                detail += "（" + (providerName.Length > 0 ? providerName : "该协议端")
                    + " 未实现该接口，请检查协议端设置或更换支持的协议端）";
            }
            throw new Exception(operationName + "失败：" + detail);
        }
        return root.TryGetProperty("data", out JsonElement dataElement) ? dataElement.Clone() : JsonDocument.Parse("null").RootElement.Clone();
    }

    // ============================================================
    // 日志
    // ============================================================

    void LogDebug(string message)
    {
        if (Configuration.DebugMode)
        {
            logger.LogInformation("[文件管家] 🔍 {Message}", message);
        }
    }

    // ============================================================
    // 权限（黑白名单双模式）
    // ============================================================

    // 检查某群的某功能是否可用：先全局白名单，再单功能黑名单
    bool CheckFeaturePermission(long groupId, string feature)
    {
        string groupText = groupId.ToString();

        if (Configuration.PermissionMode == "allow_list")
        {
            List<string> allowedGroups = SplitList(Configuration.EnabledGroups);
            if (allowedGroups.Count == 0)
            {
                interactor.Poke("❌ 群文件插件当前为白名单模式且未配置允许的群号，所有群均已禁用，请联系管理员在插件配置中填写");
                return false;
            }
            if (allowedGroups.Contains(groupText) == false)
            {
                interactor.Poke("❌ 本群（" + groupText + "）不在群文件功能的允许名单中，无法使用该功能");
                return false;
            }
        }

        string disabledRaw = feature switch
        {
            "create_folder" => Configuration.DisabledCreateFolder,
            "delete_folder" => Configuration.DisabledDeleteFolder,
            "delete_file" => Configuration.DisabledDeleteFile,
            "move_file" => Configuration.DisabledMoveFile,
            "download_file" => Configuration.DisabledDownloadFile,
            "list_files" => Configuration.DisabledListFiles,
            "rename_file" => Configuration.DisabledRenameFile,
            "rename_folder" => Configuration.DisabledRenameFolder,
            _ => ""
        };
        List<string> disabledList = SplitList(disabledRaw);
        if (disabledList.Contains("all") || disabledList.Contains(groupText))
        {
            string featureName = feature switch
            {
                "create_folder" => "创建文件夹",
                "delete_folder" => "删除文件夹",
                "delete_file" => "删除文件",
                "move_file" => "移动文件",
                "download_file" => "下载文件",
                "list_files" => "查看文件列表",
                "rename_file" => "重命名文件",
                "rename_folder" => "重命名文件夹",
                _ => feature
            };
            interactor.Poke("❌ " + featureName + "功能已在当前群禁用");
            return false;
        }
        return true;
    }

    static List<string> SplitList(string raw)
    {
        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    // ============================================================
    // 群文件数据层
    // ============================================================

    // 获取根目录文件和文件夹列表
    async Task<(List<JsonElement> Files, List<JsonElement> Folders)> GetRootEntriesAsync(long groupId, int? fileCount = null)
    {
        List<JsonElement> files = new();
        List<JsonElement> folders = new();
        try
        {
            JsonObject parameters = new() { ["group_id"] = groupId };
            if (fileCount.HasValue)
            {
                parameters["file_count"] = fileCount.Value;
            }
            string response = await CallActionAsync("get_group_root_files", parameters);
            JsonElement data = ParseActionResponse(response, "获取群文件列表");
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("files", out JsonElement filesElement) && filesElement.ValueKind == JsonValueKind.Array)
                {
                    files = filesElement.EnumerateArray().Select(item => item.Clone()).ToList();
                }
                if (data.TryGetProperty("folders", out JsonElement foldersElement) && foldersElement.ValueKind == JsonValueKind.Array)
                {
                    folders = foldersElement.EnumerateArray().Select(item => item.Clone()).ToList();
                }
            }
            else if (data.ValueKind == JsonValueKind.Array)
            {
                files = data.EnumerateArray().Select(item => item.Clone()).ToList();
            }
            LogDebug("获取到 " + files.Count + " 个文件, " + folders.Count + " 个文件夹");
        }
        catch (Exception listError)
        {
            logger.LogError("获取群文件列表异常：{Message}", listError.Message);
        }
        return (files, folders);
    }

    // 根目录写法三端不一致：LLOneBot 实测 folder_id 传空串回空列表（不是根目录），SnowLuma 用
    // folder_id || folder || "/"，NapCat 用 folder ?? folder_id ?? ""。只有 "/" 是三端都认的根目录。
    static string NormalizeFolderParam(string? folderId)
    {
        string trimmed = (folderId ?? "").TrimStart('/');
        return trimmed.Length > 0 ? trimmed : "/";
    }

    // 获取文件夹内的文件，带5秒缓存与重试
    async Task<List<JsonElement>> GetFolderFilesAsync(long groupId, string folderId, int retryCount = 2, int? fileCount = null)
    {
        string cleanFolderId = NormalizeFolderParam(folderId);
        string cacheKey = groupId + "_" + cleanFolderId + "_" + (fileCount.HasValue ? fileCount.Value.ToString() : "default");
        if (folderCache.TryGetValue(cacheKey, out (DateTime CachedTime, List<JsonElement> Files) cached)
            && (DateTime.Now - cached.CachedTime).TotalSeconds < 5)
        {
            LogDebug("使用缓存: 文件夹 " + folderId + " 内有 " + cached.Files.Count + " 个文件");
            return cached.Files;
        }

        for (int attempt = 0; attempt <= retryCount; attempt++)
        {
            try
            {
                JsonObject parameters = new()
                {
                    ["group_id"] = groupId,
                    ["folder_id"] = cleanFolderId
                };
                if (fileCount.HasValue)
                {
                    parameters["file_count"] = fileCount.Value;
                }
                string response = await CallActionAsync("get_group_files_by_folder", parameters);
                JsonElement data = ParseActionResponse(response, "获取文件夹内容");
                List<JsonElement> files = new();
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("files", out JsonElement filesElement))
                {
                    files = filesElement.EnumerateArray().Select(item => item.Clone()).ToList();
                }
                else if (data.ValueKind == JsonValueKind.Array)
                {
                    files = data.EnumerateArray().Select(item => item.Clone()).ToList();
                }
                folderCache[cacheKey] = (DateTime.Now, files);
                LogDebug("文件夹 " + folderId + " 内有 " + files.Count + " 个文件");
                return files;
            }
            catch (Exception folderError)
            {
                LogDebug("获取文件夹内容失败(尝试 " + (attempt + 1) + "/" + (retryCount + 1) + ")：" + folderError.Message);
                if (attempt < retryCount)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)));
                }
            }
        }
        return new List<JsonElement>();
    }

    void ClearCache(long groupId)
    {
        List<string> keysToRemove = folderCache.Keys
            .Where(key => key.StartsWith(groupId + "_", StringComparison.Ordinal))
            .ToList();
        foreach (string key in keysToRemove)
        {
            folderCache.Remove(key);
        }
        LogDebug("已清除群 " + groupId + " 的缓存");
    }

    static string GetEntryName(JsonElement entry, string fallback = "")
    {
        string name = GetStringField(entry, "file_name");
        if (name.Length == 0)
        {
            name = GetStringField(entry, "name");
        }
        if (name.Length == 0 && fallback.Length > 0)
        {
            name = fallback;
        }
        return name;
    }

    static string GetEntryFolderId(JsonElement folder)
    {
        string folderId = GetStringField(folder, "folder_id");
        if (folderId.Length == 0)
        {
            folderId = GetStringField(folder, "id");
        }
        if (folderId.Length == 0)
        {
            folderId = GetStringField(folder, "folder");
        }
        return folderId;
    }

    async Task<string> GetFolderIdByNameAsync(long groupId, string folderName)
    {
        (List<JsonElement> Files, List<JsonElement> Folders) root = await GetRootEntriesAsync(groupId);
        foreach (JsonElement folder in root.Folders)
        {
            string name = GetEntryName(folder);
            if (name == folderName)
            {
                return GetEntryFolderId(folder);
            }
        }
        return "";
    }

    async Task<string> GetFolderNameByIdAsync(long groupId, string folderId)
    {
        string target = (folderId ?? "").TrimStart('/');
        (List<JsonElement> Files, List<JsonElement> Folders) root = await GetRootEntriesAsync(groupId);
        foreach (JsonElement folder in root.Folders)
        {
            string candidate = GetEntryFolderId(folder);
            if (candidate == folderId || candidate.TrimStart('/') == target)
            {
                return GetEntryName(folder);
            }
        }
        return "";
    }

    // 文件条目：带位置信息的文件快照（供搜索/删除/重命名/移动使用）
    sealed class FileEntry
    {
        public JsonElement Source { get; set; }
        public string FileName { get; set; } = "";
        public string FileId { get; set; } = "";
        public string FileUuid { get; set; } = "";
        public long Busid { get; set; }
        public long Size { get; set; }
        public string Location { get; set; } = "";
        public string LocationFolderId { get; set; } = "";
    }

    static FileEntry ToFileEntry(JsonElement file, string location, string locationFolderId)
    {
        FileEntry entry = new()
        {
            Source = file.Clone(),
            FileName = GetStringField(file, "file_name").Length > 0 ? GetStringField(file, "file_name") : GetStringField(file, "name"),
            FileId = GetStringField(file, "file_id").Length > 0 ? GetStringField(file, "file_id") : GetStringField(file, "id"),
            FileUuid = GetStringField(file, "file_uuid").Length > 0 ? GetStringField(file, "file_uuid") : GetStringField(file, "file_id"),
            Busid = GetNumericField(file, "busid"),
            Size = GetNumericField(file, "file_size") > 0 ? GetNumericField(file, "file_size") : GetNumericField(file, "size"),
            Location = location,
            LocationFolderId = locationFolderId
        };
        return entry;
    }

    // 在指定文件夹（null=根目录）内按文件名精确查找一个文件
    async Task<FileEntry?> FindFileInFolderAsync(long groupId, string fileName, string? folderId)
    {
        List<JsonElement> files;
        if (string.IsNullOrEmpty(folderId) == false)
        {
            files = await GetFolderFilesAsync(groupId, folderId);
        }
        else
        {
            (List<JsonElement> Files, List<JsonElement> Folders) root = await GetRootEntriesAsync(groupId);
            files = root.Files;
            folderId = "/";
        }
        foreach (JsonElement file in files)
        {
            if (GetEntryName(file) == fileName)
            {
                FileEntry entry = ToFileEntry(file, "根目录", folderId ?? "/");
                if (folderId != "/" && string.IsNullOrEmpty(folderId) == false)
                {
                    string folderName = await GetFolderNameByIdAsync(groupId, folderId);
                    entry.Location = folderName.Length > 0 ? folderName : folderId;
                }
                LogDebug("找到匹配文件: " + fileName);
                return entry;
            }
        }
        return null;
    }

    // 在根目录及所有一级文件夹中按文件名查找（找到即返回）
    async Task<FileEntry?> FindFileInAllFoldersAsync(long groupId, string fileName)
    {
        LogDebug("开始搜索文件: " + fileName);
        FileEntry? rootMatch = await FindFileInFolderAsync(groupId, fileName, null);
        if (rootMatch != null)
        {
            return rootMatch;
        }
        (List<JsonElement> Files, List<JsonElement> Folders) root = await GetRootEntriesAsync(groupId);
        foreach (JsonElement folder in root.Folders)
        {
            string folderId = GetEntryFolderId(folder);
            if (folderId.Length == 0)
            {
                continue;
            }
            string folderName = GetEntryName(folder, "未知文件夹");
            FileEntry? match = await FindFileInFolderAsync(groupId, fileName, folderId);
            if (match != null)
            {
                match.Location = folderName;
                match.LocationFolderId = folderId;
                return match;
            }
        }
        LogDebug("未找到文件: " + fileName);
        return null;
    }

    // 查找群内所有与文件名完全一致的文件（根目录+一级文件夹），用于删除/重命名的同名歧义校验
    async Task<List<FileEntry>> FindFileMatchesAllAsync(long groupId, string fileName)
    {
        List<FileEntry> matches = new();
        (List<JsonElement> Files, List<JsonElement> Folders) root = await GetRootEntriesAsync(groupId);
        foreach (JsonElement file in root.Files)
        {
            if (GetEntryName(file) == fileName)
            {
                matches.Add(ToFileEntry(file, "根目录", "/"));
            }
        }
        foreach (JsonElement folder in root.Folders)
        {
            string folderId = GetEntryFolderId(folder);
            if (folderId.Length == 0)
            {
                continue;
            }
            string folderName = GetEntryName(folder, folderId);
            List<JsonElement> folderFiles = await GetFolderFilesAsync(groupId, folderId);
            foreach (JsonElement file in folderFiles)
            {
                if (GetEntryName(file) == fileName)
                {
                    matches.Add(ToFileEntry(file, folderName, folderId));
                }
            }
        }
        LogDebug("精确查找「" + fileName + "」: 命中 " + matches.Count + " 处");
        return matches;
    }

    // 收集群文件并附带位置信息：folderId为null=根目录+全部一级文件夹；"/"=仅根目录；其他=该文件夹
    async Task<(List<FileEntry> Files, string Scope)> CollectGroupFilesAsync(long groupId, string? folderId)
    {
        // 必须逐个顺序请求：NapCat/LLOneBot 对群文件接口的瞬时并发请求会失败/限流
        if (folderId == "/")
        {
            (List<JsonElement> Files, List<JsonElement> Folders) rootOnly = await GetRootEntriesAsync(groupId, SearchFileCount);
            List<FileEntry> rootEntries = rootOnly.Files
                .Select(file => ToFileEntry(file, "根目录", "/"))
                .ToList();
            return (rootEntries, "根目录");
        }

        if (string.IsNullOrEmpty(folderId) == false)
        {
            string folderName = await GetFolderNameByIdAsync(groupId, folderId);
            string scope = folderName.Length > 0 ? folderName : folderId;
            List<JsonElement> scopedFiles = await GetFolderFilesAsync(groupId, folderId, fileCount: SearchFileCount);
            List<FileEntry> scopedEntries = scopedFiles
                .Select(file => ToFileEntry(file, scope, folderId))
                .ToList();
            return (scopedEntries, "文件夹「" + scope + "」");
        }

        (List<JsonElement> Files, List<JsonElement> Folders) root = await GetRootEntriesAsync(groupId, SearchFileCount);
        List<FileEntry> collected = root.Files
            .Select(file => ToFileEntry(file, "根目录", "/"))
            .ToList();
        int folderIndex = 0;
        foreach (JsonElement folder in root.Folders)
        {
            folderIndex++;
            string currentFolderId = GetEntryFolderId(folder);
            if (currentFolderId.Length == 0)
            {
                LogDebug("跳过第 " + folderIndex + " 个文件夹：未能解析文件夹ID");
                continue;
            }
            string currentFolderName = GetEntryName(folder, currentFolderId);
            List<JsonElement> folderFiles = await GetFolderFilesAsync(groupId, currentFolderId, fileCount: SearchFileCount);
            if (folderFiles.Count == 0 && GetNumericField(folder, "total_file_count") > 0)
            {
                // 文件夹声称有文件却拉取为空，通常是请求失败/被限流而非真空文件夹
                logger.LogError("文件夹 {Name}（ID: {Id}）应有 {Count} 个文件，但拉取结果为空，可能被限流",
                    currentFolderName, currentFolderId, GetNumericField(folder, "total_file_count"));
            }
            collected.AddRange(folderFiles.Select(file => ToFileEntry(file, currentFolderName, currentFolderId)));
            LogDebug("遍历文件夹 " + folderIndex + "/" + root.Folders.Count + "：" + currentFolderName + " -> " + folderFiles.Count + " 个文件");
        }
        LogDebug("全量收集完成：共 " + collected.Count + " 个文件");
        return (collected, "根目录及全部文件夹");
    }

    // 按文件名关键词模糊匹配并排序：完全相同优先，其次关键词出现位置，再次字典序
    static List<FileEntry> MatchFilesByKeyword(List<FileEntry> files, string keyword)
    {
        string lowerKeyword = keyword.Trim().ToLowerInvariant();
        List<(int ExactFlag, int Position, string Name, FileEntry Entry)> scored = new();
        foreach (FileEntry file in files)
        {
            if (file.FileName.Length == 0)
            {
                continue;
            }
            string lowerName = file.FileName.ToLowerInvariant();
            int position = lowerName.IndexOf(lowerKeyword, StringComparison.Ordinal);
            if (position != -1)
            {
                scored.Add((lowerName == lowerKeyword ? 0 : 1, position, lowerName, file));
            }
        }
        return scored
            .OrderBy(item => item.ExactFlag)
            .ThenBy(item => item.Position)
            .ThenBy(item => item.Name)
            .Select(item => item.Entry)
            .ToList();
    }

    // ============================================================
    // 协议端自适应（NapCat / LLOneBot / SnowLuma 参数格式差异）
    // ============================================================

    // 启动后识别一次协议端：三端都回 app_name，go-cqhttp 系才回 name/vendor，两种都取
    void StartProviderCheck()
    {
        Task.Run(async () =>
        {
            try
            {
                // 共享连接可能比本模块晚就绪，等 QQ聊天 连上再识别（最多两分钟）
                for (int attempt = 0; attempt < 24 && qChatService.Client is not { IsConnected: true }; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), DestroyCancellationToken);
                }
                string response = await CallActionAsync("get_version_info", new JsonObject());
                JsonElement data = ParseActionResponse(response, "获取协议端版本");
                providerName = (GetStringField(data, "app_name") + " " + GetStringField(data, "name")
                    + " " + GetStringField(data, "vendor")).Trim();
                logger.LogInformation("[文件管家] 协议端：{Provider} {Version}",
                    providerName.Length > 0 ? providerName : "未识别", GetStringField(data, "app_version"));
                if (apiProvider == "auto")
                {
                    apiProvider = AnchorProviderOf(providerName);
                }
            }
            catch (Exception versionError)
            {
                logger.LogInformation("[文件管家] 未能识别协议端：{Message}", versionError.Message);
            }
        }, DestroyCancellationToken);
    }

    static string AnchorProviderOf(string identifiedName)
    {
        string lower = identifiedName.ToLowerInvariant();
        if (lower.Contains("napcat"))
        {
            return "napcat";
        }
        if (lower.Contains("llonebot"))
        {
            return "llonebot";
        }
        if (lower.Contains("snowluma"))
        {
            return "snowluma";
        }
        return "auto";
    }

    // NapCat 的 file_id 是它自己内存里编码的临时标识（约24小时过期、NapCat 重启即失效），
    // 拿旧 id 操作时它抛 'real fileUUID not found!'，不解释一句看起来像插件坏了
    static string ExplainFileIdFailure(string message)
    {
        if (message.Contains("fileUUID", StringComparison.OrdinalIgnoreCase) == false)
        {
            return message;
        }
        return message + "（NapCat 的 file_id 是协议端内存里的临时标识，重新搜索文件拿最新 id 再试）";
    }

    // 移动文件：NapCat 用 current/target_parent_directory，LLOneBot 与 SnowLuma 用 parent/target_directory。
    // 认得出协议端就只发对的那一种，认不出时两种依次试并把试出来的格式记下来。
    async Task<(bool Success, string Message)> MoveFileAsync(
        long groupId, string fileUuid, string currentFolderId, string targetFolderId, string fileName)
    {
        string cleanCurrent = NormalizeFolderParam(currentFolderId);
        string cleanTarget = NormalizeFolderParam(targetFolderId);

        // 已经试出来的格式不再浪费一次失败；否则按协议端排首选，两种都留一次机会
        // （配置填错端时也能靠回退成功，不至于把移动功能锁死）
        List<string> styles = moveParameterStyle.Length > 0
            ? new List<string> { moveParameterStyle }
            : apiProvider == "napcat"
                ? new List<string> { "napcat", "onebot" }
                : apiProvider is "llonebot" or "snowluma"
                    ? new List<string> { "onebot", "napcat" }
                    : new List<string> { "napcat", "onebot" };

        string lastMessage = "";
        foreach (string style in styles)
        {
            JsonObject parameters = style == "onebot"
                ? new JsonObject
                {
                    ["group_id"] = groupId,
                    ["file_id"] = fileUuid,
                    ["parent_directory"] = cleanCurrent,
                    ["target_directory"] = cleanTarget
                }
                : new JsonObject
                {
                    ["group_id"] = groupId,
                    ["file_id"] = fileUuid,
                    ["current_parent_directory"] = cleanCurrent,
                    ["target_parent_directory"] = cleanTarget
                };
            try
            {
                LogDebug("尝试 " + style + " 格式移动文件: " + fileName);
                string response = await CallActionAsync("move_group_file", parameters);
                ParseActionResponse(response, "移动文件");
                if (moveParameterStyle != style)
                {
                    moveParameterStyle = style;
                    logger.LogInformation("[文件管家] 移动接口参数格式：{Style}", style);
                }
                return (true, "");
            }
            catch (Exception moveError)
            {
                lastMessage = moveError.Message;
            }
        }
        return (false, ExplainFileIdFailure(lastMessage));
    }

    // ============================================================
    // AI 函数
    // ============================================================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取QQ群根目录的文件和文件夹列表")]
    public async Task ListGroupFiles(
        [Description("QQ群号")] long groupId)
    {
        if (CheckFeaturePermission(groupId, "list_files") == false)
        {
            return;
        }
        (List<JsonElement> Files, List<JsonElement> Folders) root = await GetRootEntriesAsync(groupId);
        interactor.Poke(FormatFileList(root.Files, root.Folders, Configuration.MaxFilesList));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看QQ群指定文件夹内的文件列表，folderId与folderName二选一")]
    public async Task ListFolderFiles(
        [Description("QQ群号")] long groupId,
        [Description("文件夹ID")] string? folderId = null,
        [Description("文件夹名称（与folderId二选一）")] string? folderName = null)
    {
        if (CheckFeaturePermission(groupId, "list_files") == false)
        {
            return;
        }
        string resolvedFolderId = await ResolveFolderIdAsync(groupId, folderId, folderName);
        if (resolvedFolderId.Length == 0)
        {
            interactor.Poke("❌ 请提供文件夹ID或文件夹名称（未找到同名文件夹）");
            return;
        }
        List<JsonElement> files = await GetFolderFilesAsync(groupId, resolvedFolderId);
        if (files.Count == 0)
        {
            interactor.Poke("📂 文件夹内没有文件");
            return;
        }
        StringBuilder builder = new();
        builder.Append("📂 文件夹内的文件（").Append(files.Count).Append("个）：\n");
        int index = 0;
        foreach (JsonElement file in files.Take(Configuration.MaxFilesList))
        {
            index++;
            builder.Append('\n').Append(index).Append(". ").Append(GetEntryName(file, "未知文件"))
                .Append("\n   💾 ").Append(FormatFileSize(GetNumericField(file, "file_size")))
                .Append(" | 👤 ").AppendLine(GetStringField(file, "uploader_name").Length > 0 ? GetStringField(file, "uploader_name") : "未知用户");
        }
        interactor.Poke(builder.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按文件名关键词搜索QQ群文件（不区分大小写模糊匹配）。默认搜索根目录及所有文件夹，folderId或folderName可限定范围（folderName填'根目录'仅搜根目录）。结果带file_id可直接删除/移动")]
    public async Task SearchGroupFiles(
        [Description("QQ群号")] long groupId,
        [Description("文件名关键词，支持部分匹配")] string keyword,
        [Description("可选，限定文件夹ID，'/'表示仅根目录")] string? folderId = null,
        [Description("可选，限定文件夹名称（与folderId二选一）")] string? folderName = null)
    {
        if (CheckFeaturePermission(groupId, "list_files") == false)
        {
            return;
        }
        string trimmedKeyword = (keyword ?? "").Trim();
        if (trimmedKeyword.Length == 0)
        {
            interactor.Poke("❌ 搜索关键词不能为空");
            return;
        }
        if (trimmedKeyword.Length > 100)
        {
            interactor.Poke("❌ 搜索关键词过长（最多100个字符）");
            return;
        }
        string? targetFolderId = await ResolveOptionalFolderScopeAsync(groupId, folderId, folderName);
        if (targetFolderId == null)
        {
            interactor.Poke("❌ 未找到文件夹: " + folderName);
            return;
        }
        (List<FileEntry> Files, string Scope) collected = await CollectGroupFilesAsync(groupId, targetFolderId);
        List<FileEntry> matched = MatchFilesByKeyword(collected.Files, trimmedKeyword);
        LogDebug("搜索完成: 扫描 " + collected.Files.Count + " 个文件，匹配 " + matched.Count + " 个");
        interactor.Poke(FormatSearchResults(trimmedKeyword, collected.Scope, matched, Configuration.MaxFilesList));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("在QQ群中创建文件夹")]
    public async Task CreateFolder(
        [Description("QQ群号")] long groupId,
        [Description("要创建的文件夹名称")] string folderName)
    {
        if (CheckFeaturePermission(groupId, "create_folder") == false)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(folderName))
        {
            interactor.Poke("❌ 文件夹名称不能为空");
            return;
        }
        if (folderName.Length > 50)
        {
            interactor.Poke("❌ 文件夹名称过长");
            return;
        }
        string existingId = await GetFolderIdByNameAsync(groupId, folderName);
        if (existingId.Length > 0)
        {
            interactor.Poke("📁 文件夹 '" + folderName + "' 已存在");
            return;
        }
        // 同时传 folder_name 和 name，兼容 NapCat 与 LLOneBot
        JsonObject parameters = new()
        {
            ["group_id"] = groupId,
            ["folder_name"] = folderName,
            ["name"] = folderName
        };
        string response = await CallActionAsync("create_group_file_folder", parameters);
        JsonElement data = ParseActionResponse(response, "创建文件夹");
        string createdId = GetStringField(data, "folder_id").Length > 0 ? GetStringField(data, "folder_id") : GetStringField(data, "id");
        ClearCache(groupId);
        LogDebug("创建文件夹成功: " + folderName + ", ID: " + createdId);
        interactor.Poke("✅ 成功创建文件夹: " + folderName);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("删除QQ群中的空文件夹，folderId与folderName二选一")]
    public async Task DeleteFolder(
        [Description("QQ群号")] long groupId,
        [Description("要删除的文件夹名称")] string? folderName = null,
        [Description("文件夹ID（与folderName二选一）")] string? folderId = null)
    {
        if (CheckFeaturePermission(groupId, "delete_folder") == false)
        {
            return;
        }
        string resolvedFolderId = await ResolveFolderIdAsync(groupId, folderId, folderName);
        if (resolvedFolderId.Length == 0)
        {
            interactor.Poke("❌ 请提供文件夹名称或文件夹ID（未找到同名文件夹）");
            return;
        }
        List<JsonElement> remainingFiles = await GetFolderFilesAsync(groupId, resolvedFolderId);
        if (remainingFiles.Count > 0)
        {
            interactor.Poke("❌ 文件夹不为空，请先删除文件夹内的 " + remainingFiles.Count + " 个文件");
            return;
        }
        JsonObject parameters = new()
        {
            ["group_id"] = groupId,
            ["folder_id"] = resolvedFolderId
        };
        string response = await CallActionAsync("delete_group_folder", parameters);
        ParseActionResponse(response, "删除文件夹");
        ClearCache(groupId);
        LogDebug("删除文件夹成功: " + resolvedFolderId);
        interactor.Poke("✅ 成功删除文件夹: " + (folderName ?? resolvedFolderId));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("删除QQ群文件（支持批量）。fileIds精确删除（多个用英文逗号分隔）；fileNames按名称删除（多个用英文逗号分隔，名称必须在群内唯一，同名多处会拒绝并列出位置）")]
    public async Task DeleteGroupFiles(
        [Description("QQ群号")] long groupId,
        [Description("要删除的文件ID列表，英文逗号分隔")] string? fileIds = null,
        [Description("要删除的文件名列表，英文逗号分隔（名称须唯一）")] string? fileNames = null)
    {
        if (CheckFeaturePermission(groupId, "delete_file") == false)
        {
            return;
        }
        List<string> idList = SplitList(fileIds ?? "");
        List<string> nameList = SplitList(fileNames ?? "");
        if (idList.Count == 0 && nameList.Count == 0)
        {
            interactor.Poke("❌ 请提供要删除的文件名或文件ID");
            return;
        }

        // fileIds 精确删除（去重）
        List<string> deleteIds = new();
        List<string> deleteNames = new();
        foreach (string id in idList)
        {
            if (deleteIds.Contains(id) == false)
            {
                deleteIds.Add(id);
            }
        }

        // fileNames 先解析再删：同名多处拒绝执行，防止连坐误删
        List<string> missingNames = new();
        foreach (string name in nameList)
        {
            List<FileEntry> matches = await FindFileMatchesAllAsync(groupId, name);
            if (matches.Count == 0)
            {
                missingNames.Add(name);
                continue;
            }
            if (matches.Count > 1)
            {
                StringBuilder ambiguity = new();
                ambiguity.Append("❌ 群内存在 ").Append(matches.Count).Append(" 个同名文件「").Append(name)
                    .Append("」，为避免误删已取消本次删除，请改用 fileIds 精确指定：");
                foreach (FileEntry match in matches)
                {
                    ambiguity.Append("\n  - 🆔 ").Append(match.FileId).Append("（位于 ").Append(match.Location).Append("）");
                }
                interactor.Poke(ambiguity.ToString());
                return;
            }
            FileEntry resolved = matches[0];
            if (deleteIds.Contains(resolved.FileId))
            {
                continue;
            }
            deleteIds.Add(resolved.FileId);
            deleteNames.Add(name + "（" + resolved.Location + "）");
        }

        if (missingNames.Count > 0)
        {
            interactor.Poke("❌ 未找到文件: " + string.Join("、", missingNames));
            return;
        }
        if (deleteIds.Count == 0)
        {
            interactor.Poke("❌ 未找到要删除的文件");
            return;
        }

        int successCount = 0;
        List<string> failures = new();
        foreach (string fileId in deleteIds)
        {
            try
            {
                JsonObject parameters = new()
                {
                    ["group_id"] = groupId,
                    ["file_id"] = fileId
                };
                string response = await CallActionAsync("delete_group_file", parameters);
                ParseActionResponse(response, "删除文件");
                successCount++;
            }
            catch (Exception deleteError)
            {
                failures.Add(fileId + "：" + ExplainFileIdFailure(deleteError.Message));
            }
        }
        ClearCache(groupId);

        if (failures.Count == 0)
        {
            if (deleteIds.Count == 1)
            {
                string name = deleteNames.Count > 0 ? deleteNames[0] : deleteIds[0];
                interactor.Poke("✅ 成功删除文件: " + name);
            }
            else
            {
                interactor.Poke("✅ 成功删除 " + successCount + " 个文件");
            }
            return;
        }
        // 删除失败必须带原因，否则用户只能看到"删除失败"无从下手
        StringBuilder result = new();
        if (successCount > 0)
        {
            result.Append("⚠️ 成功删除 ").Append(successCount).Append(" 个文件，")
                .Append(failures.Count).Append(" 个失败：\n");
        }
        else
        {
            result.Append("❌ 删除失败：\n");
        }
        result.Append(string.Join("\n", failures.Take(3)));
        interactor.Poke(result.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("移动QQ群文件到指定文件夹，folderName填'根目录'或'/'表示移动到根目录")]
    public async Task MoveGroupFile(
        [Description("QQ群号")] long groupId,
        [Description("要移动的文件名")] string fileName,
        [Description("目标文件夹名称，'根目录'或'/'表示根目录")] string folderName)
    {
        if (CheckFeaturePermission(groupId, "move_file") == false)
        {
            return;
        }
        string targetFolderId = "/";
        if (folderName != "根目录" && folderName != "/" && folderName.Length > 0)
        {
            targetFolderId = await GetFolderIdByNameAsync(groupId, folderName);
            if (targetFolderId.Length == 0)
            {
                interactor.Poke("❌ 未找到文件夹: " + folderName);
                return;
            }
        }
        FileEntry? fileEntry = await FindFileInAllFoldersAsync(groupId, fileName);
        if (fileEntry == null || fileEntry.FileUuid.Length == 0)
        {
            interactor.Poke("❌ 未找到文件: " + fileName);
            return;
        }
        (bool Success, string Message) moveResult = await MoveFileAsync(
            groupId, fileEntry.FileUuid, fileEntry.LocationFolderId, targetFolderId, fileName);
        if (moveResult.Success)
        {
            ClearCache(groupId);
            string destination = targetFolderId == "/" ? "根目录" : "文件夹 '" + folderName + "'";
            interactor.Poke("✅ 文件 '" + fileName + "' 已移动到" + destination);
            return;
        }
        interactor.Poke("❌ " + moveResult.Message);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("重命名QQ群文件。优先用fileId；按fileName定位时要求群内唯一。NapCat、LLOneBot、SnowLuma 三端均支持文件重命名")]
    public async Task RenameGroupFile(
        [Description("QQ群号")] long groupId,
        [Description("新文件名（保留扩展名）")] string newName,
        [Description("文件ID（来自列表/搜索结果，优先使用）")] string? fileId = null,
        [Description("要重命名的文件名（与fileId二选一，须群内唯一）")] string? fileName = null)
    {
        if (CheckFeaturePermission(groupId, "rename_file") == false)
        {
            return;
        }
        string trimmedName = (newName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            interactor.Poke("❌ 新文件名不能为空");
            return;
        }
        if (trimmedName.Contains('/') || trimmedName.Contains('\\'))
        {
            interactor.Poke("❌ 新文件名不能包含路径分隔符");
            return;
        }
        string targetFileId;
        string parentFolderId;
        string oldName;
        if (string.IsNullOrEmpty(fileId) == false)
        {
            // 按ID定位：扫描全群找到该ID实际所在文件夹，作为重命名所需父目录
            (List<FileEntry> Files, string Scope) collected = await CollectGroupFilesAsync(groupId, null);
            FileEntry? located = collected.Files.FirstOrDefault(entry => entry.FileId == fileId);
            if (located != null)
            {
                targetFileId = located.FileId;
                parentFolderId = located.LocationFolderId.Length > 0 ? located.LocationFolderId : "/";
                oldName = located.FileName.Length > 0 ? located.FileName : fileId;
            }
            else
            {
                // 未扫到该ID（可能在更深层目录），按根目录重试
                targetFileId = fileId;
                parentFolderId = "/";
                oldName = fileId;
            }
        }
        else if (string.IsNullOrEmpty(fileName) == false)
        {
            List<FileEntry> matches = await FindFileMatchesAllAsync(groupId, fileName);
            if (matches.Count == 0)
            {
                interactor.Poke("❌ 未找到文件: " + fileName);
                return;
            }
            if (matches.Count > 1)
            {
                StringBuilder ambiguity = new();
                ambiguity.Append("❌ 群内存在 ").Append(matches.Count).Append(" 个同名文件「").Append(fileName)
                    .Append("」，请用 fileId 精确指定：");
                foreach (FileEntry match in matches)
                {
                    ambiguity.Append("\n  - 🆔 ").Append(match.FileId).Append("（位于 ").Append(match.Location).Append("）");
                }
                interactor.Poke(ambiguity.ToString());
                return;
            }
            targetFileId = matches[0].FileId;
            parentFolderId = matches[0].LocationFolderId;
            oldName = fileName;
        }
        else
        {
            interactor.Poke("❌ 请提供 fileId 或 fileName");
            return;
        }

        string cleanDirectory = NormalizeFolderParam(parentFolderId);
        // 三端一致：rename_group_file 收 file_id + current_parent_directory + new_name
        JsonObject parameters = new()
        {
            ["group_id"] = groupId,
            ["file_id"] = targetFileId,
            ["current_parent_directory"] = cleanDirectory,
            ["new_name"] = trimmedName
        };
        try
        {
            string response = await CallActionAsync("rename_group_file", parameters);
            ParseActionResponse(response, "重命名文件");
        }
        catch (Exception renameError)
        {
            interactor.Poke("❌ 重命名失败: " + ExplainFileIdFailure(renameError.Message));
            return;
        }
        ClearCache(groupId);
        interactor.Poke("✅ 已将文件 " + oldName + " 重命名为 " + trimmedName);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("重命名QQ群文件夹。优先用folderId，也可传folderName定位。注意文件夹重命名只有 LLOneBot/SnowLuma 支持，NapCat 未实现该接口")]
    public async Task RenameGroupFolder(
        [Description("QQ群号")] long groupId,
        [Description("新文件夹名")] string newFolderName,
        [Description("文件夹ID（优先使用）")] string? folderId = null,
        [Description("要重命名的文件夹名称（与folderId二选一）")] string? folderName = null)
    {
        if (CheckFeaturePermission(groupId, "rename_folder") == false)
        {
            return;
        }
        string trimmedName = (newFolderName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            interactor.Poke("❌ 新文件夹名不能为空");
            return;
        }
        if (trimmedName.Contains('/') || trimmedName.Contains('\\'))
        {
            interactor.Poke("❌ 新文件夹名不能包含路径分隔符");
            return;
        }
        string targetId;
        string oldName;
        if (string.IsNullOrEmpty(folderId) == false)
        {
            targetId = folderId;
            oldName = await GetFolderNameByIdAsync(groupId, folderId);
            if (oldName.Length == 0)
            {
                oldName = folderId;
            }
        }
        else if (string.IsNullOrEmpty(folderName) == false)
        {
            targetId = await GetFolderIdByNameAsync(groupId, folderName);
            if (targetId.Length == 0)
            {
                interactor.Poke("❌ 未找到文件夹: " + folderName);
                return;
            }
            oldName = folderName;
        }
        else
        {
            interactor.Poke("❌ 请提供 folderId 或 folderName");
            return;
        }

        // LLOneBot 与 SnowLuma 的动作名都是 rename_group_file_folder（NapCat 没有这个动作，会回 1404）
        JsonObject parameters = new()
        {
            ["group_id"] = groupId,
            ["folder_id"] = targetId,
            ["new_folder_name"] = trimmedName
        };
        try
        {
            string response = await CallActionAsync("rename_group_file_folder", parameters);
            ParseActionResponse(response, "重命名文件夹");
        }
        catch (Exception renameError)
        {
            interactor.Poke("❌ 重命名失败: " + renameError.Message);
            return;
        }
        ClearCache(groupId);
        interactor.Poke("✅ 已将文件夹 " + oldName + " 重命名为 " + trimmedName);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("下载QQ群文件到本地（异步），完成后会主动通知。受扩展名白名单与大小上限约束")]
    public async Task DownloadGroupFile(
        [Description("QQ群号")] long groupId,
        [Description("要下载的文件名")] string fileName)
    {
        if (CheckFeaturePermission(groupId, "download_file") == false)
        {
            return;
        }
        FileEntry? fileEntry = await FindFileInAllFoldersAsync(groupId, fileName);
        if (fileEntry == null || fileEntry.FileId.Length == 0)
        {
            interactor.Poke("❌ 未找到文件: " + fileName);
            return;
        }
        string rejection = CheckDownloadAllowed(fileName, fileEntry.Size);
        if (rejection.Length > 0)
        {
            interactor.Poke(rejection);
            return;
        }
        string taskId = groupId + "_" + fileName + "_" + DateTimeOffset.Now.ToUnixTimeSeconds();
        DownloadTaskRecord record = new() { Status = "pending", FileName = fileName };
        pendingDownloadTasks[taskId] = record;
        _ = Task.Run(() => RunDownloadTaskAsync(taskId, groupId, fileName, fileEntry.FileId, fileEntry.Busid), DestroyCancellationToken);
        interactor.Poke("✅ 已开始下载: " + fileName + "\n📋 任务ID: " + taskId + "\n⏳ 下载完成后我会通知你~");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("检查下载任务状态，taskId来自DownloadGroupFile的返回")]
    public async Task CheckDownloadTask(
        [Description("下载任务ID")] string taskId)
    {
        if (pendingDownloadTasks.TryGetValue(taskId, out DownloadTaskRecord? record) == false)
        {
            interactor.Poke("❌ 未找到任务ID: " + taskId);
            return;
        }
        if (record.Status == "pending")
        {
            interactor.Poke("⏳ 下载中，请稍后再问我吧~");
            return;
        }
        if (record.Status == "success")
        {
            pendingDownloadTasks.TryRemove(taskId, out _);
            interactor.Poke("✅ 下载完成！📄 " + record.FileName + "\n💾 " + FormatFileSize(record.FileSize));
            return;
        }
        pendingDownloadTasks.TryRemove(taskId, out _);
        interactor.Poke("❌ 下载失败: " + record.ErrorMessage);
    }

    // ============================================================
    // 下载实现
    // ============================================================

    async Task RunDownloadTaskAsync(string taskId, long groupId, string fileName, string fileId, long busid)
    {
        DownloadTaskRecord record = pendingDownloadTasks[taskId];
        try
        {
            JsonObject urlParameters = new()
            {
                ["group_id"] = groupId,
                ["file_id"] = fileId,
                // busid 是文件条目自带的业务号（同群里 102/104 都出现过），拿不到时按各端默认的 102
                ["busid"] = busid > 0 ? busid : 102
            };
            string urlResponse = await CallActionAsync("get_group_file_url", urlParameters);
            JsonElement urlData = ParseActionResponse(urlResponse, "获取下载链接");
            string downloadUrl = GetStringField(urlData, "url");
            if (downloadUrl.Length == 0)
            {
                record.Status = "failed";
                record.ErrorMessage = "未获取到下载链接";
                return;
            }

            string safeName = SanitizeFileName(fileName);
            string savePath = Path.Combine(downloadDirectory, safeName);
            if (File.Exists(savePath))
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string extension = Path.GetExtension(safeName);
                savePath = Path.Combine(downloadDirectory, Path.GetFileNameWithoutExtension(safeName) + "_" + timestamp + extension);
            }

            LogDebug("开始下载文件: " + fileName + " -> " + savePath);
            using CancellationTokenSource downloadTimeout = new(TimeSpan.FromSeconds(Math.Max(1, Configuration.DownloadTimeoutSeconds)));
            using HttpResponseMessage fileResponse = await sharedDownloadClient.GetAsync(downloadUrl, downloadTimeout.Token);
            if (fileResponse.IsSuccessStatusCode == false)
            {
                record.Status = "failed";
                record.ErrorMessage = "HTTP " + (int)fileResponse.StatusCode;
                return;
            }
            // 按实际响应大小二次校验（兜底文件列表中大小缺失的情况）
            long contentLength = fileResponse.Content.Headers.ContentLength ?? 0;
            if (maxFileSizeBytes > 0 && contentLength > maxFileSizeBytes)
            {
                record.Status = "failed";
                record.ErrorMessage = "文件大小 " + FormatFileSize(contentLength) + " 超过限制（" + maxFileSizeBytes / (1024 * 1024) + "MB）";
                logger.LogError("文件超出大小限制，取消下载: {File}", fileName);
                return;
            }
            await using (FileStream fileStream = File.Create(savePath))
            {
                await fileResponse.Content.CopyToAsync(fileStream, downloadTimeout.Token);
            }

            record.Status = "success";
            record.SavePath = savePath;
            record.FileSize = new FileInfo(savePath).Length;
            LogDebug("文件下载完成: " + fileName);
            interactor.Poke("✅ 文件「" + fileName + "」已下载完成，保存在 " + savePath);
        }
        catch (Exception downloadError)
        {
            record.Status = "failed";
            record.ErrorMessage = downloadError.Message;
            logger.LogError("下载异常：{Message}", downloadError.Message);
        }
    }

    // 下载前校验扩展名白名单与大小限制，返回拒绝原因（空串=允许）
    string CheckDownloadAllowed(string fileName, long size)
    {
        if (allowedExtensions.Count > 0)
        {
            string extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            if (allowedExtensions.Contains(extension) == false)
            {
                return "❌ 文件类型「" + (extension.Length > 0 ? extension : "无扩展名") + "」不在允许下载的列表中（允许："
                    + string.Join("、", allowedExtensions) + "）";
            }
        }
        if (maxFileSizeBytes > 0 && size > maxFileSizeBytes)
        {
            return "❌ 文件大小 " + FormatFileSize(size) + " 超过允许下载的最大值（" + maxFileSizeBytes / (1024 * 1024) + "MB）";
        }
        return "";
    }

    static string SanitizeFileName(string fileName)
    {
        char[] invalidCharacters = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        string sanitized = fileName;
        foreach (char invalid in invalidCharacters)
        {
            sanitized = sanitized.Replace(invalid, '_');
        }
        if (sanitized.Length > 200)
        {
            string extension = Path.GetExtension(sanitized);
            sanitized = sanitized[..(200 - extension.Length)] + extension;
        }
        return sanitized;
    }

    // ============================================================
    // 格式化与字段辅助
    // ============================================================

    string FormatFileList(List<JsonElement> files, List<JsonElement> folders, int maxItems)
    {
        List<string> parts = new();
        if (folders.Count > 0)
        {
            parts.Add("📁 文件夹（" + folders.Count + "个）：\n");
            int index = 0;
            foreach (JsonElement folder in folders.Take(maxItems))
            {
                index++;
                parts.Add(index + ". " + GetEntryName(folder, "未知文件夹") + "\n   🆔 ID: " + GetEntryFolderId(folder));
            }
            parts.Add("");
        }
        if (files.Count > 0)
        {
            parts.Add("📄 文件（" + files.Count + "个）：\n");
            int index = 0;
            foreach (JsonElement file in files.Take(maxItems))
            {
                index++;
                string uploader = GetStringField(file, "uploader_name").Length > 0 ? GetStringField(file, "uploader_name") : "未知用户";
                parts.Add(index + ". " + GetEntryName(file, "未知文件")
                    + "\n   💾 " + FormatFileSize(GetNumericField(file, "file_size")) + " | 👤 " + uploader);
            }
        }
        return parts.Count > 0 ? string.Join("\n", parts) : "当前群文件列表为空";
    }

    string FormatSearchResults(string keyword, string scope, List<FileEntry> matchedFiles, int maxItems)
    {
        if (matchedFiles.Count == 0)
        {
            return "🔍 在" + scope + "中未找到文件名包含「" + keyword + "」的文件";
        }
        StringBuilder builder = new();
        builder.Append("🔍 搜索「").Append(keyword).Append("」共找到 ").Append(matchedFiles.Count)
            .Append(" 个文件（").Append(scope).Append("）：\n");
        int index = 0;
        foreach (FileEntry file in matchedFiles.Take(maxItems))
        {
            index++;
            string uploader = GetStringField(file.Source, "uploader_name").Length > 0
                ? GetStringField(file.Source, "uploader_name")
                : "未知用户";
            string dateText = FormatDate(GetNumericField(file.Source, "upload_time"));
            builder.Append('\n').Append(index).Append(". ").Append(file.FileName)
                .Append("\n   📁 ").Append(file.Location)
                .Append(" | 💾 ").Append(FormatFileSize(file.Size))
                .Append(" | 👤 ").Append(uploader);
            if (dateText.Length > 0)
            {
                builder.Append(" | 📅 ").Append(dateText);
            }
            builder.Append("\n   🆔 ").Append(file.FileId);
        }
        if (matchedFiles.Count > maxItems)
        {
            builder.Append("\n\n⚠️ 结果较多，仅显示前 ").Append(maxItems).Append(" 条，请使用更精确的关键词缩小范围");
        }
        return builder.ToString();
    }

    static string FormatDate(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return "";
        }
        long seconds = unixSeconds > 1000000000000 ? unixSeconds / 1000 : unixSeconds;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }

    static string FormatFileSize(long sizeBytes)
    {
        if (sizeBytes < 1024)
        {
            return sizeBytes + "B";
        }
        if (sizeBytes < 1024 * 1024)
        {
            return (sizeBytes / 1024d).ToString("F1", CultureInfo.InvariantCulture) + "KB";
        }
        if (sizeBytes < 1024L * 1024 * 1024)
        {
            return (sizeBytes / (1024d * 1024)).ToString("F1", CultureInfo.InvariantCulture) + "MB";
        }
        return (sizeBytes / (1024d * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + "GB";
    }

    async Task<string> ResolveFolderIdAsync(long groupId, string? folderId, string? folderName)
    {
        if (string.IsNullOrEmpty(folderId) == false)
        {
            return folderId;
        }
        if (string.IsNullOrEmpty(folderName) == false)
        {
            return await GetFolderIdByNameAsync(groupId, folderName);
        }
        return "";
    }

    // 解析搜索范围：folderId优先；folderName填'根目录'返回"/"；都为空返回空串（全量）；找不到返回null
    async Task<string?> ResolveOptionalFolderScopeAsync(long groupId, string? folderId, string? folderName)
    {
        if (string.IsNullOrEmpty(folderId) == false)
        {
            return folderId;
        }
        if (string.IsNullOrEmpty(folderName) == false)
        {
            if (folderName == "根目录" || folderName == "/")
            {
                return "/";
            }
            string resolved = await GetFolderIdByNameAsync(groupId, folderName);
            if (resolved.Length == 0)
            {
                return null;
            }
            return resolved;
        }
        return "";
    }

    static string GetStringField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return "";
        }
        return field.ValueKind switch
        {
            JsonValueKind.String => field.GetString() ?? "",
            JsonValueKind.Number => field.GetRawText(),
            _ => ""
        };
    }

    static long GetNumericField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return 0;
        }
        return field.ValueKind switch
        {
            JsonValueKind.Number => field.GetInt64(),
            JsonValueKind.String => long.TryParse(field.GetString(), out long parsed) ? parsed : 0,
            _ => 0
        };
    }

    // 下载任务状态记录
    sealed class DownloadTaskRecord
    {
        public string Status { get; set; } = "pending";
        public string FileName { get; set; } = "";
        public string SavePath { get; set; } = "";
        public long FileSize { get; set; }
        public string ErrorMessage { get; set; } = "";
    }
}
