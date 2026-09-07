using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RomPackageMaker.Services;
using Windows.Storage.Pickers;

namespace RomPackageMaker.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        // 缓存页面：切换导航后保留未保存的设置编辑状态
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = AppSettings.Current;
        ThemeCombo.SelectedIndex = s.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        WorkspaceBox.Text = s.DefaultWorkspace;
        OutputBox.Text = s.DefaultOutputDir;
        OpenOutputCheck.IsChecked = s.OpenOutputAfterPack;
        KeyBox.Text = s.SignKeyPath;
        CertBox.Text = s.SignCertPath;
        BlockSizeCombo.SelectedIndex = s.SparseBlockSize switch
        {
            1024 => 1,
            2048 => 2,
            _ => 0,
        };
    }

    private static void InitializePicker(object picker)
    {
        if (App.MainWindow is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is not ComboBoxItem item) return;
        string tag = (string)item.Tag;
        var app = (App)Application.Current;
        app.ApplyTheme(tag);
    }

    private async void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) WorkspaceBox.Text = folder.Path;
    }

    private void ClearWorkspace_Click(object sender, RoutedEventArgs e) => WorkspaceBox.Text = string.Empty;

    private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) OutputBox.Text = folder.Path;
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e) => OutputBox.Text = string.Empty;

    private async void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pk8");
        picker.FileTypeFilter.Add(".pfx");
        picker.FileTypeFilter.Add(".pem");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) KeyBox.Text = file.Path;
    }

    private async void BrowseCert_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pem");
        picker.FileTypeFilter.Add(".crt");
        picker.FileTypeFilter.Add(".cer");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) CertBox.Text = file.Path;
    }

    private void ClearSign_Click(object sender, RoutedEventArgs e)
    {
        KeyBox.Text = string.Empty;
        CertBox.Text = string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        if (ThemeCombo.SelectedItem is ComboBoxItem themeItem) s.Theme = (string)themeItem.Tag;
        s.DefaultWorkspace = WorkspaceBox.Text;
        s.DefaultOutputDir = OutputBox.Text;
        s.OpenOutputAfterPack = OpenOutputCheck.IsChecked == true;
        s.SignKeyPath = KeyBox.Text;
        s.SignCertPath = CertBox.Text;
        if (BlockSizeCombo.SelectedItem is ComboBoxItem bsItem)
            s.SparseBlockSize = uint.Parse((string)bsItem.Tag);
        s.Save();
        StatusText.Text = "设置已保存。";
    }
}
