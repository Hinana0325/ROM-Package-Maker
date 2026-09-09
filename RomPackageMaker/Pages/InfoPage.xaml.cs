using RomPackageMaker.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.UI;

namespace RomPackageMaker.Pages;

public sealed partial class InfoPage : Page
{
    private bool _busy;
    private bool _userTouchedWorkspace;

    public InfoPage()
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

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string dir = WorkspaceBox.Text.Trim();
        if (!Directory.Exists(dir))
        {
            StatusText.Text = "工作目录不存在。";
            return;
        }
        _userTouchedWorkspace = true;
        WorkspaceState.CurrentWorkspace = dir;

        _busy = true;
        ScanButton.IsEnabled = false;
        StatusText.Text = "正在扫描工作区...";
        try
        {
            RomInfo info = await Task.Run(() => RomInfoService.Collect(dir));
            Fill(info);
            StatusText.Text = $"扫描完成：{info.Props.Count} 项属性，{info.Partitions.Count} 个分区，{info.AppCount} 个预装应用。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
        }
    }

    private void Fill(RomInfo info)
    {
        StatApps.Text = info.AppCount > 0 ? info.AppCount.ToString() : "—";
        StatGms.Text = info.GmsPresent ? "已内置" : "未内置";
        StatAvb.Text = info.AvbPresent
            ? info.AvbAllDisabled ? "已禁用验证" : "验证开启"
            : "未检测到";
        StatRoot.Text = info.RootSuPresent ? "检测到 su" : "未检测到";
        StatPartitions.Text = info.Partitions.Count > 0 ? info.Partitions.Count.ToString() : "—";
        StatTotalSize.Text = info.TotalSizeText;
        StatSourceType.Text = info.SourceType.Length > 0 ? info.SourceType : "—";
        StatSourceFile.Text = info.SourceName.Length > 0 ? info.SourceName : "—";

        StatGms.Foreground = info.GmsPresent ? OkBrush : MutedBrush;
        StatAvb.Foreground = info.AvbPresent
            ? info.AvbAllDisabled ? OkBrush : WarnBrush
            : MutedBrush;
        StatRoot.Foreground = info.RootSuPresent ? WarnBrush : MutedBrush;

        PartitionList.ItemsSource = info.Partitions;
        PropList.ItemsSource = info.Props;
    }

    private static SolidColorBrush OkBrush { get; } = new(Color.FromArgb(255, 0x4C, 0xAF, 0x50));
    private static SolidColorBrush WarnBrush { get; } = new(Color.FromArgb(255, 0xF5, 0x7C, 0x00));
    private static SolidColorBrush MutedBrush { get; } = new(Microsoft.UI.Colors.Gray);
}
