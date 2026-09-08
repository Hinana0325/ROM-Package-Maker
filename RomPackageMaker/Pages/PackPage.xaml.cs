using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RomPackageMaker.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class PackPage : Page
{
    private readonly IRomPackService _romService = new RomPackService();
    private CancellationTokenSource? _cts;
    private bool _userTouchedWorkspace;

    public PackPage()
    {
        InitializeComponent();
        // 缓存页面：切换导航后保留已选路径、进度与日志
        NavigationCacheMode = NavigationCacheMode.Required;
        var s = AppSettings.Current;
        if (!string.IsNullOrEmpty(s.DefaultWorkspace)) WorkspaceBox.Text = s.DefaultWorkspace;
        if (!string.IsNullOrEmpty(s.DefaultOutputDir)) OutputPathBox.Text = Path.Combine(s.DefaultOutputDir, "new_rom.zip");
        DragOver += PackPage_DragOver;
        DragLeave += PackPage_DragLeave;
        Drop += PackPage_Drop;
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

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeChoices.Add("刷机包", new List<string> { ".zip" });
        picker.SuggestedFileName = "new_rom";
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is not null) OutputPathBox.Text = file.Path;
    }

    private void PackPage_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (DropHintOverlay.Visibility != Visibility.Visible)
                DropHintOverlay.Visibility = Visibility.Visible;
        }
    }

    private void PackPage_DragLeave(object sender, DragEventArgs e)
    {
        DropHintOverlay.Visibility = Visibility.Collapsed;
    }

    private async void PackPage_Drop(object sender, DragEventArgs e)
    {
        DropHintOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;
        if (items[0] is StorageFolder folder)
        {
            WorkspaceBox.Text = folder.Path;
            _userTouchedWorkspace = true;
            WorkspaceState.CurrentWorkspace = folder.Path;
        }
        else if (items[0] is StorageFile file)
        {
            OutputPathBox.Text = file.Path;
        }
    }

    private async void PackButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceBox.Text) || string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            AppendLog("请先选择工作目录和输出文件。");
            return;
        }

        // 打包前防呆检查（后台执行，不卡 UI）
        var issues = await Task.Run(() => PackPreflightService.RunChecks(WorkspaceBox.Text, OutputPathBox.Text));
        if (issues.Count > 0 && !await ConfirmPreflightAsync(issues))
        {
            AppendLog("已取消打包（预检未通过）。");
            return;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<RomTaskProgress>(p =>
        {
            PackProgress.Value = p.Percent;
            StageText.Text = p.Stage;
            if (p.Detail is not null) AppendLog(p.Detail);
        });

        try
        {
            await _romService.PackAsync(WorkspaceBox.Text, OutputPathBox.Text, progress, _cts.Token, sign: SignCheckBox.IsChecked == true);
            AppendLog($"打包完成：{OutputPathBox.Text}");

            // 顺带生成 fastboot 刷机脚本（按解包清单）
            try
            {
                string? script = await Task.Run(() => PackPreflightService.GenerateFastbootScript(WorkspaceBox.Text, OutputPathBox.Text));
                if (script is not null) AppendLog($"已生成刷机脚本：{script}");
            }
            catch
            {
                /* 脚本生成失败不影响打包结果 */
            }
            if (AppSettings.Current.OpenOutputAfterPack)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", $"/select,\"{OutputPathBox.Text}\"") { UseShellExecute = true });
                }
                catch
                {
                    /* 忽略 */
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("已取消。");
        }
        catch (Exception ex)
        {
            AppendLog($"错误：{ex.Message}");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    /// <summary>预检问题确认对话框：错误红 / 警告橙 / 提示灰。返回用户是否选择继续。</summary>
    private async Task<bool> ConfirmPreflightAsync(List<PreflightIssue> issues)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = $"打包前检查发现 {issues.Count} 个问题：",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var issue in issues)
        {
            panel.Children.Add(new TextBlock
            {
                Text = issue.Level switch
                {
                    PreflightLevel.Error => "✗ ",
                    PreflightLevel.Warn => "⚠ ",
                    _ => "ℹ ",
                } + issue.Message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(issue.Level switch
                {
                    PreflightLevel.Error => Microsoft.UI.Colors.Red,
                    PreflightLevel.Warn => Microsoft.UI.Colors.Orange,
                    _ => Microsoft.UI.Colors.Gray,
                }),
            });
        }

        var dlg = new ContentDialog
        {
            Title = "打包前检查",
            Content = new ScrollViewer { MaxHeight = 320, Content = panel },
            PrimaryButtonText = issues.Any(i => i.Level == PreflightLevel.Error) ? "仍要打包" : "继续打包",
            CloseButtonText = "取消",
            DefaultButton = issues.Any(i => i.Level == PreflightLevel.Error)
                ? ContentDialogButton.Close
                : ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AppendLog("正在取消...");
    }

    private void SetBusy(bool busy)
    {
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PackButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BrowseWorkspaceButton.IsEnabled = !busy;
        BrowseOutputButton.IsEnabled = !busy;
        SignCheckBox.IsEnabled = !busy;
        StageText.Text = busy ? "正在打包..." : "就绪";
        if (busy) PackProgress.Value = 0;
    }

    private void ClearLogBtn_Click(object sender, RoutedEventArgs e) => LogText.Text = string.Empty;

    private void CopyLogBtn_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(LogText.Text);
        Clipboard.SetContent(dp);
        AppendLog("日志已复制到剪贴板。");
    }

    private void AppendLog(string line)
    {
        LogText.Text = line + Environment.NewLine + LogText.Text;
    }
}
