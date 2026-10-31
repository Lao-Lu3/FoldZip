using System.Buffers.Binary;
using System.Text;

namespace FoldZip.Core;

public sealed class TarEntry
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string TypeFlag { get; init; } = "0";
}

/// <summary>
/// 自研 TAR 读取器（ustar）：前向式（流式）读取，不需要可寻址流。
/// 支持 '0'/'\0' 常规文件、'5' 目录、GNU 'L' 长文件名、pax 'x' 的 path 记录；
/// 符号链接/硬链接条目列出但跳过内容（安全考虑）。
/// </summary>
public sealed class TarReader
{
    private readonly Stream _stream;
    private string? _pendingLongName;
    private string? _pendingPaxPath;
    private int _index;

    public TarReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!_stream.CanRead) throw new ArgumentException("流必须可读");
    }

    public TarEntry? ReadNext()
    {
        var header = new byte[512];
        if (!ReadBlock(header)) return null; // EOF（按约定在两个全零块处停止）
        if (IsZeroBlock(header)) return null;

        long storedChecksum = ParseNumeric(header, 148, 8);
        long computed = Checksum(header);
        if (computed != storedChecksum)
            throw new InvalidDataException("TAR 头校验失败（文件损坏或不是 TAR）");

        char type = (char)header[156];
        long size = ParseNumeric(header, 124, 12);

        switch (type)
        {
            case 'L': // GNU 长文件名
            {
                var data = new byte[size];
                ArchiveDetector.ReadFull(_stream, data, (int)size);
                SkipPadding(size);
                _pendingLongName = Encoding.UTF8.GetString(data).TrimEnd('\0');
                return ReadNext();
            }
            case 'x': // pax 扩展头
            {
                var data = new byte[size];
                ArchiveDetector.ReadFull(_stream, data, (int)size);
                SkipPadding(size);
                _pendingPaxPath = ParsePaxPath(data);
                return ReadNext();
            }
            case 'g': // pax 全局头
            {
                SkipData(size);
                return ReadNext();
            }
        }

        string name = _pendingLongName ?? _pendingPaxPath ?? ParseString(header, 0, 100);
        string prefix = ParseString(header, 345, 155);
        if (_pendingLongName == null && _pendingPaxPath == null && prefix.Length > 0)
            name = prefix + "/" + name;
        _pendingLongName = null;
        _pendingPaxPath = null;

        bool isDir = type == '5' || name.EndsWith("/");
        long mtime = ParseNumeric(header, 136, 12);
        var modified = mtime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime
            : new DateTime(1970, 1, 1);

        return new TarEntry
        {
            Index = _index++,
            Name = name,
            IsDirectory = isDir,
            Size = size,
            Modified = modified,
            TypeFlag = type.ToString(),
        };
    }

    /// <summary>把当前条目数据复制到输出流，并跳过 512 填充。必须在下一次 ReadNext 之前调用。</summary>
    public void CopyDataTo(Stream output, long size)
    {
        var buffer = new byte[81920];
        long remaining = size;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = _stream.Read(buffer, 0, want);
            if (read <= 0) throw new InvalidDataException("TAR 数据段提前结束（文件损坏）");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        SkipPadding(size);
    }

    public void SkipData(long size)
    {
        var buffer = new byte[81920];
        long remaining = size;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = _stream.Read(buffer, 0, want);
            if (read <= 0) throw new InvalidDataException("TAR 数据段提前结束（文件损坏）");
            remaining -= read;
        }
        SkipPadding(size);
    }

    private void SkipPadding(long size)
    {
        long pad = (512 - size % 512) % 512;
        while (pad > 0)
        {
            int want = (int)Math.Min(81920, pad);
            int read = _stream.Read(new byte[want], 0, want);
            if (read <= 0) break;
            pad -= read;
        }
    }

    private bool ReadBlock(byte[] block)
    {
        int read = ArchiveDetector.ReadFull(_stream, block, 512);
        return read == 512;
    }

    private static bool IsZeroBlock(byte[] block)
    {
        foreach (var b in block)
            if (b != 0) return false;
        return true;
    }

    private static long Checksum(byte[] header)
    {
        long sum = 0;
        for (int i = 0; i < 512; i++)
            sum += (i >= 148 && i < 156) ? 0x20 : header[i];
        return sum;
    }

    private static string ParseString(byte[] header, int offset, int length)
    {
        int end = offset;
        int max = offset + length;
        while (end < max && header[end] != 0) end++;
        return Encoding.UTF8.GetString(header, offset, end - offset);
    }

    private static long ParseNumeric(byte[] header, int offset, int length)
    {
        if ((header[offset] & 0x80) != 0)
        {
            // base-256 大数
            long value = header[offset] & 0x7F;
            for (int i = 1; i < length; i++)
                value = (value << 8) | header[offset + i];
            return value;
        }
        var text = ParseString(header, offset, length).Trim('\0', ' ');
        if (text.Length == 0) return 0;
        return Convert.ToInt64(text, 8);
    }

    private static string? ParsePaxPath(byte[] data)
    {
        int i = 0;
        string? path = null;
        while (i < data.Length)
        {
            int sp = Array.IndexOf(data, (byte)' ', i);
            if (sp < 0 || sp == i) break;
            if (!int.TryParse(Encoding.ASCII.GetString(data, i, sp - i), out int len) || len <= 0 || i + len > data.Length)
                break;
            var record = Encoding.UTF8.GetString(data, i, len);
            i += len;
            if (record.StartsWith("path=", StringComparison.Ordinal))
                path = record[5..].TrimEnd('\n');
        }
        return path;
    }
}
