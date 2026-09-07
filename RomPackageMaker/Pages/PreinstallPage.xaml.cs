using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RomPackageMaker.Services;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

/// <summary>待预装 APK 条目（后台解析包名/版本用于确认）。</summary>
public sealed partial class PendingApkItem : INotifyPropertyChanged
{
    public string SourcePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(SourcePath);

    private string _infoText = "APK 解析中...";
    public string InfoText
    {
        get => _infoText;
        private set
        {
            if (_infoText == value) return;
            _infoText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InfoText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>后台解析 manifest，完成后刷新副行。</summary>
    public void Enrich()
    {
        try
        {
            var info = ApkParser.Parse(SourcePath);
            InfoText = info.Summary + $" · SDK {info.MinSdk}–{info.TargetSdk}";
        }
        catch (Exception ex)
        {
            InfoText = "解析失败：" + ex.Message;
        }
    }
}

public sealed partial class PreinstallPage : Page
{
    private string? _workspaceDir;
    private bool _busy;
    private bool _userTouchedWorkspace;

    public ObservableCollection<PendingApkItem> ApkItems { get; } = new();
    public int ApkCount => ApkItems.Count;

    public PreinstallPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
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

    private string ResolveWorkspace()
    {
        string dir = WorkspaceBox.Text.Trim();
        if (Directory.Exists(dir))
        {
            _workspaceDir = dir;
            WorkspaceState.CurrentWorkspace = dir;
        }
        return dir;
    }

    // ==================== 应用预装 ====================

    /// <summary>列表变化后同步边框可见性。</summary>
    private void UpdateApkListVisibility() =>
        ApkListBorder.Visibility = ApkItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void AddApkButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".apk");
        InitializePicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
        {
            if (ApkItems.Any(a => a.SourcePath == file.Path)) continue;
            var item = new PendingApkItem { SourcePath = file.Path };
            ApkItems.Add(item);
            _ = Task.Run(item.Enrich);
        }
        UpdateApkListVisibility();
    }

