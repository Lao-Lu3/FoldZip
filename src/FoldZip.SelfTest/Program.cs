using System.IO.Compression;
using System.Text;
using FoldZip.Core;

Console.OutputEncoding = Encoding.UTF8;
int failures = 0;

var root = Path.Combine(Path.GetTempPath(), "foldzip-selftest-" + Guid.NewGuid().ToString("N")[..8]);
var src = Path.Combine(root, "src");
Directory.CreateDirectory(src);

try
{
    BuildTestTree(src);

    // ---------- T1 自研 zip 写 → 自研 zip 读 ----------
    var t1zip = Path.Combine(root, "t1.zip");
    ZipPacker.CreateFromDirectory(t1zip, src, entryRoot: "打包目录");
    RunTest("T1 自研ZIP 写→读 往返", () =>
    {
        var outDir = Path.Combine(root, "t1-out");
        using var zr = new ZipReader(t1zip);
        zr.ExtractAll(outDir);
        CompareTrees(Path.Combine(outDir, "打包目录"), src);
        Assert(zr.Entries.Count >= 7, $"条目数不符：{zr.Entries.Count}");
    });

    // ---------- T2 外部实现（BCL）写 → 自研读 ----------
    var t2zip = Path.Combine(root, "t2.zip");
    ZipFile.CreateFromDirectory(src, t2zip, CompressionLevel.Optimal, includeBaseDirectory: false);
    RunTest("T2 互操作：.NET BCL 写 → 自研读", () =>
    {
        var outDir = Path.Combine(root, "t2-out");
        using var zr = new ZipReader(t2zip);
        zr.ExtractAll(outDir);
        CompareTrees(outDir, src);
    });

    // ---------- T3 自研写 → 外部实现（BCL）读 ----------
    var t3zip = Path.Combine(root, "t3.zip");
    ZipPacker.CreateFromDirectory(t3zip, src, entryRoot: null);
    RunTest("T3 互操作：自研写 → .NET BCL 读", () =>
    {
        var outDir = Path.Combine(root, "t3-out");
        using var za = ZipFile.OpenRead(t3zip);
        Assert(za.Entries.Count >= 6, $"BCL 读到条目数不符：{za.Entries.Count}");
        za.ExtractToDirectory(outDir);
        // entryRoot 为 null 时 ExpandDirectory 会以目录名作为包内根目录
        CompareTrees(Path.Combine(outDir, "src"), src);
    });

    // ---------- T4 Store 模式 ----------
    var t4zip = Path.Combine(root, "t4.zip");
    ZipPacker.CreateFromFileList(t4zip,
        CollectFiles(src).Select(f => (f, Path.GetRelativePath(src, f).Replace('\\', '/'))),
        ZipCreateMode.Store);
    RunTest("T4 ZIP Store 模式往返", () =>
    {
        var outDir = Path.Combine(root, "t4-out");
        using var zr = new ZipReader(t4zip);
        zr.ExtractAll(outDir);
        CompareTrees(outDir, src);
    });

    // ---------- T5 TAR 往返（含超长中文名） ----------
    var t5tar = Path.Combine(root, "t5.tar");
    {
        var items = CollectFiles(src).Select(f => (f, Path.GetRelativePath(src, f).Replace('\\', '/'))).ToList();
        var longDir = Path.Combine(src, "非常长的目录名称用来测试ustar前缀拆分机制aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        Directory.CreateDirectory(longDir);
        var longFile = Path.Combine(longDir, "超长文件名测试GNU-L条目回退机制bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.txt");
        File.WriteAllBytes(longFile, "long name tar test"u8.ToArray());
        items.Add((longFile, Path.GetRelativePath(src, longFile).Replace('\\', '/')));
        TarPacker.CreateFromFileList(t5tar, items);
    }
    RunTest("T5 TAR 写→读 往返（长名）", () =>
    {
        var outDir = Path.Combine(root, "t5-out");
        using var fs = File.OpenRead(t5tar);
        TarExtractor.ExtractTo(fs, outDir);
        CompareTrees(outDir, src, ignoreEmptyDirs: false);
    });

    // ---------- T6 GZip 往返（含 trailer 校验） ----------
    var t6gz = Path.Combine(root, "t6.gz");
    RunTest("T6 GZip 写→读（含 trailer 校验）", () =>
    {
        using (var fsOut = File.Create(t6gz))
        using (var fsIn = File.OpenRead(Path.Combine(src, "binary.bin")))
            GZipHelper.Compress(fsIn, fsOut, CompressionLevel.Optimal);
        var outPath = Path.Combine(root, "t6-out.bin");
        using (var fs = File.OpenRead(t6gz))
        {
            GZipHelper.ParseHeader(fs);
            using var outFs = File.Create(outPath);
            var counting = new CrcWriteStream(outFs);
            using (var inflater = new DeflateStream(fs, CompressionMode.Decompress, leaveOpen: true))
                inflater.CopyTo(counting, 81920);
            GZipHelper.VerifyTrailer(t6gz, counting.FinalCrc, counting.BytesWritten);
        }
        CompareFiles(outPath, Path.Combine(src, "binary.bin"));
    });

    // ---------- T7 tar.gz 链式解压（统一门面） ----------
    var t7tgz = Path.Combine(root, "t7.tar.gz");
    {
        var tmpTar = Path.Combine(root, "t7-inner.tar");
        TarPacker.CreateFromFileList(tmpTar, CollectFiles(src).Select(f => (f, Path.GetRelativePath(src, f).Replace('\\', '/'))));
        using (var fsOut = File.Create(t7tgz))
        using (var fsIn = File.OpenRead(tmpTar))
            GZipHelper.Compress(fsIn, fsOut, CompressionLevel.Optimal);
        File.Delete(tmpTar);
    }
    RunTest("T7 tar.gz 链式解压（ArchiveOpener）", () =>
    {
        var outDir = Path.Combine(root, "t7-out");
        using var archive = ArchiveOpener.Open(t7tgz);
        archive.ExtractEntries(Array.Empty<int>(), outDir);
        CompareTrees(outDir, src);
    });

    // ---------- T8 7z（外部引擎，若可用） ----------
    var exe = ExternalEngines.FindSevenZip();
    if (exe != null)
    {
        var t8 = Path.Combine(root, "t8.7z");
        RunTest("T8 7z 外部引擎（写→读→解压）", () =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("a");
            psi.ArgumentList.Add(t8);
            psi.ArgumentList.Add(src);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            Assert(p.ExitCode == 0, $"7z a 失败：退出码 {p.ExitCode}");
            var outDir = Path.Combine(root, "t8-out");
            using var archive = ArchiveOpener.Open(t8);
            archive.ExtractEntries(Array.Empty<int>(), outDir);
            // 7z a dir 会保留 src 目录名本身
            var innerRoot = Path.Combine(outDir, Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar)));
            var actualRoot = Directory.Exists(innerRoot) ? innerRoot : outDir;
            CompareTrees(actualRoot, src);
        });
    }
    else
    {
        Console.WriteLine("T8 7z 外部引擎（写→读→解压）…… SKIP（未找到 7z.exe）");
    }

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "全部通过 ✅" : $"失败 {failures} 项 ❌");
    return failures == 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

