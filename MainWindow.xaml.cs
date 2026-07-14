using AndroidLogViewer.Models;
using AndroidLogViewer.Services;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AndroidLogViewer;

public partial class MainWindow : Window
{
    private const int MaxVisibleRows = 5000;
    private const int MaxStoredRows = 20000;
    private const int MaxFlushRowsPerTick = 300;
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidLogViewer");
    private static readonly string LastTagFilterPath = Path.Combine(SettingsDirectory, "last-tag-filter.txt");

    private readonly AdbService adbService = new();
    private readonly LogcatReader logcatReader;
    private readonly List<LogEntry> allLogs = [];
    private readonly List<LogEntry> crashLogs = [];
    private readonly ConcurrentQueue<LogEntry> pendingLogs = new();
    private readonly DispatcherTimer logFlushTimer = new();

    private bool autoFollowLogs = true;
    private int crashContextLinesRemaining;
    private string autoCrashLogPath = string.Empty;
    private int lastLocatedLogIndex = -1;
    private string lockedLocatedRawLine = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        logGrid.ItemsSource = VisibleLogs;

        logcatReader = new LogcatReader(adbService);
        logcatReader.EntryReceived += (_, entry) => pendingLogs.Enqueue(entry);
        logcatReader.ErrorReceived += (_, message) =>
        {
            Dispatcher.InvokeAsync(() => SetStatus(message));
        };

        logFlushTimer.Interval = TimeSpan.FromMilliseconds(120);
        logFlushTimer.Tick += (_, _) => FlushPendingLogs();

        statusLabel.Text = $"ADB: {adbService.AdbPath}";

