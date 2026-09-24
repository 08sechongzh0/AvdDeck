using AvdDeck.Models;
using AvdDeck.Services;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AvdDeck.Dialogs;

public partial class SnapshotDialog : Window
{
    private readonly AndroidSdkService _sdk;
    private readonly AvdItem _avd;
    private readonly ObservableCollection<SnapshotItem> _snapshots = [];

    public SnapshotDialog(AndroidSdkService sdk, AvdItem avd)
    {
        InitializeComponent();
        _sdk = sdk; _avd = avd;
        Subtitle.Text = avd.Name;
        SnapshotList.ItemsSource = _snapshots;
        NameBox.Text = $"snapshot_{DateTime.Now:yyyyMMdd_HHmm}";
        StateDot.Fill = avd.IsRunning ? (Brush)FindResource("Success") : new SolidColorBrush(Color.FromRgb(249, 112, 102));
        StateText.Text = avd.IsRunning ? $"设备运行中 · {avd.Serial} · 可创建、加载和删除快照" : "设备已停止 · 可以查看快照，操作前请先启动设备";
        StateText.Foreground = avd.IsRunning ? (Brush)FindResource("Success") : (Brush)FindResource("Muted");
        SaveButton.IsEnabled = avd.IsRunning;
        NameBox.IsEnabled = avd.IsRunning;
        Loaded += async (_, _) => await RefreshSnapshotsAsync();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }

    private async Task RefreshSnapshotsAsync(string? selectName = null)
    {
        SetBusy(true);
        try
        {
            var items = await _sdk.GetSnapshotsAsync(_avd);
            _snapshots.Clear();
            foreach (var item in items) _snapshots.Add(item);
            EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SnapshotList.SelectedItem = selectName is null ? items.FirstOrDefault() : items.FirstOrDefault(x => x.Name == selectName);
        }
        finally { SetBusy(false); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_.-]{1,48}$") || name.Equals("default_boot", StringComparison.OrdinalIgnoreCase))
        {
            StateText.Text = "名称须为 1–48 个字母、数字、点、下划线或连字符，且不能使用 default_boot。";
            StateText.Foreground = (Brush)FindResource("Danger"); return;
        }
        await RunOperationAsync(() => _sdk.SaveSnapshotAsync(_avd, name), $"正在创建快照 {name}…", name);
        NameBox.Text = $"snapshot_{DateTime.Now:yyyyMMdd_HHmm}";
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotItem item) return;
        if (!ConfirmDialog.Ask(this, "加载这个快照？", "虚拟设备当前尚未保存的状态会被快照内容覆盖。", "加载快照")) return;
        await RunOperationAsync(() => _sdk.LoadSnapshotAsync(_avd, item.Name), $"正在加载 {item.DisplayName}…", item.Name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotItem item) return;
        if (!ConfirmDialog.Ask(this, "删除这个快照？", $"快照“{item.DisplayName}”将被永久删除。", "删除快照", true)) return;
        await RunOperationAsync(() => _sdk.DeleteSnapshotAsync(_avd, item.Name), $"正在删除 {item.DisplayName}…");
    }

    private async Task RunOperationAsync(Func<Task<ProcessResult>> operation, string message, string? selectName = null)
    {
        SetBusy(true); StateText.Text = message; StateText.Foreground = (Brush)FindResource("Accent");
        try
        {
            var result = await operation();
            var failed = !result.Success || result.StdOut.Contains("KO", StringComparison.OrdinalIgnoreCase) || result.StdOut.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
            if (failed) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.BestMessage) ? "Emulator 未完成快照操作。" : result.BestMessage);
            StateText.Text = "操作完成"; StateText.Foreground = (Brush)FindResource("Success");
            await RefreshSnapshotsAsync(selectName);
        }
        catch (Exception ex) { StateText.Text = ex.Message; StateText.Foreground = (Brush)FindResource("Danger"); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !busy && _avd.IsRunning;
        LoadButton.IsEnabled = !busy && _avd.IsRunning && SnapshotList.SelectedItem is not null;
        DeleteButton.IsEnabled = !busy && _avd.IsRunning && SnapshotList.SelectedItem is not null;
    }

    private void SnapshotList_SelectionChanged(object sender, SelectionChangedEventArgs e) { LoadButton.IsEnabled = _avd.IsRunning && SnapshotList.SelectedItem is not null; DeleteButton.IsEnabled = LoadButton.IsEnabled; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshSnapshotsAsync((SnapshotList.SelectedItem as SnapshotItem)?.Name);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
