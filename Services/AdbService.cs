using AndroidLogViewer.Models;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;

namespace AndroidLogViewer.Services;

public sealed class AdbService
{
    public const string DefaultAdbPath = @"D:\Android\android-sdk\platform-tools\adb.exe";
    private readonly string adbPath;
    private readonly string aaptPath;

    public AdbService(string adbPath = DefaultAdbPath)
    {
        this.adbPath = ResolveAdbPath(adbPath);
        aaptPath = FindAaptPath(this.adbPath);
    }

    public bool Exists => File.Exists(adbPath);

    public string AdbPath => adbPath;

    public string AaptPath => aaptPath;

    public async Task<IReadOnlyList<AndroidDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        AdbCommandResult result = await RunAsync("devices -l", cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.ErrorText);
        }

        return ParseDevices(result.OutputText);
    }

    public async Task<AdbCommandResult> InstallApkAsync(
        string serial, 
        string apkPath, 
        IProgress<string>? progress, 
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"Installing: {Path.GetFileName(apkPath)}");
        return await RunAsync($"-s {Quote(serial)} install -r {Quote(apkPath)}", cancellationToken);
    }

    public async Task<AdbCommandResult> UninstallPackageAsync(string serial, string packageName, CancellationToken cancellationToken = default)
    {
        return await RunAsync($"-s {Quote(serial)} uninstall {Quote(packageName)}", cancellationToken);
    }

    public async Task<AdbCommandResult> StartPackageAsync(string serial, string packageName, CancellationToken cancellationToken = default)
    {
        return await RunAsync($"-s {Quote(serial)} shell monkey -p {Quote(packageName)} -c android.intent.category.LAUNCHER 1", cancellationToken);
    }
    public async Task<IReadOnlyList<string>> GetUserPackagesAsync(string serial, CancellationToken cancellationToken = default)
    {
        AdbCommandResult result = await RunAsync($"-s {Quote(serial)} shell pm list packages -3", cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.CombinedText);
        }

        return result.OutputText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["package:".Length..].Trim())
            .Where(packageName => !string.IsNullOrWhiteSpace(packageName))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string?> GetApkPackageNameAsync(string apkPath, CancellationToken cancellationToken = default)
    {
        ApkInfo? info = await GetApkInfoAsync(apkPath, cancellationToken);
        return info?.PackageName;
    }

    public async Task<ApkInfo?> GetApkInfoAsync(string apkPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(aaptPath) || !File.Exists(aaptPath))
        {
            return null;
        }

        AdbCommandResult result = await RunToolAsync(aaptPath, $"dump badging {Quote(apkPath)}", cancellationToken);
        string text = result.CombinedText;
        Match packageMatch = Regex.Match(text, @"package:\s+name='(?<name>[^']+)'\s+versionCode='(?<code>[^']*)'\s+versionName='(?<version>[^']*)'", RegexOptions.IgnoreCase);
        if (!packageMatch.Success)
        {
            return null;
        }

        string label = MatchValue(text, @"application-label(?:-[^:]+)?:'(?<value>[^']*)'");
        if (string.IsNullOrWhiteSpace(label))
        {
            label = MatchValue(text, @"application:\s+label='(?<value>[^']*)'");
        }

        string[] permissions = Regex
            .Matches(text, @"uses-permission:\s+name='(?<name>[^']+)'", RegexOptions.IgnoreCase)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var fileInfo = new FileInfo(apkPath);
        return new ApkInfo(
            apkPath,
            Path.GetFileName(apkPath),
            fileInfo.Exists ? fileInfo.Length : 0,
            packageMatch.Groups["name"].Value,
            label,
            packageMatch.Groups["version"].Value,
            packageMatch.Groups["code"].Value,
            permissions);
    }

    public async Task<AdbCommandResult> ClearLogcatAsync(string serial, CancellationToken cancellationToken = default)
    {
        return await RunAsync($"-s {Quote(serial)} logcat -c", cancellationToken);
    }

    public ProcessStartInfo CreateLogcatStartInfo(string serial)
    {
        EnsureAdbExists();

        return new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = $"-s {Quote(serial)} logcat -v threadtime",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    /// <summary>
    /// adb命令
    /// </summary>
    /// <param name="arguments"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<AdbCommandResult> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        EnsureAdbExists();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = adbPath, //程序入口
                Arguments = arguments, //参数
                UseShellExecute = false, //是否使用shell
                RedirectStandardOutput = true,  //重定向输出
                RedirectStandardError = true, //重定向错误
                CreateNoWindow = true, //不创建黑框
                StandardOutputEncoding = Encoding.UTF8, // 编码
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true  //事件通知。
        };

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        
        return new AdbCommandResult(process.ExitCode, output, error);
    }

    /// <summary>
    /// 执行命令 aapt.exe，获取apk的包名
    /// </summary>
    /// <param name="fileName"></param>
    /// <param name="arguments"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async Task<AdbCommandResult> RunToolAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new AdbCommandResult(process.ExitCode, output, error);
    }

    private void EnsureAdbExists()
    {
        if (!Exists)
        {
            throw new FileNotFoundException("adb.exe not found.", adbPath);
        }
    }

    /// <summary>
    /// 查找adb.exe的路径，优先使用当前项目下的platform-tools目录，如果不存在就使用传入的fallbackPath，如果还不存在就去环境变量PATH里面找
    /// </summary>
    /// <param name="fallbackPath"></param>
    /// <returns></returns>
    private static string ResolveAdbPath(string fallbackPath)
    {
        //当前项目下是否存在adb
        string bundledAdbPath = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        if (File.Exists(bundledAdbPath))
        {
            return bundledAdbPath;
        }

        if (File.Exists(fallbackPath))
        {
            return fallbackPath;
        }
        //如果没用就使用环境变量的
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string pathAdb = Path.Combine(directory.Trim(), "adb.exe");
                if (File.Exists(pathAdb))
                {
                    return pathAdb;
                }
            }
        }

        return fallbackPath;
    }

    /// <summary>
    /// 解析 
    /// </summary>
    /// <param name="output"></param>
    /// <returns></returns>
    private static IReadOnlyList<AndroidDevice> ParseDevices(string output)
    {
        //解析设备
        var devices = new List<AndroidDevice>();
        //使用回车分割 ，跳过第一行标题
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            // 使用空格分割，去掉空白项
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            string serial = parts[0];
            string state = parts[1];
            string model = parts.FirstOrDefault(part => part.StartsWith("model:", StringComparison.OrdinalIgnoreCase))?.Replace("model:", "") ?? string.Empty;
            string displayName = string.IsNullOrWhiteSpace(model)
                ? $"{serial} ({state})"
                : $"{model} - {serial} ({state})";
            devices.Add(new AndroidDevice(serial, state, displayName));
        }

        return devices;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string MatchValue(string text, string pattern)
    {
        Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }

    /// <summary>
    /// 找出adb的目录下的aapt.exe，如果没有就去build-tools里面找最新的aapt.exe
    /// </summary>
    /// <param name="adbPath"></param>
    /// <returns></returns>
    private static string FindAaptPath(string adbPath)
    {
        string platformToolsPath = Path.GetDirectoryName(adbPath) ?? string.Empty;
        string platformToolsAapt = Path.Combine(platformToolsPath, "aapt.exe");
        if (File.Exists(platformToolsAapt))
        {
            return platformToolsAapt;
        }

        string? sdkPath = Directory.GetParent(platformToolsPath)?.FullName;
        string buildToolsPath = string.IsNullOrWhiteSpace(sdkPath) ? string.Empty : Path.Combine(sdkPath, "build-tools");
        if (!Directory.Exists(buildToolsPath))
        {
            return string.Empty;
        }

        return Directory
            .GetFiles(buildToolsPath, "aapt.exe", SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
    }
}

/// <summary>
/// 执行完 adb 命令后的结果
/// </summary>
/// <param name="ExitCode">退出代码</param>
/// <param name="OutputText">标准输出文本</param>
/// <param name="ErrorText">错误输出文本</param>
public sealed record AdbCommandResult(int ExitCode, string OutputText, string ErrorText)
{
    public bool Success => ExitCode == 0;

    public string CombinedText => string.Join(
        Environment.NewLine,
        new[] { OutputText.Trim(), ErrorText.Trim() }.Where(text => !string.IsNullOrWhiteSpace(text)));
}
