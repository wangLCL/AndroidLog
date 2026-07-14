# AndroidLogViewer

C# WPF Android 日志查看工具，默认使用：

```text
D:\Android\android-sdk\platform-tools\adb.exe
```

## 功能

- 显示已连接设备并选择设备
- 拖入 APK 安装
- 输入包名卸载
- 实时显示 `adb logcat -v threadtime`
- 按文本、Tag、最低日志级别筛选
- 清空设备日志、清空当前显示、保存日志、分析日志文件
- 自动捕获并保存崩溃相关日志

## 运行

```powershell
dotnet run --project D:\github\AndroidLogViewer\AndroidLogViewer.csproj
```

## 打包

```powershell
dotnet publish D:\github\AndroidLogViewer\AndroidLogViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o D:\github\AndroidLogViewer\publish\win-x64
```

打包后运行：

```powershell
D:\github\AndroidLogViewer\publish\win-x64\AndroidLogViewer.exe
```
