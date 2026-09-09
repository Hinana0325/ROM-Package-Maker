using RomPackageMaker.Application;
using RomPackageMaker.Engine;
using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class BuildPropPage : Page
{
    /// <summary>完整行列表（含注释与空行，保存时需要）。</summary>
    private List<BuildPropLine> _lines = new();

    /// <summary>过滤后的属性行视图（绑定到列表）。</summary>
    public ObservableCollection<BuildPropLine> View { get; } = new();

    /// <summary>预设下拉数据源。</summary>
    public List<string> PresetNames { get; } = BuildPropPresets.All.Select(p => p.Name).ToList();

    private string? _filePath;

    public BuildPropPage()
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

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".prop");
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        LoadFile(file.Path);
    }

    private async void FindInWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        // 优先跟随会话最近使用的工作目录（如刚解包的那个），其次是设置中的默认目录
        string? root = WorkspaceState.CurrentWorkspace;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            root = AppSettings.Current.DefaultWorkspace;
        }
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            root = folder.Path;
            WorkspaceState.CurrentWorkspace = root;
        }

        var candidates = BuildPropService.FindCandidates(root);
        switch (candidates.Count)
        {
            case 0:
                await ShowInfoAsync($"在 {root} 中未找到 build.prop。");
                return;
            case 1:
                LoadFile(candidates[0]);
                return;
            default:
                var combo = new ComboBox
                {
                    Header = "找到多个 build.prop，请选择：",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = candidates,
                    SelectedIndex = 0,
                };
                var dlg = new ContentDialog
                {
                    Title = "选择 build.prop",
                    Content = combo,
                    PrimaryButtonText = "打开",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot,
                };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary && combo.SelectedItem is string path)
                {
                    LoadFile(path);
                }
                return;
        }
    }

    private void LoadFile(string path)
    {
        try
        {
            _lines = BuildPropService.Parse(File.ReadAllText(path));
            _filePath = path;
            ApplyFilter();
            UpdateUi();
        }
        catch (Exception ex)
        {
            _ = ShowInfoAsync($"无法读取文件：{ex.Message}");
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        View.Clear();
        string q = SearchBox.Text.Trim();
        foreach (var line in _lines)
        {
            if (!line.IsProperty) continue;
            if (q.Length > 0 &&
                !line.Key.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !line.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            View.Add(line);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var keyBox = new TextBox { Header = "属性键（如 ro.product.model）" };
        var valueBox = new TextBox { Header = "值（可为空）" };
        var dlg = new ContentDialog
        {
            Title = "添加属性",
            Content = new StackPanel { Spacing = 12, Children = { keyBox, valueBox } },
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        string key = keyBox.Text.Trim();
        if (key.Length == 0 || key.Contains('=') || key.Any(char.IsWhiteSpace))
        {
            await ShowInfoAsync("属性键不能为空，且不能包含 = 或空白字符。");
            return;
        }
        if (_lines.Any(l => l.IsProperty && l.Key == key))
        {
            await ShowInfoAsync($"属性 {key} 已存在。如需修改，请直接编辑现有行。");
            return;
        }

        var line = new BuildPropLine { IsProperty = true, Key = key, Value = valueBox.Text, IsNew = true };
        _lines.Add(line);

        // 清空搜索，确保新行可见并滚动到底部
        SearchBox.Text = string.Empty;
        ApplyFilter();
        if (View.Count > 0) PropList.ScrollIntoView(View[^1]);
        UpdateUi();
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BuildPropLine line })
        {
            _lines.Remove(line);
            View.Remove(line);
            UpdateUi();
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath is null) return;
        try
        {
            // 首次保存前备份原文件
            string bak = _filePath + ".bak";
            if (!File.Exists(bak)) File.Copy(_filePath, bak, overwrite: false);

            File.WriteAllText(_filePath, BuildPropService.Serialize(_lines), new UTF8Encoding(false));

            // 已保存的 dirty 行重置为干净状态（保留注释行原样）
            foreach (var line in _lines)
            {
                if (line.IsProperty && line.IsDirty)
                {
                    line.IsNew = false;
                    line.Raw = $"{line.Key}={line.Value}";
                    line.Original = line.Raw;
                }
            }
            UpdateUi();
        }
        catch (Exception ex)
        {
            await ShowInfoAsync($"保存失败：{ex.Message}");
        }
    }

    /// <summary>应用选中的参数预设（已存在的键改值，不存在的追加）。</summary>
    private async void ApplyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath is null) return;
        int idx = PresetCombo.SelectedIndex;
        if (idx < 0 || idx >= BuildPropPresets.All.Count) return;
        var preset = BuildPropPresets.All[idx];

        var (modified, added) = BuildPropPresets.Apply(_lines, preset);

        // 清空搜索以显示全部改动行
        SearchBox.Text = string.Empty;
        ApplyFilter();
        UpdateUi();

        await ShowInfoAsync(
            $"已应用预设「{preset.Name}」：修改 {modified} 项，新增 {added} 项。\n" +
            $"{preset.Description}\n注意：改动尚未保存，请检查后 Ctrl+S 保存。");
    }

    /// <summary>值编辑时刷新状态条（未保存计数）。</summary>
    private void Value_TextChanged(object sender, TextChangedEventArgs e) => UpdateStatusText();

    private void UpdateUi()
    {
        bool hasFile = _filePath is not null;
        EmptyState.Visibility = hasFile ? Visibility.Collapsed : Visibility.Visible;
        EditorArea.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = hasFile;
        AddButton.IsEnabled = hasFile;
        ApplyPresetButton.IsEnabled = hasFile;
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (_filePath is null)
        {
            StatusText.Text = "未打开文件";
            return;
        }
        int total = _lines.Count(l => l.IsProperty);
        int dirty = BuildPropService.CountDirty(_lines);
        StatusText.Text = dirty > 0
            ? $"{_filePath} · {total} 个属性 · {dirty} 处未保存修改"
            : $"{_filePath} · {total} 个属性";
    }

    private async Task ShowInfoAsync(string message)
    {
        var dlg = new ContentDialog
        {
            Title = "提示",
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
