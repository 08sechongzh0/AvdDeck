using AvdDeck.Models;
using AvdDeck.Services;
using System.Text.RegularExpressions;
using System.Windows;

namespace AvdDeck.Dialogs;

public partial class CreateAvdDialog : Window
{
    private readonly AndroidSdkService _sdk;
    public string AvdName => NameBox.Text.Trim();
    public SystemImageInfo? SelectedImage => ImageBox.SelectedItem as SystemImageInfo;
    public string DeviceId => (DeviceBox.SelectedItem as DeviceProfileInfo)?.Id ?? "pixel";

    public CreateAvdDialog(AndroidSdkService sdk, IEnumerable<SystemImageInfo> images)
    {
        InitializeComponent();
        _sdk = sdk;
        ImageBox.ItemsSource = images;
        ImageBox.SelectedIndex = 0;
        NoImages.Visibility = ImageBox.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        Loaded += async (_, _) => await LoadProfilesAsync();
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = await _sdk.GetDeviceProfilesAsync();
            DeviceBox.ItemsSource = profiles;
            DeviceBox.SelectedItem = profiles.FirstOrDefault(x => x.Id.Equals("pixel_7", StringComparison.OrdinalIgnoreCase)) ?? profiles.FirstOrDefault();
            DeviceBox.IsEnabled = profiles.Count > 0;
            CreateButton.IsEnabled = profiles.Count > 0 && SelectedImage is not null;
            DeviceHint.Text = profiles.Count > 0 ? $"已载入 {profiles.Count} 个" : "未找到模板";
        }
        catch (Exception ex)
        {
            DeviceHint.Text = "加载失败";
            ErrorText.Text = ex.Message;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!Regex.IsMatch(AvdName, @"^[A-Za-z0-9_.-]+$")) { ErrorText.Text = "名称只能包含字母、数字、下划线、点和连字符。"; return; }
        if (SelectedImage is null) { ErrorText.Text = "请选择一个系统镜像。"; return; }
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
