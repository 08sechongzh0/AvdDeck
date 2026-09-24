# AvdDeck

面向 Windows 10/11 的轻量 Android Virtual Device 图形管理器。

## 功能

- 自动发现 Android SDK 和本机 AVD
- 实时识别运行中的 Emulator
- 启动、停止、冷启动、擦除数据
- 创建和删除虚拟设备
- 编辑 RAM、CPU、GPU 与物理键盘配置
- 搜索设备、查看 API / ABI / 分辨率等详情
- 首次启动自动检查运行环境，通过后不再重复检查
- 手动环境复检与一键下载 Emulator、ADB、Android 15 镜像
- 没有 AVD 时自动创建默认 Pixel 设备并启动
- 快照管理：查看、创建、加载和删除一般快照与 Quick Boot 快照

## 开发运行

```powershell
dotnet run --project .\AvdDeck.csproj
```

## 发布

```powershell
dotnet publish .\AvdDeck.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 端到端烟雾测试

```powershell
dotnet run --project .\tests\AvdDeck.SmokeTests\AvdDeck.SmokeTests.csproj -c Release
```

测试会创建一个以 `AvdDeck_QA_` 开头的一次性 AVD，验证配置、冷启动、停止、擦除数据启动以及快照创建/加载/删除，并在结束时自动清理测试设备。运行前请关闭其他 Emulator 实例。

如果 Android SDK 未安装在默认位置，可在左下角点击“设置”选择 SDK 根目录。
