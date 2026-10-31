using System.Buffers.Binary;
using System.IO.Compression;

namespace FoldZip.Core;

public enum ZipCreateMode
{
    /// <summary>文本等可压缩内容用 Deflate，已压缩内容（jpg/mp4/zip 等）自动转 Store。</summary>
    Auto,
    Store,
    Fastest,
    Optimal,
}

/// <summary>
/// 自研 ZIP 写入器：顺序写本地头占位 → 流式压缩 → 回填 CRC/尺寸 → 末尾写中央目录。
/// 输出流必须可寻址。单条目与总偏移超 4GB / 条目数超 65535 时明确报错（ZIP64 写入在路线图中）。
/// </summary>
public sealed class ZipWriter : IDisposable
{
    private struct CentralRecord
    {
        public byte[] Name;
        public ushort Method, Flags, DosTime, DosDate;
        public uint Crc, CSize, USize;
        public long Offset;
        public uint ExternalAttr;
        public byte[]? Comment;
    }

    private static readonly IReadOnlySet<string> AlreadyCompressedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif", ".bmp",
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".mp3", ".flac", ".ogg", ".opus", ".wav",
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst", ".cab",
        ".pdf", ".docx", ".xlsx", ".pptx", ".apk", ".iso", ".woff2",
    };

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly CompressionLevel? _level;
    private readonly List<CentralRecord> _records = new();
    private bool _finished;

    public ZipWriter(Stream output, ZipCreateMode mode = ZipCreateMode.Auto, bool leaveOpen = false)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        if (!_output.CanWrite || !_output.CanSeek)
            throw new ArgumentException("ZIP 输出流必须可写且可寻址");
        _leaveOpen = leaveOpen;
        _level = mode switch
        {
            ZipCreateMode.Store => null,
            ZipCreateMode.Fastest => CompressionLevel.Fastest,
            _ => CompressionLevel.Optimal,
        };
    }

    /// <summary>添加一个条目。目录条目 entryName 以 '/' 结尾、source 为 null。</summary>
    public void AddEntry(string entryName, Stream? source, DateTime modified,
        uint externalAttributes = 0, string? comment = null)
    {
        if (_finished) throw new InvalidOperationException("压缩包已收尾，不能再添加条目");

        entryName = entryName.Replace('\\', '/').TrimStart('/');
        bool isDir = entryName.EndsWith('/');
        var name = NameEncoding.Encode(entryName);
        if (name.Length == 0) throw new ArgumentException("条目名不能为空");
        if (name.Length > 0xFFFF) throw new ArgumentException("条目名过长");
        var commentBytes = comment == null ? null : NameEncoding.Encode(comment);
        if (commentBytes is { Length: > 0xFFFF }) throw new ArgumentException("注释过长");

        if (source != null && isDir) throw new ArgumentException("目录条目不能携带数据流");
        if (!isDir && source == null) throw new ArgumentException("文件条目必须提供数据流");

        if (source is { CanSeek: true } && source.Length >= 0xFFFFFFF0L)
            throw new NotSupportedException($"条目超过 4GB，当前版本不支持（ZIP64 写入在路线图中）：{entryName}");

        var (dosTime, dosDate) = DosDateTime.ToDos(modified);
        long offset = _output.Position;
        ushort flags = 0x0800; // UTF-8 文件名
        ushort method = 0;
        uint crc = 0, csize = 0, usize = 0;

        WriteLocalHeader(name, 0, flags, dosTime, dosDate, 0, 0, 0);
        long dataStart = _output.Position;

        if (!isDir && source != null)
        {
            CompressionLevel? level = _level;
            if (level != null && ZipCreateAutoStore(source, out var ext))
                level = null; // Auto：已压缩扩展名直接 Store
            if (level != null && source is { CanSeek: true } && source.Length < 4096)
                level = CompressionLevel.Fastest; // 小文件压缩收益低

            var crcSource = new CrcReadStream(source);
            if (level is null)
            {
                crcSource.CopyTo(_output, 81920);
                method = 0;
            }
            else
            {
                method = 8;
                using var ds = new DeflateStream(_output, level.Value, leaveOpen: true);
                crcSource.CopyTo(ds, 81920);
            }
            crc = crcSource.FinalCrc;
            usize = ToUint32(crcSource.BytesRead, entryName);
            csize = ToUint32(_output.Position - dataStart, entryName);

            // 回填本地头：method @ offset+8；crc/csize/usize 连续 @ offset+14..26（只写有效字节）
            Span<byte> m = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(m, method);
            _output.Seek(offset + 8, SeekOrigin.Begin);
            _output.Write(m);
            Span<byte> vals = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(vals, crc);
            BinaryPrimitives.WriteUInt32LittleEndian(vals.Slice(4), csize);
            BinaryPrimitives.WriteUInt32LittleEndian(vals.Slice(8), usize);
            _output.Seek(offset + 14, SeekOrigin.Begin);
            _output.Write(vals);
            _output.Seek(0, SeekOrigin.End);
        }

        _records.Add(new CentralRecord
        {
            Name = name,
            Method = method,
            Flags = flags,
            DosTime = dosTime,
            DosDate = dosDate,
            Crc = crc,
            CSize = csize,
            USize = usize,
            Offset = offset,
            ExternalAttr = externalAttributes,
            Comment = commentBytes,
        });

        static uint ToUint32(long v, string entryName)
        {
            if (v >= 0xFFFFFFF0L)
                throw new NotSupportedException($"条目超 4GB，当前版本不支持（ZIP64 写入在路线图中）：{entryName}");
            return (uint)v;
        }
    }

    private static bool ZipCreateAutoStore(Stream source, out string extension)
    {
        extension = "";
        return source is FileStream fs
            && AlreadyCompressedExtensions.Contains(Path.GetExtension(fs.Name));
    }

    private void WriteLocalHeader(byte[] name, ushort method, ushort flags,
        ushort dosTime, ushort dosDate, uint crc, uint csize, uint usize)
    {
        Span<byte> h = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x04034B50u);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(4), 20);       // version needed
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(6), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(8), method);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(10), dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(12), dosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(18), csize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(22), usize);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(26), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(28), 0);       // extra len
        _output.Write(h);
        _output.Write(name);
    }

    public void Dispose()
    {
        if (_finished) { if (!_leaveOpen) _output.Dispose(); return; }
        _finished = true;

        if (_records.Count > 0xFFFE)
            throw new NotSupportedException("条目数超过 65535，当前版本不支持（ZIP64 写入在路线图中）");

        long cdStart = _output.Position;
        Span<byte> h = stackalloc byte[46];
        foreach (var r in _records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h, 0x02014B50u);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(4), 20);   // version made by (FAT)
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(6), 20);   // version needed
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(8), r.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(10), r.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(12), r.DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(14), r.DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(16), r.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(20), r.CSize);
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(24), r.USize);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(28), (ushort)r.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(30), 0);   // extra len
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(32), (ushort)(r.Comment?.Length ?? 0));
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(34), 0);   // disk start
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(36), 0);   // internal attrs
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(38), r.ExternalAttr);
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(42), (uint)r.Offset);
            _output.Write(h);
            _output.Write(r.Name);
            if (r.Comment is { Length: > 0 }) _output.Write(r.Comment);
        }
        long cdSize = _output.Position - cdStart;
        if (cdStart >= 0xFFFFFFF0L || cdSize >= 0xFFFFFFF0L)
            throw new NotSupportedException("压缩包超过 4GB，当前版本不支持（ZIP64 写入在路线图中）");

        Span<byte> eocd = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054B50u);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(8), (ushort)_records.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(10), (ushort)_records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(12), (uint)cdSize);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(16), (uint)cdStart);
        _output.Write(eocd);
        _output.Flush();

        if (!_leaveOpen) _output.Dispose();
    }
}
