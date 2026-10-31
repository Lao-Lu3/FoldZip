using System.IO.Compression;

namespace FoldZip.Core;

/// <summary>GZip（.gz）：自实现头部/尾部（magic、可选字段、CRC32+ISIZE 校验），Deflate 数据体由 Inflate 完成。</summary>
public static class GZipHelper
{
    public static void Compress(Stream source, Stream dest, CompressionLevel level)
    {
        dest.Write(new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF });
        var crcSource = new CrcReadStream(source);
        using (var ds = new DeflateStream(dest, level, leaveOpen: true))
        {
            crcSource.CopyTo(ds, 81920);
        }
        Span<byte> trailer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(trailer, crcSource.FinalCrc);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(trailer.Slice(4), (uint)crcSource.BytesRead);
        dest.Write(trailer);
        dest.Flush();
    }

    /// <summary>解析 GZip 头（当前定位须在流开头），返回 (deflate 数据起点, 内部文件名或 null)。</summary>
    public static (long DataStart, string? InnerName) ParseHeader(Stream stream)
    {
        var h = new byte[10];
        if (ArchiveDetector.ReadFull(stream, h, 10) != 10)
            throw new InvalidDataException("不是有效的 GZip 文件（头部不完整）");
        if (h[0] != 0x1F || h[1] != 0x8B)
            throw new InvalidDataException("不是有效的 GZip 文件（magic 不符）");
        if (h[2] != 8)
            throw new NotSupportedException($"不支持的 GZip 压缩方法 {h[2]}（仅支持 Deflate）");
        byte flg = h[3];

        string? innerName = null;
        if ((flg & 0x04) != 0) // FEXTRA
        {
            var lenBuf = new byte[2];
            if (ArchiveDetector.ReadFull(stream, lenBuf, 2) != 2) throw new InvalidDataException("GZip 头损坏");
            int xlen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
            stream.Seek(xlen, SeekOrigin.Current);
        }
        if ((flg & 0x08) != 0) innerName = ReadNullTerminated(stream); // FNAME
        if ((flg & 0x10) != 0) ReadNullTerminated(stream);              // FCOMMENT
        if ((flg & 0x02) != 0) stream.Seek(2, SeekOrigin.Current);      // FHCRC

        return (stream.Position, innerName);
    }

    /// <summary>校验文件末尾 8 字节 trailer（CRC32 + ISIZE）。count 为解压字节数，crc 为写入侧算得的 CRC。</summary>
    public static void VerifyTrailer(string filePath, uint crc, long count)
    {
        using var fs = File.OpenRead(filePath);
        if (fs.Length < 8) throw new InvalidDataException("GZip 文件过短");
        fs.Seek(-8, SeekOrigin.End);
        var t = new byte[8];
        ArchiveDetector.ReadFull(fs, t, 8);
        uint storedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(t);
        uint storedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(4));
        if (storedCrc != crc)
            throw new InvalidDataException($"GZip CRC32 校验失败：期望 {crc:X8}，实际 {storedCrc:X8}");
        if (storedSize != (uint)(count & 0xFFFFFFFFL))
            throw new InvalidDataException($"GZip 长度校验失败：期望 {(uint)count}，实际 {storedSize}");
    }

    private static string ReadNullTerminated(Stream stream)
    {
        var ms = new MemoryStream();
        var b = new byte[1];
        while (stream.Read(b, 0, 1) == 1 && b[0] != 0)
            ms.WriteByte(b[0]);
        return NameEncoding.Decode(ms.ToArray(), utf8Flag: true);
    }
}
