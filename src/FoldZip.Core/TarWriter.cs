using System.Text;

namespace FoldZip.Core;

/// <summary>自研 TAR 写入器（ustar），超长名先尝试 ustar 前缀拆分，不行用 GNU 'L' 长名条目。</summary>
public sealed class TarWriter : IDisposable
{
    private readonly Stream _output;
    private bool _finished;

    public TarWriter(Stream output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        if (!_output.CanWrite) throw new ArgumentException("输出流必须可写");
    }

    public void AddEntry(string entryName, Stream? source, long size, DateTime modified, bool isDirectory)
    {
        if (_finished) throw new InvalidOperationException("TAR 已收尾");
        entryName = entryName.Replace('\\', '/').TrimStart('/');
        if (entryName.Length == 0) throw new ArgumentException("条目名不能为空");
        if (isDirectory && !entryName.EndsWith('/')) entryName += "/";

        string prefix = "";
        string shortName = entryName;
        var nameBytes = Encoding.UTF8.GetBytes(entryName);
        if (nameBytes.Length > 100)
        {
            if (!TrySplitName(entryName, out prefix, out shortName))
            {
                WriteLongName(entryName);
                prefix = "";
                shortName = TruncateUtf8(entryName, 100);
            }
        }

        long mtime = new DateTimeOffset(modified.ToUniversalTime()).ToUnixTimeSeconds();
        WriteHeader(shortName, prefix, isDirectory ? 0 : size, mtime,
            isDirectory ? '5' : '0', isDirectory ? "0000755" : "0000644");

        if (!isDirectory && source != null)
        {
            if (source.CanSeek) source.Seek(0, SeekOrigin.Begin);
            source.CopyTo(_output, 81920);
            long pad = (512 - size % 512) % 512;
            if (pad > 0) _output.Write(new byte[pad]);
        }
    }

    /// <summary>按 UTF-8 字节数截断字符串（不切断多字节字符）。</summary>
    private static string TruncateUtf8(string s, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= maxBytes) return s;
        int cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
        return Encoding.UTF8.GetString(bytes, 0, cut);
    }

    private void WriteLongName(string name)
    {
        var data = Encoding.UTF8.GetBytes(name + "\0");
        long size = data.Length;
        long mtime = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        WriteHeader("././@LongLink", "", size, mtime, 'L', "0000644");
        _output.Write(data);
        long pad = (512 - size % 512) % 512;
        if (pad > 0) _output.Write(new byte[pad]);
    }

    private static bool TrySplitName(string name, out string prefix, out string shortName)
    {
        for (int i = name.Length - 1; i > 0; i--)
        {
            if (name[i] != '/') continue;
            var p = name[..i];
            var s = name[(i + 1)..];
            if (Encoding.UTF8.GetByteCount(p) <= 155 && Encoding.UTF8.GetByteCount(s) <= 100 && s.Length > 0)
            {
                prefix = p;
                shortName = s;
                return true;
            }
        }
        prefix = "";
        shortName = name;
        return false;
    }

    private void WriteHeader(string name, string prefix, long size, long mtime, char typeFlag, string mode)
    {
        var h = new byte[512];
        PutString(h, 0, 100, name);
        PutOctal(h, 100, 8, Convert.ToInt64(mode, 8));
        PutOctal(h, 108, 8, 0);           // uid
        PutOctal(h, 116, 8, 0);           // gid
        PutOctal(h, 124, 12, size);
        PutOctal(h, 136, 12, Math.Max(0, mtime));
        // 148..155 校验和，先留空格再计算
        h[156] = (byte)typeFlag;
        // linkname 157..156+100 全零
        PutString(h, 257, 6, "ustar");
        PutString(h, 263, 2, "00");
        PutString(h, 345, 155, prefix);

        long checksum = 0;
        for (int i = 0; i < 512; i++) checksum += h[i] == 0 && i >= 148 && i < 156 ? 0x20 : h[i];
        var cs = Convert.ToString(checksum, 8).PadLeft(6, '0');
        for (int i = 0; i < 6; i++) h[148 + i] = (byte)cs[i];
        h[154] = 0; h[155] = (byte)' ';

        _output.Write(h);
    }

    private static void PutString(byte[] buf, int offset, int length, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > length) throw new ArgumentException($"TAR 字段超长：{value}");
        Array.Clear(buf, offset, length);
        bytes.CopyTo(buf, offset);
    }

    private static void PutOctal(byte[] buf, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8);
        if (text.Length > length - 1) throw new ArgumentException($"TAR 数值字段溢出：{value}");
        var padded = text.PadLeft(length - 1, '0');
        for (int i = 0; i < length - 1; i++) buf[offset + i] = (byte)padded[i];
        buf[offset + length - 1] = 0;
    }

    public void Dispose()
    {
        if (_finished) { return; }
        _finished = true;
        _output.Write(new byte[1024]); // 两个全零结束块
        _output.Flush();
    }
}