        Loaded += async (_, _) => await RefreshDevicesAsync();
        Closing += (_, _) =>
        {
            SaveLastTagFilter();
            logFlushTimer.Stop();
            logcatReader.Dispose();
        };
    }

    public ObservableCollection<LogEntry> VisibleLogs { get; } = [];

    private void SaveLastTagFilter()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(LastTagFilterPath, tagTextBox.Text.Trim());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            SetBusy(true, "正在读取设备...");
            IReadOnlyList<AndroidDevice> devices = await adbService.GetDevicesAsync();
            deviceCombo.ItemsSource = devices;
            if (devices.Count > 0)
            {
                deviceCombo.SelectedIndex = 0;
            }
            else
            {
                packageComboBox.ItemsSource = null;
            }

            SetStatus($"找到 {devices.Count} 个设备");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "ADB 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadUserPackagesForSelectedDeviceAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        if (device is null) return;

        string currentText = packageComboBox.Text.Trim();
        try
        {
            IReadOnlyList<string> packages = await adbService.GetUserPackagesAsync(device.Serial);
            packageComboBox.ItemsSource = packages;
            packageComboBox.Text = currentText;
            SetStatus($"已加载 {packages.Count} 个用户应用包名");
        }
        catch (Exception ex)
        {
            SetStatus($"读取用户包名失败：{ex.Message}");
        }
    }

    private async Task StartLogcatAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        if (device is null)
        {
            MessageBox.Show(this, "请先选择设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        startLogButton.IsEnabled = false;
        stopLogButton.IsEnabled = false;
        if (clearBeforeStartCheckBox.IsChecked == true)
        {
            SetStatus("正在清空历史日志...");
            logcatReader.Stop();
            logFlushTimer.Stop();
            ClearVisibleLogs();
            AdbCommandResult clearResult = await adbService.ClearLogcatAsync(device.Serial);
            if (!clearResult.Success)
            {
                SetStatus(clearResult.CombinedText);
                startLogButton.IsEnabled = true;
                return;
            }
        }

        logcatReader.Start(device.Serial);
        logFlushTimer.Start();
        startLogButton.IsEnabled = false;
        stopLogButton.IsEnabled = true;
        SetStatus($"正在读取日志：{device.DisplayName}");
    }

    private void StopLogcat()
    {
        logcatReader.Stop();
        FlushPendingLogs();
        logFlushTimer.Stop();
        startLogButton.IsEnabled = true;
        stopLogButton.IsEnabled = false;
        SetStatus("日志已停止");
    }

    private async Task ClearDeviceLogAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        if (device is null) return;
        AdbCommandResult result = await adbService.ClearLogcatAsync(device.Serial);
        SetStatus(result.Success ? "设备日志已清空" : result.CombinedText);
    }

    private async Task DropApkAsync(DragEventArgs e)
    {
        if (!TryGetApkPath(e, out string apkPath)) return;
        AndroidDevice? device = GetSelectedDevice();
        if (device is null)
        {
            MessageBox.Show(this, "请先选择设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true, "正在读取 APK 包名...");
            string? packageName = await adbService.GetApkPackageNameAsync(apkPath);
            if (!string.IsNullOrWhiteSpace(packageName)) SetPackageName(packageName);

            SetStatus("正在安装 APK...");
            AdbCommandResult result = await adbService.InstallApkAsync(device.Serial, apkPath, new Progress<string>(SetStatus));
            SetStatus(result.Success ? $"安装完成：{Path.GetFileName(apkPath)}" : result.CombinedText);
            MessageBox.Show(this, result.CombinedText, result.Success ? "安装完成" : "安装失败",
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UninstallAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        string packageName = packageComboBox.Text.Trim();
        if (device is null || string.IsNullOrWhiteSpace(packageName))
        {
            MessageBox.Show(this, "请选择设备并输入包名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AdbCommandResult result = await adbService.UninstallPackageAsync(device.Serial, packageName);
        SetStatus(result.Success ? $"卸载完成：{packageName}" : result.CombinedText);
        MessageBox.Show(this, result.CombinedText, result.Success ? "卸载完成" : "卸载失败",
            MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        await LoadUserPackagesForSelectedDeviceAsync();
    }

    private void FlushPendingLogs()
    {
        if (pendingLogs.IsEmpty) return;
        bool shouldFollow = autoFollowLogs;

        for (int index = 0; index < MaxFlushRowsPerTick && pendingLogs.TryDequeue(out LogEntry? entry); index++)
        {
            CaptureCrashLog(entry);
            StoreLog(entry);
            if (ShouldShowInLogTab(entry))
            {
                VisibleLogs.Add(entry);
            }
        }

        while (VisibleLogs.Count > MaxVisibleRows)
        {
            VisibleLogs.RemoveAt(0);
        }

        if (!string.IsNullOrEmpty(lockedLocatedRawLine) && RestoreLockedLocatedRow())
        {
            autoFollowLogs = false;
        }
        else if (shouldFollow)
        {
            ScrollLogToBottom();
        }

        if (!pendingLogs.IsEmpty)
        {
            SetStatus($"日志读取中，待刷新 {pendingLogs.Count} 行");
        }
    }

    private void CaptureCrashLog(LogEntry entry)
    {
        if (captureCrashCheckBox.IsChecked != true) return;
        bool isCrashStart = IsCrashStart(entry);
        bool shouldCapture = isCrashStart || crashContextLinesRemaining > 0 || IsCrashTag(entry);
        if (!shouldCapture) return;

        if (isCrashStart)
        {
            crashContextLinesRemaining = 120;
            autoCrashLogPath = CreateAutoCrashLogPath();
            AppendCrashSeparator();
            AppendAutoCrashSeparator();
        }
        else if (crashContextLinesRemaining > 0)
        {
            crashContextLinesRemaining--;
        }
        else if (string.IsNullOrWhiteSpace(autoCrashLogPath))
        {
            autoCrashLogPath = CreateAutoCrashLogPath();
            AppendAutoCrashSeparator();
        }

        crashLogs.Add(entry);
        crashTextBox.AppendText(entry.RawLine + Environment.NewLine);
        crashTextBox.ScrollToEnd();
        AppendAutoCrashLine(entry.RawLine);
    }

    private void StoreLog(LogEntry entry)
    {
        allLogs.Add(entry);
        if (allLogs.Count > MaxStoredRows) allLogs.RemoveAt(0);
    }

    private bool ShouldStoreLog(LogEntry entry)
    {
        string selectedLevel = GetComboText(storeLevelCombo);
        if (selectedLevel == "None") return false;
        return selectedLevel == "All" || entry.PassesMinimumLevel(selectedLevel);
    }

    private void AppendCrashSeparator()
    {
        if (crashTextBox.Text.Length > 0) crashTextBox.AppendText(Environment.NewLine);
        crashTextBox.AppendText("========== Crash ==========" + Environment.NewLine);
    }

    private void AppendAutoCrashSeparator()
    {
        if (!string.IsNullOrWhiteSpace(autoCrashLogPath))
        {
            File.AppendAllText(autoCrashLogPath, Environment.NewLine + "========== Crash ==========" + Environment.NewLine);
        }
    }

    private void AppendAutoCrashLine(string line)
    {
        if (!string.IsNullOrWhiteSpace(autoCrashLogPath))
        {
            File.AppendAllText(autoCrashLogPath, line + Environment.NewLine);
        }
    }

    private string CreateAutoCrashLogPath()
    {
        string packageName = packageComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return string.Empty;
        }

        string directory = Path.Combine(AppContext.BaseDirectory, "crash-logs");
        Directory.CreateDirectory(directory);

        string safePackageName = SanitizeFileName(packageName);
        string fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{safePackageName}.log";
        string path = Path.Combine(directory, fileName);
        int suffix = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safePackageName}_{suffix}.log");
            suffix++;
        }

        return path;
    }

    private static string SanitizeFileName(string value)
    {
        string safeName = string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(safeName) ? "unknown-package" : safeName;
    }

    private static bool IsCrashStart(LogEntry entry)
    {
        string line = entry.RawLine;
        return line.Contains("FATAL EXCEPTION", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Fatal signal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("beginning of crash", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Process:", StringComparison.OrdinalIgnoreCase) && line.Contains("PID:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCrashTag(LogEntry entry)
    {
        return entry.Tag.Contains("AndroidRuntime", StringComparison.OrdinalIgnoreCase)
            || entry.Tag.Contains("DEBUG", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("java.lang.", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("Caused by:", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("backtrace", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilters()
    {
        FlushPendingLogs();
        autoFollowLogs = true;
        lastLocatedLogIndex = -1;
        lockedLocatedRawLine = string.Empty;
        VisibleLogs.Clear();
        foreach (LogEntry entry in allLogs.Where(ShouldShowInLogTab).TakeLast(MaxVisibleRows))
        {
            VisibleLogs.Add(entry);
        }
        ScrollLogToBottom();
    }

    private void ClearVisibleLogs()
    {
        autoFollowLogs = true;
        lastLocatedLogIndex = -1;
        lockedLocatedRawLine = string.Empty;
        pendingLogs.Clear();
        allLogs.Clear();
        VisibleLogs.Clear();
        SetStatus("显示已清空");
    }

    private void SaveAllLogs()
    {
        FlushPendingLogs();
        List<string> lines = allLogs
            .Where(ShouldStoreLog)
            .Select(entry => entry.RawLine)
            .Distinct()
            .ToList();
        if (lines.Count == 0)
        {
            MessageBox.Show(this, "当前没有可保存的日志。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog { Title = "保存日志", Filter = "Log files|*.log|Text files|*.txt|All files|*.*", FileName = $"logcat-{DateTime.Now:yyyyMMdd-HHmmss}.log" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllLines(dialog.FileName, lines);
        SetStatus($"日志已保存：{dialog.FileName}");
    }

    private void SaveCrashLogs()
    {
        if (crashLogs.Count == 0)
        {
            MessageBox.Show(this, "当前没有捕获到崩溃信息。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog { Title = "保存崩溃信息", Filter = "Log files|*.log|Text files|*.txt|All files|*.*", FileName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllLines(dialog.FileName, crashLogs.Select(entry => entry.RawLine));
        SetStatus($"崩溃信息已保存：{dialog.FileName}");
    }

    private void ClearCrashLogs()
    {
        crashLogs.Clear();
        crashTextBox.Clear();
        crashContextLinesRemaining = 0;
        autoCrashLogPath = string.Empty;
        SetStatus("崩溃信息已清空");
    }

    private void AnalyzeLogFile()
    {
        var dialog = new OpenFileDialog { Title = "选择日志文件", Filter = "Log files|*.log;*.txt|All files|*.*", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;

        autoFollowLogs = true;
        lastLocatedLogIndex = -1;
        lockedLocatedRawLine = string.Empty;
        pendingLogs.Clear();
        allLogs.Clear();
        VisibleLogs.Clear();
        crashLogs.Clear();
        crashTextBox.Clear();
        crashContextLinesRemaining = 0;
        autoCrashLogPath = string.Empty;

        int total = 0;
        foreach (string line in File.ReadLines(dialog.FileName))
        {
            total++;
            var entry = new LogEntry(line);
            CaptureCrashLog(entry);
            StoreLog(entry);
            if (ShouldShowInLogTab(entry))
            {
                VisibleLogs.Add(entry);
                while (VisibleLogs.Count > MaxVisibleRows) VisibleLogs.RemoveAt(0);
            }
        }

        ScrollLogToBottom();
        SetStatus($"文件分析完成：{total} 行，崩溃相关 {crashLogs.Count} 行");
    }

    private void LocateNextLog()
    {
        FlushPendingLogs();
        string keyword = locateTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SetStatus("请输入定位关键字");
            return;
        }
        if (VisibleLogs.Count == 0)
        {
            SetStatus("当前没有可定位的日志");
            return;
        }

        int startIndex = Math.Clamp(lastLocatedLogIndex + 1, 0, VisibleLogs.Count);
        int foundIndex = FindVisibleLogIndex(keyword, startIndex);
        if (foundIndex < 0 && startIndex > 0) foundIndex = FindVisibleLogIndex(keyword, 0);
        if (foundIndex < 0)
        {
            SetStatus($"未找到：{keyword}");
            return;
        }

        SelectLogRow(foundIndex);
        lastLocatedLogIndex = foundIndex;
        lockedLocatedRawLine = VisibleLogs[foundIndex].RawLine;
        autoFollowLogs = false;
        SetStatus($"定位到第 {foundIndex + 1} 行：{keyword}");
    }

    private int FindVisibleLogIndex(string keyword, int startIndex)
    {
        for (int index = startIndex; index < VisibleLogs.Count; index++)
        {
            if (VisibleLogs[index].RawLine.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private bool RestoreLockedLocatedRow()
    {
        int index = -1;
        for (int i = 0; i < VisibleLogs.Count; i++)
        {
            if (string.Equals(VisibleLogs[i].RawLine, lockedLocatedRawLine, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            lockedLocatedRawLine = string.Empty;
            return false;
        }
        SelectLogRow(index);
        lastLocatedLogIndex = index;
        return true;
    }

    private void SelectLogRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= VisibleLogs.Count) return;
        LogEntry entry = VisibleLogs[rowIndex];
        logGrid.SelectedItem = entry;
        logGrid.ScrollIntoView(entry);
    }

    private void ScrollLogToBottom()
    {
        if (VisibleLogs.Count == 0) return;
        LogEntry entry = VisibleLogs[^1];
        logGrid.ScrollIntoView(entry);
        autoFollowLogs = true;
    }

    private AndroidDevice? GetSelectedDevice()
    {
        return deviceCombo.SelectedItem as AndroidDevice;
    }

    private bool ShouldShowInLogTab(LogEntry entry)
    {
        return entry.Matches(searchTextBox.Text, tagTextBox.Text, GetComboText(levelCombo));
    }

    private void SetPackageName(string packageName)
    {
        if (packageComboBox.ItemsSource is IEnumerable<string> packages && !packages.Contains(packageName, StringComparer.OrdinalIgnoreCase))
        {
            packageComboBox.ItemsSource = new[] { packageName }.Concat(packages).ToArray();
        }
        packageComboBox.Text = packageName;
    }

    private static bool TryGetApkPath(DragEventArgs e, out string apkPath)
    {
        apkPath = string.Empty;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        apkPath = files.FirstOrDefault(file => string.Equals(Path.GetExtension(file), ".apk", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(apkPath);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        refreshDevicesButton.IsEnabled = !busy;
        uninstallButton.IsEnabled = !busy;
        dropPanel.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(message)) SetStatus(message);
    }

    private void SetStatus(string message)
    {
        statusLabel.Text = message;
    }

    private static string GetComboText(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : comboBox.Text;
    }

    private void LogGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not LogEntry entry) return;
        e.Row.Foreground = entry.Level switch
        {
            "E" or "F" => new SolidColorBrush(Color.FromRgb(190, 18, 60)),
            "W" => new SolidColorBrush(Color.FromRgb(180, 83, 9)),
            "I" => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            "D" => new SolidColorBrush(Color.FromRgb(55, 65, 81)),
            _ => new SolidColorBrush(Color.FromRgb(17, 24, 39))
        };
    }

    private void LogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (logGrid.SelectedItem is not null && string.IsNullOrEmpty(lockedLocatedRawLine))
        {
            autoFollowLogs = false;
        }
    }

    private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadUserPackagesForSelectedDeviceAsync();
    }

    private async void StartLogButton_Click(object sender, RoutedEventArgs e)
    {
        await StartLogcatAsync();
    }

    private void StopLogButton_Click(object sender, RoutedEventArgs e)
    {
        StopLogcat();
    }

    private async void ClearDeviceLogButton_Click(object sender, RoutedEventArgs e)
    {
        await ClearDeviceLogAsync();
    }

    private void ClearViewButton_Click(object sender, RoutedEventArgs e)
    {
        ClearVisibleLogs();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Android Log Viewer\n给测试用的工具\n\ngithub: wangLCL/AndroidLog\n\n是否打开项目地址？",
            "About",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo("https://github.com/wangLCL/AndroidLog") { UseShellExecute = true });
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        await UninstallAsync();
    }

    private void FilterTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void FilterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilters();
    }

    private void LocateTextChanged(object sender, TextChangedEventArgs e)
    {
        lastLocatedLogIndex = -1;
        lockedLocatedRawLine = string.Empty;
    }

    private void LocateTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LocateNextLog();
            e.Handled = true;
        }
    }

    private void LocateNextButton_Click(object sender, RoutedEventArgs e)
    {
        LocateNextLog();
    }

    private void SaveCrashButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCrashLogs();
    }

    private void ClearCrashButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCrashLogs();
    }

    private void SaveAllLogsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveAllLogs();
    }

    private void AnalyzeFileButton_Click(object sender, RoutedEventArgs e)
    {
        AnalyzeLogFile();
    }

    private void DropPanel_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = TryGetApkPath(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropPanel_Drop(object sender, DragEventArgs e)
    {
        await DropApkAsync(e);
    }
}
