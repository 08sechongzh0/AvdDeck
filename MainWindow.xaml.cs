using AvdDeck.Dialogs;
using AvdDeck.Models;
using AvdDeck.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AvdDeck;

public partial class MainWindow : Window
{
    private readonly AndroidSdkService _sdk = new();
    private readonly ObservableCollection<AvdItem> _avds = [];
    private ICollectionView? _view;
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            LoadSavedSdk();
            AvdList.ItemsSource = _avds;
            _view = CollectionViewSource.GetDefaultView(_avds);
            _view.Filter = FilterAvd;
            UpdateSdkStatus();
            await RefreshAsync();
            await RunFirstEnvironmentCheckAsync();
            _ = _sdk.GetDeviceProfilesAsync();
        };
        StateChanged += (_, _) => { };
    }

    private async Task RefreshAsync(string? preserveName = null)
    {
        if (_refreshing) return;
        _refreshing = true;
        LoadingBar.Visibility = Visibility.Visible;
        StatusText.Text = "正在扫描 Android 虚拟设备…";
        try
        {
            preserveName ??= (AvdList.SelectedItem as AvdItem)?.Name;
            var items = await _sdk.LoadAvdsAsync();
            _avds.Clear();
            foreach (var item in items) _avds.Add(item);
            var running = items.Count(x => x.IsRunning);
            CountText.Text = $"{items.Count} 台设备" + (running > 0 ? $"  ·  {running} 台正在运行" : "");
            EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (preserveName is not null) AvdList.SelectedItem = _avds.FirstOrDefault(x => x.Name == preserveName);
            if (AvdList.SelectedItem is null && _avds.Count > 0) AvdList.SelectedIndex = 0;
            StatusText.Text = $"上次刷新 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "刷新失败";
            ShowToast(ex.Message, false);
        }
        finally { LoadingBar.Visibility = Visibility.Collapsed; _refreshing = false; }
    }

    private bool FilterAvd(object item)
    {
        if (item is not AvdItem avd) return false;
        var query = SearchBox?.Text?.Trim();
        return string.IsNullOrEmpty(query) || avd.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || avd.DeviceName.Contains(query, StringComparison.OrdinalIgnoreCase) || avd.ApiLevel.Contains(query, StringComparison.OrdinalIgnoreCase) || avd.AndroidVersion.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ToggleAvdAsync(AvdItem avd, bool coldBoot = false)
    {
        if (avd.IsBusy) return;
        if (!avd.IsRunning && !_sdk.IsReady)
        {
            await ShowEnvironmentDialogAsync(await _sdk.CheckEnvironmentAsync());
            return;
        }
        avd.IsBusy = true;
        try
        {
            if (avd.IsRunning)
            {
                if (string.IsNullOrWhiteSpace(avd.Serial)) throw new InvalidOperationException("未能识别该模拟器的 ADB 序列号。");
                StatusText.Text = $"正在停止 {avd.Name}…";
                await _sdk.StopAvdAsync(avd.Serial);
                ShowToast($"已向 {avd.Name} 发送停止命令");
                await Task.Delay(900);
            }
            else
            {
                _sdk.StartAvd(avd.Name, coldBoot);
                StatusText.Text = $"正在启动 {avd.Name}…";
                ShowToast(coldBoot ? $"{avd.Name} 正在冷启动" : $"{avd.Name} 正在启动");
                await _sdk.WaitUntilRunningAsync(avd.Name, TimeSpan.FromSeconds(25));
            }
            await RefreshAsync(avd.Name);
        }
        catch (Exception ex) { ShowToast(ex.Message, false); StatusText.Text = "操作失败"; }
        finally { avd.IsBusy = false; }
    }

    private async void QuickStart_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is AvdItem avd) await ToggleAvdAsync(avd);
    }

    private async void MainAction_Click(object sender, RoutedEventArgs e)
    {
        if (AvdList.SelectedItem is AvdItem avd) await ToggleAvdAsync(avd);
    }

    private async void ColdBoot_Click(object sender, RoutedEventArgs e)
    {
        if (AvdList.SelectedItem is not AvdItem avd) return;
        if (avd.IsRunning) { ShowToast("请先停止设备，再执行冷启动。", false); return; }
        await ToggleAvdAsync(avd, true);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (AvdList.SelectedItem is null) return;
        MorePopup.IsOpen = !MorePopup.IsOpen;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MorePopup.IsOpen && !MoreButton.IsMouseOver) MorePopup.IsOpen = false;
    }

    private void MenuEdit_Click(object sender, RoutedEventArgs e) { MorePopup.IsOpen = false; if (AvdList.SelectedItem is AvdItem avd) OpenEditor(avd); }
    private void MenuSnapshots_Click(object sender, RoutedEventArgs e) { MorePopup.IsOpen = false; if (AvdList.SelectedItem is AvdItem avd) new SnapshotDialog(_sdk, avd) { Owner = this }.ShowDialog(); }
    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e) { MorePopup.IsOpen = false; if (AvdList.SelectedItem is AvdItem avd) _sdk.OpenFolder(avd.FolderPath); }
    private async void MenuWipe_Click(object sender, RoutedEventArgs e) { MorePopup.IsOpen = false; if (AvdList.SelectedItem is AvdItem avd) await WipeAndStartAsync(avd); }
    private async void MenuDelete_Click(object sender, RoutedEventArgs e) { MorePopup.IsOpen = false; if (AvdList.SelectedItem is AvdItem avd) await DeleteAsync(avd); }

    private async Task WipeAndStartAsync(AvdItem avd)
    {
        if (avd.IsRunning) { ShowToast("请先停止设备，再擦除数据。", false); return; }
        if (!ConfirmDialog.Ask(this, "擦除设备数据？", $"{avd.Name} 中的应用、账号和设置都会被永久删除。", "擦除并启动", true)) return;
        try { _sdk.StartAvd(avd.Name, wipeData: true); ShowToast($"正在擦除并启动 {avd.Name}"); await Task.Delay(1200); await RefreshAsync(avd.Name); }
        catch (Exception ex) { ShowToast(ex.Message, false); }
    }

    private async Task DeleteAsync(AvdItem avd)
    {
        if (avd.IsRunning) { ShowToast("请先停止设备，再删除。", false); return; }
        if (!ConfirmDialog.Ask(this, "删除虚拟设备？", $"{avd.Name} 的配置、用户数据和快照都会被永久删除。", "删除设备", true)) return;
        try
        {
            LoadingBar.Visibility = Visibility.Visible;
            var result = await _sdk.DeleteAvdAsync(avd.Name);
            if (!result.Success) throw new InvalidOperationException(result.BestMessage);
            ShowToast($"已删除 {avd.Name}");
            await RefreshAsync();
        }
        catch (Exception ex) { ShowToast(ex.Message, false); }
        finally { LoadingBar.Visibility = Visibility.Collapsed; }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (AvdList.SelectedItem is AvdItem avd) OpenEditor(avd);
    }

    private void OpenEditor(AvdItem avd)
    {
        if (avd.IsRunning) { ShowToast("请先停止设备，再修改硬件配置。", false); return; }
        var dialog = new DeviceSettingsDialog(_sdk, avd) { Owner = this };
        if (dialog.ShowDialog() == true) { ShowToast("设备配置已保存"); _ = RefreshAsync(avd.Name); }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_sdk.AvdManagerPath is null) { ShowToast("未找到 avdmanager，请检查 Android SDK。", false); return; }
        var dialog = new CreateAvdDialog(_sdk, _sdk.GetInstalledSystemImages()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        LoadingBar.Visibility = Visibility.Visible;
        StatusText.Text = $"正在创建 {dialog.AvdName}…";
        try
        {
            var result = await _sdk.CreateAvdAsync(dialog.AvdName, dialog.SelectedImage!.PackageId, dialog.DeviceId);
            if (!result.Success) throw new InvalidOperationException(result.BestMessage);
            ShowToast($"已创建 {dialog.AvdName}");
            await RefreshAsync(dialog.AvdName);
        }
        catch (Exception ex) { ShowToast(ex.Message, false); }
        finally { LoadingBar.Visibility = Visibility.Collapsed; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (SearchHint is not null) SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed; _view?.Refresh(); }

    private void AvdList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var avd = AvdList.SelectedItem as AvdItem;
        NoSelection.Visibility = avd is null ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = avd is null ? Visibility.Collapsed : Visibility.Visible;
        if (avd is null) return;
        DetailStatusText.Text = avd.StatusText;
        DetailStatusDot.Fill = avd.IsRunning ? (Brush)FindResource("Success") : new SolidColorBrush(Color.FromRgb(89, 98, 115));
        MainActionText.Text = avd.IsRunning ? "停止设备" : "启动设备";
        MainActionIcon.Text = avd.IsRunning ? "\uE71A" : "\uE768";
        MainActionButton.Background = avd.IsRunning ? new SolidColorBrush(Color.FromRgb(35, 66, 57)) : (Brush)FindResource("Accent");
    }

    private async void SdkSettings_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择 Android SDK 目录", InitialDirectory = _sdk.SdkRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        _sdk.SetSdkRoot(picker.FolderName);
        UpdateSdkStatus();
        if (_sdk.IsReady)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, picker.FolderName);
            await RefreshAsync();
        }
        else ShowToast("所选目录不是有效的 Android SDK。", false);
    }

    private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvdDeck", "sdk.path");
    private string CheckMarkerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvdDeck", "environment.checked");
    private void LoadSavedSdk() { try { if (File.Exists(SettingsPath)) _sdk.SetSdkRoot(File.ReadAllText(SettingsPath).Trim()); } catch { } }

    private async Task RunFirstEnvironmentCheckAsync()
    {
        if (File.Exists(CheckMarkerPath)) return;
        try
        {
            StatusText.Text = "正在执行首次环境检测…";
            var report = await _sdk.CheckEnvironmentAsync();
            if (report.FullyReady) SaveEnvironmentMarker();
            else await ShowEnvironmentDialogAsync(report);
        }
        catch (Exception ex) { StatusText.Text = "首次检测失败"; ShowToast(ex.Message, false); }
    }

    private async void EnvironmentCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "正在检测 Android 环境…";
            var report = await _sdk.CheckEnvironmentAsync();
            await ShowEnvironmentDialogAsync(report);
        }
        catch (Exception ex) { ShowToast(ex.Message, false); }
    }

    private async Task ShowEnvironmentDialogAsync(EnvironmentReport report)
    {
        var dialog = new EnvironmentDialog(_sdk, report) { Owner = this };
        dialog.ShowDialog();
        if (dialog.EnvironmentReady) SaveEnvironmentMarker();
        if (dialog.EnvironmentChanged)
        {
            UpdateSdkStatus();
            await RefreshAsync();
        }
        StatusText.Text = dialog.EnvironmentReady ? "Android 环境已就绪" : "环境检测完成";
    }

    private void SaveEnvironmentMarker()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CheckMarkerPath)!);
        File.WriteAllText(CheckMarkerPath, DateTimeOffset.Now.ToString("O"));
    }
    private void UpdateSdkStatus()
    {
        SdkPathText.Text = _sdk.SdkRoot ?? "未检测到 Android SDK";
        SdkStatus.Text = _sdk.IsReady ? "SDK 已就绪" : "SDK 未配置";
        SdkDot.Fill = _sdk.IsReady ? (Brush)FindResource("Success") : (Brush)FindResource("Danger");
    }

    private async void ShowToast(string message, bool success = true)
    {
        ToastText.Text = message.Length > 90 ? message[..90] + "…" : message;
        ToastIcon.Text = success ? "\uE73E" : "\uEA39";
        ToastIcon.Foreground = (Brush)FindResource(success ? "Success" : "Danger");
        Toast.Opacity = 0;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        await Task.Delay(2800);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
