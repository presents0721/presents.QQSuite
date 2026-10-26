using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Text;

namespace QQSuite.JmShare;

/// <summary>
/// 手写的 ZIP 打包器，支持传统 PKWARE（ZipCrypto）加密。
/// 之所以不复用 ZipArchive：.NET 的 ZipArchive 不支持加密，且会在写入过程中才算出 CRC，
/// 而 ZipCrypto 的 12 字节加密头需要提前知道 CRC。这里先压缩再加密，手写本地头 / 中央目录 / 目录结束记录。
/// 生成的文件可被 7-Zip / WinRAR / 常见手机解压 App 打开（Windows 资源管理器不支持加密 ZIP）。
/// </summary>
public static class JmZipCrypto
{
    const uint LocalHeaderSignature = 0x04034b50;
    const uint CentralHeaderSignature = 0x02014b50;
    const uint EndOfCentralSignature = 0x06054b50;

    /// <summary>加密标记（general purpose bit flag 的第 0 位）。</summary>
    const ushort FlagEncrypted = 0x0001;
    /// <summary>文件名按 UTF-8 解释（第 11 位），文件名是 ASCII 时同样安全。</summary>
    const ushort FlagUtf8 = 0x0800;

    /// <summary>
    /// 把指定文件打包成 ZIP；password 非空时逐条目使用 ZipCrypto 加密。
    /// 单次调用最多 65535 个条目、总大小不超过 4 GB（与未加密打包的适用场景一致）。
    /// 返回压缩包字节数。
    /// </summary>
    public static long Pack(IReadOnlyList<string> files, string zipPath, string? password)
    {
        bool encrypt = !string.IsNullOrEmpty(password);
        byte[] passwordBytes = encrypt ? Encoding.UTF8.GetBytes(password!) : Array.Empty<byte>();

        if (files.Count > 65535) throw new Exception("文件数量超过 65535，无法打包为 ZIP");

        string? directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        List<(string Name, byte[] NameBytes, ushort Flags, ushort Time, ushort Date,
            uint Crc, uint CompressedSize, uint UncompressedSize, uint Offset)> entries = new();

        using FileStream output = new(zipPath, FileMode.Create, FileAccess.Write);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);

            byte[] raw = File.ReadAllBytes(file);
            byte[] deflated = Deflate(raw);

            uint crc = Crc32(raw);
            DateTime writeTime = SafeWriteTime(file);
            (ushort dosTime, ushort dosDate) = ToDosTime(writeTime);

            ushort flags = FlagUtf8;
            uint storedSize = (uint)deflated.Length;
            uint offset = (uint)output.Position;

            byte[] payload = deflated;
            if (encrypt)
            {
                flags |= FlagEncrypted;
                payload = EncryptEntry(deflated, crc, dosTime, passwordBytes);
                storedSize = (uint)payload.Length;
            }

            if ((long)offset + 30 + nameBytes.Length + payload.Length > uint.MaxValue)
            {
                throw new Exception("压缩包超过 4 GB，请降低单本页数或改用 ZIP 之外的输出格式");
            }

            WriteLocalHeader(output, flags, dosTime, dosDate, crc, storedSize, (uint)raw.Length, nameBytes);
            output.Write(payload, 0, payload.Length);

