using System.Diagnostics;
using System.Text;

namespace FoldZip.Core;

/// <summary>外部引擎（7z.exe）定位：优先程序目录 / engines 子目录，再找常见安装位置。</summary>
public static class ExternalEngines
{
    public static string? FindSevenZip()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "7z.exe"),
            Path.Combine(AppContext.BaseDirectory, "engines", "7z.exe"),
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// 通过外部 7z.exe 支持 7z / RAR 等格式：列表用 `7z l -slt -sccUTF-8`，
/// 解压用 `7z x -o&lt;dir&gt; -y`。只读（创建 7z 请使用 7-Zip 官方工具或后续版本内置支持）。
/// </summary>
public sealed class SevenZipArchive : IArchive
{
    private readonly string _exe;
    private readonly List<ArchiveEntry> _entries;

    public ArchiveKind Kind { get; }
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    public static SevenZipArchive Open(string exe, string path, ArchiveKind kind)
    {
        var entries = ListEntries(exe, path);
        return new SevenZipArchive(exe, path, kind, entries);
    }

    private SevenZipArchive(string exe, string path, ArchiveKind kind, List<ArchiveEntry> entries)
    {
        _exe = exe;
        ArchivePath = path;
        Kind = kind;
        _entries = entries;
    }

    private static List<ArchiveEntry> ListEntries(string exe, string path)
    {
        var (code, stdout) = Run(exe, new[] { "l", "-slt", "-sccUTF-8", "-y", path });
        if (code != 0)
            throw new InvalidDataException($"7z 列表失败（退出码 {code}）：\n{Tail(stdout, 400)}");

        var list = new List<ArchiveEntry>();
        int index = 0;
        var block = new Dictionary<string, string>(StringComparer.Ordinal);
        bool filesSection = false;

        void FlushBlock()
        {
            if (block.Count == 0) return;
            // 条目块必须同时有 Size 与 Attributes（排除压缩包自身的摘要块）
            if (block.TryGetValue("Size", out var sizeText) && block.ContainsKey("Attributes"))
            {
                long.TryParse(sizeText, out long size);
                string pathName = block.TryGetValue("Path", out var p) ? p : "";
                bool isDir = (block.TryGetValue("Attributes", out var a) && a.Contains('D'))
                             || pathName.EndsWith('/') || pathName.EndsWith('\\');
                string method = block.TryGetValue("Method", out var m) ? m.Trim() : "";
                DateTime modified = block.TryGetValue("Modified", out var mo)
                    && DateTime.TryParse(mo, out var dt) ? dt : default;
                list.Add(new ArchiveEntry
                {
                    Index = index++,
                    Name = pathName.Replace('\\', '/'),
                    IsDirectory = isDir,
                    Size = size,
                    Modified = modified,
                    Method = method,
                });
            }
            block.Clear();
        }

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("----------", StringComparison.Ordinal))
            {
                FlushBlock();
                filesSection = true;
                continue;
            }
            if (!filesSection) continue;
            int eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq > 0)
                block[line[..eq]] = line[(eq + 3)..];
            else if (line.Length == 0)
                FlushBlock();
        }
        FlushBlock();
        return list;
    }

    public void ExtractEntries(IReadOnlyList<int> indexes, string destDir,
        IProgress<double>? progress = null, CancellationToken ct = default, Action<string>? log = null)
    {
        Directory.CreateDirectory(destDir);
        var all = indexes.Count == 0 || indexes.Count == Entries.Count;
        var args = new List<string> { "x", "-y", $"-o{destDir}", ArchivePath };
        if (!all)
        {
            foreach (var i in indexes)
            {
                var e = Entries[i];
                if (!e.IsDirectory) args.Add(e.Name);
            }
        }
        log?.Invoke("调用外部引擎 7z.exe 解压…");
        var (code, stdout) = Run(_exe, args);
        progress?.Report(1.0);
        if (code != 0 && code != 1) // 1 = 有警告但成功
            throw new InvalidDataException($"7z 解压失败（退出码 {code}）：\n{Tail(stdout, 400)}");
        log?.Invoke("外部引擎解压完成。");
    }

    private static (int Code, string Stdout) Run(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout.Length > 0 ? stdout : stderr);
    }

    private static string Tail(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }

    public void Dispose() { }
}
