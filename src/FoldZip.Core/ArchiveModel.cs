using System.Buffers.Binary;

namespace FoldZip.Core;

public enum ArchiveKind
{
    Unknown,
    Zip,
    Tar,
    GZip,
    SevenZip,
    Rar,
}

/// <summary>统一条目模型（供界面与各格式适配器使用）。</summary>
public sealed class ArchiveEntry
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    /// <summary>原始大小；未知时为 -1。</summary>
    public long Size { get; init; }
    public long CompressedSize { get; init; } = -1;
    public DateTime Modified { get; init; }
    public string Method { get; init; } = "";
}

/// <summary>统一的已打开压缩包接口。</summary>
public interface IArchive : IDisposable
{
    ArchiveKind Kind { get; }
    string ArchivePath { get; }
    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>解压指定条目（按索引）到目标目录。indexes 为空时解压全部。</summary>
    void ExtractEntries(IReadOnlyList<int> indexes, string destDir,
        IProgress<double>? progress = null, CancellationToken ct = default, Action<string>? log = null);
}

public static class ArchiveSecurity
{
    /// <summary>把压缩包内条目名安全地拼接到目标目录下，拒绝绝对路径与 ".." 穿越。</summary>
    public static string SafeJoin(string rootDir, string entryName)
    {
        entryName = entryName.Replace('\\', '/');
        while (entryName.StartsWith('/')) entryName = entryName[1..];
        foreach (var part in entryName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
                throw new IOException($"检测到路径穿越条目，已拒绝解压：{entryName}");
            if (part.EndsWith(':'))
                throw new IOException($"检测到非法盘符条目，已拒绝解压：{entryName}");
        }
        var full = Path.GetFullPath(Path.Combine(rootDir, entryName));
        var root = Path.GetFullPath(rootDir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"检测到路径穿越条目，已拒绝解压：{entryName}");
        return full;
    }
}

public static class ArchiveDetector
{
    /// <summary>按扩展名识别（.tar.gz / .tgz 归为 GZip）。</summary>
    public static ArchiveKind DetectByPath(string path)
    {
        var lower = Path.GetFileName(path).ToLowerInvariant();
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") || lower.EndsWith(".taz")) return ArchiveKind.GZip;
        var ext = Path.GetExtension(lower);
        return ext switch
        {
            ".zip" or ".jar" or ".apk" or ".zipx" or ".docx" or ".xlsx" or ".pptx" or ".vsix" or ".nupkg" => ArchiveKind.Zip,
            ".tar" => ArchiveKind.Tar,
            ".gz" => ArchiveKind.GZip,
            ".7z" => ArchiveKind.SevenZip,
            ".rar" => ArchiveKind.Rar,
            _ => ArchiveKind.Unknown,
        };
    }

    public static ArchiveKind DetectByMagic(Stream stream)
    {
        var head = new byte[8];
        long saved = stream.Position;
        int n = ReadFull(stream, head, 8);
        stream.Seek(saved, SeekOrigin.Begin);
        if (n >= 4)
        {
            if (head[0] == 'P' && head[1] == 'K' && (head[2] == 3 || head[2] == 5 || head[2] == 7)) return ArchiveKind.Zip;
            if (head[0] == 0x1F && head[1] == 0x8B) return ArchiveKind.GZip;
            if (n >= 6 && head[0] == '7' && head[1] == 'z' && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C) return ArchiveKind.SevenZip;
            if (head[0] == 'R' && head[1] == 'a' && head[2] == 'r' && head[3] == '!') return ArchiveKind.Rar;
        }
        if (n >= 265)
        {
            var magic = new byte[265];
            stream.Seek(saved, SeekOrigin.Begin);
            ReadFull(stream, magic, 265);
            if (magic[257] == (byte)'u' && magic[258] == (byte)'s' && magic[259] == (byte)'t' && magic[260] == (byte)'a' && magic[261] == (byte)'r')
                return ArchiveKind.Tar;
        }
        return ArchiveKind.Unknown;
    }

    internal static int ReadFull(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = s.Read(buffer, total, count - total);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }
}
