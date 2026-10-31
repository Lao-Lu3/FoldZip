using System.IO.Compression;

namespace FoldZip.Core;

/// <summary>统一入口：按扩展名 + magic 打开压缩包，返回对应适配器。</summary>
public static class ArchiveOpener
{
    public static IArchive Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到压缩包文件", path);
        var kind = ArchiveDetector.DetectByPath(path);
        if (kind == ArchiveKind.Unknown)
        {
            using var sniff = File.OpenRead(path);
            kind = ArchiveDetector.DetectByMagic(sniff);
        }

        return kind switch
        {
            ArchiveKind.Zip => new ZipArchiveAdapter(path),
            ArchiveKind.Tar => new TarArchiveAdapter(path),
            ArchiveKind.GZip => new GZipArchiveAdapter(path),
            ArchiveKind.SevenZip or ArchiveKind.Rar => OpenWithExternalEngine(path, kind),
            _ => throw new NotSupportedException(
                $"无法识别的压缩包格式：{Path.GetFileName(path)}\n当前支持：zip / tar / tar.gz / tgz / gz（读取与创建）、7z / rar（需外部引擎）"),
        };
    }

    private static IArchive OpenWithExternalEngine(string path, ArchiveKind kind)
    {
        var exe = ExternalEngines.FindSevenZip()
            ?? throw new NotSupportedException(
                $"打开 {kind} 需要外部引擎：请将 7z.exe 放到程序目录（或 engines 子目录），或安装 7-Zip 后重试。\n下载地址：https://www.7-zip.org");
        return SevenZipArchive.Open(exe, path, kind);
    }
}

public sealed class ZipArchiveAdapter : IArchive
{
    private readonly ZipReader _reader;

    public ArchiveKind Kind => ArchiveKind.Zip;
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    public ZipArchiveAdapter(string path)
    {
        ArchivePath = path;
        _reader = new ZipReader(path);
        Entries = _reader.Entries.Select(e => new ArchiveEntry
        {
            Index = e.Index,
            Name = e.Name,
            IsDirectory = e.IsDirectory,
            Size = (long)Math.Min(e.Size, long.MaxValue),
            CompressedSize = (long)Math.Min(e.CompressedSize, long.MaxValue),
            Modified = e.Modified,
            Method = e.Method switch
            {
                0 => "Store",
                8 => "Deflate",
                9 => "Deflate64",
                12 => "BZip2",
                14 => "LZMA",
                93 => "Zstd",
                99 => "AES(加密)",
                var m => $"方法{m}",
            },
        }).ToList();
    }

    public void ExtractEntries(IReadOnlyList<int> indexes, string destDir,
        IProgress<double>? progress = null, CancellationToken ct = default, Action<string>? log = null)
    {
        if (indexes.Count == 0 || indexes.Count == Entries.Count)
        {
            _reader.ExtractAll(destDir, progress, ct, log);
            return;
        }
        Directory.CreateDirectory(destDir);
        long total = 0, done = 0;
        var targets = new List<(ZipEntry Entry, string Target)>();
        foreach (var i in indexes)
        {
            var e = _reader.Entries[i];
            if (e.IsDirectory) continue;
            total += (long)Math.Min(e.Size, long.MaxValue);
            targets.Add((e, ArchiveSecurity.SafeJoin(destDir, e.Name)));
        }
        foreach (var (e, target) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            using (var output = File.Create(target))
                _reader.ExtractInto(e, output);
            done += (long)Math.Min(e.Size, long.MaxValue);
            progress?.Report(total == 0 ? 1.0 : (double)done / total);
        }
        progress?.Report(1.0);
    }

    public void Dispose() => _reader.Dispose();
}

public sealed class TarArchiveAdapter : IArchive
{
    private readonly List<ArchiveEntry> _entries = new();

