using AvdDeck.Models;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AvdDeck.Services;

public sealed class AndroidSdkService
{
    private List<DeviceProfileInfo>? _deviceProfileCache;
    public string? SdkRoot { get; private set; }
    public string? EmulatorPath => FindTool("emulator", "emulator.exe");
    public string? AdbPath => FindTool("platform-tools", "adb.exe");
    public string? AvdManagerPath => FindCmdTool("avdmanager.bat");
    public string? SdkManagerPath => FindCmdTool("sdkmanager.bat");
    public bool IsReady => EmulatorPath is not null && AdbPath is not null;

    public AndroidSdkService() => SdkRoot = DiscoverSdk();

    public void SetSdkRoot(string path) => SdkRoot = Directory.Exists(path) ? path : null;

    public async Task<EnvironmentReport> CheckEnvironmentAsync()
    {
        var sdk = SdkRoot is not null && Directory.Exists(SdkRoot);
        var images = GetInstalledSystemImages().Count > 0;
        var avds = false;
        if (EmulatorPath is not null)
        {
            var list = await RunCaptureAsync(EmulatorPath, "-list-avds", timeoutMs: 15_000);
            avds = list.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length > 0;
        }
        return new(sdk, SdkManagerPath is not null && AvdManagerPath is not null, EmulatorPath is not null, AdbPath is not null, images, avds);
    }

    public async Task<InstallResult> EnsureEnvironmentAsync(IProgress<InstallProgress>? progress = null)
    {
        try
        {
            var sdkRoot = SdkRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
            Directory.CreateDirectory(sdkRoot);
            SdkRoot = sdkRoot;

            if (SdkManagerPath is null)
            {
                progress?.Report(new("正在下载 Android 命令行工具…", 8));
                await InstallCommandLineToolsAsync(sdkRoot, progress);
            }

            var manager = SdkManagerPath ?? throw new InvalidOperationException("Android 命令行工具安装失败，未找到 sdkmanager。请检查网络后重试。");
            progress?.Report(new("正在安装 Emulator、ADB 和 Android 15 镜像…", 38));
            var packages = "\"platform-tools\" \"emulator\" \"platforms;android-35\" \"system-images;android-35;google_apis;x86_64\"";
            var approvals = string.Concat(Enumerable.Repeat("y\n", 120));
            var install = await RunCaptureAsync(manager, $"--sdk_root={Quote(sdkRoot)} {packages}", approvals, 30 * 60_000);
            if (!install.Success) throw new InvalidOperationException(string.IsNullOrWhiteSpace(install.BestMessage) ? "SDK 组件安装失败。" : install.BestMessage);

            progress?.Report(new("正在验证安装结果…", 84));
            var report = await CheckEnvironmentAsync();
            if (!report.RuntimeReady) throw new InvalidOperationException("部分 Android 组件未能正确安装，请点击“重新检测”查看状态。");

            string? created = null;
            if (!report.HasAvd)
            {
                progress?.Report(new("正在创建默认虚拟设备…", 91));
                var profiles = await GetDeviceProfilesAsync();
                var device = profiles.FirstOrDefault(x => x.Id.Equals("pixel_7", StringComparison.OrdinalIgnoreCase))
                    ?? profiles.FirstOrDefault(x => x.Id.StartsWith("pixel", StringComparison.OrdinalIgnoreCase))
                    ?? profiles.FirstOrDefault()
                    ?? new DeviceProfileInfo("pixel", "Pixel");
                created = "AvdDeck_Pixel_API_35";
                var create = await CreateAvdAsync(created, "system-images;android-35;google_apis;x86_64", device.Id);
                if (!create.Success) throw new InvalidOperationException(create.BestMessage);
            }

            progress?.Report(new("环境已就绪", 100));
            return new(true, created, created is null ? "Android 虚拟机环境已就绪。" : $"已创建 {created}，可以立即启动。");
        }
        catch (Exception ex) { return new(false, null, ex.Message); }
    }

