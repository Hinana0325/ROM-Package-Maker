using RomPackageMaker.Application;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class TaskQueuePage : Page
{
    private TaskQueueService Queue => TaskQueueService.Instance;

    public TaskQueuePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        ItemsList.ItemsSource = Queue.Items;
        Queue.Items.CollectionChanged += Items_CollectionChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 空框预填最近工作目录（不清空用户已输入的内容）
        string? ws = WorkspaceState.CurrentWorkspace ?? AppSettings.Current.DefaultWorkspace;
        if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws))
        {
            if (string.IsNullOrEmpty(UnpackWsBox.Text)) UnpackWsBox.Text = ws;
            if (string.IsNullOrEmpty(PackWsBox.Text)) PackWsBox.Text = ws;
        }
        UpdateStats();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (TaskQueueItem item in e.NewItems)
            {
                item.PropertyChanged += Item_PropertyChanged;
            }
        }
        UpdateStats();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskQueueItem.Status))
        {
            UpdateStats();
        }
    }

    private static void InitializePicker(object picker)
    {
        if (App.MainWindow is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        }
    }

    // ==================== 新建任务 ====================

    private async void BrowseUnpackSource_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".img");
        picker.FileTypeFilter.Add(".bin");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) UnpackSourceBox.Text = file.Path;
    }

    private async void BrowseUnpackWs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            UnpackWsBox.Text = folder.Path;
            WorkspaceState.CurrentWorkspace = folder.Path;
        }
    }

    private async void BrowsePackWs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            PackWsBox.Text = folder.Path;
            WorkspaceState.CurrentWorkspace = folder.Path;
        }
    }

    private async void BrowsePackOut_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeChoices.Add("刷机包", new List<string> { ".zip" });
        picker.SuggestedFileName = "new_rom";
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is not null) PackOutBox.Text = file.Path;
    }

    private void AddUnpackButton_Click(object sender, RoutedEventArgs e)
    {
        string source = UnpackSourceBox.Text.Trim();
        string ws = UnpackWsBox.Text.Trim();
        if (!File.Exists(source))
        {
            StatsText.Text = "请先选择存在的 ROM 文件。";
            return;
        }
        if (ws.Length == 0)
        {
            StatsText.Text = "请先选择解包工作目录。";
            return;
        }

        var item = Queue.EnqueueUnpack(source, ws);
        WorkspaceState.CurrentWorkspace = ws;
        StatsText.Text = $"已加入队列：{item.DisplayName}";
    }

    private void AddPackButton_Click(object sender, RoutedEventArgs e)
    {
        string ws = PackWsBox.Text.Trim();
        string output = PackOutBox.Text.Trim();
        if (!Directory.Exists(ws))
        {
            StatsText.Text = "请先选择存在的工作目录。";
            return;
        }
        if (output.Length == 0)
        {
            StatsText.Text = "请先选择输出 zip 路径。";
            return;
        }

        var item = Queue.EnqueuePack(ws, output, SignCheck.IsChecked == true);
        WorkspaceState.CurrentWorkspace = ws;
        StatsText.Text = $"已加入队列：{item.DisplayName}";
    }

    // ==================== 队列操作 ====================

    private void CancelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskQueueItem item })
        {
            Queue.Cancel(item);
        }
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskQueueItem item })
        {
            Queue.Remove(item);
        }
    }

    private void ClearFinishedButton_Click(object sender, RoutedEventArgs e) => Queue.ClearFinished();

    private void UpdateStats()
    {
        var items = Queue.Items;
        int queued = items.Count(i => i.Status == TaskQueueStatus.Queued);
        int running = items.Count(i => i.Status == TaskQueueStatus.Running);
        int finished = items.Count(i => i.CanRemove);

        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatsText.Text = items.Count == 0
            ? "队列为空。任务按加入顺序依次执行（同一时间仅运行一个）。"
            : $"排队 {queued} · 运行 {running} · 已结束 {finished}";
    }
}
