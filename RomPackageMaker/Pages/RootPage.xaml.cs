using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RomPackageMaker.Services;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class RootPage : Page
{
    private bool _userTouchedWorkspace;

    private string Workspace => WorkspaceBox.Text.Trim();

    public RootPage()
    {
        InitializeComponent();
        // 缓存页面：切换导航后保留已选路径与状态
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
                RefreshButton_Click(this, new RoutedEventArgs());
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
            RefreshButton_Click(this, new RoutedEventArgs());
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(Workspace))
        {
            DetectText.Text = "工作目录不存在。";
            return;
        }
        var bootParts = RootService.GetBootPartitions(Workspace);
        string? systemDir = RootService.FindSystemDir(Workspace);
        string bootInfo = bootParts.Count > 0
            ? string.Join("、", bootParts)
            : "未检测到（替换时将新建 boot 分区条目）";
        DetectText.Text = $"boot 类分区：{bootInfo}\n" +
                          (systemDir is not null
                              ? $"system 分区：已解包（{systemDir}）"
                              : "system 分区：未解包（内置 Magisk 管理器需要）");
    }

    private string? SelectedBootPartition()
    {
        if (BootPartCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }
        return null;
    }

    private async void BrowsePatchedBoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".img");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) PatchedBootBox.Text = file.Path;
    }

    private async void ApplyReplaceBoot_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        if (!Directory.Exists(Workspace))
        {
            StatusText.Text = "请先选择有效的工作目录。";
            return;
        }
        if (!File.Exists(PatchedBootBox.Text.Trim()))
        {
            StatusText.Text = "请先选择已修补的 boot.img 文件。";
            return;
        }

        // 大镜像复制放后台线程，避免卡界面
        string ws = Workspace;
        string img = PatchedBootBox.Text.Trim();
        string? part = SelectedBootPartition();
        ApplyReplaceButton.IsEnabled = false;
        try
        {
            string used = await Task.Run(() => RootService.ReplaceBootImage(ws, img, part));
            StatusText.Text = $"已替换分区 {used}：打包时将直接使用该修补镜像。";
            RefreshButton_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            ApplyReplaceButton.IsEnabled = true;
        }
    }

    private async void BrowseMagisk_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".apk");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) MagiskApkBox.Text = file.Path;
    }

    private async void ApplyMagisk_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        if (!Directory.Exists(Workspace))
        {
            StatusText.Text = "请先选择有效的工作目录。";
            return;
        }
        if (!File.Exists(MagiskApkBox.Text.Trim()))
        {
            StatusText.Text = "请先选择 Magisk.apk 文件。";
            return;
        }

        string ws = Workspace;
        string apk = MagiskApkBox.Text.Trim();
        ApplyMagiskButton.IsEnabled = false;
        try
        {
            await Task.Run(() => RootService.InstallMagiskManager(ws, apk));
            StatusText.Text = "已内置 Magisk 管理器（system/priv-app/Magisk/Magisk.apk）。";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            ApplyMagiskButton.IsEnabled = true;
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dlg = new ContentDialog
        {
            Title = "操作失败",
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