    public ArchiveKind Kind => ArchiveKind.Tar;
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    public TarArchiveAdapter(string path)
    {
        ArchivePath = path;
        // 列表需要一次完整前向扫描
        using var fs = File.OpenRead(path);
        var reader = new TarReader(fs);
        while (reader.ReadNext() is { } e)
        {
            _entries.Add(new ArchiveEntry
            {
                Index = e.Index,
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Size = e.Size,
                Modified = e.Modified,
                Method = e.TypeFlag switch
                {
                    "0" or "\0" => "Store",
                    "5" => "目录",
                    var t => $"类型{t}",
                },
            });
            reader.SkipData(e.Size);
        }
    }

    public void ExtractEntries(IReadOnlyList<int> indexes, string destDir,
        IProgress<double>? progress = null, CancellationToken ct = default, Action<string>? log = null)
    {
        bool all = indexes.Count == 0 || indexes.Count == Entries.Count;
        var fi = new FileInfo(ArchivePath);
        using var fs = File.OpenRead(ArchivePath);

        if (all)
        {
            TarExtractor.ExtractTo(fs, destDir, fi.Length, progress, ct, log);
            return;
        }

        // 子集解压：单次前向扫描，只落盘选中条目
        Directory.CreateDirectory(destDir);
        var wanted = indexes.ToHashSet();
        var reader = new TarReader(fs);
        long done = 0;
        long total = Entries.Where(e => wanted.Contains(e.Index)).Sum(e => Math.Max(0, e.Size));
        while (reader.ReadNext() is { } e)
        {
            ct.ThrowIfCancellationRequested();
            if (!e.IsDirectory && wanted.Contains(e.Index))
            {
                var target = ArchiveSecurity.SafeJoin(destDir, e.Name);
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                using var output = File.Create(target);
                reader.CopyDataTo(output, e.Size);
                done += e.Size;
                progress?.Report(total == 0 ? 1.0 : (double)done / total);
            }
            else
            {
                reader.SkipData(e.Size);
            }
        }
        progress?.Report(1.0);
    }

    public void Dispose() { }
}

public sealed class GZipArchiveAdapter : IArchive
{
    private readonly List<ArchiveEntry> _entries = new();
    private readonly string _innerName;

    public ArchiveKind Kind => ArchiveKind.GZip;
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    public GZipArchiveAdapter(string path)
    {
        ArchivePath = path;
        using var fs = File.OpenRead(path);
        var (_, inner) = GZipHelper.ParseHeader(fs);
        var fileName = Path.GetFileName(path);
        _innerName = inner ?? Path.GetFileNameWithoutExtension(fileName);
        _entries.Add(new ArchiveEntry
        {
            Index = 0,
            Name = _innerName,
            IsDirectory = false,
            Size = -1,
            Method = "Deflate",
        });
    }

    public void ExtractEntries(IReadOnlyList<int> indexes, string destDir,
        IProgress<double>? progress = null, CancellationToken ct = default, Action<string>? log = null)
    {
        Directory.CreateDirectory(destDir);
        var target = ArchiveSecurity.SafeJoin(destDir, _innerName);

        using (var fs = File.OpenRead(ArchivePath))
        {
            var (dataStart, _) = GZipHelper.ParseHeader(fs);
            using var output = File.Create(target);
            var counting = new CrcWriteStream(output);
            using (var inflater = new DeflateStream(fs, CompressionMode.Decompress, leaveOpen: true))
            {
                inflater.CopyTo(counting, 81920);
            }
            GZipHelper.VerifyTrailer(ArchivePath, counting.FinalCrc, counting.BytesWritten);
        }
        log?.Invoke($"GZip 解压完成：{Path.GetFileName(target)}");

        // .tar.gz / .tgz → 继续解 TAR 到同一目录
        if (_innerName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("检测到内部为 TAR，继续解包…");
            using (var tarFs = File.OpenRead(target))
            {
                TarExtractor.ExtractTo(tarFs, destDir, tarFs.Length, progress, ct, log);
            }
            try { File.Delete(target); log?.Invoke("已删除中间 .tar 文件。"); } catch { }
        }
        progress?.Report(1.0);
    }

    public void Dispose() { }
}
