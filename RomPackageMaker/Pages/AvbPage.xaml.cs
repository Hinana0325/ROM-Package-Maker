using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RomPackageMaker.Services;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

/// <summary>AVB 检测结果行（每次扫描整体重建，无需变更通知）。注意：不能使用 init 属性，会破坏 XAML 类型生成。</summary>
public sealed class AvbRow
{
    public string Name { get; set; } = string.Empty;
    public string KindText { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string DetailText { get; set; } = string.Empty;
}

public sealed partial class AvbPage : Page
{
    public ObservableCollection<AvbRow> Rows { get; } = new();

    private string? _workspace;
    private bool _busy;

    public AvbPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (string.IsNullOrEmpty(WorkspaceBox.Text))
        {
            string? ws = WorkspaceState.CurrentWorkspace ?? AppSettings.Current.DefaultWorkspace;
            if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws))
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
            WorkspaceState.CurrentWorkspace = folder.Path;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanCoreAsync();

    private async Task ScanCoreAsync()
    {
        if (_busy) return;
        string ws = WorkspaceBox.Text.Trim();
        if (!Directory.Exists(ws))
        {
            HintText.Text = "工作目录不存在。";
            return;
        }
        _workspace = ws;

        _busy = true;
        ScanButton.IsEnabled = false;
        DisableAllButton.IsEnabled = false;
        StripAllButton.IsEnabled = false;
        try
        {
            var entries = await Task.Run(() => AvbService.ScanWorkspace(ws));
            Rows.Clear();
            foreach (var entry in entries)
            {
                Rows.Add(ToRow(ws, entry));
            }

            int vbmeta = entries.Count(x => x.IsVbmeta);
            int disabled = entries.Count(x => x.IsVbmeta && x.VerificationDisabled && x.HashtreeDisabled);
            int footers = entries.Count(x => x.HasFooter);
            HintText.Text = entries.Count == 0
                ? "工作目录中未发现镜像（需先解包）。"
                : $"发现 {entries.Count} 个镜像：vbmeta {vbmeta} 个（其中 {disabled} 个已禁用验证）· 带校验页脚 {footers} 个。";
            DisableAllButton.IsEnabled = vbmeta > 0;
            StripAllButton.IsEnabled = footers > 0;
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
        }
    }

    private static AvbRow ToRow(string ws, AvbEntry e) => new()
    {
        Name = Path.GetFileName(e.Path),
        KindText = e.IsVbmeta ? "vbmeta" : e.IsSparse ? "sparse 镜像" : "分区镜像",
        StatusText = e.IsVbmeta
            ? (e.VerificationDisabled && e.HashtreeDisabled ? "验证已禁用" : "验证未禁用")
            : e.IsSparse
                ? "页脚处理需先展开"
                : e.HasFooter
                    ? $"含校验页脚（原像 {e.OriginalImageSize:N0} 字节）"
                    : "无 AVB 页脚",
        DetailText = Path.GetRelativePath(ws, e.Path)
            + (e.IsVbmeta && e.ReleaseString.Length > 0 ? $" · {e.ReleaseString}" : string.Empty),
    };

    private async void DisableAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _workspace is null) return;
        _busy = true;
        ScanButton.IsEnabled = false;
        DisableAllButton.IsEnabled = false;
        StripAllButton.IsEnabled = false;
        try
        {
            string ws = _workspace;
            var log = await Task.Run(() => AvbService.DisableAllVerification(ws));
            HintText.Text = string.Join('\n', log);
        }
        catch (Exception ex)
        {
            HintText.Text = "失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
        }
        await ScanCoreAsync();
    }

    private async void StripAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _workspace is null) return;
        _busy = true;
        ScanButton.IsEnabled = false;
        DisableAllButton.IsEnabled = false;
        StripAllButton.IsEnabled = false;
        try
        {
            string ws = _workspace;
            var log = await Task.Run(() => AvbService.StripAllFooters(ws));
            HintText.Text = string.Join('\n', log);
        }
        catch (Exception ex)
        {
            HintText.Text = "失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
        }
        await ScanCoreAsync();
    }
}
