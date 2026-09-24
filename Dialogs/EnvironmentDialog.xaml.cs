using AvdDeck.Models;
using AvdDeck.Services;
using System.Windows;
using System.Windows.Media;

namespace AvdDeck.Dialogs;

public partial class EnvironmentDialog : Window
{
    private readonly AndroidSdkService _sdk;
    private EnvironmentReport _report;
    public bool EnvironmentReady => _report.FullyReady;
    public bool EnvironmentChanged { get; private set; }

    public EnvironmentDialog(AndroidSdkService sdk, EnvironmentReport initialReport)
    {
        InitializeComponent();
        _sdk = sdk; _report = initialReport;
        Render(initialReport);
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }

    private void Render(EnvironmentReport report)
    {
        _report = report;
        SetStatus(SdkCheck, report.SdkDirectory);
        SetStatus(ToolsCheck, report.CommandLineTools);
        SetStatus(EmulatorCheck, report.Emulator);
        SetStatus(AdbCheck, report.Adb);
        SetStatus(ImageCheck, report.SystemImage);
        SetStatus(AvdCheck, report.HasAvd);
        if (report.FullyReady)
        {
            SummaryTitle.Text = "环境已就绪"; SummaryText.Text = "所有组件均已安装，可以直接运行虚拟设备";
            SummaryIcon.Text = "\uE73E"; SummaryIcon.Foreground = (Brush)FindResource("Success"); SummaryIconBg.Background = new SolidColorBrush(Color.FromRgb(23, 56, 45));
            InstallButton.Content = "完成";
        }
        else
        {
            SummaryTitle.Text = "发现缺失组件"; SummaryText.Text = report.RuntimeReady ? "运行环境完整，但还没有虚拟设备" : "可一键下载并补全运行环境";
            SummaryIcon.Text = "\uE7BA"; SummaryIcon.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); SummaryIconBg.Background = new SolidColorBrush(Color.FromRgb(57, 45, 22));
            InstallButton.Content = report.RuntimeReady ? "创建并启动 AVD" : "一键安装并创建";
        }
    }

    private void SetStatus(System.Windows.Controls.TextBlock block, bool ok)
    {
        block.Text = ok ? "已安装" : "缺失";
        block.Foreground = (Brush)FindResource(ok ? "Success" : "Danger");
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await RecheckAsync();

    private async Task RecheckAsync()
    {
        SetBusy(true, "正在重新检测…");
        try { Render(await _sdk.CheckEnvironmentAsync()); }
        catch (Exception ex) { ProgressText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_report.FullyReady) { DialogResult = true; return; }
        var message = _report.RuntimeReady
            ? "将使用已安装的系统镜像创建一个默认 Pixel 虚拟设备，并在完成后启动。"
            : "将从 Google 官方仓库下载 Android 命令行工具、Emulator、ADB 和 Android 15 系统镜像。下载量可能超过 2 GB；继续即表示你同意 Android SDK 许可条款。";
        if (!ConfirmDialog.Ask(this, "开始自动配置？", message, _report.RuntimeReady ? "创建并启动" : "同意并开始")) return;

        SetBusy(true, "正在准备安装…");
        InstallProgress.Visibility = Visibility.Visible;
        var progress = new Progress<InstallProgress>(p => { ProgressText.Text = p.Step; InstallProgress.Value = p.Percent; });
        var result = await _sdk.EnsureEnvironmentAsync(progress);
        EnvironmentChanged = result.Success;
        if (result.Success && result.CreatedAvdName is not null)
        {
            try { _sdk.StartAvd(result.CreatedAvdName); ProgressText.Text = $"{result.CreatedAvdName} 正在启动…"; }
            catch (Exception ex) { ProgressText.Text = "环境安装完成，但启动失败：" + ex.Message; }
        }
        else ProgressText.Text = result.Message;
        Render(await _sdk.CheckEnvironmentAsync());
        SetBusy(false);
    }

    private void SetBusy(bool busy, string? text = null)
    {
        RecheckButton.IsEnabled = !busy; InstallButton.IsEnabled = !busy;
        if (text is not null) ProgressText.Text = text;
        if (!busy && InstallProgress.Value < 100) InstallProgress.Visibility = Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = EnvironmentReady;
}
