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
    private ApkInfo? currentApkInfo;

    public MainWindow()
    {
        InitializeComponent();

        LogDataGrid.ItemsSource = visibleLogs;
        SelectInitialLanguage();
        ApplyLocalization();
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

    /// <summary>
    /// 显示设备列表并选择第一个设备，如果没有设备则清空包名列表。
    /// </summary>
    /// <returns></returns>
    private async Task RefreshDevicesAsync()
    {
        try
        {
            SetBusy(true, T("LoadingDevicesStatus"));
            IReadOnlyList<AndroidDevice> devices = await adbService.GetDevicesAsync();
            DeviceComboBox.ItemsSource = devices;
            DeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
            if (devices.Count == 0) PackageComboBox.ItemsSource = null;
            SetStatus(F("LoadDevicesStatus", devices.Count));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, T("AdbErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SelectInitialLanguage()
    {
        string cultureName = Localization.CurrentCulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : "zh-CN";
        Localization.SetCulture(cultureName);

        foreach (object item in LanguageComboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem && string.Equals(comboBoxItem.Tag?.ToString(), cultureName, StringComparison.OrdinalIgnoreCase))
            {
                LanguageComboBox.SelectedItem = comboBoxItem;
                break;
            }
        }
    }

    private void ApplyLocalization()
    {
        DeviceLabel.Text = T("Device");
        RefreshDevicesButton.Content = T("RefreshDevices");
        StartLogButton.Content = T("StartLog");
        ClearBeforeStartCheckBox.Content = T("StartClear");
        StopLogButton.Content = T("Stop");
        ClearDeviceLogButton.Content = T("ClearDeviceLog");
        ClearViewButton.Content = T("ClearView");
        AboutButton.Content = T("About");
        LanguageLabel.Text = T("Language");

        InstallApkLabel.Text = T("InstallApk");
        DropApkLabel.Text = T("DragApkInstall");
        PackageNameLabel.Text = T("PackageName");
        UninstallButton.Content = T("Uninstall");
        ApkInfoTitleLabel.Text = T("ApkInfo");
        InstructionsTextBlock.Text = ApkInfoGrid.Visibility == Visibility.Visible ? string.Empty : T("ApkInfoEmpty");
        ApkAppLabel.Text = T("ApkApp");
        ApkVersionLabel.Text = T("ApkVersion");
        ApkVersionCodeLabel.Text = T("ApkVersionCode");
        ApkDetailsButton.Content = T("ApkDetails");

        TextFilterLabel.Text = T("Text");
        LevelLabel.Text = T("Level");
        LocateLabel.Text = T("Locate");
        LocateNextButton.Content = T("Next");
        StorageLabel.Text = T("Storage");
        AnalyzeFileButton.Content = T("AnalyzeFile");
        CaptureCrashCheckBox.Content = T("CaptureCrash");
        SaveCrashButton.Content = T("SaveCrash");
        ClearCrashButton.Content = T("ClearCrash");
        SaveLogsButton.Content = T("SaveLogs");

        LogTabItem.Header = T("Log");
        CrashTabItem.Header = T("CrashInfo");
        TimeColumn.Header = T("Time");
        LevelColumn.Header = T("Level");
        ContentColumn.Header = T("Content");
    }

    private static string T(string key)
    {
        return Localization.Text(key);
    }

    private static string F(string key, params object[] args)
    {
        return Localization.Format(key, args);
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
            SetStatus(F("LoadingPackagesStatus", packages.Count));
        }
        catch (Exception ex)
        {
            SetStatus(F("LoadingPackagesFailedStatus", ex.Message));
        }
    }

    private async Task StartLogcatAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        if (device is null)
        {
            MessageBox.Show(this, T("PleaseSelectDevice"), T("PromptTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StartLogButton.IsEnabled = false;
        StopLogButton.IsEnabled = false;

        if (ClearBeforeStartCheckBox.IsChecked == true)
        {
            SetStatus(T("ClearingHistoryLogStatus"));
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
        SetStatus(F("ReadingLogStatus", device.DisplayName));
    }

    private void StopLogcat()
    {
        logcatReader.Stop();
        FlushPendingLogs();
        flushTimer.Stop();
        StartLogButton.IsEnabled = true;
        StopLogButton.IsEnabled = false;
        SetStatus(T("LogStoppedStatus"));
    }

    private async Task ClearDeviceLogAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        if (device is null) return;

        AdbCommandResult result = await adbService.ClearLogcatAsync(device.Serial);
        SetStatus(result.Success ? T("DeviceLogClearedStatus") : result.CombinedText);
    }

    private async Task InstallDroppedApkAsync(DragEventArgs e)
    {
        if (!TryGetApkPath(e, out string apkPath)) return;

        AndroidDevice? device = GetSelectedDevice();
        if (device is null)
        {
            MessageBox.Show(this, T("PleaseSelectDevice"), T("PromptTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true, T("ReadingApkInfoStatus"));
            ApkInfo? apkInfo = await adbService.GetApkInfoAsync(apkPath);
            ShowApkInfo(apkInfo, apkPath);
            string? packageName = apkInfo?.PackageName;
            if (!string.IsNullOrWhiteSpace(packageName)) SetPackageName(packageName);

            SetStatus(T("InstallingApkStatus"));
            AdbCommandResult result = await adbService.InstallApkAsync(device.Serial, apkPath, new Progress<string>(SetStatus));
            if (!result.Success && IsUpdateIncompatible(result))
            {
                if (string.IsNullOrWhiteSpace(packageName))
                {
                    SetStatus(T("ForceInstallPackageMissing"));
                    MessageBox.Show(this, result.CombinedText, T("ApkInstallFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                bool forceInstall = ShowForceInstallDialog(packageName, result.CombinedText);
                if (!forceInstall)
                {
                    SetStatus(result.CombinedText);
                    return;
                }

                SetStatus(F("ForceInstallingStatus", packageName));
                AdbCommandResult uninstallResult = await adbService.UninstallPackageAsync(device.Serial, packageName);
                if (!uninstallResult.Success)
                {
                    SetStatus(uninstallResult.CombinedText);
                    MessageBox.Show(this, uninstallResult.CombinedText, T("UninstallFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                result = await adbService.InstallApkAsync(device.Serial, apkPath, new Progress<string>(SetStatus));
            }

            if (result.Success && !string.IsNullOrWhiteSpace(packageName))
            {
                await StartInstalledPackageAsync(device.Serial, packageName);
            }
            else
            {
                SetStatus(result.Success ? F("InstallCompleteStatus", Path.GetFileName(apkPath)) : result.CombinedText);
            }
            MessageBox.Show(this, result.CombinedText, result.Success ? T("ApkInstallSuccessTitle") : T("ApkInstallFailedTitle"),
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, T("ApkInstallFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(this, T("PleaseSelectDeviceAndPackage"), T("PromptTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AdbCommandResult result = await adbService.UninstallPackageAsync(device.Serial, packageName);
        SetStatus(result.Success ? F("UninstallCompleteStatus", packageName) : result.CombinedText);
        MessageBox.Show(this, result.CombinedText, result.Success ? T("UninstallSuccessTitle") : T("UninstallFailedTitle"),
            MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        await LoadPackagesAsync();
    }

    private async Task StartInstalledPackageAsync(string serial, string packageName)
    {
        SetStatus(F("StartingAppStatus", packageName));
        AdbCommandResult startResult = await adbService.StartPackageAsync(serial, packageName);
        SetStatus(startResult.Success ? F("InstallCompleteStatus", packageName) : F("StartAppFailedStatus", startResult.CombinedText));
    }

    private bool ShowForceInstallDialog(string packageName, string errorText)
    {
        bool accepted = false;
        var dialog = new Window
        {
            Title = T("ApkInstallFailedTitle"),
            Owner = this,
            Width = 560,
            MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.White
        };

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prompt = new TextBlock
        {
            Text = F("ForceInstallPrompt", packageName),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(prompt);

        var errorBox = new TextBox
        {
            Text = errorText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 82,
            MaxHeight = 120,
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(errorBox, 1);
        root.Children.Add(errorBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetRow(buttons, 2);

        var forceButton = new Button
        {
            Content = T("ForceInstall"),
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        forceButton.Click += (_, _) =>
        {
            accepted = true;
            dialog.DialogResult = true;
            dialog.Close();
        };

        var cancelButton = new Button
        {
            Content = T("Cancel"),
            MinWidth = 86,
            IsCancel = true
        };
        cancelButton.Click += (_, _) =>
        {
            accepted = false;
            dialog.DialogResult = false;
            dialog.Close();
        };

        buttons.Children.Add(forceButton);
        buttons.Children.Add(cancelButton);
        root.Children.Add(buttons);

        dialog.Content = root;
        dialog.ShowDialog();
        return accepted;
    }

    private static bool IsUpdateIncompatible(AdbCommandResult result)
    {
        return result.CombinedText.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.OrdinalIgnoreCase)
            || result.CombinedText.Contains("signatures do not match", StringComparison.OrdinalIgnoreCase);
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
            SetStatus(F("LogReadingPendingStatus", pendingLogs.Count));
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
            MessageBox.Show(this, T("MessageNoLogs"), T("PromptTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = T("SaveLogTitle"),
            Filter = "Log files|*.log|Text files|*.txt|All files|*.*",
            FileName = $"logcat-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };
        if (dialog.ShowDialog(this) != true) return;

        File.WriteAllLines(dialog.FileName, lines);
        SetStatus(F("LogSavedStatus", dialog.FileName));
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
            MessageBox.Show(this, T("MessageNoCrash"), T("PromptTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = T("SaveCrashTitle"),
            Filter = "Log files|*.log|Text files|*.txt|All files|*.*",
            FileName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };
        if (dialog.ShowDialog(this) != true) return;

        File.WriteAllLines(dialog.FileName, crashLogs.Select(entry => entry.RawLine));
        SetStatus(F("CrashSavedStatus", dialog.FileName));
    }

    private void ClearCrashLogs()
    {
        crashLogs.Clear();
        CrashTextBox.Clear();
        crashContextLinesRemaining = 0;
        autoCrashLogPath = string.Empty;
        SetStatus(T("CrashClearedStatus"));
    }

    private void AnalyzeLogFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = T("OpenLogFileTitle"),
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
        SetStatus(F("FileAnalyzeCompleteStatus", total, crashLogs.Count));
    }

    private void LocateNextLog()
    {
        FlushPendingLogs();
        string keyword = LocateTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SetStatus(T("LocateKeywordRequiredStatus"));
            return;
        }

        int startIndex = Math.Clamp(lastLocatedLogIndex + 1, 0, visibleLogs.Count);
        int foundIndex = FindVisibleLogIndex(keyword, startIndex);
        if (foundIndex < 0 && startIndex > 0) foundIndex = FindVisibleLogIndex(keyword, 0);
        if (foundIndex < 0)
        {
            SetStatus(F("NotFoundStatus", keyword));
            return;
        }

        LogEntry entry = visibleLogs[foundIndex];
        LogDataGrid.SelectedItem = entry;
        LogDataGrid.ScrollIntoView(entry);
        lastLocatedLogIndex = foundIndex;
        autoFollowLogs = false;
        SetStatus(F("LocatedStatus", foundIndex + 1, keyword));
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
        SetStatus(T("ViewClearedStatus"));
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

    private void ShowApkInfo(ApkInfo? apkInfo, string apkPath)
    {
        ApkInfoGrid.Visibility = Visibility.Visible;
        InstructionsTextBlock.Text = string.Empty;
        currentApkInfo = apkInfo;

        ApkAppTextBlock.Text = string.IsNullOrWhiteSpace(apkInfo?.ApplicationLabel)
            ? Path.GetFileNameWithoutExtension(apkPath)
            : apkInfo.ApplicationLabel;
        ApkVersionTextBlock.Text = string.IsNullOrWhiteSpace(apkInfo?.VersionName) ? T("Unknown") : apkInfo.VersionName;
        ApkVersionCodeTextBlock.Text = string.IsNullOrWhiteSpace(apkInfo?.VersionCode) ? T("Unknown") : apkInfo.VersionCode;
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

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageComboBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string cultureName)
        {
            return;
        }

        Localization.SetCulture(cultureName);
        if (IsInitialized)
        {
            ApplyLocalization();
        }
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
        MessageBox.Show(this, T("AboutMessage"), T("About"), MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void ApkDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        string packageName = string.IsNullOrWhiteSpace(currentApkInfo?.PackageName)
            ? T("Unknown")
            : currentApkInfo.PackageName;
        string permissions = currentApkInfo?.Permissions.Count > 0
            ? string.Join(Environment.NewLine, currentApkInfo.Permissions)
            : T("None");

        MessageBox.Show(
            this,
            $"{T("ApkPackage")}:\n{packageName}\n\n{T("ApkPermissions")}:\n{permissions}",
            T("ApkDetailsTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
