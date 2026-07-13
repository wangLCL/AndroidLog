using AndroidLogViewer.Models;
using System.Diagnostics;
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

    public async Task<AdbCommandResult> InstallApkAsync(string serial, string apkPath, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        progress?.Report($"Installing: {Path.GetFileName(apkPath)}");
        return await RunAsync($"-s {Quote(serial)} install -r {Quote(apkPath)}", cancellationToken);
    }

    public async Task<AdbCommandResult> UninstallPackageAsync(string serial, string packageName, CancellationToken cancellationToken = default)
    {
        return await RunAsync($"-s {Quote(serial)} uninstall {Quote(packageName)}", cancellationToken);
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
        if (string.IsNullOrWhiteSpace(aaptPath) || !File.Exists(aaptPath))
        {
            return null;
        }

        AdbCommandResult result = await RunToolAsync(aaptPath, $"dump badging {Quote(apkPath)}", cancellationToken);
        string text = result.CombinedText;
        Match match = Regex.Match(text, @"package:\s+name='(?<name>[^']+)'", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value : null;
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

    private async Task<AdbCommandResult> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        EnsureAdbExists();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = adbPath,
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

    private static string ResolveAdbPath(string fallbackPath)
    {
        string bundledAdbPath = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        if (File.Exists(bundledAdbPath))
        {
            return bundledAdbPath;
        }

        if (File.Exists(fallbackPath))
        {
            return fallbackPath;
        }

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

    private static IReadOnlyList<AndroidDevice> ParseDevices(string output)
    {
        var devices = new List<AndroidDevice>();
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
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

public sealed record AdbCommandResult(int ExitCode, string OutputText, string ErrorText)
{
    public bool Success => ExitCode == 0;

    public string CombinedText => string.Join(
        Environment.NewLine,
        new[] { OutputText.Trim(), ErrorText.Trim() }.Where(text => !string.IsNullOrWhiteSpace(text)));
}
