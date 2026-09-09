using RomPackageMaker.Application;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class TemplatePage : Page
{
    private RomTemplate? _current;

    public ObservableCollection<TemplatePropOverride> PropOverrides { get; } = new();
    public ObservableCollection<TemplateTextItem> RemoveAppItems { get; } = new();

    public TemplatePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        ReloadTemplates();
    }

    private static void InitializePicker(object picker)
    {
        if (App.MainWindow is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        }
    }

    // ==================== 模板列表 ====================

    private void ReloadTemplates(string? selectName = null)
    {
        var templates = TemplateService.LoadAll();
        TemplateList.ItemsSource = templates;
        if (templates.Count == 0)
        {
            _current = null;
            LoadEditor(null);
            return;
        }
        var select = selectName is not null
            ? templates.FirstOrDefault(t => t.Name == selectName)
            : null;
        TemplateList.SelectedItem = select ?? templates[0];
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateList.SelectedItem is RomTemplate t)
        {
            _current = CloneTemplate(t);
            LoadEditor(_current);
        }
    }

    private static RomTemplate CloneTemplate(RomTemplate t) => new()
    {
        Name = t.Name,
        Description = t.Description,
        BuildPropOverrides = new Dictionary<string, string>(t.BuildPropOverrides),
        RemoveApps = new List<string>(t.RemoveApps),
        RootMode = t.RootMode,
        RootSourcePath = t.RootSourcePath,
        RootBootPartition = t.RootBootPartition,
        UpdatedAt = t.UpdatedAt,
    };

    // ==================== 编辑器读写 ====================

    private void LoadEditor(RomTemplate? t)
    {
        if (t is null)
        {
            NameBox.Text = string.Empty;
            DescBox.Text = string.Empty;
            PropOverrides.Clear();
            RemoveAppItems.Clear();
            RootModeCombo.SelectedIndex = 0;
            RootSourceBox.Text = string.Empty;
            RootPartCombo.SelectedIndex = 0;
            return;
        }

        NameBox.Text = t.Name;
        DescBox.Text = t.Description ?? string.Empty;

        PropOverrides.Clear();
        foreach (var (key, value) in t.BuildPropOverrides)
        {
            PropOverrides.Add(new TemplatePropOverride { Key = key, Value = value });
        }

        RemoveAppItems.Clear();
        foreach (var app in t.RemoveApps)
        {
            RemoveAppItems.Add(new TemplateTextItem { Text = app });
        }

        RootModeCombo.SelectedIndex = t.RootMode switch
        {
            RootModes.ReplaceBoot => 1,
            RootModes.MagiskSystem => 2,
            _ => 0,
        };
        RootSourceBox.Text = t.RootSourcePath ?? string.Empty;
        SelectBootCombo(RootPartCombo, t.RootBootPartition);
    }

    /// <summary>把编辑器当前内容写入模板对象。</summary>
    private RomTemplate ReadEditor()
    {
        var t = _current ?? new RomTemplate();
        t.Name = NameBox.Text.Trim();
        t.Description = string.IsNullOrWhiteSpace(DescBox.Text) ? null : DescBox.Text.Trim();
        t.BuildPropOverrides = PropOverrides
            .Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .GroupBy(p => p.Key.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value);
        t.RemoveApps = RemoveAppItems
            .Select(a => a.Text.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        t.RootMode = (RootModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? RootModes.None;
        t.RootSourcePath = string.IsNullOrWhiteSpace(RootSourceBox.Text) ? null : RootSourceBox.Text.Trim();
        t.RootBootPartition = (RootPartCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return t;
    }

    private static void SelectBootCombo(ComboBox combo, string? tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (string?)item.Tag == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static string? ComboTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    // ==================== 工具栏动作 ====================

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        _current = new RomTemplate();
        LoadEditor(_current);
        TemplateList.SelectedItem = null;
        NameBox.Focus(FocusState.Programmatic);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var t = ReadEditor();
        if (t.Name.Length == 0)
        {
            await ShowInfoAsync("请填写模板名称。");
            return;
        }
        TemplateService.Save(t);
        _current = t;
        ReloadTemplates(selectName: t.Name);
        await ShowInfoAsync($"模板「{t.Name}」已保存。");
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        // 以列表选中项为准，避免编辑器改名后误删
        if (TemplateList.SelectedItem is not RomTemplate selected)
        {
            await ShowInfoAsync("请先在列表中选择要删除的模板。");
            return;
        }
        var dlg = new ContentDialog
        {
            Title = "删除模板",
            Content = $"确定删除模板「{selected.Name}」？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        TemplateService.Delete(selected.Name);
        ReloadTemplates();
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var t = ReadEditor();
        if (t.Name.Length == 0)
        {
            await ShowInfoAsync("请先填写模板名称。");
            return;
        }

        // 选择工作目录：优先会话最近使用（如刚解包的），其次设置中的默认值
        string? workspace = WorkspaceState.CurrentWorkspace;
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
        {
            workspace = AppSettings.Current.DefaultWorkspace;
        }
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            workspace = folder.Path;
            WorkspaceState.CurrentWorkspace = workspace;
        }

        List<string> log = new();
        try
        {
            log = await System.Threading.Tasks.Task.Run(() => TemplateService.Apply(t, workspace!));
        }
        catch (Exception ex)
        {
            await ShowInfoAsync($"应用模板失败：{ex.Message}");
            return;
        }

        var dlg = new ContentDialog
        {
            Title = $"模板「{t.Name}」应用完成",
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock { Text = string.Join('\n', log), TextWrapping = TextWrapping.Wrap },
            },
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }

    // ==================== 行编辑 ====================

    private void AddProp_Click(object sender, RoutedEventArgs e) =>
        PropOverrides.Add(new TemplatePropOverride());

    private void RemoveProp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TemplatePropOverride row })
        {
            PropOverrides.Remove(row);
        }
    }

    private void AddApp_Click(object sender, RoutedEventArgs e) =>
        RemoveAppItems.Add(new TemplateTextItem());

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TemplateTextItem row })
        {
            RemoveAppItems.Remove(row);
        }
    }

    private async void BrowseRootSource_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".img");
        picker.FileTypeFilter.Add(".apk");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) RootSourceBox.Text = file.Path;
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
