using Microsoft.UI.Xaml;
using RomPackageMaker.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace RomPackageMaker;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>应用主窗口，供文件/文件夹选择器等需要窗口句柄的场景使用。</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (s, e) =>
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "rompkg_crash.log"), e.Exception.ToString());
            e.Handled = false;
        };
        // 启动时应用保存的主题
        ApplyTheme(AppSettings.Current.Theme);
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "rompkg_crash.log"), ex.ToString());
            throw;
        }
    }

    /// <summary>切换应用主题：system / light / dark。</summary>
    public void ApplyTheme(string theme)
    {
        var root = _window?.Content as FrameworkElement;
        if (root is null)
        {
            // 窗口尚未创建，存到资源中稍后应用
            Resources["RequestedThemeOverride"] = theme;
            return;
        }
        root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