    public async Task<List<AvdItem>> LoadAvdsAsync()
    {
        if (EmulatorPath is null) return [];
        var output = await RunCaptureAsync(EmulatorPath, "-list-avds");
        var running = await GetRunningAvdsAsync();
        var result = new List<AvdItem>();

        foreach (var name in output.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var avdFolder = ResolveAvdFolder(name);
            var config = ReadIni(Path.Combine(avdFolder, "config.ini"));
            var api = ExtractApi(config.GetValueOrDefault("image.sysdir.1"), config.GetValueOrDefault("target"));
            var width = config.GetValueOrDefault("hw.lcd.width");
            var height = config.GetValueOrDefault("hw.lcd.height");
            var ram = config.GetValueOrDefault("hw.ramSize");
            var data = HumanSize(config.GetValueOrDefault("disk.dataPartition.size"));
            var runInfo = running.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            var isRunning = !string.IsNullOrWhiteSpace(runInfo.Name);

            result.Add(new AvdItem
            {
                Name = name,
                FolderPath = avdFolder,
                DeviceName = FriendlyDevice(config.GetValueOrDefault("hw.device.name"), name),
                Manufacturer = config.GetValueOrDefault("hw.device.manufacturer") ?? "Android",
                ApiLevel = api,
                AndroidVersion = AndroidName(api),
                Abi = config.GetValueOrDefault("abi.type") ?? config.GetValueOrDefault("hw.cpu.arch") ?? "—",
                Resolution = width is not null && height is not null ? $"{width} × {height}" : "—",
                Ram = int.TryParse(ram, out var ramMb) ? $"{ramMb:N0} MB" : "自动",
                DataSize = data,
                Target = config.GetValueOrDefault("tag.display") ?? config.GetValueOrDefault("target") ?? "Android",
                IsRunning = isRunning,
                Serial = isRunning ? runInfo.Serial : null
            });
        }
        return result.OrderByDescending(x => x.IsRunning).ThenBy(x => x.Name).ToList();
    }

