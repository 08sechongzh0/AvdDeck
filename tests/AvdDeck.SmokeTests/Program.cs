using AvdDeck.Models;
using AvdDeck.Services;
using System.Diagnostics;

var sdk = new AndroidSdkService();
var checks = new List<(string Name, bool Passed, string Detail)>();
var testName = "AvdDeck_QA_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
AvdItem? testAvd = null;

void Check(string name, bool passed, string detail = "")
{
    checks.Add((name, passed, detail));
    Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}{(string.IsNullOrWhiteSpace(detail) ? "" : " — " + detail)}");
    if (!passed) throw new InvalidOperationException(name + ": " + detail);
}

bool CommandOk(ProcessResult result) => result.Success && !result.StdOut.Contains("KO", StringComparison.OrdinalIgnoreCase) && !result.StdOut.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

async Task<string?> WaitForSerialAsync(string avdName, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        var found = (await sdk.LoadAvdsAsync()).FirstOrDefault(x => x.Name == avdName && x.IsRunning && !string.IsNullOrWhiteSpace(x.Serial));
        if (found?.Serial is not null) return found.Serial;
        await Task.Delay(1500);
    }
    return null;
}

async Task<bool> WaitForBootAsync(string serial, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        try
        {
            var psi = new ProcessStartInfo(sdk.AdbPath!, $"-s \"{serial}\" shell getprop sys.boot_completed") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (output.Trim() == "1") return true;
        }
        catch { }
        await Task.Delay(2000);
    }
    return false;
}

async Task WaitForStoppedAsync(string avdName, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        if (!(await sdk.LoadAvdsAsync()).Any(x => x.Name == avdName && x.IsRunning)) return;
        await Task.Delay(1000);
    }
}

try
{
    var report = await sdk.CheckEnvironmentAsync();
    Check("SDK 环境检测", report.RuntimeReady, $"SDK={sdk.SdkRoot}");

    var originalAvds = await sdk.LoadAvdsAsync();
    Check("AVD 列表与配置解析", originalAvds.Count > 0, $"发现 {originalAvds.Count} 台设备");
    var originalSnapshots = await sdk.GetSnapshotsAsync(originalAvds[0]);
    Check("停止状态快照目录读取", originalSnapshots.Count >= 0, $"发现 {originalSnapshots.Count} 个快照");

    var images = sdk.GetInstalledSystemImages();
    var profiles = await sdk.GetDeviceProfilesAsync(true);
    Check("系统镜像枚举", images.Count > 0, $"{images.Count} 个镜像");
    Check("硬件模板枚举", profiles.Count > 0, $"{profiles.Count} 个模板");
    var image = images.First();
    var profile = profiles.FirstOrDefault(x => x.Id.Equals("pixel_4", StringComparison.OrdinalIgnoreCase)) ?? profiles.First();

    var create = await sdk.CreateAvdAsync(testName, image.PackageId, profile.Id);
    Check("创建临时 AVD", create.Success, create.BestMessage);
    testAvd = (await sdk.LoadAvdsAsync()).SingleOrDefault(x => x.Name == testName);
    Check("新 AVD 自动刷新", testAvd is not null, testName);

    sdk.SaveAvdConfig(testAvd!, 1536, 2, true, "auto");
    var config = sdk.ReadAvdConfig(testAvd!);
    Check("RAM 配置保存", config.GetValueOrDefault("hw.ramSize") == "1536");
    Check("CPU 配置保存", config.GetValueOrDefault("hw.cpu.ncore") == "2");
    Check("键盘配置保存", config.GetValueOrDefault("hw.keyboard") == "yes");
    Check("GPU 配置保存", config.GetValueOrDefault("hw.gpu.mode") == "auto");

    sdk.StartAvd(testName, coldBoot: true, headless: true);
    var serial = await WaitForSerialAsync(testName, TimeSpan.FromMinutes(2));
    Check("冷启动与运行状态识别", serial is not null, serial ?? "未发现序列号");
    Check("系统启动完成", await WaitForBootAsync(serial!, TimeSpan.FromMinutes(4)), serial!);
    testAvd = (await sdk.LoadAvdsAsync()).Single(x => x.Name == testName);

    var snapshotName = "qa_snapshot";
    var save = await sdk.SaveSnapshotAsync(testAvd, snapshotName);
    Check("创建快照", CommandOk(save), save.BestMessage);
    var snapshots = await sdk.GetSnapshotsAsync(testAvd);
    Check("快照列表刷新", snapshots.Any(x => x.Name == snapshotName), $"{snapshots.Count} 个快照");

    var load = await sdk.LoadSnapshotAsync(testAvd, snapshotName);
    Check("加载快照", CommandOk(load), load.BestMessage);
    await Task.Delay(2500);
    testAvd = (await sdk.LoadAvdsAsync()).Single(x => x.Name == testName);

    var deleteSnapshot = await sdk.DeleteSnapshotAsync(testAvd, snapshotName);
    Check("删除快照", CommandOk(deleteSnapshot), deleteSnapshot.BestMessage);
    snapshots = await sdk.GetSnapshotsAsync(testAvd);
    Check("快照删除后刷新", snapshots.All(x => x.Name != snapshotName));

    await sdk.StopAvdAsync(testAvd.Serial!);
    await WaitForStoppedAsync(testName, TimeSpan.FromSeconds(30));
    Check("停止 AVD", !(await sdk.LoadAvdsAsync()).Any(x => x.Name == testName && x.IsRunning));

    sdk.StartAvd(testName, wipeData: true, headless: true);
    serial = await WaitForSerialAsync(testName, TimeSpan.FromMinutes(2));
    Check("擦除数据后启动", serial is not null, serial ?? "未发现序列号");
    testAvd = (await sdk.LoadAvdsAsync()).Single(x => x.Name == testName);
    await sdk.StopAvdAsync(testAvd.Serial!);
    await WaitForStoppedAsync(testName, TimeSpan.FromSeconds(30));
}
catch (Exception ex)
{
    Console.Error.WriteLine("SMOKE TEST FAILED: " + ex);
    Environment.ExitCode = 1;
}
finally
{
    try
    {
        var live = (await sdk.LoadAvdsAsync()).FirstOrDefault(x => x.Name == testName && x.IsRunning);
        if (live?.Serial is not null) { await sdk.StopAvdAsync(live.Serial); await WaitForStoppedAsync(testName, TimeSpan.FromSeconds(30)); }
        if ((await sdk.LoadAvdsAsync()).Any(x => x.Name == testName))
        {
            var deleted = await sdk.DeleteAvdAsync(testName);
            Console.WriteLine($"[{(deleted.Success ? "PASS" : "FAIL")}] 清理临时 AVD — {testName}");
            if (!deleted.Success) Environment.ExitCode = 2;
        }
    }
    catch (Exception cleanup) { Console.Error.WriteLine("CLEANUP FAILED: " + cleanup.Message); Environment.ExitCode = 2; }
}

Console.WriteLine($"Completed {checks.Count} checks. ExitCode={Environment.ExitCode}");
