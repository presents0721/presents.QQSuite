using System;
using System.IO;
using System.Text.Json;
using Alife.Foundation;

namespace QQSuite.JmShare;

/// <summary>
/// JM 模块的轻量本地状态存储：每个状态一个 JSON 文件，原子写入，任何异常都不外抛。
/// 存放位置固定在 Storage/Plugins/presents.QQSuite/jm/state，与用户的「下载目录」配置解耦，
/// 这样改下载目录或清理下载缓存都不会影响配额、收藏与订阅数据。
/// </summary>
public static class JmStateStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    static readonly object Gate = new();

    /// <summary>状态目录的绝对路径。</summary>
    public static string StateDirectory => Path.Combine(
        AlifePath.StorageFolderPath, "Plugins", "presents.QQSuite", "jm", "state");

    /// <summary>读取状态；文件不存在、解析失败或为空时返回 factory() 给出的默认值。</summary>
    public static T Load<T>(string name, Func<T> factory) where T : class
    {
        lock (Gate)
        {
            try
            {
                string path = Path.Combine(StateDirectory, name + ".json");
                if (!File.Exists(path)) return factory();

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return factory();

                return JsonSerializer.Deserialize<T>(json, Options) ?? factory();
            }
            catch
            {
                return factory();
            }
        }
    }

    /// <summary>写入状态：先写 .tmp 再替换，避免进程中途退出留下半截文件。</summary>
    public static void Save<T>(string name, T value)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(StateDirectory);
                string path = Path.Combine(StateDirectory, name + ".json");
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
                File.Move(temp, path, true);
            }
            catch
            {
                // 状态保存失败只影响配额/收藏/订阅的记忆，不影响主流程
            }
        }
    }
}
