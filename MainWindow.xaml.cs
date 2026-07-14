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

    private readonly AdbService adbService = new();
    private readonly LogcatReader logcatReader;
    private readonly ConcurrentQueue<LogEntry> pendingLogs = new();
    private readonly DispatcherTimer flushTimer = new();
    private readonly ObservableCollection<LogEntry> visibleLogs = [];
    private readonly List<LogEntry> allLogs = [];
    private readonly List<LogEntry> crashLogs = [];

    private bool autoFollowLogs = true;
    private int crashContextLinesRemaining;
    private string autoCrashLogPath = string.Empty;
    private int lastLocatedLogIndex = -1;

    public MainWindow()
    {
        InitializeComponent();

        LogDataGrid.ItemsSource = visibleLogs;
        StatusTextBlock.Text = $"ADB: {adbService.AdbPath}";

        logcatReader = new LogcatReader(adbService);
        logcatReader.EntryReceived += (_, entry) => pendingLogs.Enqueue(entry);
        logcatReader.ErrorReceived += (_, message) => Dispatcher.InvokeAsync(() => SetStatus(message));

        flushTimer.Interval = TimeSpan.FromMilliseconds(120);
        flushTimer.Tick += (_, _) => FlushPendingLogs();

        Loaded += async (_, _) => await RefreshDevicesAsync();
        Closing += (_, _) =>
        {
            flushTimer.Stop();
            logcatReader.Dispose();
        };
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            SetBusy(true, "正在读取设备...");
            IReadOnlyList<AndroidDevice> devices = await adbService.GetDevicesAsync();
            DeviceComboBox.ItemsSource = devices;
            DeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
            if (devices.Count == 0) PackageComboBox.ItemsSource = null;
            SetStatus($"已加载 {devices.Count} 个设备");
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

    private async Task LoadPackagesAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        if (device is null) return;

        string currentPackage = PackageComboBox.Text.Trim();
        try
        {
            IReadOnlyList<string> packages = await adbService.GetUserPackagesAsync(device.Serial);
            PackageComboBox.ItemsSource = packages;
            PackageComboBox.Text = currentPackage;
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

        StartLogButton.IsEnabled = false;
        StopLogButton.IsEnabled = false;

        if (ClearBeforeStartCheckBox.IsChecked == true)
        {
            SetStatus("正在清空历史日志...");
            logcatReader.Stop();
            flushTimer.Stop();
            ClearVisibleLogs();

            AdbCommandResult clearResult = await adbService.ClearLogcatAsync(device.Serial);
            if (!clearResult.Success)
            {
                SetStatus(clearResult.CombinedText);
                StartLogButton.IsEnabled = true;
                return;
            }
        }

        autoFollowLogs = true;
        logcatReader.Start(device.Serial);
        flushTimer.Start();
        StartLogButton.IsEnabled = false;
        StopLogButton.IsEnabled = true;
        SetStatus($"正在读取日志：{device.DisplayName}");
    }

    private void StopLogcat()
    {
        logcatReader.Stop();
        FlushPendingLogs();
        flushTimer.Stop();
        StartLogButton.IsEnabled = true;
        StopLogButton.IsEnabled = false;
        SetStatus("日志已停止");
    }

    private async Task ClearDeviceLogAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        if (device is null) return;

        AdbCommandResult result = await adbService.ClearLogcatAsync(device.Serial);
        SetStatus(result.Success ? "设备日志已清空" : result.CombinedText);
    }

    private async Task InstallDroppedApkAsync(DragEventArgs e)
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
        string packageName = PackageComboBox.Text.Trim();
        if (device is null || string.IsNullOrWhiteSpace(packageName))
        {
            MessageBox.Show(this, "请选择设备并输入包名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AdbCommandResult result = await adbService.UninstallPackageAsync(device.Serial, packageName);
        SetStatus(result.Success ? $"卸载完成：{packageName}" : result.CombinedText);
        MessageBox.Show(this, result.CombinedText, result.Success ? "卸载完成" : "卸载失败",
            MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        await LoadPackagesAsync();
    }

    private void FlushPendingLogs()
    {
        if (pendingLogs.IsEmpty) return;

        bool shouldFollow = autoFollowLogs || IsLogGridAtBottom();
        int added = 0;
        for (int index = 0; index < MaxFlushRowsPerTick && pendingLogs.TryDequeue(out LogEntry? entry); index++)
        {
            StoreLog(entry);
            CaptureCrashLog(entry);
            if (ShouldShowLog(entry))
            {
                visibleLogs.Add(entry);
                added++;
            }
        }

        while (visibleLogs.Count > MaxVisibleRows)
        {
            visibleLogs.RemoveAt(0);
        }

        if (shouldFollow && added > 0)
        {
            ScrollLogToBottom();
        }

        if (!pendingLogs.IsEmpty)
        {
            SetStatus($"日志读取中，待刷新 {pendingLogs.Count} 行");
        }
    }

    private void StoreLog(LogEntry entry)
    {
        allLogs.Add(entry);
        if (allLogs.Count > MaxStoredRows) allLogs.RemoveAt(0);
    }

    private bool ShouldShowLog(LogEntry entry)
    {
        return entry.Matches(TextFilterTextBox.Text, TagFilterTextBox.Text, GetComboText(LevelComboBox));
    }

    private void ApplyFilters()
    {
        FlushPendingLogs();
        lastLocatedLogIndex = -1;
        visibleLogs.Clear();
        foreach (LogEntry entry in allLogs.Where(ShouldShowLog).TakeLast(MaxVisibleRows))
        {
            visibleLogs.Add(entry);
        }
        if (autoFollowLogs) ScrollLogToBottom();
    }

    private void CaptureCrashLog(LogEntry entry)
    {
        if (CaptureCrashCheckBox.IsChecked != true) return;

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
        CrashTextBox.AppendText(entry.RawLine + Environment.NewLine);
        CrashTextBox.ScrollToEnd();
        AppendAutoCrashLine(entry.RawLine);
    }

    private void SaveLogs()
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

        var dialog = new SaveFileDialog
        {
            Title = "保存日志",
            Filter = "Log files|*.log|Text files|*.txt|All files|*.*",
            FileName = $"logcat-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };
        if (dialog.ShowDialog(this) != true) return;

        File.WriteAllLines(dialog.FileName, lines);
        SetStatus($"日志已保存：{dialog.FileName}");
    }

    private bool ShouldStoreLog(LogEntry entry)
    {
        string selectedLevel = GetComboText(StoreLevelComboBox);
        if (selectedLevel == "None") return false;
        return selectedLevel == "All" || entry.PassesMinimumLevel(selectedLevel);
    }

    private void SaveCrashLogs()
    {
        if (crashLogs.Count == 0)
        {
            MessageBox.Show(this, "当前没有捕获到崩溃信息。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存崩溃信息",
            Filter = "Log files|*.log|Text files|*.txt|All files|*.*",
            FileName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };
        if (dialog.ShowDialog(this) != true) return;

        File.WriteAllLines(dialog.FileName, crashLogs.Select(entry => entry.RawLine));
        SetStatus($"崩溃信息已保存：{dialog.FileName}");
    }

    private void ClearCrashLogs()
    {
        crashLogs.Clear();
        CrashTextBox.Clear();
        crashContextLinesRemaining = 0;
        autoCrashLogPath = string.Empty;
        SetStatus("崩溃信息已清空");
    }

    private void AnalyzeLogFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择日志文件",
            Filter = "Log files|*.log;*.txt|All files|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        ClearVisibleLogs();
        ClearCrashLogs();

        int total = 0;
        foreach (string line in File.ReadLines(dialog.FileName))
        {
            total++;
            var entry = new LogEntry(line);
            StoreLog(entry);
            CaptureCrashLog(entry);
            if (ShouldShowLog(entry))
            {
                visibleLogs.Add(entry);
                while (visibleLogs.Count > MaxVisibleRows) visibleLogs.RemoveAt(0);
            }
        }

        autoFollowLogs = true;
        ScrollLogToBottom();
        SetStatus($"文件分析完成：{total} 行，崩溃相关 {crashLogs.Count} 行");
    }

    private void LocateNextLog()
    {
        FlushPendingLogs();
        string keyword = LocateTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SetStatus("请输入定位关键字");
            return;
        }

        int startIndex = Math.Clamp(lastLocatedLogIndex + 1, 0, visibleLogs.Count);
        int foundIndex = FindVisibleLogIndex(keyword, startIndex);
        if (foundIndex < 0 && startIndex > 0) foundIndex = FindVisibleLogIndex(keyword, 0);
        if (foundIndex < 0)
        {
            SetStatus($"未找到：{keyword}");
            return;
        }

        LogEntry entry = visibleLogs[foundIndex];
        LogDataGrid.SelectedItem = entry;
        LogDataGrid.ScrollIntoView(entry);
        lastLocatedLogIndex = foundIndex;
        autoFollowLogs = false;
        SetStatus($"定位到第 {foundIndex + 1} 行：{keyword}");
    }

    private int FindVisibleLogIndex(string keyword, int startIndex)
    {
        for (int index = startIndex; index < visibleLogs.Count; index++)
        {
            if (visibleLogs[index].RawLine.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private void ClearVisibleLogs()
    {
        pendingLogs.Clear();
        allLogs.Clear();
        visibleLogs.Clear();
        lastLocatedLogIndex = -1;
        autoFollowLogs = true;
        SetStatus("显示已清空");
    }

    private void ScrollLogToBottom()
    {
        if (visibleLogs.Count == 0) return;
        if (FindVisualChild<ScrollViewer>(LogDataGrid) is { } scrollViewer)
        {
            scrollViewer.ScrollToEnd();
        }
        else
        {
            LogDataGrid.ScrollIntoView(visibleLogs[^1]);
        }
        autoFollowLogs = true;
    }

    private bool IsLogGridAtBottom()
    {
        ScrollViewer? scrollViewer = FindVisualChild<ScrollViewer>(LogDataGrid);
        if (scrollViewer is null) return true;
        return scrollViewer.ScrollableHeight <= 0 || scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 1;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            T? nestedChild = FindVisualChild<T>(child);
            if (nestedChild is not null) return nestedChild;
        }
        return null;
    }

    private void AppendCrashSeparator()
    {
        if (CrashTextBox.Text.Length > 0) CrashTextBox.AppendText(Environment.NewLine);
        CrashTextBox.AppendText("========== Crash ==========" + Environment.NewLine);
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
        string packageName = PackageComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(packageName)) return string.Empty;

        string directory = Path.Combine(AppContext.BaseDirectory, "crash-logs");
        Directory.CreateDirectory(directory);

        string safePackageName = SanitizeFileName(packageName);
        string path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safePackageName}.log");
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

    private AndroidDevice? GetSelectedDevice()
    {
        return DeviceComboBox.SelectedItem as AndroidDevice;
    }

    private void SetPackageName(string packageName)
    {
        if (PackageComboBox.ItemsSource is IEnumerable<string> packages && !packages.Contains(packageName, StringComparer.OrdinalIgnoreCase))
        {
            PackageComboBox.ItemsSource = new[] { packageName }.Concat(packages).ToArray();
        }
        PackageComboBox.Text = packageName;
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
        RefreshDevicesButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        ApkDropBox.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(message)) SetStatus(message);
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private static string GetComboText(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : comboBox.Text;
    }

    private void LogDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not LogEntry entry) return;
        e.Row.Foreground = entry.Level switch
        {
            "E" or "F" => Brushes.Crimson,
            "W" => Brushes.DarkOrange,
            "I" => Brushes.RoyalBlue,
            "D" => Brushes.DimGray,
            _ => Brushes.Black
        };
    }

    private void LogDataGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0) return;
        autoFollowLogs = IsLogGridAtBottom();
    }

    private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadPackagesAsync();
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
        MessageBox.Show(this, "Android Log Viewer\nWPF 版日志查看工具", "About", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        await UninstallAsync();
    }

    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void FilterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilters();
    }

    private void LocateTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        lastLocatedLogIndex = -1;
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

    private void SaveLogsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveLogs();
    }

    private void AnalyzeFileButton_Click(object sender, RoutedEventArgs e)
    {
        AnalyzeLogFile();
    }

    private void ApkDropBox_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = TryGetApkPath(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ApkDropBox_Drop(object sender, DragEventArgs e)
    {
        await InstallDroppedApkAsync(e);
    }
}
