using TaskQueueService = RomPackageMaker.Application.TaskQueueService;
using Microsoft.UI.Xaml;
// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace RomPackageMaker;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    /// <summary>应用主窗口，供文件/文件夹选择器等需要窗口句柄的场景使用。</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // 记录未处理异常，便于诊断（写入 %TEMP%\rompkg_crash.log）
        UnhandledException += (s, e) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "rompkg_crash.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{e.Exception}");
            }
            catch { /* 忽略日志写入失败 */ }
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindow = _window;

        // 任务队列服务的 UI 线程调度（状态/进度通知回 UI 线程）
        var dispatcher = _window.DispatcherQueue;
        TaskQueueService.Instance.UiMarshal = action => dispatcher.TryEnqueue(() => action());

        _window.Activate();
    }

    /// <summary>切换应用主题：system / light / dark。窗口存在时即时生效。</summary>
    public void ApplyTheme(string theme)
    {
        if (_window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }
}
