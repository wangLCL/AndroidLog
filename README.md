# AndroidLogViewer

AndroidLogViewer 是一个 WPF 版 Android 日志查看工具，用于连接 Android 设备、读取 logcat、安装/卸载 APK、捕获崩溃日志和分析本地日志文件。

项目已内置 ADB 工具：

```text
platform-tools\adb.exe
platform-tools\aapt.exe
```

## 使用说明

完整说明书见：

[docs/使用说明.md](docs/使用说明.md)

## 主要功能

- 选择已连接的 Android 设备
- 实时读取 `adb logcat -v threadtime`
- 按文本、Tag、日志级别筛选
- 定位关键字并跳转到下一条匹配日志
- 拖入 APK 安装，并显示 APK 名称、版本和 versionCode
- 安装签名不一致时支持强制安装
- 安装完成后自动打开应用
- 输入包名卸载应用
- 自动捕获崩溃信息并保存
- 分析本地 `.log` / `.txt` 日志文件
- 中文 / English 界面切换

## 运行

```powershell
dotnet run --project D:\github\AndroidLogViewer\AndroidLogViewer.csproj
```

## 打包

```cmd
dotnet publish AndroidLogViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o D:\github\AndroidLogViewer\publish\win-x64
```

发布后把整个目录发给使用者：

```text
D:\github\AndroidLogViewer\publish\win-x64
```

对方直接运行：

```text
AndroidLogViewer.exe
```
