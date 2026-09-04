using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RomPackageMaker.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class PackPage : Page
{
    private readonly IRomPackService _romService = new RomPackService();
    private CancellationTokenSource? _cts;

    public PackPage()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        if (!string.IsNullOrEmpty(s.DefaultWorkspace)) WorkspaceBox.Text = s.DefaultWorkspace;
        if (!string.IsNullOrEmpty(s.DefaultOutputDir)) OutputPathBox.Text = Path.Combine(s.DefaultOutputDir, "new_rom.zip");
        DragOver += PackPage_DragOver;
        Drop += PackPage_Drop;
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
        if (folder is not null) WorkspaceBox.Text = folder.Path;
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
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void PackPage_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;
        if (items[0] is StorageFolder folder)
        {
            WorkspaceBox.Text = folder.Path;
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
