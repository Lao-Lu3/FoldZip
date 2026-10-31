using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FoldZip.Core;

public sealed class ZipEntry
{
    public int Index { get; init; }
    public string Name { get; set; } = "";
    public bool IsDirectory { get; init; }
    public ulong CompressedSize { get; init; }
    public ulong Size { get; init; }
    public uint Crc32 { get; init; }
    public ushort Method { get; init; }
    public ushort Flags { get; init; }
    public DateTime Modified { get; init; }
    public long LocalHeaderOffset { get; init; }
    public string? Comment { get; init; }
    public uint ExternalAttributes { get; init; }
}

/// <summary>
/// 自研 ZIP 读取器：直接解析中央目录（支持 ZIP64 读取），
/// 文件名按 UTF-8 标志解码，无标志时回退 GB18030。
/// 解压支持方法 0（Store）与 8（Deflate），并做 CRC32 校验与路径穿越防护。
/// </summary>
public sealed class ZipReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    public IReadOnlyList<ZipEntry> Entries { get; }

    public ZipReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        if (!_stream.CanRead || !_stream.CanSeek)
            throw new ArgumentException("ZIP 流必须可读且可寻址");
        Entries = Parse();
    }

    public ZipReader(string path)
        : this(File.OpenRead(path)) { }

    private (int Count, long CdOffset) LocateCentralDirectory()
    {
        long len = _stream.Length;
        if (len < 22) throw new InvalidDataException("文件过小，不是有效的 ZIP");
        int window = (int)Math.Min(len, 22 + 0xFFFFL);
        _stream.Seek(len - window, SeekOrigin.Begin);
        var buf = new byte[window];
        ArchiveDetector.ReadFull(_stream, buf, window);

        int idx = -1;
        for (int i = window - 22; i >= 0; i--)
        {
            if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) throw new InvalidDataException("未找到 ZIP 结尾记录（文件损坏或不是 ZIP）");

        int count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(idx + 10));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(idx + 16));
        long eocdPos = len - window + idx;

        // ZIP64：EOCD 定位器固定 20 字节，位于 EOCD 之前
        if (eocdPos >= 20)
        {
            _stream.Seek(eocdPos - 20, SeekOrigin.Begin);
            var loc = new byte[20];
            ArchiveDetector.ReadFull(_stream, loc, 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(loc) == 0x07064B50u)
            {
                long z64Pos = (long)BinaryPrimitives.ReadUInt64LittleEndian(loc.AsSpan(8));
                if (z64Pos > 0 && z64Pos < len - 56)
                {
                    _stream.Seek(z64Pos, SeekOrigin.Begin);
                    var z = new byte[56];
                    ArchiveDetector.ReadFull(_stream, z, 56);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(z) == 0x06064B50u)
                    {
                        count = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(z.AsSpan(32)));
                        cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(z.AsSpan(48));
                    }
                }
            }
        }
        return (count, cdOffset);
    }

    private IReadOnlyList<ZipEntry> Parse()
    {
        var (count, cdOffset) = LocateCentralDirectory();
        if (cdOffset >= _stream.Length && count > 0)
            throw new InvalidDataException("ZIP 中央目录偏移越界（文件损坏）");

        _stream.Seek(cdOffset, SeekOrigin.Begin);
        var list = new List<ZipEntry>(Math.Max(0, count));
        using var br = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

        for (int i = 0; i < count; i++)
        {
            if (br.ReadUInt32() != 0x02014B50u)
                throw new InvalidDataException("ZIP 中央目录条目损坏");
            br.ReadUInt16(); // version made by
            br.ReadUInt16(); // version needed
            ushort flags = br.ReadUInt16();
            ushort method = br.ReadUInt16();
            ushort dosTime = br.ReadUInt16();
            ushort dosDate = br.ReadUInt16();
            uint crc = br.ReadUInt32();
            uint csize = br.ReadUInt32();
            uint usize = br.ReadUInt32();
            ushort nameLen = br.ReadUInt16();
            ushort extraLen = br.ReadUInt16();
            ushort commentLen = br.ReadUInt16();
            br.ReadUInt16(); // disk start
            br.ReadUInt16(); // internal attrs
            uint extAttr = br.ReadUInt32();
            uint localOffset = br.ReadUInt32();
            var nameBytes = br.ReadBytes(nameLen);
            var extra = br.ReadBytes(extraLen);
            var commentBytes = br.ReadBytes(commentLen);

            ParseZip64Extra(extra, csize, usize, localOffset, out var c64, out var u64, out var off64);
            ulong cs = c64 ?? csize;
            ulong us = u64 ?? usize;
            long lo = off64 ?? localOffset;

            bool isDir = (extAttr & 0x10) != 0
                || (nameBytes.Length > 0 && nameBytes[^1] == (byte)'/');

            bool utf8 = (flags & 0x0800) != 0;
            list.Add(new ZipEntry
            {
                Index = i,
                Name = NameEncoding.Decode(nameBytes, utf8),
                IsDirectory = isDir,
                CompressedSize = cs,
                Size = us,
                Crc32 = crc,
                Method = method,
                Flags = flags,
                Modified = DosDateTime.FromDos(dosDate, dosTime),
                LocalHeaderOffset = lo,
                Comment = commentBytes.Length > 0 ? NameEncoding.Decode(commentBytes, utf8) : null,
                ExternalAttributes = extAttr,
            });
        }
        return list;
    }

    private static void ParseZip64Extra(byte[] extra, uint csize, uint usize, uint localOffset,
        out ulong? c64, out ulong? u64, out long? off64)
    {
        c64 = null; u64 = null; off64 = null;
        int i = 0;
        while (i + 4 <= extra.Length)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(i));
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(i + 2));
            if (i + 4 + size > extra.Length) break;
            if (id == 0x0001)
            {
                int p = i + 4;
                if (usize == 0xFFFFFFFFu && p + 8 <= i + 4 + size)
                { u64 = BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(p)); p += 8; }
                if (csize == 0xFFFFFFFFu && p + 8 <= i + 4 + size)
                { c64 = BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(p)); p += 8; }
                if (localOffset == 0xFFFFFFFFu && p + 8 <= i + 4 + size)
                { off64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(p)); p += 8; }
            }
            i += 4 + size;
        }
    }

    public void Extract(ZipEntry entry, string destFilePath)
    {
        using var fs = File.Create(destFilePath);
        ExtractInto(entry, fs);
    }

    public void ExtractInto(ZipEntry entry, Stream output)
    {
        if (entry.IsDirectory) throw new IOException($"条目是目录，无法按文件解压：{entry.Name}");
        _stream.Seek(entry.LocalHeaderOffset, SeekOrigin.Begin);
        using var br = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        if (br.ReadUInt32() != 0x04034B50u)
            throw new InvalidDataException($"条目本地头损坏：{entry.Name}");
        br.ReadUInt16(); // version
        br.ReadUInt16(); // local flags
        br.ReadUInt16(); // local method
        br.ReadUInt16(); // time
        br.ReadUInt16(); // date
        br.ReadUInt32(); // crc
        br.ReadUInt32(); // csize
        br.ReadUInt32(); // usize
        ushort lname = br.ReadUInt16();
        ushort lextra = br.ReadUInt16();
        _stream.Seek(lname + lextra, SeekOrigin.Current);
        long dataStart = _stream.Position;

        if (entry.Size >= ulong.MaxValue / 2 || entry.CompressedSize >= ulong.MaxValue / 2)
            throw new InvalidDataException($"条目尺寸异常：{entry.Name}");

        var data = new PartialStream(_stream, dataStart, (long)entry.CompressedSize);
        Stream? inflater = null;
        Stream src = data;
        if (entry.Method == 8)
            src = inflater = new DeflateStream(data, CompressionMode.Decompress);
        else if (entry.Method != 0)
        {
            inflater?.Dispose();
            throw new NotSupportedException($"暂不支持的 ZIP 压缩方法 {entry.Method}（条目：{entry.Name}）");
        }

        try
        {
            uint crc = Crc32.Initial;
            long total = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                crc = Crc32.Update(crc, buffer, read);
                total += read;
            }
            if (total != (long)entry.Size)
                throw new InvalidDataException($"解压后大小不符（{entry.Name}）：期望 {entry.Size} 字节，实际 {total} 字节");
            // 部分流式写入器（flags bit3）中央目录里 CRC 可能为 0，此时跳过校验
            if (entry.Crc32 != 0)
            {
                uint actual = Crc32.Final(crc);
                if (actual != entry.Crc32)
                    throw new InvalidDataException($"CRC32 校验失败（{entry.Name}）：期望 {entry.Crc32:X8}，实际 {actual:X8}");
            }
        }
        finally
        {
            inflater?.Dispose();
        }
    }

    public void ExtractAll(string destDir, IProgress<double>? progress = null,
        CancellationToken ct = default, Action<string>? log = null)
    {
        Directory.CreateDirectory(destDir);
        long total = 0;
        foreach (var e in Entries) total += (long)Math.Min(e.Size, long.MaxValue);
        long done = 0;
        int logged = 0;

        foreach (var e in Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = ArchiveSecurity.SafeJoin(destDir, e.Name);
            if (e.IsDirectory)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                Extract(e, target);
            }
            done += (long)Math.Min(e.Size, long.MaxValue);
            progress?.Report(total == 0 ? 1.0 : (double)done / total);
            if (log != null && ++logged % 500 == 0)
                log($"… 已处理 {logged}/{Entries.Count} 个条目");
        }
        progress?.Report(1.0);
    }

    public void Dispose()
    {
        if (!_leaveOpen) _stream.Dispose();
    }
}
