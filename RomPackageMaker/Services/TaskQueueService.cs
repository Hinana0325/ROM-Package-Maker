using System.Collections.ObjectModel;
using System.ComponentModel;

namespace RomPackageMaker.Services;

public enum TaskQueueKind
{
    Unpack,
    Pack,
}

public enum TaskQueueStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>队列中的单个任务（解包或打包），带进度与状态通知。</summary>
public sealed class TaskQueueItem : INotifyPropertyChanged
{
    private volatile TaskQueueStatus _status;
    private int _percent;
    private string _stage = string.Empty;
    private string? _error;
    private DateTime? _startedAt;
    private DateTime? _finishedAt;

    public Guid Id { get; } = Guid.NewGuid();

    // 注意：这些属性不能使用 init（会破坏 XAML 类型信息生成），对象初始化器赋值用 set 即可
    public TaskQueueKind Kind { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    // 解包：SourcePath + WorkspaceDir；打包：WorkspaceDir + OutputPath
    public string SourcePath { get; set; } = string.Empty;
    public string WorkspaceDir { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public bool Sign { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TaskQueueStatus Status
    {
        get => _status;
        set { _status = value; Raise(nameof(Status), nameof(StatusText), nameof(IsRunning), nameof(CanCancel), nameof(CanRemove)); }
    }

    public int Percent
    {
        get => _percent;
        set { _percent = value; Raise(nameof(Percent), nameof(PercentText)); }
    }

    public string Stage
    {
        get => _stage;
        set { _stage = value; Raise(nameof(Stage)); }
    }

    public string? Error
    {
        get => _error;
        set { _error = value; Raise(nameof(Error), nameof(HasError)); }
    }

    public DateTime? StartedAt
    {
        get => _startedAt;
        set { _startedAt = value; Raise(nameof(StartedAt)); }
    }

    public DateTime? FinishedAt
    {
        get => _finishedAt;
        set { _finishedAt = value; Raise(nameof(FinishedAt)); }
    }

    public string StatusText => Status switch
    {
        TaskQueueStatus.Queued => "排队中",
        TaskQueueStatus.Running => "运行中",
        TaskQueueStatus.Succeeded => "已完成",
        TaskQueueStatus.Failed => "失败",
        TaskQueueStatus.Canceled => "已取消",
        _ => Status.ToString(),
    };

    public string KindGlyph => Kind == TaskQueueKind.Unpack ? "\uE8C8" : "\uE8EF";

    /// <summary>进度百分比显示文本（如 "42%"）。</summary>
    public string PercentText => $"{Percent}%";

    public bool HasError => Error is not null;
    public bool IsRunning => Status == TaskQueueStatus.Running;
    public bool CanCancel => Status is TaskQueueStatus.Queued or TaskQueueStatus.Running;
    public bool CanRemove => Status is TaskQueueStatus.Succeeded or TaskQueueStatus.Failed or TaskQueueStatus.Canceled;

    internal CancellationTokenSource? Cts { get; set; }
    internal IProgress<RomTaskProgress>? Progress { get; set; }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>
/// 多任务队列：解包 / 打包任务按加入顺序依次执行（同一时间仅运行一个，避免磁盘争用）。
/// 支持排队任务的取消、运行中任务的取消（CancellationToken）、已结束任务的移除。
/// </summary>
public sealed class TaskQueueService
{
    public static TaskQueueService Instance { get; } = new();

    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _lock = new();
    private readonly List<TaskQueueItem> _pending = new();
    private Task? _worker;
    private readonly IRomPackService _rom = new RomPackService();

    /// <summary>
    /// UI 线程调度（App 启动时注入 DispatcherQueue.TryEnqueue；测试 / 控制台默认直接执行）。
    /// </summary>
    public Action<Action> UiMarshal { get; set; } = static action => action();

    /// <summary>全部任务（含已结束），按加入顺序。仅在 UI 线程变更。</summary>
    public ObservableCollection<TaskQueueItem> Items { get; } = new();

    public TaskQueueItem EnqueueUnpack(string sourcePath, string workspaceDir)
    {
        var item = new TaskQueueItem
        {
            Kind = TaskQueueKind.Unpack,
            DisplayName = $"解包 {Path.GetFileName(sourcePath)}",
            SourcePath = sourcePath,
            WorkspaceDir = workspaceDir,
        };
        return EnqueueCore(item);
    }

    public TaskQueueItem EnqueuePack(string workspaceDir, string outputPath, bool sign)
    {
        var item = new TaskQueueItem
        {
            Kind = TaskQueueKind.Pack,
            DisplayName = $"打包 → {Path.GetFileName(outputPath)}",
            WorkspaceDir = workspaceDir,
            OutputPath = outputPath,
            Sign = sign,
        };
        return EnqueueCore(item);
    }

    /// <summary>取消任务：排队中直接标记取消；运行中通过 CancellationToken 中断。</summary>
    public void Cancel(TaskQueueItem item)
    {
        if (item.Status == TaskQueueStatus.Queued)
        {
            item.Status = TaskQueueStatus.Canceled;
            lock (_lock) _pending.Remove(item);
        }
        else if (item.Status == TaskQueueStatus.Running)
        {
            item.Cts?.Cancel();
        }
    }

    /// <summary>移除已结束的任务记录。</summary>
    public void Remove(TaskQueueItem item)
    {
        if (!item.CanRemove) return;
        lock (_lock) _pending.Remove(item);
        Items.Remove(item);
    }

    /// <summary>清空全部已结束的任务记录。</summary>
    public void ClearFinished()
    {
        var finished = Items.Where(i => i.CanRemove).ToList();
        lock (_lock)
        {
            foreach (var item in finished) _pending.Remove(item);
        }
        foreach (var item in finished) Items.Remove(item);
    }

    private TaskQueueItem EnqueueCore(TaskQueueItem item)
    {
        var marshal = UiMarshal;
        // Progress 在调用线程创建（App 中为 UI 线程），回调再经 UiMarshal 回 UI 线程
        item.Progress = new Progress<RomTaskProgress>(p => marshal(() =>
        {
            item.Percent = p.Percent;
            item.Stage = p.Stage;
        }));

        lock (_lock) _pending.Add(item);
        Items.Add(item);
        _signal.Release();
        EnsureWorker();
        return item;
    }

    private void EnsureWorker()
    {
        lock (_lock)
        {
            _worker ??= Task.Run(WorkerLoopAsync);
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            await _signal.WaitAsync();
            TaskQueueItem? item;
            lock (_lock)
            {
                item = _pending.FirstOrDefault(i => i.Status == TaskQueueStatus.Queued);
            }
            if (item is null) continue;
            await RunItemAsync(item);
        }
    }

    private async Task RunItemAsync(TaskQueueItem item)
    {
        if (item.Status != TaskQueueStatus.Queued) return; // 排队期间被取消
        var marshal = UiMarshal;

        item.Cts = new CancellationTokenSource();
        marshal(() => { item.Status = TaskQueueStatus.Running; item.StartedAt = DateTime.Now; });

        try
        {
            if (item.Kind == TaskQueueKind.Unpack)
            {
                await _rom.UnpackAsync(item.SourcePath, item.WorkspaceDir, item.Progress!, item.Cts.Token);
            }
            else
            {
                await _rom.PackAsync(item.WorkspaceDir, item.OutputPath, item.Progress!, item.Cts.Token, item.Sign);
            }

            marshal(() => { item.Status = TaskQueueStatus.Succeeded; item.FinishedAt = DateTime.Now; });
            WorkspaceState.CurrentWorkspace = item.WorkspaceDir;
        }
        catch (OperationCanceledException)
        {
            marshal(() => { item.Status = TaskQueueStatus.Canceled; item.FinishedAt = DateTime.Now; });
        }
        catch (Exception ex)
        {
            marshal(() => { item.Status = TaskQueueStatus.Failed; item.Error = ex.Message; item.FinishedAt = DateTime.Now; });
        }
        finally
        {
            lock (_lock) _pending.Remove(item);
            var cts = item.Cts;
            item.Cts = null; // 先置空，避免 Cancel() 线程拿到已 Dispose 的 CTS
            cts?.Dispose();
        }
    }
}
