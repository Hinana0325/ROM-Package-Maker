using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RomPackageMaker.Services;
using Windows.System;

namespace RomPackageMaker.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshWorkspaceStatus();
    }

    /// <summary>根据会话工作目录 / 设置默认目录刷新状态卡片。</summary>
    private void RefreshWorkspaceStatus()
    {
        string? ws = WorkspaceState.CurrentWorkspace;
        if (string.IsNullOrEmpty(ws) && !string.IsNullOrEmpty(AppSettings.Current.DefaultWorkspace))
        {
            ws = AppSettings.Current.DefaultWorkspace;
        }

        if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws))
        {
            WorkspaceStatusText.Text = ws;
            WorkspaceStatusIcon.Glyph = "\uE8DA"; // 已就绪（对勾风）
            OpenWorkspaceButton.Visibility = Visibility.Visible;
        }
        else
        {
            WorkspaceStatusText.Text = "尚未选择。解包 ROM 后，各定制页面会自动跟随该目录。";
            WorkspaceStatusIcon.Glyph = "\uE8B7"; // 文件夹
            OpenWorkspaceButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void OpenWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        string? ws = WorkspaceState.CurrentWorkspace ?? AppSettings.Current.DefaultWorkspace;
        if (string.IsNullOrEmpty(ws) || !Directory.Exists(ws)) return;
        try
        {
            await Launcher.LaunchFolderPathAsync(ws);
        }
        catch
        {
            // 打开失败（路径被删等）静默忽略，状态由下次进入时刷新
        }
    }

    private void NavUnpack_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(UnpackPage));
    private void NavPack_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(PackPage));
    private void NavQueue_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(TaskQueuePage));
    private void NavInfo_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(InfoPage));
    private void NavBuildProp_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(BuildPropPage));
    private void NavTemplates_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(TemplatePage));
}
