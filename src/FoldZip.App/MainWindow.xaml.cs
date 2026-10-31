using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FoldZip.Core;
using Microsoft.Win32;

namespace FoldZip.App;

public partial class MainWindow : Window
{
    public const string Version = "0.1.0";

    private sealed class EntryRow
    {
        public string Name { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string PackedText { get; init; } = "";
        public string ModifiedText { get; init; } = "";
        public string MethodText { get; init; } = "";
        public int Index { get; init; } = -1;          // 已打开压缩包时的条目索引
        public string? SourcePath { get; init; }       // 新建模式时的源文件路径
        public bool IsDir { get; init; }
        public string IconText { get; init; } = "\uE7C3"; // 文件
        public long SizeVal { get; init; }
        public DateTime ModifiedVal { get; init; }
    }

    private sealed class StagedItem
    {
        public string SourcePath { get; init; } = "";
        public string EntryName { get; init; } = "";
        public long Size { get; init; }
        public bool IsDir { get; init; }
    }

    private IArchive? _archive;
    private string? _archivePath;
    private readonly List<StagedItem> _staged = new();
    private CancellationTokenSource? _cts;
    private bool _dark;
    private readonly AppSettings _settings;
    private string? _sortKey;
    private bool _sortDesc;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        // 首次运行（无设置文件）跟随系统深浅色
        if (!SettingsStore.LoadedFromFile) _settings.DarkTheme = SystemPrefersDark();
        ApplyTheme(_settings.DarkTheme);
        if (!double.IsNaN(_settings.Width) && _settings.Width >= MinWidth)
        {
            Width = _settings.Width;
            Height = _settings.Height;
            if (!double.IsNaN(_settings.Left) && Math.Abs(_settings.Left) < 10000)
            {
                Left = _settings.Left;
                Top = _settings.Top;
            }
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = $"v{Version}";
        Log($"FoldZip v{Version} 已启动。");
        Log("支持：zip / tar / tar.gz / tgz / gz（读取与创建）、7z / rar（需把 7z.exe 放到程序目录）");
        Log("提示：可把文件或压缩包直接拖进本窗口。");
        UpdateUi();
    }

