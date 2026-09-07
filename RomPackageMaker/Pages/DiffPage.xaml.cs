using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RomPackageMaker.Services;
using Windows.Storage.Pickers;
using Windows.UI;

namespace RomPackageMaker.Pages;

/// <summary>属性变化类型 → 符号（＋ 新增 / － 移除 / ± 修改）。</summary>
public sealed class PropKindToSignConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string kind = value as string ?? string.Empty;
        return kind switch
        {
            "added" => "＋",
            "removed" => "－",
            _ => "±",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>属性变化类型 → 颜色（新增绿 / 移除红 / 修改橙）。</summary>
public sealed class PropKindToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromArgb(255, 0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush Red = new(Color.FromArgb(255, 0xE5, 0x39, 0x35));
    private static readonly SolidColorBrush Orange = new(Color.FromArgb(255, 0xF5, 0x7C, 0x00));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string kind = value as string ?? string.Empty;
        return kind switch
        {
            "added" => Green,
            "removed" => Red,
            _ => Orange,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class DiffPage : Page
{
    private bool _busy;
    private bool _userTouchedWorkspace;

    public DiffPage()
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
            if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws))
            {
                // 默认把当前工作目录当作“新版本”一侧
                if (string.IsNullOrEmpty(NewBox.Text) && NewBox.Text != ws) NewBox.Text = ws;
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

    private async void BrowseOldButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            OldBox.Text = folder.Path;
            _userTouchedWorkspace = true;
        }
    }

    private async void BrowseNewButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            NewBox.Text = folder.Path;
            _userTouchedWorkspace = true;
            WorkspaceState.CurrentWorkspace = folder.Path;
        }
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string oldDir = OldBox.Text.Trim();
        string newDir = NewBox.Text.Trim();
        if (!Directory.Exists(oldDir) || !Directory.Exists(newDir))
        {
            SummaryText.Text = "请先选择两个有效的工作目录。";
            return;
        }
        if (string.Equals(Path.GetFullPath(oldDir).TrimEnd('\\', '/'),
                          Path.GetFullPath(newDir).TrimEnd('\\', '/'),
                          StringComparison.OrdinalIgnoreCase))
        {
            SummaryText.Text = "两个目录相同，无需对比。";
            return;
        }

        _busy = true;
        CompareButton.IsEnabled = false;
        SummaryText.Text = "正在对比两个工作区（扫描应用与属性）...";
        try
        {
            WorkspaceDiff diff = await Task.Run(() => WorkspaceDiffService.Compare(oldDir, newDir));
            Fill(diff);
            SummaryText.Text = diff.HasChanges
                ? "对比完成：" + diff.SummaryText
                : "对比完成：两个工作区没有发现差异。";
        }
        catch (Exception ex)
        {
            SummaryText.Text = "对比失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            CompareButton.IsEnabled = true;
        }
    }

    private void Fill(WorkspaceDiff diff)
    {
        bool hasApps = diff.AddedApps.Count > 0 || diff.RemovedApps.Count > 0;
        AppsCard.Visibility = hasApps ? Visibility.Visible : Visibility.Collapsed;
        AddedAppsHeader.Visibility = diff.AddedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddedAppsList.Visibility = diff.AddedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddedAppsList.ItemsSource = diff.AddedApps;
        RemovedAppsHeader.Visibility = diff.RemovedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemovedAppsList.Visibility = diff.RemovedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemovedAppsList.ItemsSource = diff.RemovedApps;

        var props = diff.ModifiedProps.Concat(diff.AddedProps).Concat(diff.RemovedProps).ToList();
        PropsCard.Visibility = props.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PropsList.ItemsSource = props;

        PartitionsCard.Visibility = diff.Partitions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PartitionsList.ItemsSource = diff.Partitions;
    }
}