    private void RemoveApkItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PendingApkItem item })
        {
            ApkItems.Remove(item);
            UpdateApkListVisibility();
        }
    }

    private void ClearApkListButton_Click(object sender, RoutedEventArgs e)
    {
        ApkItems.Clear();
        UpdateApkListVisibility();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ApkItems.Count == 0) return;
        string ws = ResolveWorkspace();
        if (!Directory.Exists(ws))
        {
            ApkHintText.Text = "请先选择有效的工作目录。";
            return;
        }
        bool privileged = PartitionCombo.SelectedIndex == 1;

        _busy = true;
        InstallButton.IsEnabled = false;
        try
        {
            var results = await Task.Run(() =>
            {
                var log = new List<(PendingApkItem Item, string Line)>();
                foreach (var item in ApkItems)
                {
                    try
                    {
                        string dest = PreinstallService.AddApk(ws, item.SourcePath, privileged);
                        log.Add((item, $"✓ {item.FileName} → {Path.GetRelativePath(ws, dest)}"));
                    }
                    catch (Exception ex)
                    {
                        log.Add((item, $"✗ {item.FileName}：{ex.Message}"));
                    }
                }
                return log;
            });

            int ok = results.Count(r => r.Line.StartsWith("✓"));
            ApkHintText.Text = $"预装完成：成功 {ok}，跳过/失败 {ApkItems.Count - ok}。";
            foreach (var (_, line) in results) ApkHintText.Text += "\n" + line;

            if (ok == ApkItems.Count)
            {
                ApkItems.Clear();
                UpdateApkListVisibility();
            }
        }
        finally
        {
            _busy = false;
            InstallButton.IsEnabled = true;
        }
    }

    // ==================== 资源替换 ====================

    private async void ReplaceBootanimation_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".zip");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        string ws = ResolveWorkspace();
        if (!Directory.Exists(ws))
        {
            BootanimationStatus.Text = "请先选择有效的工作目录。";
            return;
        }
        try
        {
            string dest = await Task.Run(() => PreinstallService.ReplaceResource(ws, file.Path, ResourceKind.Bootanimation));
            BootanimationStatus.Text = $"已替换：{dest}";
        }
        catch (Exception ex)
        {
            BootanimationStatus.Text = "失败：" + ex.Message;
        }
    }

    private async void ReplaceFonts_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".ttf");
        InitializePicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        string ws = ResolveWorkspace();
        if (!Directory.Exists(ws))
        {
            FontStatus.Text = "请先选择有效的工作目录。";
            return;
        }
        var log = new List<string>();
        foreach (var file in files)
        {
            try
            {
                string dest = await Task.Run(() => PreinstallService.ReplaceResource(ws, file.Path, ResourceKind.Font));
                log.Add($"✓ {file.Name} → {Path.GetRelativePath(ws, dest)}");
            }
            catch (Exception ex)
            {
                log.Add($"✗ {file.Name}：{ex.Message}");
            }
        }
        FontStatus.Text = string.Join("  ", log);
    }

    private async void ReplaceAudio_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        picker.FileTypeFilter.Add(".ogg");
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".m4a");
        InitializePicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        string ws = ResolveWorkspace();
        if (!Directory.Exists(ws))
        {
            AudioStatus.Text = "请先选择有效的工作目录。";
            return;
        }
        var kind = AudioKindCombo.SelectedIndex switch
        {
            1 => ResourceKind.Notification,
            2 => ResourceKind.Alarm,
            _ => ResourceKind.Ringtone,
        };
        var log = new List<string>();
        foreach (var file in files)
        {
            try
            {
                string dest = await Task.Run(() => PreinstallService.ReplaceResource(ws, file.Path, kind));
                log.Add($"✓ {file.Name} → {Path.GetRelativePath(ws, dest)}");
            }
            catch (Exception ex)
            {
                log.Add($"✗ {file.Name}：{ex.Message}");
            }
        }
        AudioStatus.Text = string.Join("  ", log);
    }

    // ==================== hosts 广告过滤 ====================

    private async void ApplyHostsButton_Click(object sender, RoutedEventArgs e)
    {
        string ws = ResolveWorkspace();
        if (!Directory.Exists(ws))
        {
            HostsStatus.Text = "请先选择有效的工作目录。";
            return;
        }
        try
        {
            int count = await Task.Run(() => HostsService.Apply(ws, HostsService.BuiltInDomains));
            HostsStatus.Text = $"已应用内置广告过滤：屏蔽 {count} 个域名（system/etc/hosts）。";
        }
        catch (Exception ex)
        {
            HostsStatus.Text = "失败：" + ex.Message;
        }
    }

    private async void AppendHostsButton_Click(object sender, RoutedEventArgs e)
    {
        var domains = HostsService.ParseCustomDomains(CustomHostsBox.Text);
        if (domains.Count == 0)
        {
            HostsStatus.Text = "未输入有效的自定义域名。";
            return;
        }
        string ws = ResolveWorkspace();
        if (!Directory.Exists(ws))
        {
            HostsStatus.Text = "请先选择有效的工作目录。";
            return;
        }
        try
        {
            int count = await Task.Run(() => HostsService.Apply(ws, domains));
            HostsStatus.Text = $"已追加 {domains.Count} 个自定义域名，当前共屏蔽 {count} 个域名。";
            CustomHostsBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            HostsStatus.Text = "失败：" + ex.Message;
        }
    }

    private async void RemoveHostsButton_Click(object sender, RoutedEventArgs e)
    {
        string ws = ResolveWorkspace();
        if (!Directory.Exists(ws))
        {
            HostsStatus.Text = "请先选择有效的工作目录。";
            return;
        }
        try
        {
            bool removed = await Task.Run(() => HostsService.Remove(ws));
            HostsStatus.Text = removed ? "已移除广告过滤块（原有 hosts 内容保持不变）。" : "当前 hosts 中没有过滤块。";
        }
        catch (Exception ex)
        {
            HostsStatus.Text = "失败：" + ex.Message;
        }
    }
}
