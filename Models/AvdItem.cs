using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AvdDeck.Models;

public sealed class AvdItem : INotifyPropertyChanged
{
    private bool _isRunning;
    private bool _isBusy;
    private string? _serial;

    public required string Name { get; init; }
    public required string FolderPath { get; init; }
    public string DeviceName { get; init; } = "Android device";
    public string Manufacturer { get; init; } = "Android";
    public string ApiLevel { get; init; } = "—";
    public string AndroidVersion { get; init; } = "Android";
    public string Abi { get; init; } = "—";
    public string Resolution { get; init; } = "—";
    public string Ram { get; init; } = "—";
    public string DataSize { get; init; } = "—";
    public string Target { get; init; } = "—";

    public bool IsRunning { get => _isRunning; set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); } }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); } }
    public string? Serial { get => _serial; set { _serial = value; OnPropertyChanged(); } }
    public string StatusText => IsRunning ? "运行中" : "已停止";
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "A" : Name[..1].ToUpperInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SystemImageInfo(string PackageId, string Label)
{
    public override string ToString() => Label;
}

public sealed record DeviceProfileInfo(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record EnvironmentReport(
    bool SdkDirectory,
    bool CommandLineTools,
    bool Emulator,
    bool Adb,
    bool SystemImage,
    bool HasAvd)
{
    public bool RuntimeReady => SdkDirectory && CommandLineTools && Emulator && Adb && SystemImage;
    public bool FullyReady => RuntimeReady && HasAvd;
}

public sealed record InstallProgress(string Step, int Percent);

public sealed record InstallResult(bool Success, string? CreatedAvdName, string Message);

public sealed record SnapshotItem(string Name, DateTime Modified, long SizeBytes, bool IsQuickBoot)
{
    public string DisplayName => IsQuickBoot ? "Quick Boot" : Name;
    public string SizeText => SizeBytes <= 0 ? "—" : SizeBytes >= 1_073_741_824 ? $"{SizeBytes / 1_073_741_824d:0.##} GB" : $"{SizeBytes / 1_048_576d:0} MB";
    public string ModifiedText => Modified == DateTime.MinValue ? "未知时间" : Modified.ToString("yyyy-MM-dd  HH:mm");
}
