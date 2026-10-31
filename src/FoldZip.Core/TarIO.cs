namespace FoldZip.Core;

/// <summary>TAR 解压到目录（供 TarAdapter 与 .tar.gz 链式解压共用）。</summary>
public static class TarExtractor
{
    public static void ExtractTo(Stream tarStream, string destDir, long? totalBytes = null,
        IProgress<double>? progress = null, CancellationToken ct = default, Action<string>? log = null)
    {
        Directory.CreateDirectory(destDir);
        var reader = new TarReader(tarStream);
        long done = 0;
        int count = 0;

        TarEntry? entry;
        while ((entry = reader.ReadNext()) != null)
        {
            ct.ThrowIfCancellationRequested();
            var target = ArchiveSecurity.SafeJoin(destDir, entry.Name);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                reader.SkipData(entry.Size);
            }
            else
            {
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                using var fs = File.Create(target);
                reader.CopyDataTo(fs, entry.Size);
            }
            done += entry.Size;
            count++;
            if (totalBytes is > 0) progress?.Report(Math.Min(1.0, (double)done / totalBytes.Value));
            if (log != null && count % 500 == 0) log($"… 已处理 {count} 个条目");
        }
        progress?.Report(1.0);
    }
}

public static class TarPacker
{
    public static void CreateFromFileList(string tarPath,
        IEnumerable<(string SourcePath, string EntryName)> items)
    {
        using var fs = File.Create(tarPath);
        using var tw = new TarWriter(fs);
        foreach (var (src, entry) in items)
        {
            if (src.Length == 0)
            {
                tw.AddEntry(entry, null, 0, DateTime.Now, isDirectory: true);
            }
            else
            {
                var fi = new FileInfo(src);
                using var input = File.OpenRead(src);
                tw.AddEntry(entry, input, fi.Length, fi.LastWriteTime, isDirectory: false);
            }
        }
    }

    public static List<(string SourcePath, string EntryName)> ExpandDirectory(string dir, string? entryRoot)
        => ZipPacker.ExpandDirectory(dir, entryRoot);
}