    // ---------- 日志 ----------
    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(message));
            return;
        }
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        LogBox.ScrollToEnd();
    }

    // ---------- 设置持久化与退出保护 ----------
    private void SaveSettings()
    {
        _settings.DarkTheme = _dark;
        if (WindowState == WindowState.Normal)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = Width;
            _settings.Height = Height;
            _settings.Maximized = false;
        }
        else
        {
            _settings.Maximized = WindowState == WindowState.Maximized;
        }
        SettingsStore.Save(_settings);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_staged.Count > 0)
        {
            var r = MessageBox.Show(this,
                $"新建模式还有 {_staged.Count} 个未保存的文件，确定退出吗？",
                "FoldZip", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        SaveSettings();
    }

    // ---------- 标题栏窗口控制 ----------
    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // 最大化时内容会越出屏幕边缘（无边框窗口特性），补内边距
        bool max = WindowState == WindowState.Maximized;
        RootPanel.Margin = max ? new Thickness(7) : new Thickness(0);
        BtnMax.Content = max ? "\uE923" : "\uE922"; // 还原 / 最大化 图标
    }

    // ---------- 主题 ----------
    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        var res = Application.Current.Resources;
        (string key, string light, string darkColor)[] palette =
        {
            // Windows 11 资源管理器风格：中性灰、系统蓝、同色标题栏
            ("Brush.Window",      "#F3F3F3", "#202020"),
            ("Brush.Panel",       "#FFFFFF", "#272727"),
            ("Brush.Text",        "#1B1B1B", "#F2F2F2"),
            ("Brush.SubText",     "#616161", "#A6A6A6"),
            ("Brush.Border",      "#E5E5E5", "#3B3B3B"),
            ("Brush.Accent",      "#0067C0", "#4CC2FF"),
            ("Brush.AccentSoft",  "#CCE8FF", "#0C5A8C"),
            ("Brush.ButtonPressed","#E9E9E9", "#3A3A3A"),
            ("Brush.LogBg",       "#FFFFFF", "#272727"),
            ("Brush.HeaderBg",    "#FFFFFF", "#272727"),
            ("Brush.HoverBg",     "#F1F1F1", "#323232"),
            ("Brush.AltRow",      "#FFFFFF", "#272727"),
            ("Brush.TitleBg",     "#F3F3F3", "#202020"),
            ("Brush.TitleHover",  "#E5E5E5", "#3D3D3D"),
            ("Brush.TitlePressed","#D5D5D5", "#2E2E2E"),
        };
        foreach (var (key, light, darkColor) in palette)
            res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : light));
        Title = $"FoldZip v{Version}";
    }

    // ---------- 界面状态 ----------
    private int SelectedCount => ArchiveList.SelectedItems.Count;

    private void UpdateUi()
    {
        bool browsing = _archive != null;
        string sel = SelectedCount > 0 ? $"，已选中 {SelectedCount} 项" : "";
        StatusText.Text = browsing
            ? $"已打开：{Path.GetFileName(_archivePath)}（{_archive!.Entries.Count} 个条目{sel}）"
            : _staged.Count > 0
                ? $"新建模式：已添加 {_staged.Count} 项，点「保存压缩包」生成文件{sel}"
                : "就绪";

        BtnSave.IsEnabled = !browsing && _staged.Count > 0;
        BtnAddFiles.IsEnabled = !browsing;
        BtnAddFolder.IsEnabled = !browsing;
        BtnRemove.IsEnabled = !browsing && _staged.Count > 0;
        BtnExtractAll.IsEnabled = browsing;
        BtnExtractSelected.IsEnabled = browsing && SelectedCount > 0;
        BtnCancel.IsEnabled = _cts != null;
        Title = $"FoldZip v{Version}"
            + (browsing ? $" — {Path.GetFileName(_archivePath)}" : "");
    }

    private void RefreshList()
    {
        List<EntryRow> rows;
        if (_archive != null)
        {
            rows = _archive.Entries.Select(e => new EntryRow
            {
                Index = e.Index,
                Name = e.Name,
                IconText = e.IsDirectory ? "\uE8B7" : "\uE7C3",
                IsDir = e.IsDirectory,
                SizeVal = e.Size,
                ModifiedVal = e.Modified,
                SizeText = e.IsDirectory ? "目录" : e.Size < 0 ? "—" : HumanSize(e.Size),
                PackedText = e.CompressedSize < 0 ? "—" : HumanSize(e.CompressedSize),
                ModifiedText = e.Modified == default ? "—" : e.Modified.ToString("yyyy-MM-dd HH:mm"),
                MethodText = e.Method,
            }).ToList();
        }
        else
        {
            rows = _staged.Select((s, i) => new EntryRow
            {
                Index = i,
                Name = s.EntryName,
                IconText = s.IsDir ? "\uE8B7" : "\uE7C3",
                IsDir = s.IsDir,
                SizeVal = s.Size,
                SizeText = s.IsDir ? "目录" : HumanSize(s.Size),
                PackedText = "—",
                ModifiedText = "—",
                MethodText = "待写入",
                SourcePath = s.SourcePath,
            }).ToList();
        }

        rows = SortRows(rows);
        ArchiveList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<EntryRow> SortRows(List<EntryRow> rows)
    {
        if (_sortKey == null) return rows;
        return _sortKey switch
        {
            "Size" => _sortDesc ? rows.OrderByDescending(r => r.SizeVal).ToList()
                                : rows.OrderBy(r => r.SizeVal).ToList(),
            "Packed" => _sortDesc ? rows.OrderByDescending(r => r.PackedText).ToList()
                                  : rows.OrderBy(r => r.PackedText).ToList(),
            "Modified" => _sortDesc ? rows.OrderByDescending(r => r.ModifiedVal).ToList()
                                    : rows.OrderBy(r => r.ModifiedVal).ToList(),
            "Method" => _sortDesc ? rows.OrderByDescending(r => r.MethodText, StringComparer.CurrentCulture).ToList()
                                  : rows.OrderBy(r => r.MethodText, StringComparer.CurrentCulture).ToList(),
            _ => _sortDesc ? rows.OrderByDescending(r => r.Name, StringComparer.CurrentCulture).ToList()
                           : rows.OrderBy(r => r.Name, StringComparer.CurrentCulture).ToList(),
        };
    }

    private void ColHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is GridViewColumnHeader h && h.Tag is string key)
        {
            if (_sortKey == key) _sortDesc = !_sortDesc;
            else { _sortKey = key; _sortDesc = false; }
            RefreshList();
            Log($"已按「{h.Content}」{(_sortDesc ? "降序" : "升序")}排列。");
        }
    }

    private void MenuCopyName_Click(object sender, RoutedEventArgs e)
    {
        var names = ArchiveList.SelectedItems.Cast<EntryRow>().Select(r => r.Name).ToList();
        if (names.Count == 0) { Log("提示：请先选中条目。"); return; }
        try
        {
            Clipboard.SetText(string.Join("\n", names));
            Log($"已复制 {names.Count} 个名称。");
        }
        catch (Exception ex) { Log($"[错误] 复制失败：{ex.Message}"); }
    }

    private void ArchiveList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateUi();

    private static string HumanSize(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F2} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }

    // ---------- 打开压缩包 ----------
    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "打开压缩包",
            Filter = "压缩包|*.zip;*.jar;*.apk;*.7z;*.rar;*.tar;*.gz;*.tgz;*.tar.gz|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
            OpenArchive(dlg.FileName);
    }

    private async void OpenArchive(string path)
    {
        Log($"打开 {path} …");
        SetBusy(true, $"正在打开 {Path.GetFileName(path)}");
        try
        {
            var archive = await Task.Run(() => ArchiveOpener.Open(path));
            var count = archive.Entries.Count;
            _archive?.Dispose();
            _archive = archive;
            _archivePath = path;
            _staged.Clear();
            RefreshList();
            Log($"✅ 已打开：{Path.GetFileName(path)}，共 {count} 个条目。选中条目后可「解压选中」或「解压到…」。");
        }
        catch (Exception ex)
        {
            Log($"[错误] 打开失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- 新建压缩包 ----------
    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        _archive?.Dispose();
        _archive = null;
        _archivePath = null;
        _staged.Clear();
        RefreshList();
        Log("已切换到新建模式：点「添加文件 / 添加文件夹」或直接拖入，然后「保存压缩包」。");
        UpdateUi();
    }

    private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_archive != null) { Log("提示：已打开压缩包，请先「新建压缩包」再添加。"); return; }
        var dlg = new OpenFileDialog { Title = "添加文件", Multiselect = true, Filter = "所有文件|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) AddStagedFile(f, entryNameOverride: null);
        RefreshList();
        UpdateUi();
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_archive != null) { Log("提示：已打开压缩包，请先「新建压缩包」再添加。"); return; }
        var dlg = new OpenFolderDialog { Title = "添加文件夹" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var expanded = ZipPacker.ExpandDirectory(dlg.FolderName, entryRoot: null);
            int n = 0;
            foreach (var (src, entry) in expanded)
            {
                if (src.Length == 0)
                    _staged.Add(new StagedItem { SourcePath = "", EntryName = entry, IsDir = true });
                else
                    _staged.Add(new StagedItem
                    {
                        SourcePath = src,
                        EntryName = entry,
                        Size = new FileInfo(src).Length,
                    });
                n++;
            }
            Log($"✅ 已添加文件夹：{Path.GetFileName(dlg.FolderName)}（{n} 项）");
        }
        catch (Exception ex)
        {
            Log($"[错误] 添加文件夹失败：{ex.Message}");
        }
        RefreshList();
        UpdateUi();
    }

    private void AddStagedFile(string path, string? entryNameOverride)
    {
        try
        {
            var name = entryNameOverride ?? Path.GetFileName(path);
            _staged.Add(new StagedItem
            {
                SourcePath = path,
                EntryName = name,
                Size = new FileInfo(path).Length,
            });
        }
        catch (Exception ex)
        {
            Log($"[错误] 无法添加 {path}：{ex.Message}");
        }
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = ArchiveList.SelectedItems.Cast<EntryRow>().Where(r => r.Index >= 0).ToList();
        if (selected.Count == 0) return;
        var names = selected.Select(r => r.Name).ToHashSet();
        _staged.RemoveAll(s => names.Contains(s.EntryName));
        RefreshList();
        UpdateUi();
        Log($"已移除 {selected.Count} 项。");
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_archive != null || _staged.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Title = "保存压缩包",
            Filter = "ZIP 压缩包|*.zip|TAR 归档|*.tar|GZip 压缩|*.gz|所有支持格式|*.zip;*.tar;*.gz",
            FileName = "archive.zip",
        };
        if (dlg.ShowDialog(this) != true) return;
        string path = dlg.FileName;
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".gz" && _staged.Count(s => !s.IsDir) != 1)
        {
            Log("[错误] .gz 只能包含单个文件，请改用 .zip / .tar，或只保留一个文件。");
            return;
        }

        var snapshot = _staged.Where(s => !s.IsDir).ToList();
        SetBusy(true, $"正在写入 {Path.GetFileName(path)}（不定长进度，请看日志）");
        Log($"开始写入 {path}（{snapshot.Count} 个文件）…");
        try
        {
            await Task.Run(() =>
            {
                switch (ext)
                {
                    case ".zip":
                        ZipPacker.CreateFromFileList(path,
                            snapshot.Select(s => (s.SourcePath, s.EntryName)), ZipCreateMode.Auto);
                        break;
                    case ".tar":
                        TarPacker.CreateFromFileList(path,
                            snapshot.Select(s => (s.SourcePath, s.EntryName)));
                        break;
                    case ".gz":
                    {
                        var item = snapshot[0];
                        using var input = File.OpenRead(item.SourcePath);
                        using var output = File.Create(path);
                        GZipHelper.Compress(input, output, System.IO.Compression.CompressionLevel.Optimal);
                        break;
                    }
                    default:
                        throw new NotSupportedException($"不支持的扩展名：{ext}");
                }
            });
            Log($"✅ 保存完成：{path}（{HumanSize(new FileInfo(path).Length)}）");
        }
        catch (Exception ex)
        {
            Log($"[错误] 保存失败：{ex.Message}");
            try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); } catch { }
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- 解压 ----------
    private void BtnExtractAll_Click(object sender, RoutedEventArgs e) => Extract(selectedOnly: false);
    private void BtnExtractSelected_Click(object sender, RoutedEventArgs e) => Extract(selectedOnly: true);

    private async void Extract(bool selectedOnly)
    {
        if (_archive == null) return;
        var dlg = new OpenFolderDialog { Title = "选择解压目标目录" };
        if (dlg.ShowDialog(this) != true) return;
        string dest = dlg.FolderName;

        List<int> indexes;
        if (selectedOnly)
        {
            indexes = ArchiveList.SelectedItems.Cast<EntryRow>()
                .Where(r => r.Index >= 0).Select(r => r.Index).ToList();
            if (indexes.Count == 0) { Log("提示：请先在列表中选中要解压的条目。"); return; }
        }
        else
        {
            indexes = new List<int>();
        }

        _cts = new CancellationTokenSource();
        UpdateUi();
        Log($"开始解压到 {dest}（{(_archive.Kind switch
        {
            ArchiveKind.Zip => "zip",
            ArchiveKind.Tar => "tar",
            ArchiveKind.GZip => "gzip",
            ArchiveKind.SevenZip => "7z（外部引擎）",
            ArchiveKind.Rar => "rar（外部引擎）",
            _ => "未知",
        })}）…");
        SetBusy(true, "正在解压…");
        var progress = new Progress<double>(v =>
        {
            Progress.Value = v * 100;
            ProgressText.Text = $"{v * 100:F0}%";
        });
        try
        {
            await Task.Run(() => _archive.ExtractEntries(indexes, dest, progress, _cts.Token, s => Log(s)));
            Log($"✅ 解压完成 → {dest}");
        }
        catch (OperationCanceledException)
        {
            Log("⏹ 已取消当前操作。");
        }
        catch (Exception ex)
        {
            Log($"[错误] 解压失败：{ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            ProgressText.Text = "";
            SetBusy(false);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("已请求取消…");
    }

    // ---------- 其他 ----------
    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(!_dark);
        Log(_dark ? "已切换到深色主题。" : "已切换到浅色主题。");
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            $"FoldZip v{Version}\n\n" +
            "免费开源的 Windows 压缩工具（MIT 许可证）。\n" +
            "内置自研内核：zip / tar / tar.gz / tgz / gz 读取与创建；\n" +
            "7z / rar 通过外部 7z.exe 引擎读取（放到程序目录即可）。\n\n" +
            "特性：中文文件名兼容（UTF-8 / GB18030）、CRC32 校验、路径穿越防护、ZIP64 读取。",
            "关于 FoldZip", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
            Log("已复制全部日志。");
        }
        catch (Exception ex)
        {
            Log($"[错误] 复制失败：{ex.Message}");
        }
    }

    // ---------- 拖拽 ----------
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        // 单个压缩包文件拖入 → 直接打开
        if (files.Length == 1 && File.Exists(files[0]))
        {
            var kind = ArchiveDetector.DetectByPath(files[0]);
            if (kind != ArchiveKind.Unknown)
            {
                OpenArchive(files[0]);
                return;
            }
        }

        if (_archive != null)
        {
            Log("提示：浏览模式不支持拖拽添加；要打包新文件请先「新建压缩包」。");
            return;
        }
        foreach (var f in files)
        {
            if (Directory.Exists(f))
            {
                try
                {
                    foreach (var (src, entry) in ZipPacker.ExpandDirectory(f, entryRoot: null))
                    {
                        if (src.Length == 0)
                            _staged.Add(new StagedItem { SourcePath = "", EntryName = entry, IsDir = true });
                        else
                            _staged.Add(new StagedItem
                            {
                                SourcePath = src,
                                EntryName = entry,
                                Size = new FileInfo(src).Length,
                            });
                    }
                    Log($"已拖入文件夹：{Path.GetFileName(f)}");
                }
                catch (Exception ex)
                {
                    Log($"[错误] 拖入文件夹失败：{ex.Message}");
                }
            }
            else if (File.Exists(f))
            {
                AddStagedFile(f, entryNameOverride: null);
            }
        }
        RefreshList();
        UpdateUi();
    }

    // ---------- 忙碌状态 ----------
    private void SetBusy(bool busy, string? statusText = null)
    {
        Progress.IsIndeterminate = busy && Progress.Value <= 0;
        if (!busy) Progress.Value = 0;
        if (statusText != null && busy) StatusText.Text = statusText;
        BtnNew.IsEnabled = BtnOpen.IsEnabled = BtnTheme.IsEnabled = BtnAbout.IsEnabled = !busy
            || true; // 打开/主题始终可用
        BtnSave.IsEnabled = !busy && !(_archive != null) && _staged.Count > 0;
        BtnAddFiles.IsEnabled = !busy && _archive == null;
        BtnAddFolder.IsEnabled = !busy && _archive == null;
        BtnRemove.IsEnabled = !busy && _archive == null && _staged.Count > 0;
        BtnExtractAll.IsEnabled = !busy && _archive != null;
        BtnExtractSelected.IsEnabled = !busy && _archive != null && ArchiveList.SelectedItems.Count > 0;
        BtnCancel.IsEnabled = busy && _cts != null;
    }
}
