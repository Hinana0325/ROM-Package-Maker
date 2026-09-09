using RomPackageMaker.Application;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RomPackageMaker.Pages;
// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace RomPackageMaker;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 应用保存的主题
        ((FrameworkElement)Content).RequestedTheme = AppSettings.Current.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        NavFrame.Navigated += NavFrame_Navigated;
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack) NavFrame.GoBack();
    }

    private static Type? PageTypeForTag(object? tag) => tag switch
    {
        "home" => typeof(HomePage),
        "info" => typeof(InfoPage),
        "unpack" => typeof(UnpackPage),
        "pack" => typeof(PackPage),
        "taskqueue" => typeof(TaskQueuePage),
        "payload" => typeof(PayloadPage),
        "buildprop" => typeof(BuildPropPage),
        "debloat" => typeof(DebloatPage),
        "preinstall" => typeof(PreinstallPage),
        "root" => typeof(RootPage),
        "avb" => typeof(AvbPage),
        "diff" => typeof(DiffPage),
        "templates" => typeof(TemplatePage),
        "about" => typeof(AboutPage),
        _ => null,
    };

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            if (NavFrame.Content is not SettingsPage)
                NavFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            Type? pageType = PageTypeForTag(item.Tag);
            if (pageType is null)
                throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            // 已在该页面时跳过，避免重复入栈与重建（配合页面缓存保留状态）
            if (NavFrame.Content?.GetType() != pageType)
                NavFrame.Navigate(pageType);
        }
    }

    /// <summary>导航完成后同步选中项高亮（覆盖 GoBack 场景），并更新标题栏返回按钮可见性。</summary>
    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // Frame.CanGoBack 无变更通知，x:Bind 不会刷新，这里手动驱动
        AppTitleBar.IsBackButtonVisible = NavFrame.CanGoBack;

        object? target = null;
        if (e.Content is SettingsPage)
        {
            target = NavView.SettingsItem;
        }
        else
        {
            string? tag = e.Content switch
            {
                HomePage => "home",
                InfoPage => "info",
                UnpackPage => "unpack",
                PackPage => "pack",
                TaskQueuePage => "taskqueue",
                PayloadPage => "payload",
                BuildPropPage => "buildprop",
                DebloatPage => "debloat",
                PreinstallPage => "preinstall",
                RootPage => "root",
                AvbPage => "avb",
                DiffPage => "diff",
                TemplatePage => "templates",
                AboutPage => "about",
                _ => null,
            };
            if (tag is not null)
            {
                target = NavView.MenuItems
                    .Concat(NavView.FooterMenuItems)
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(i => (string?)i.Tag == tag);
            }
        }

        if (target is not null && !ReferenceEquals(NavView.SelectedItem, target))
        {
            NavView.SelectedItem = target;
        }
    }
}