void RunTest(string name, Action act)
{
    try
    {
        act();
        Console.WriteLine($"{name} …… PASS");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"{name} …… FAIL: {ex.Message}");
    }
}

void Assert(bool cond, string message)
{
    if (!cond) throw new InvalidOperationException(message);
}

List<string> CollectFiles(string dir)
    => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();

void BuildTestTree(string dir)
{
    File.WriteAllText(Path.Combine(dir, "你好世界.txt"),
        ("你好，FoldZip！\n这是一个中文内容测试文件。" + new string('测', 3000) + "\n"),
        new UTF8Encoding(false));
    File.WriteAllBytes(Path.Combine(dir, "empty.bin"), Array.Empty<byte>());
    WriteRandom(Path.Combine(dir, "binary.bin"), 200_000);
    WriteRandom(Path.Combine(dir, "big.bin"), 3_000_000);
    var sub = Path.Combine(dir, "子目录", "深层");
    Directory.CreateDirectory(sub);
    File.WriteAllText(Path.Combine(sub, "文件.txt"), "nested file 中文", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(dir, "子目录", "另一个.txt"), "another", new UTF8Encoding(false));
}

static void WriteRandom(string path, int length)
{
    var rng = new Random(20261031);
    var buffer = new byte[64 * 1024];
    using var fs = File.Create(path);
    int remaining = length;
    while (remaining > 0)
    {
        int n = Math.Min(buffer.Length, remaining);
        rng.NextBytes(buffer.AsSpan(0, n));
        fs.Write(buffer, 0, n);
        remaining -= n;
    }
}

void CompareTrees(string a, string b, bool ignoreEmptyDirs = true)
{
    var filesA = CollectFiles(a).Select(f => Path.GetRelativePath(a, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var filesB = CollectFiles(b).Select(f => Path.GetRelativePath(b, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (ignoreEmptyDirs) filesA.RemoveWhere(f => Directory.Exists(Path.Combine(a, f)));
    Assert(filesA.SetEquals(filesB),
        $"文件名集合不一致：仅A有[{string.Join(",", filesA.Except(filesB))}] 仅B有[{string.Join(",", filesB.Except(filesA))}]");
    foreach (var rel in filesA)
        CompareFiles(Path.Combine(a, rel), Path.Combine(b, rel));
}

void CompareFiles(string a, string b)
{
    long ha = new FileInfo(a).Length;
    long hb = new FileInfo(b).Length;
    Assert(ha == hb, $"大小不一致 {a}({ha}) vs {b}({hb})");
    using var sa = File.OpenRead(a);
    using var sb = File.OpenRead(b);
    var ba = new byte[81920];
    var bb = new byte[81920];
    int ra, rb;
    while ((ra = sa.Read(ba, 0, ba.Length)) > 0)
    {
        rb = sb.Read(bb, 0, bb.Length);
        Assert(ra == rb && ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb)), $"内容不一致：{a} vs {b}");
    }
    Assert(sb.Read(bb, 0, 1) == 0, $"B 比 A 长：{b}");
}
