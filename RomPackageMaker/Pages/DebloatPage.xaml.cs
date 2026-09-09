using RomPackageMaker.Application;
using RomPackageMaker.Engine;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class DebloatPage : Page
{
    private string? _workspaceDir;
    private List<WorkspaceApp> _apps = new();
    private bool _busy;
    private bool _userTouchedWorkspace;

    public ObservableCollection<WorkspaceApp> View { get; } = new();

    public DebloatPage()
    {
        InitializeComponent();
        // 缓存页面：切换导航后保留扫描结果与勾选状态
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 未手动选择过工作目录时，跟随会话最近使用的工作目录（如刚解包的那个）
        if (!_userTouchedWorkspace)
        {
            string? ws = WorkspaceState.CurrentWorkspace ?? AppSettings.Current.DefaultWorkspace;
            if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws) && WorkspaceBox.Text != ws)
            {
                WorkspaceBox.Text = ws;
            }
        }
    }

    private static void InitializePicker(object picker)
    {
        if (App.MainWindow is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        }
    }

    private async void BrowseWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            WorkspaceBox.Text = folder.Path;
            _userTouchedWorkspace = true;
            WorkspaceState.CurrentWorkspace = folder.Path;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string dir = WorkspaceBox.Text.Trim();
        if (!Directory.Exists(dir))
        {
            StatusText.Text = "工作目录不存在。";
            return;
        }
        _workspaceDir = dir;
        _userTouchedWorkspace = true;
        WorkspaceState.CurrentWorkspace = dir;

        _busy = true;
        ScanButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        StatusText.Text = "正在扫描预装应用...";
        try
        {
            // 大工作目录（整个 system 树）扫描耗时，放后台线程避免卡界面
            _apps = await Task.Run(() => WorkspaceScanner.ScanApps(dir));
            foreach (var app in _apps)
            {
                app.PropertyChanged += App_PropertyChanged;
            }
            ApplyFilter();
            UpdateStatus();
            EnrichApkInfo(); // 后台解析 APK manifest，完成后经 INPC 刷新列表副行
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
        }
    }

    /// <summary>后台逐个解析主 APK 的 AndroidManifest（包名 / 版本 / 权限等），经 INPC 刷新列表。</summary>
    private async void EnrichApkInfo()
    {
        var pending = _apps.Where(a => a.MainApkPath is not null).ToList();
        if (pending.Count == 0) return;

        int ok = 0, failed = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (var app in pending)
                {
                    try
                    {
                        app.ApkInfo = ApkParser.Parse(app.MainApkPath!);
                        // 依据包名查询删除风险知识库（列表副行 / 删除确认分级提示）
                        app.Risk = DebloatSafetyService.GetRisk(app.ApkInfo.PackageName);
                        app.RiskReason = DebloatSafetyService.GetReason(app.ApkInfo.PackageName) ?? string.Empty;
                        ok++;
                    }
                    catch
                    {
                        app.ParseFailed = true;
                        app.ApkInfo = null; // 触发 PackageText 刷新为失败占位
                        failed++;
                    }
                }
            });
        }
        catch
        {
            // 页面已失效等，忽略
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (failed > 0)
                StatusText.Text = $"扫描完成 · APK 解析：成功 {ok}，失败 {failed}（可能是非标准 manifest）";
        });
    }

    /// <summary>勾选状态变化时即时刷新统计与「删除选中」按钮。</summary>
    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceApp.IsSelected))
        {
            UpdateStatus();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        View.Clear();
        string q = SearchBox.Text.Trim();
        foreach (var app in _apps)
        {
            if (q.Length > 0 &&
                !app.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !app.RelPath.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !(app.ApkInfo?.PackageName.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }
            View.Add(app);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var app in View) app.IsSelected = true;
        UpdateStatus();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var app in View) app.IsSelected = false;
        UpdateStatus();
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceDir is null || _busy) return;
        var selected = View.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) return;

        // 风险分级：禁删项单独列出强提示
        var keepRisk = selected.Where(a => a.Risk == DebloatRisk.Keep).ToList();
        var cautionRisk = selected.Where(a => a.Risk == DebloatRisk.Caution).ToList();

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"即将从工作目录删除 {selected.Count} 个应用（{WorkspaceScanner.FormatSize(selected.Sum(a => a.SizeBytes))}）。\n" +
                   "该操作直接修改工作目录，且打包时这些应用不会进入 ROM。",
            TextWrapping = TextWrapping.Wrap,
        });
        if (cautionRisk.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"⚠ {cautionRisk.Count} 个需谨慎：{string.Join("、", cautionRisk.Select(a => a.Name))}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
            });
        }
        if (keepRisk.Count > 0)
        {
            var lines = new TextBlock { TextWrapping = TextWrapping.Wrap };
            lines.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = $"⛔ {keepRisk.Count} 个为系统核心（禁删，删除大概率无法开机）：\n",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            });
            foreach (var app in keepRisk)
            {
                lines.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = $"  · {app.Name}" + (app.ApkInfo is { } ai ? $"（{ai.PackageName}）" : "") + "\n",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                });
            }
            content.Children.Add(lines);
        }

        var dlg = new ContentDialog
        {
            Title = "删除预装应用",
            Content = new ScrollViewer { MaxHeight = 360, Content = content },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (keepRisk.Count > 0) dlg.PrimaryButtonText = "仍要删除（风险自负）";
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        string ws = _workspaceDir;
        int failed = 0;
        _busy = true;
        ScanButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                foreach (var app in selected)
                {
                    try
                    {
                        WorkspaceScanner.RemoveApp(ws, app);
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }
            });

            // 重新扫描刷新列表
            _apps = await Task.Run(() => WorkspaceScanner.ScanApps(ws));
            foreach (var app in _apps)
            {
                app.PropertyChanged += App_PropertyChanged;
            }
            ApplyFilter();
            UpdateStatus();
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
        }

        if (failed > 0)
        {
            // 放在 UpdateStatus 之后，避免被统计文本覆盖
            StatusText.Text = $"完成，但 {failed} 个应用删除失败（文件被占用？）。";
        }
    }

    /// <summary>风险徽标颜色（DataTemplate 函数绑定用）。</summary>
    public static Microsoft.UI.Xaml.Media.SolidColorBrush RiskToBrush(DebloatRisk risk) => risk switch
    {
        DebloatRisk.Safe => new(Windows.UI.Color.FromArgb(255, 0x4C, 0xAF, 0x50)),
        DebloatRisk.Caution => new(Windows.UI.Color.FromArgb(255, 0xF5, 0x7C, 0x00)),
        DebloatRisk.Keep => new(Windows.UI.Color.FromArgb(255, 0xE5, 0x39, 0x35)),
        _ => new(Microsoft.UI.Colors.Gray),
    };

    /// <summary>未知风险时隐藏徽标。</summary>
    public static Visibility RiskVisible(DebloatRisk risk) =>
        risk == DebloatRisk.Unknown ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>点击应用行：弹窗展示 AndroidManifest 解析详情。</summary>
    private async void AppList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WorkspaceApp app) return;
        var a = app.ApkInfo;

        var lines = new List<string>
        {
            $"应用：{app.Name}",
            $"位置：{app.LocationText}",
            $"路径：{app.RelPath}",
            $"大小：{app.SizeText}",
        };
        if (a is null)
        {
            lines.Add(app.ParseFailed
                ? "APK 信息：解析失败（非标准或损坏的 manifest）"
                : "APK 信息：尚未解析完成");
        }
        else
        {
            lines.Add("");
            if (app.Risk != DebloatRisk.Unknown)
                lines.Add($"删除风险：{app.RiskText} —— {app.RiskReason}");
            lines.Add($"包名：{a.PackageName}");
            lines.Add($"版本：{a.VersionName}（versionCode {a.VersionCode}）");
            lines.Add($"SDK：{(a.MinSdk >= 0 ? $"min {a.MinSdk}" : "min 未声明")} · {(a.TargetSdk >= 0 ? $"target {a.TargetSdk}" : "target 未声明")}");
            lines.Add($"debuggable：{(a.Debuggable ? "是" : "否")}");
            if (a.ExtractNativeLibs.HasValue) lines.Add($"extractNativeLibs：{(a.ExtractNativeLibs.Value ? "是" : "否")}");
            if (a.HasCode.HasValue) lines.Add($"hasCode：{(a.HasCode.Value ? "是" : "否")}");
            lines.Add("");
            lines.Add(a.Permissions.Count == 0
                ? "权限：无"
                : $"权限（{a.Permissions.Count} 项）：");
            lines.AddRange(a.Permissions.Select(p => "  · " + p));
        }

        var dlg = new ContentDialog
        {
            Title = "应用详情",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = string.Join('\n', lines),
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    IsTextSelectionEnabled = true,
                },
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private void UpdateStatus()
    {
        int selected = View.Count(a => a.IsSelected);
        long selectedBytes = View.Where(a => a.IsSelected).Sum(a => a.SizeBytes);
        long totalBytes = View.Sum(a => a.SizeBytes);
        RemoveButton.IsEnabled = !_busy && selected > 0;
        StatusText.Text = View.Count == 0
            ? _apps.Count == 0 ? "未发现预装应用（需先解包 system / product 等分区）" : "无匹配结果"
            : $"共 {View.Count} 个应用 · {WorkspaceScanner.FormatSize(totalBytes)} | 已选 {selected} 个 · {WorkspaceScanner.FormatSize(selectedBytes)}";
    }
}