            entries.Add((name, nameBytes, flags, dosTime, dosDate, crc, storedSize, (uint)raw.Length, offset));
        }

        long centralOffset = output.Position;
        foreach (var entry in entries)
        {
            WriteCentralHeader(output, entry.Flags, entry.Time, entry.Date, entry.Crc,
                entry.CompressedSize, entry.UncompressedSize, entry.NameBytes, entry.Offset);
        }
        long centralSize = output.Position - centralOffset;

        WriteEndOfCentral(output, (ushort)entries.Count, (uint)centralSize, (uint)centralOffset);
        output.Flush();
        return output.Length;
    }

    #region ZIP 结构写入

    static void WriteLocalHeader(Stream output, ushort flags, ushort time, ushort date,
        uint crc, uint compressedSize, uint uncompressedSize, byte[] nameBytes)
    {
        using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(LocalHeaderSignature);
        writer.Write((ushort)20);            // version needed to extract（2.0，支持 deflate）
        writer.Write(flags);
        writer.Write((ushort)8);             // 压缩方法：deflate
        writer.Write(time);
        writer.Write(date);
        writer.Write(crc);
        writer.Write(compressedSize);
        writer.Write(uncompressedSize);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);             // extra field 长度
        writer.Write(nameBytes);
    }

    static void WriteCentralHeader(Stream output, ushort flags, ushort time, ushort date, uint crc,
        uint compressedSize, uint uncompressedSize, byte[] nameBytes, uint localOffset)
    {
        using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(CentralHeaderSignature);
        writer.Write((ushort)20);            // version made by
        writer.Write((ushort)20);            // version needed
        writer.Write(flags);
        writer.Write((ushort)8);
        writer.Write(time);
        writer.Write(date);
        writer.Write(crc);
        writer.Write(compressedSize);
        writer.Write(uncompressedSize);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);             // extra
        writer.Write((ushort)0);             // comment
        writer.Write((ushort)0);             // disk number start
        writer.Write((ushort)0);             // internal attributes
        writer.Write(0u);                    // external attributes
        writer.Write(localOffset);
        writer.Write(nameBytes);
    }

    static void WriteEndOfCentral(Stream output, ushort count, uint centralSize, uint centralOffset)
    {
        using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(EndOfCentralSignature);
        writer.Write((ushort)0);             // 当前磁盘号
        writer.Write((ushort)0);             // 中央目录起始磁盘号
        writer.Write(count);                 // 本磁盘条目数
        writer.Write(count);                 // 总条目数
        writer.Write(centralSize);
        writer.Write(centralOffset);
        writer.Write((ushort)0);             // 注释长度
    }

    #endregion

    #region 压缩与时间

    /// <summary>用 .NET 的 DeflateStream 生成原始 deflate 数据（不带 zlib 头，符合 ZIP 的 method 8）。</summary>
    static byte[] Deflate(byte[] data)
    {
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    static DateTime SafeWriteTime(string file)
    {
        try
        {
            return File.GetLastWriteTime(file);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    /// <summary>把本地时间转成 DOS 的 (time, date) 结构；年份早于 1980 时钳到 1980。</summary>
    static (ushort Time, ushort Date) ToDosTime(DateTime value)
    {
        if (value.Year < 1980) value = new DateTime(1980, 1, 1, 0, 0, 0);
        ushort time = (ushort)((value.Hour << 11) | (value.Minute << 5) | (value.Second / 2));
        ushort date = (ushort)(((value.Year - 1980) << 9) | (value.Month << 5) | value.Day);
        return (time, date);
    }

    #endregion

    #region ZipCrypto

    /// <summary>
    /// 生成 12 字节加密头 + 加密后的 deflate 数据。
    /// 头的最后两个字节是 CRC 的高 16 位（低字节在前），解密方用它做校验；
    /// 加密时先写头再写数据，密钥随「明文」逐字节推进。
    /// </summary>
    static byte[] EncryptEntry(byte[] deflated, uint crc, ushort dosTime, byte[] password)
    {
        byte[] header = new byte[12];
        Random.Shared.NextBytes(header);
        header[10] = (byte)((crc >> 16) & 0xFF);
        header[11] = (byte)((crc >> 24) & 0xFF);

        CryptoKeys keys = new();
        keys.Init(password);

        byte[] result = new byte[header.Length + deflated.Length];
        for (int i = 0; i < header.Length; i++)
        {
            byte plain = header[i];
            result[i] = (byte)(plain ^ keys.DecryptByte());
            keys.Update(plain);
        }
        for (int i = 0; i < deflated.Length; i++)
        {
            byte plain = deflated[i];
            result[header.Length + i] = (byte)(plain ^ keys.DecryptByte());
            keys.Update(plain);
        }
        return result;
    }

    /// <summary>ZipCrypto 的三个 32 位状态与 CRC32 更新表。</summary>
    struct CryptoKeys
    {
        uint key0;
        uint key1;
        uint key2;

        public void Init(ReadOnlySpan<byte> password)
        {
            key0 = 0x12345678;
            key1 = 0x23456789;
            key2 = 0x34567890;
            foreach (byte value in password) Update(value);
        }

        public void Update(byte plain)
        {
            key0 = Crc32Update(key0, plain);
            key1 = unchecked(key1 + (key0 & 0xFF));
            key1 = unchecked(key1 * 134775813 + 1);
            key2 = Crc32Update(key2, (byte)(key1 >> 24));
        }

        /// <summary>当前密钥流字节：((key2 | 2) * ((key2 | 2) ^ 1)) >> 8 的低 8 位。</summary>
        public readonly byte DecryptByte()
        {
            uint temp = (key2 | 2) & 0xFFFF;
            return (byte)((temp * (temp ^ 1)) >> 8);
        }
    }

    #endregion

    #region CRC32

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }
            table[i] = value;
        }
        return table;
    }

    static uint Crc32Update(uint crc, byte value) => (crc >> 8) ^ CrcTable[(crc ^ value) & 0xFF];

    /// <summary>标准 CRC32（初值取反、结果取反），即 ZIP 里的 CRC 字段。</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data) crc = Crc32Update(crc, value);
        return crc ^ 0xFFFFFFFF;
    }

    #endregion
}
