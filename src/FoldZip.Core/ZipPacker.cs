namespace FoldZip.Core;

/// <summary>常用打包便捷方法（文件系统 → 压缩包）。</summary>
public static class ZipPacker
{
    public static void CreateFromFileList(string zipPath,
        IEnumerable<(string SourcePath, string EntryName)> items, ZipCreateMode mode = ZipCreateMode.Auto)
    {
        using var fs = File.Create(zipPath);
        using var zw = new ZipWriter(fs, mode);
        foreach (var (src, entry) in items)
        {
            using var input = File.OpenRead(src);
            zw.AddEntry(entry, input, File.GetLastWriteTime(src), externalAttributes: 0x20);
        }
    }

    /// <summary>把目录（含子目录）展开为条目列表。entryRoot 为包内根目录名（可空）。</summary>
    public static List<(string SourcePath, string EntryName)> ExpandDirectory(string dir, string? entryRoot)
    {
        var list = new List<(string, string)>();
        var rootName = string.IsNullOrWhiteSpace(entryRoot)
            ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : entryRoot.Trim('/');
        Walk(dir, rootName);
        return list;

        void Walk(string path, string entryDir)
        {
            foreach (var d in Directory.EnumerateDirectories(path))
            {
                var sub = entryDir + "/" + Path.GetFileName(d);
                list.Add(("", sub.TrimEnd('/') + "/")); // 目录条目
                Walk(d, sub);
            }
            foreach (var f in Directory.EnumerateFiles(path))
                list.Add((f, entryDir + "/" + Path.GetFileName(f)));
        }
    }

    /// <summary>把上面的展开结果写入 ZIP（自动跳过空的目录占位项）。</summary>
    public static void CreateFromDirectory(string zipPath, string dir, string? entryRoot = null,
        ZipCreateMode mode = ZipCreateMode.Auto)
    {
        using var fs = File.Create(zipPath);
        using var zw = new ZipWriter(fs, mode);
        foreach (var (src, entry) in ExpandDirectory(dir, entryRoot))
        {
            if (src.Length == 0)
            {
                zw.AddEntry(entry, null, DateTime.Now, externalAttributes: 0x10);
            }
            else
            {
                using var input = File.OpenRead(src);
                zw.AddEntry(entry, input, File.GetLastWriteTime(src), externalAttributes: 0x20);
            }
        }
    }
}
