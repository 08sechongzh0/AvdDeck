using AvdDeck.Models;
using AvdDeck.Services;
using System.Windows;
using System.Windows.Controls;

namespace AvdDeck.Dialogs;

public partial class DeviceSettingsDialog : Window
{
    private readonly AndroidSdkService _sdk;
    private readonly AvdItem _avd;

    public DeviceSettingsDialog(AndroidSdkService sdk, AvdItem avd)
    {
        InitializeComponent();
        _sdk = sdk; _avd = avd;
        Subtitle.Text = avd.Name;
        var config = sdk.ReadAvdConfig(avd);
        RamBox.Text = new string((config.GetValueOrDefault("hw.ramSize") ?? "2048").TakeWhile(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(RamBox.Text)) RamBox.Text = "2048";
        SelectCombo(CoreBox, config.GetValueOrDefault("hw.cpu.ncore") ?? "4", false);
        SelectCombo(GpuBox, config.GetValueOrDefault("hw.gpu.mode") ?? "auto", true);
        KeyboardBox.IsChecked = string.Equals(config.GetValueOrDefault("hw.keyboard"), "yes", StringComparison.OrdinalIgnoreCase);
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }

    private static void SelectCombo(ComboBox box, string value, bool byTag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            var actual = byTag ? item.Tag?.ToString() : item.Content?.ToString();
            if (actual == value) { box.SelectedItem = item; return; }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RamBox.Text, out var ram) || ram < 512 || ram > 32768) { ErrorText.Text = "内存请输入 512–32768 之间的整数。"; return; }
        var cores = int.Parse(((ComboBoxItem)CoreBox.SelectedItem).Content.ToString()!);
        var gpu = ((ComboBoxItem)GpuBox.SelectedItem).Tag?.ToString() ?? "auto";
        try { _sdk.SaveAvdConfig(_avd, ram, cores, KeyboardBox.IsChecked == true, gpu); DialogResult = true; }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