    public List<SystemImageInfo> GetInstalledSystemImages()
    {
        var root = SdkRoot is null ? null : Path.Combine(SdkRoot, "system-images");
        if (root is null || !Directory.Exists(root)) return [];
        var images = new List<SystemImageInfo>();
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (!File.Exists(Path.Combine(dir, "package.xml"))) continue;
            var relative = Path.GetRelativePath(SdkRoot!, dir).Replace(Path.DirectorySeparatorChar, ';');
            var parts = relative.Split(';');
            if (parts.Length < 4) continue;
            var api = parts[1].Replace("android-", "API ");
            var flavor = parts[2].Replace('_', ' ');
            images.Add(new(relative, $"Android {api} · {flavor} · {parts[3]}"));
        }
        return images.OrderByDescending(x => ApiNumber(x.PackageId)).ThenBy(x => x.Label).ToList();
    }

    public async Task<List<DeviceProfileInfo>> GetDeviceProfilesAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _deviceProfileCache is not null) return _deviceProfileCache;
        if (AvdManagerPath is null) return [];
        var output = await RunCaptureAsync(AvdManagerPath, "list device -c", timeoutMs: 30_000);
        if (!output.Success) return [];
        _deviceProfileCache = output.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !x.StartsWith("Loading", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new DeviceProfileInfo(id, FriendlyDevice(id, id)))
            .OrderByDescending(x => x.Id.StartsWith("pixel", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Label)
            .ToList();
        return _deviceProfileCache;
    }

    public Task<ProcessResult> CreateAvdAsync(string name, string packageId, string deviceId) =>
        RunCaptureAsync(AvdManagerPath ?? throw new InvalidOperationException("未找到 avdmanager。"),
            $"create avd -n {Quote(name)} -k {Quote(packageId)} -d {Quote(deviceId)} --force", "no\n", 120_000);

    public Task<ProcessResult> DeleteAvdAsync(string name) =>
        RunCaptureAsync(AvdManagerPath ?? throw new InvalidOperationException("未找到 avdmanager。"), $"delete avd -n {Quote(name)}", timeoutMs: 60_000);

    public void StartAvd(string name, bool coldBoot = false, bool wipeData = false, bool headless = false)
    {
        if (EmulatorPath is null) throw new InvalidOperationException("未找到 Android Emulator。请在设置中选择 SDK 目录。");
        var args = $"-avd {Quote(name)}" + (coldBoot ? " -no-snapshot-load" : "") + (wipeData ? " -wipe-data" : "") + (headless ? " -no-window -no-audio -no-boot-anim -gpu host" : "");
        Process.Start(new ProcessStartInfo(EmulatorPath, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(EmulatorPath)! });
    }

    public async Task StopAvdAsync(string serial)
    {
        if (AdbPath is null) throw new InvalidOperationException("未找到 adb。请在设置中选择 SDK 目录。");
        await RunCaptureAsync(AdbPath, $"-s {Quote(serial)} emu kill", timeoutMs: 20_000);
    }

    public async Task<List<SnapshotItem>> GetSnapshotsAsync(AvdItem avd)
    {
        return await Task.Run(() =>
        {
            var root = Path.Combine(avd.FolderPath, "snapshots");
            if (!Directory.Exists(root)) return new List<SnapshotItem>();
            var result = new List<SnapshotItem>();
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    var size = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(x => { try { return new FileInfo(x).Length; } catch { return 0L; } });
                    var name = info.Name;
                    result.Add(new(name, info.LastWriteTime, size, name.Equals("default_boot", StringComparison.OrdinalIgnoreCase)));
                }
                catch { }
            }
            return result.OrderByDescending(x => x.IsQuickBoot).ThenByDescending(x => x.Modified).ToList();
        });
    }

    public Task<ProcessResult> SaveSnapshotAsync(AvdItem avd, string name) => RunSnapshotCommandAsync(avd, "save", name, 180_000);
    public Task<ProcessResult> LoadSnapshotAsync(AvdItem avd, string name) => RunSnapshotCommandAsync(avd, "load", name, 180_000);
    public Task<ProcessResult> DeleteSnapshotAsync(AvdItem avd, string name) => RunSnapshotCommandAsync(avd, "delete", name, 120_000);

    private Task<ProcessResult> RunSnapshotCommandAsync(AvdItem avd, string action, string name, int timeoutMs)
    {
        if (AdbPath is null) throw new InvalidOperationException("未找到 adb。请先完成环境检测。");
        if (!avd.IsRunning || string.IsNullOrWhiteSpace(avd.Serial)) throw new InvalidOperationException("快照操作需要先启动该虚拟设备。");
        return RunCaptureAsync(AdbPath, $"-s {Quote(avd.Serial)} emu avd snapshot {action} {Quote(name)}", timeoutMs: timeoutMs);
    }

    public async Task<bool> WaitUntilRunningAsync(string name, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if ((await GetRunningAvdsAsync()).Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return true;
            await Task.Delay(800);
        }
        return false;
    }

    public void OpenFolder(string folder) => Process.Start(new ProcessStartInfo("explorer.exe", Quote(folder)) { UseShellExecute = true });

    public Dictionary<string, string> ReadAvdConfig(AvdItem avd) => ReadIni(Path.Combine(avd.FolderPath, "config.ini"));

    public void SaveAvdConfig(AvdItem avd, int ram, int cores, bool keyboard, string gpuMode)
    {
        var path = Path.Combine(avd.FolderPath, "config.ini");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        SetIni(lines, "hw.ramSize", ram.ToString());
        SetIni(lines, "hw.cpu.ncore", cores.ToString());
        SetIni(lines, "hw.keyboard", keyboard ? "yes" : "no");
        SetIni(lines, "hw.gpu.enabled", gpuMode == "off" ? "no" : "yes");
        SetIni(lines, "hw.gpu.mode", gpuMode);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private async Task<List<(string Name, string Serial)>> GetRunningAvdsAsync()
    {
        if (AdbPath is null) return [];
        var devices = await RunCaptureAsync(AdbPath, "devices");
        var serials = devices.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('\t')[0].Trim()).Where(x => x.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)).ToList();
        var result = new List<(string, string)>();
        foreach (var serial in serials)
        {
            var name = await RunCaptureAsync(AdbPath, $"-s {Quote(serial)} emu avd name", timeoutMs: 5_000);
            var clean = name.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x != "OK")?.Trim();
            if (!string.IsNullOrWhiteSpace(clean)) result.Add((clean, serial));
        }
        return result;
    }

    private string ResolveAvdFolder(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var iniPath = Path.Combine(home, ".android", "avd", name + ".ini");
        var ini = ReadIni(iniPath);
        return ini.GetValueOrDefault("path") ?? Path.Combine(home, ".android", "avd", name + ".avd");
    }

    private string? FindTool(string subdir, string file)
    {
        var path = SdkRoot is null ? null : Path.Combine(SdkRoot, subdir, file);
        return path is not null && File.Exists(path) ? path : null;
    }

    private string? FindCmdTool(string file)
    {
        if (SdkRoot is null) return null;
        var latest = Path.Combine(SdkRoot, "cmdline-tools", "latest", "bin", file);
        if (File.Exists(latest)) return latest;
        return Directory.Exists(Path.Combine(SdkRoot, "cmdline-tools"))
            ? Directory.EnumerateFiles(Path.Combine(SdkRoot, "cmdline-tools"), file, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static string? DiscoverSdk()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"), Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
        };
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Android Studio");
            candidates.Add(key?.GetValue("SdkPath") as string);
        }
        catch { }
        return candidates.Where(x => !string.IsNullOrWhiteSpace(x)).FirstOrDefault(x => File.Exists(Path.Combine(x!, "emulator", "emulator.exe")));
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            var at = line.IndexOf('=');
            if (at <= 0) continue;
            result[line[..at].Trim()] = line[(at + 1)..].Trim();
        }
        return result;
    }

    private static void SetIni(List<string> lines, string key, string value)
    {
        var index = lines.FindIndex(x => x.TrimStart().StartsWith(key + " =", StringComparison.OrdinalIgnoreCase) || x.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) lines[index] = $"{key} = {value}"; else lines.Add($"{key} = {value}");
    }

    private static string ExtractApi(params string?[] values)
    {
        foreach (var value in values)
        {
            var match = Regex.Match(value ?? "", @"android-(\d+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;
        }
        return "—";
    }

    private static int ApiNumber(string id) => int.TryParse(Regex.Match(id, @"android-(\d+)").Groups[1].Value, out var n) ? n : 0;
    private static string FriendlyDevice(string? id, string fallback) => string.IsNullOrWhiteSpace(id) ? fallback : string.Join(' ', id.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
    private static string AndroidName(string api) => api switch { "35" => "Android 15", "34" => "Android 14", "33" => "Android 13", "32" => "Android 12L", "31" => "Android 12", "30" => "Android 11", "29" => "Android 10", _ => api == "—" ? "Android" : $"Android · API {api}" };
    private static string HumanSize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        if (!long.TryParse(Regex.Match(raw, @"\d+").Value, out var bytes)) return raw;
        if (raw.EndsWith("MB", StringComparison.OrdinalIgnoreCase)) return $"{bytes:N0} MB";
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824d:0.#} GB";
        return $"{bytes / 1_048_576d:0} MB";
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static async Task InstallCommandLineToolsAsync(string sdkRoot, IProgress<InstallProgress>? progress)
    {
        const string url = "https://dl.google.com/android/repository/commandlinetools-win-15859902_latest.zip";
        const string expectedSha256 = "90ae805d20434428bffcb699c290860f19bb5f66a67e6b330067e3de801fb04a";
        var tempRoot = Path.Combine(Path.GetTempPath(), "AvdDeck", Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(tempRoot, "commandlinetools.zip");
        var extractPath = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true))
            {
                var buffer = new byte[1024 * 128];
                long readTotal = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    readTotal += read;
                    var percent = total > 0 ? 8 + (int)(readTotal * 24 / total) : 18;
                    progress?.Report(new($"正在下载 Android 命令行工具… {readTotal / 1_048_576:N0} MB", Math.Min(32, percent)));
                }
            }

            await using (var stream = File.OpenRead(zipPath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
                if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("命令行工具校验失败，下载文件可能不完整。");
            }

            progress?.Report(new("正在解压 Android 命令行工具…", 34));
            ZipFile.ExtractToDirectory(zipPath, extractPath);
            var extracted = Path.Combine(extractPath, "cmdline-tools");
            if (!Directory.Exists(extracted)) throw new InvalidOperationException("命令行工具压缩包结构无效。");
            var destination = Path.Combine(sdkRoot, "cmdline-tools", "latest");
            CopyDirectory(extracted, destination);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static async Task<ProcessResult> RunCaptureAsync(string file, string arguments, string? input = null, int timeoutMs = 30_000)
    {
        var isBatch = file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var actualFile = isBatch ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : file;
        var actualArgs = isBatch ? $"/d /s /c \"\"{file}\" {arguments}\"" : arguments;
        var psi = new ProcessStartInfo(actualFile, actualArgs) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        var javaHome = FindJavaHome();
        if (javaHome is not null) psi.Environment["JAVA_HOME"] = javaHome;
        using var process = new Process { StartInfo = psi };
        process.Start();
        if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } return new(-1, await stdout, "命令执行超时。"); }
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static string? FindJavaHome()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("JAVA_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "jbr"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "jre")
        };
        return candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(Path.Combine(x!, "bin", "java.exe")));
    }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public string BestMessage => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}
