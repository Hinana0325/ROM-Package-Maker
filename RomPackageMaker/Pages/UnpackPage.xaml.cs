using RomPackageMaker.Application;
using RomPackageMaker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class UnpackPage : Page
{
    private readonly IRomPackService _romService = new RomPackService();
    private CancellationTokenSource? _cts;

    public UnpackPage()
    {
        InitializeComponent();
        // 缓存页面：切换导航后保留已选路径、进度与日志
        NavigationCacheMode = NavigationCacheMode.Required;
        // 填充默认工作目录
        if (!string.IsNullOrEmpty(AppSettings.Current.DefaultWorkspace))
        {
            WorkspaceBox.Text = AppSettings.Current.DefaultWorkspace;
        }
        // 拖放
        DragOver += UnpackPage_DragOver;
        DragLeave += UnpackPage_DragLeave;
        Drop += UnpackPage_Drop;
    }

    private static void InitializePicker(object picker)
    {
        if (App.MainWindow is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        }
    }

    private async void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".img");
        picker.FileTypeFilter.Add(".bin");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) SourcePathBox.Text = file.Path;
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
            WorkspaceState.CurrentWorkspace = folder.Path;
        }
    }

    private void UnpackPage_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (DropHintOverlay.Visibility != Visibility.Visible)
                DropHintOverlay.Visibility = Visibility.Visible;
        }
    }

    private void UnpackPage_DragLeave(object sender, DragEventArgs e)
    {
        DropHintOverlay.Visibility = Visibility.Collapsed;
    }

    private async void UnpackPage_Drop(object sender, DragEventArgs e)
    {
        DropHintOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;
        if (items[0] is StorageFile file)
        {
            string ext = Path.GetExtension(file.Name).ToLowerInvariant();
            if (ext is ".zip" or ".img" or ".bin")
            {
                SourcePathBox.Text = file.Path;
            }
        }
        else if (items[0] is StorageFolder folder)
        {
            WorkspaceBox.Text = folder.Path;
            WorkspaceState.CurrentWorkspace = folder.Path;
        }
    }

    private async void UnpackButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourcePathBox.Text) || string.IsNullOrWhiteSpace(WorkspaceBox.Text))
        {
            AppendLog("请先选择 ROM 文件和工作目录。");
            return;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<RomTaskProgress>(p =>
        {
            UnpackProgress.Value = p.Percent;
            StageText.Text = p.Stage;
            if (p.Detail is not null) AppendLog(p.Detail);
        });

        try
        {
            await _romService.UnpackAsync(SourcePathBox.Text, WorkspaceBox.Text, progress, _cts.Token);
            AppendLog("解包完成。");
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AppendLog("正在取消...");
    }

    private void SetBusy(bool busy)
    {
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UnpackButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BrowseSourceButton.IsEnabled = !busy;
        BrowseWorkspaceButton.IsEnabled = !busy;
        if (busy)
        {
            UnpackProgress.Value = 0;
            StageText.Text = "正在解包...";
        }
        else
        {
            StageText.Text = "就绪";
        }
    }

    private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
    {
        LogText.Text = string.Empty;
    }

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
