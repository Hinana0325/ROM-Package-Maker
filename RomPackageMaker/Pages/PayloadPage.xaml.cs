using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RomPackageMaker.Services;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class PayloadPage : Page
{
    private CancellationTokenSource? _cts;
    private string? _payloadPath;
    private bool _busy;

    public ObservableCollection<PayloadPartitionInfo> Parts { get; } = new();

    public PayloadPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
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
        picker.FileTypeFilter.Add(".bin");
        picker.FileTypeFilter.Add(".zip");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) SourceBox.Text = file.Path;
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) OutputBox.Text = folder.Path;
    }

    private async void ParseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string src = SourceBox.Text.Trim();
        if (!File.Exists(src))
        {
            HintText.Text = "请先选择存在的 payload.bin 或 OTA zip。";
            return;
        }

        _busy = true;
        ParseButton.IsEnabled = false;
        try
        {
            // zip：先解出 payload.bin 到临时文件
            string payloadPath;
            if (src.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                HintText.Text = "正在从 zip 中提取 payload.bin...";
                payloadPath = await Task.Run(() => PayloadBinService.ExtractFromZip(src))
                    ?? throw new InvalidOperationException("zip 中未找到 payload.bin。");
                HintText.Text = "payload.bin 已解出到临时文件。";
            }
            else
            {
                payloadPath = src;
            }
            _payloadPath = payloadPath;

            var info = await Task.Run(() => PayloadBinService.Parse(payloadPath));

            Parts.Clear();
            foreach (var part in info.Partitions)
            {
                part.IsSelected = part.IsSupported; // 默认勾选全部可提取分区
                Parts.Add(part);
            }

            int supported = Parts.Count(p => p.IsSupported);
            int hashed = Parts.Count(p => p.HasHash);
            HintText.Text = $"payload v{info.Version}（minor {info.MinorVersion}）· 块大小 {info.BlockSize} · {Parts.Count} 个分区（可提取 {supported} 个，含哈希 {hashed} 个）";
            ExtractButton.IsEnabled = Parts.Any(p => p.IsSelected);
        }
        catch (Exception ex)
        {
            HintText.Text = "解析失败：" + ex.Message;
            Parts.Clear();
            ExtractButton.IsEnabled = false;
        }
        finally
        {
            _busy = false;
            ParseButton.IsEnabled = true;
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Parts.Where(p => p.IsSupported)) p.IsSelected = true;
        UpdateExtractState();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Parts) p.IsSelected = false;
        UpdateExtractState();
    }

    private void UpdateExtractState() =>
        ExtractButton.IsEnabled = !_busy && Parts.Any(p => p.IsSelected && p.IsSupported);

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _payloadPath is null) return;
        string outputDir = OutputBox.Text.Trim();
        if (!Directory.Exists(outputDir))
        {
            HintText.Text = "请先选择输出目录。";
            return;
        }
        var selected = Parts.Where(p => p.IsSelected && p.IsSupported).ToList();
        if (selected.Count == 0) return;

        _busy = true;
        SetBusyUi(true);
        _cts = new CancellationTokenSource();
        string payloadPath = _payloadPath;

        // 整体进度 = 已完成分区数 + 当前分区内进度，折算为 0-100
        int completedParts = 0;
        int failed = 0;
        var progress = new Progress<RomTaskProgress>(p =>
        {
            double overall = (completedParts + p.Percent / 100.0) / Math.Max(1, selected.Count) * 100;
            ExtractProgress.Value = overall;
            StageText.Text = p.Stage;
        });
        var log = new List<string>();

        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var part = selected[i];
                completedParts = i;
                log.Add($"提取 {part.Name}（{part.SizeText}）...");
                try
                {
                    bool verified = await Task.Run(() => PayloadBinService.ExtractPartition(payloadPath, part, outputDir, progress, _cts.Token));
                    log.Add(verified
                        ? $"  完成（SHA-256 校验通过）：{Path.Combine(outputDir, part.Name + ".img")}"
                        : $"  完成：{Path.Combine(outputDir, part.Name + ".img")}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.Add($"  失败：{ex.Message}");
                }
            }
            completedParts = selected.Count;

            log.Add(failed == 0
                ? $"全部完成：{selected.Count} 个分区已提取到 {outputDir}。可在「解包」页继续处理。"
                : $"完成：成功 {selected.Count - failed} 个，失败 {failed} 个。");
        }
        catch (OperationCanceledException)
        {
            log.Add("已取消。");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _busy = false;
            SetBusyUi(false);
        }

        var dlg = new ContentDialog
        {
            Title = failed == 0 ? "提取完成" : "提取结束（有失败项）",
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock { Text = string.Join('\n', log), TextWrapping = TextWrapping.Wrap },
            },
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
        HintText.Text = log[^1];
        ExtractProgress.Value = 0;
        StageText.Text = "就绪";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StageText.Text = "正在取消...";
    }

    private void SetBusyUi(bool busy)
    {
        ExtractButton.IsEnabled = !busy;
        ParseButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            ExtractProgress.Value = 0;
            StageText.Text = "正在提取...";
        }
        else
        {
            UpdateExtractState();
        }
    }
}
