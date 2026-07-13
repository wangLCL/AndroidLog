using AndroidLogViewer.Models;
using AndroidLogViewer.Services;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace AndroidLogViewer;

public sealed class MainForm : Form
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
    private readonly BindingList<LogEntry> visibleLogs = [];
    private readonly List<LogEntry> allLogs = [];
    private readonly List<LogEntry> crashLogs = [];
    private readonly ConcurrentQueue<LogEntry> pendingLogs = new();
    private readonly System.Windows.Forms.Timer logFlushTimer = new();

    private ComboBox deviceCombo = null!;
    private ComboBox packageComboBox = null!;
    private ComboBox levelCombo = null!;
    private ComboBox storeLevelCombo = null!;
    private TextBox searchTextBox = null!;
    private TextBox tagTextBox = null!;
    private TextBox locateTextBox = null!;
    private TextBox crashTextBox = null!;
    private DataGridView logGrid = null!;
    private Label statusLabel = null!;
    private Panel dropPanel = null!;
    private Button refreshDevicesButton = null!;
    private Button startLogButton = null!;
    private Button stopLogButton = null!;
    private Button uninstallButton = null!;
    private CheckBox clearBeforeStartCheckBox = null!;
    private CheckBox captureCrashCheckBox = null!;

    private bool autoFollowLogs = true;
    private int crashContextLinesRemaining;
    private string autoCrashLogPath = string.Empty;
    private int lastLocatedLogIndex = -1;
    private string lockedLocatedRawLine = string.Empty;
    private int lockedLocatedOffset = 6;
    private bool restoringLocatedRow;

    public MainForm()
    {
        logcatReader = new LogcatReader(adbService);
        logcatReader.EntryReceived += (_, entry) => pendingLogs.Enqueue(entry);
        logcatReader.ErrorReceived += (_, message) =>
        {
            if (!IsDisposed) BeginInvoke(() => SetStatus(message));
        };

        logFlushTimer.Interval = 120;
        logFlushTimer.Tick += (_, _) => FlushPendingLogs();

        Text = "Android Log Viewer";
        Size = new Size(1220, 760);
        MinimumSize = new Size(1100, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 252);

        BuildUi();
        tagTextBox.Text = LoadLastTagFilter();
        Load += async (_, _) => await RefreshDevicesAsync();
        FormClosing += (_, _) =>
        {
            SaveLastTagFilter();
            logFlushTimer.Stop();
            logcatReader.Dispose();
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
            BackColor = Color.FromArgb(246, 248, 252)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(CreateTopBarV2(), 0, 0);
        root.Controls.Add(CreateMainArea(), 0, 1);

        statusLabel = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Height = 28, Text = $"ADB: {adbService.AdbPath}" };
        root.Controls.Add(statusLabel, 0, 2);
    }

#if false
    private Control CreateTopBar()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 8, Margin = new Padding(0, 0, 0, 10) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label { Text = "设备", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        deviceCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        deviceCombo.SelectedIndexChanged += async (_, _) => await LoadUserPackagesForSelectedDeviceAsync();
        panel.Controls.Add(deviceCombo, 1, 0);

        refreshDevicesButton = new Button { Text = "刷新设备", AutoSize = true };
        refreshDevicesButton.Click += async (_, _) => await RefreshDevicesAsync();
        panel.Controls.Add(refreshDevicesButton, 2, 0);

        startLogButton = new Button { Text = "开始日志", AutoSize = true };
        startLogButton.Click += async (_, _) => await StartLogcatAsync();
        panel.Controls.Add(startLogButton, 3, 0);

        clearBeforeStartCheckBox = new CheckBox { Text = "开始前清空", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 8, 0) };
        panel.Controls.Add(clearBeforeStartCheckBox, 4, 0);

        stopLogButton = new Button { Text = "停止", AutoSize = true, Enabled = false };
        stopLogButton.Click += (_, _) => StopLogcat();
        panel.Controls.Add(stopLogButton, 5, 0);

        var clearDeviceLogButton = new Button { Text = "清空设备日志", AutoSize = true };
        clearDeviceLogButton.Click += async (_, _) => await ClearDeviceLogAsync();
        panel.Controls.Add(clearDeviceLogButton, 6, 0);

        var clearViewButton = new Button { Text = "清空显示", AutoSize = true };
        clearViewButton.Click += (_, _) => ClearVisibleLogs();
        panel.Controls.Add(clearViewButton, 7, 0);
        return panel;
    }

#endif

    private Control CreateTopBarV2()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 7,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 2; i < 7; i++) panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label { Text = "设备", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        deviceCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        deviceCombo.SelectedIndexChanged += async (_, _) => await LoadUserPackagesForSelectedDeviceAsync();
        panel.Controls.Add(deviceCombo, 1, 0);
        panel.SetColumnSpan(deviceCombo, 5);

        refreshDevicesButton = new Button { Text = "刷新设备", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        refreshDevicesButton.Click += async (_, _) => await RefreshDevicesAsync();
        panel.Controls.Add(refreshDevicesButton, 6, 0);

        startLogButton = new Button { Text = "开始日志", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
        startLogButton.Click += async (_, _) => await StartLogcatAsync();
        panel.Controls.Add(startLogButton, 1, 1);

        clearBeforeStartCheckBox = new CheckBox { Text = "开始前清空", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) };
        panel.Controls.Add(clearBeforeStartCheckBox, 2, 1);

        stopLogButton = new Button { Text = "停止", AutoSize = true, Enabled = false, Margin = new Padding(0, 8, 8, 0) };
        stopLogButton.Click += (_, _) => StopLogcat();
        panel.Controls.Add(stopLogButton, 3, 1);

        var clearDeviceLogButton = new Button { Text = "清空设备日志", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
        clearDeviceLogButton.Click += async (_, _) => await ClearDeviceLogAsync();
        panel.Controls.Add(clearDeviceLogButton, 4, 1);

        var clearViewButton = new Button { Text = "清空显示", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        clearViewButton.Click += (_, _) => ClearVisibleLogs();
        panel.Controls.Add(clearViewButton, 5, 1);
        return panel;
    }

    private Control CreateMainArea()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(CreateCard(CreateDevicePanel(), new Padding(14)), 0, 0);
        panel.Controls.Add(CreateCard(CreateLogPanel(), new Padding(12)), 1, 0);
        return panel;
    }

    private Control CreateDevicePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Color.White };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(new Label { Text = "安装 APK", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        dropPanel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, AllowDrop = true, BackColor = Color.FromArgb(248, 250, 252), Margin = new Padding(0, 0, 0, 2) };
        dropPanel.DragEnter += DropPanel_DragEnter;
        dropPanel.DragDrop += async (_, e) => await DropPanel_DragDropAsync(e);
        dropPanel.Paint += DropPanel_Paint;
        panel.Controls.Add(dropPanel, 0, 1);

        panel.Controls.Add(new Label { Text = "卸载包名", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 18, 0, 8) }, 0, 2);
        packageComboBox = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, Margin = new Padding(0, 0, 0, 8) };
        panel.Controls.Add(packageComboBox, 0, 3);

        uninstallButton = new Button { Text = "卸载", Dock = DockStyle.Top, Height = 34 };
        uninstallButton.Click += async (_, _) => await UninstallAsync();
        panel.Controls.Add(uninstallButton, 0, 4);

        panel.Controls.Add(new TextBox
        {
            Text = "说明：\r\n1. 选择设备后，把 APK 拖到上方区域安装。\r\n2. 拖入 APK 会自动读取包名并填入卸载框。\r\n3. 下拉框只列出第三方用户应用。",
            Dock = DockStyle.Top,
            Height = 104,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(90, 96, 106),
            Margin = new Padding(0, 18, 0, 0)
        }, 0, 5);
        return panel;
    }

    private static Control CreateCard(Control content, Padding padding)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = padding, Margin = new Padding(0, 0, 10, 0) };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(226, 232, 240));
            Rectangle rect = card.ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;
            e.Graphics.DrawRectangle(pen, rect);
        };
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        return card;
    }

    private Control CreateLogPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(CreateFilterBar(), 0, 0);
        panel.Controls.Add(CreateLogTabs(), 0, 1);
        return panel;
    }

    private Control CreateLogTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var logPage = new TabPage("日志") { BackColor = Color.White };
        logPage.Controls.Add(CreateLogGrid());
        tabs.TabPages.Add(logPage);

        var crashPage = new TabPage("崩溃信息") { BackColor = Color.White };
        crashTextBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F), BackColor = Color.White };
        crashPage.Controls.Add(crashTextBox);
        tabs.TabPages.Add(crashPage);
        return tabs;
    }

    private Control CreateLogGrid()
    {
        logGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = Color.FromArgb(226, 232, 240),
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 32,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(248, 250, 252), ForeColor = Color.FromArgb(15, 23, 42), Font = new Font(Font, FontStyle.Bold) },
            DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Color.FromArgb(17, 24, 39), SelectionBackColor = Color.FromArgb(219, 234, 254), SelectionForeColor = Color.FromArgb(15, 23, 42) },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(250, 252, 255) },
            DataSource = visibleLogs
        };
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时间", DataPropertyName = nameof(LogEntry.Time), Width = 120 });
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "级别", DataPropertyName = nameof(LogEntry.Level), Width = 54 });
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tag", DataPropertyName = nameof(LogEntry.Tag), Width = 180 });
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PID", DataPropertyName = nameof(LogEntry.ProcessId), Width = 70 });
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "内容", DataPropertyName = nameof(LogEntry.Message), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        logGrid.CellFormatting += LogGrid_CellFormatting;
        logGrid.Scroll += (_, _) =>
        {
            autoFollowLogs = IsLogViewAtBottom();
            if (!restoringLocatedRow) lockedLocatedRawLine = string.Empty;
        };
        return logGrid;
    }

    private Control CreateFilterBar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(0, 0, 0, 6),
            BackColor = Color.White
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var filterRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 4),
            BackColor = Color.White
        };

        filterRow.Controls.Add(CreateInlineLabel("文本"));
        searchTextBox = new TextBox { Width = 260, PlaceholderText = "筛选日志内容", Margin = new Padding(0, 0, 18, 0) };
        searchTextBox.TextChanged += (_, _) => ApplyFilters();
        filterRow.Controls.Add(searchTextBox);

        filterRow.Controls.Add(CreateInlineLabel("Tag"));
        tagTextBox = new TextBox { Width = 220, PlaceholderText = "多个 Tag 用空格/逗号分隔", Margin = new Padding(0, 0, 18, 0) };
        tagTextBox.TextChanged += (_, _) => ApplyFilters();
        filterRow.Controls.Add(tagTextBox);

        filterRow.Controls.Add(CreateInlineLabel("级别"));
        levelCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 86, Margin = new Padding(0, 0, 18, 0) };
        levelCombo.Items.AddRange(["All", "V", "D", "I", "W", "E", "F"]);
        levelCombo.SelectedIndex = 0;
        levelCombo.SelectedIndexChanged += (_, _) => ApplyFilters();
        filterRow.Controls.Add(levelCombo);

        filterRow.Controls.Add(CreateInlineLabel("定位"));
        locateTextBox = new TextBox { Width = 210, PlaceholderText = "跳到关键字", Margin = new Padding(0, 0, 8, 0) };
        locateTextBox.TextChanged += (_, _) =>
        {
            lastLocatedLogIndex = -1;
            lockedLocatedRawLine = string.Empty;
        };
        locateTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                LocateNextLog();
                e.SuppressKeyPress = true;
            }
        };
        filterRow.Controls.Add(locateTextBox);

        var locateButton = new Button { Text = "下一个", AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
        locateButton.Click += (_, _) => LocateNextLog();
        filterRow.Controls.Add(locateButton);
        panel.Controls.Add(filterRow, 0, 0);

        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            BackColor = Color.White
        };

        captureCrashCheckBox = new CheckBox { Text = "捕获崩溃", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) };
        actionRow.Controls.Add(captureCrashCheckBox);

        var saveCrashButton = new Button { Text = "保存崩溃", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
        saveCrashButton.Click += (_, _) => SaveCrashLogs();
        actionRow.Controls.Add(saveCrashButton);

        var clearCrashButton = new Button { Text = "清空崩溃", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
        clearCrashButton.Click += (_, _) => ClearCrashLogs();
        actionRow.Controls.Add(clearCrashButton);

        var saveAllLogsButton = new Button { Text = "保存日志", AutoSize = true, Margin = new Padding(0, 4, 14, 0) };
        saveAllLogsButton.Click += (_, _) => SaveAllLogs();
        actionRow.Controls.Add(saveAllLogsButton);

        actionRow.Controls.Add(CreateInlineLabel("存储", top: 8));
        storeLevelCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 86, Margin = new Padding(0, 4, 14, 0) };
        storeLevelCombo.Items.AddRange(["None", "W", "E", "All"]);
        storeLevelCombo.SelectedItem = "W";
        actionRow.Controls.Add(storeLevelCombo);

        var analyzeFileButton = new Button { Text = "分析文件", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        analyzeFileButton.Click += (_, _) => AnalyzeLogFile();
        actionRow.Controls.Add(analyzeFileButton);
        panel.Controls.Add(actionRow, 0, 1);

        return panel;
    }

    private static Label CreateInlineLabel(string text, int top = 4)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, top, 8, 0)
        };
    }

    private static string LoadLastTagFilter()
    {
        try
        {
            return File.Exists(LastTagFilterPath)
                ? File.ReadAllText(LastTagFilterPath).Trim()
                : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

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
            deviceCombo.Items.Clear();
            deviceCombo.Items.AddRange(devices.Cast<object>().ToArray());
            if (deviceCombo.Items.Count > 0) deviceCombo.SelectedIndex = 0;
            else packageComboBox.Items.Clear();
            SetStatus($"找到 {devices.Count} 个设备");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "ADB 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadUserPackagesForSelectedDeviceAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        if (device is null || packageComboBox is null) return;
        string currentText = packageComboBox.Text.Trim();
        try
        {
            IReadOnlyList<string> packages = await adbService.GetUserPackagesAsync(device.Serial);
            packageComboBox.BeginUpdate();
            packageComboBox.Items.Clear();
            packageComboBox.Items.AddRange(packages.Cast<object>().ToArray());
            packageComboBox.Text = currentText;
            packageComboBox.EndUpdate();
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
            MessageBox.Show(this, "请先选择设备。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        startLogButton.Enabled = false;
        stopLogButton.Enabled = false;
        if (clearBeforeStartCheckBox.Checked)
        {
            SetStatus("正在清空历史日志...");
            logcatReader.Stop();
            logFlushTimer.Stop();
            ClearVisibleLogs();
            AdbCommandResult clearResult = await adbService.ClearLogcatAsync(device.Serial);
            if (!clearResult.Success)
            {
                SetStatus(clearResult.CombinedText);
                startLogButton.Enabled = true;
                return;
            }
        }

        logcatReader.Start(device.Serial);
        logFlushTimer.Start();
        startLogButton.Enabled = false;
        stopLogButton.Enabled = true;
        SetStatus($"正在读取日志：{device.DisplayName}");
    }

    private void StopLogcat()
    {
        logcatReader.Stop();
        FlushPendingLogs();
        logFlushTimer.Stop();
        startLogButton.Enabled = true;
        stopLogButton.Enabled = false;
        SetStatus("日志已停止");
    }

    private async Task ClearDeviceLogAsync()
    {
        AndroidDevice? device = GetSelectedDevice();
        if (device is null) return;
        AdbCommandResult result = await adbService.ClearLogcatAsync(device.Serial);
        SetStatus(result.Success ? "设备日志已清空" : result.CombinedText);
    }

    private async Task DropPanel_DragDropAsync(DragEventArgs e)
    {
        if (!TryGetApkPath(e, out string apkPath)) return;
        AndroidDevice? device = GetSelectedDevice();
        if (device is null)
        {
            MessageBox.Show(this, "请先选择设备。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            MessageBox.Show(this, "请选择设备并输入包名。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AdbCommandResult result = await adbService.UninstallPackageAsync(device.Serial, packageName);
        SetStatus(result.Success ? $"卸载完成：{packageName}" : result.CombinedText);
        MessageBox.Show(this, result.CombinedText, result.Success ? "卸载完成" : "卸载失败",
            MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        await LoadUserPackagesForSelectedDeviceAsync();
    }

    private void FlushPendingLogs()
    {
        if (pendingLogs.IsEmpty) return;
        bool shouldFollow = autoFollowLogs || IsLogViewAtBottom();
        int firstVisibleRow = GetFirstDisplayedLogRow();
        logGrid.SuspendLayout();
        try
        {
            for (int index = 0; index < MaxFlushRowsPerTick && pendingLogs.TryDequeue(out LogEntry? entry); index++)
            {
                CaptureCrashLog(entry);
                StoreRegularLogIfNeeded(entry);
                if (entry.Matches(searchTextBox.Text, tagTextBox.Text, levelCombo.Text)) visibleLogs.Add(entry);
            }
            while (visibleLogs.Count > MaxVisibleRows) visibleLogs.RemoveAt(0);
        }
        finally
        {
            logGrid.ResumeLayout();
            if (!string.IsNullOrEmpty(lockedLocatedRawLine) && RestoreLockedLocatedRow()) autoFollowLogs = false;
            else if (shouldFollow) ScrollLogToBottom();
            else
            {
                RestoreFirstDisplayedLogRow(firstVisibleRow);
                autoFollowLogs = false;
            }
        }

        if (!pendingLogs.IsEmpty) SetStatus($"日志读取中，待刷新 {pendingLogs.Count} 行");
    }

    private void CaptureCrashLog(LogEntry entry)
    {
        if (!captureCrashCheckBox.Checked) return;
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
        AppendAutoCrashLine(entry.RawLine);
    }

    private void StoreRegularLogIfNeeded(LogEntry entry)
    {
        if (!ShouldStoreLog(entry)) return;
        allLogs.Add(entry);
        if (allLogs.Count > MaxStoredRows) allLogs.RemoveAt(0);
    }

    private bool ShouldStoreLog(LogEntry entry)
    {
        string selectedLevel = storeLevelCombo.SelectedItem?.ToString() ?? "W";
        if (selectedLevel == "None") return false;
        return selectedLevel == "All" || entry.PassesMinimumLevel(selectedLevel);
    }

    private void AppendCrashSeparator()
    {
        if (crashTextBox.TextLength > 0) crashTextBox.AppendText(Environment.NewLine);
        crashTextBox.AppendText("========== Crash ==========" + Environment.NewLine);
    }

    private void AppendAutoCrashSeparator()
    {
        if (string.IsNullOrWhiteSpace(autoCrashLogPath))
        {
            return;
        }

        File.AppendAllText(autoCrashLogPath, Environment.NewLine + "========== Crash ==========" + Environment.NewLine);
    }

    private void AppendAutoCrashLine(string line)
    {
        if (string.IsNullOrWhiteSpace(autoCrashLogPath))
        {
            return;
        }

        File.AppendAllText(autoCrashLogPath, line + Environment.NewLine);
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
        visibleLogs.RaiseListChangedEvents = false;
        visibleLogs.Clear();
        foreach (LogEntry entry in allLogs.Where(entry => entry.Matches(searchTextBox.Text, tagTextBox.Text, levelCombo.Text)).TakeLast(MaxVisibleRows))
        {
            visibleLogs.Add(entry);
        }
        visibleLogs.RaiseListChangedEvents = true;
        visibleLogs.ResetBindings();
    }

    private void ClearVisibleLogs()
    {
        autoFollowLogs = true;
        lastLocatedLogIndex = -1;
        lockedLocatedRawLine = string.Empty;
        pendingLogs.Clear();
        allLogs.Clear();
        visibleLogs.Clear();
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
            MessageBox.Show(this, "当前没有可保存的日志。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog { Title = "保存日志", Filter = "Log files|*.log|Text files|*.txt|All files|*.*", FileName = $"logcat-{DateTime.Now:yyyyMMdd-HHmmss}.log" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllLines(dialog.FileName, lines);
        SetStatus($"日志已保存：{dialog.FileName}");
    }

    private void SaveCrashLogs()
    {
        if (crashLogs.Count == 0)
        {
            MessageBox.Show(this, "当前没有捕获到崩溃信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog { Title = "保存崩溃信息", Filter = "Log files|*.log|Text files|*.txt|All files|*.*", FileName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
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
        using var dialog = new OpenFileDialog { Title = "选择日志文件", Filter = "Log files|*.log;*.txt|All files|*.*", Multiselect = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        autoFollowLogs = true;
        lastLocatedLogIndex = -1;
        lockedLocatedRawLine = string.Empty;
        pendingLogs.Clear();
        allLogs.Clear();
        visibleLogs.Clear();
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
            StoreRegularLogIfNeeded(entry);
            if (entry.Matches(searchTextBox.Text, tagTextBox.Text, levelCombo.Text))
            {
                visibleLogs.Add(entry);
                while (visibleLogs.Count > MaxVisibleRows) visibleLogs.RemoveAt(0);
            }
        }
        visibleLogs.ResetBindings();
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
        if (visibleLogs.Count == 0)
        {
            SetStatus("当前没有可定位的日志");
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

        SelectLogRow(foundIndex);
        lastLocatedLogIndex = foundIndex;
        lockedLocatedRawLine = visibleLogs[foundIndex].RawLine;
        lockedLocatedOffset = Math.Min(6, foundIndex);
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

    private bool RestoreLockedLocatedRow()
    {
        int index = -1;
        for (int i = 0; i < visibleLogs.Count; i++)
        {
            if (string.Equals(visibleLogs[i].RawLine, lockedLocatedRawLine, StringComparison.Ordinal))
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
        SelectLogRow(index, lockedLocatedOffset);
        lastLocatedLogIndex = index;
        return true;
    }

    private void SelectLogRow(int rowIndex, int contextRowsBefore = 6)
    {
        if (rowIndex < 0 || rowIndex >= logGrid.RowCount) return;
        restoringLocatedRow = true;
        try
        {
            logGrid.ClearSelection();
            logGrid.FirstDisplayedScrollingRowIndex = Math.Max(rowIndex - Math.Max(contextRowsBefore, 0), 0);
            logGrid.Rows[rowIndex].Selected = true;
            if (logGrid.Columns.Count > 0) logGrid.CurrentCell = logGrid.Rows[rowIndex].Cells[0];
        }
        finally
        {
            restoringLocatedRow = false;
        }
    }

    private void LogGrid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (logGrid.Rows[e.RowIndex].DataBoundItem is not LogEntry entry) return;
        logGrid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = entry.Level switch
        {
            "E" or "F" => Color.FromArgb(190, 18, 60),
            "W" => Color.FromArgb(180, 83, 9),
            "I" => Color.FromArgb(37, 99, 235),
            "D" => Color.FromArgb(55, 65, 81),
            _ => Color.FromArgb(17, 24, 39)
        };
    }

    private void DropPanel_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = TryGetApkPath(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void DropPanel_Paint(object? sender, PaintEventArgs e)
    {
        string text = "拖入 APK 安装";
        using var brush = new SolidBrush(Color.FromArgb(75, 85, 99));
        using var font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        SizeF size = e.Graphics.MeasureString(text, font);
        e.Graphics.DrawString(text, font, brush, (dropPanel.Width - size.Width) / 2, (dropPanel.Height - size.Height) / 2);
    }

    private void ScrollLogToBottom()
    {
        if (logGrid.RowCount <= 0) return;
        int displayedRows = Math.Max(logGrid.DisplayedRowCount(includePartialRow: false), 1);
        logGrid.FirstDisplayedScrollingRowIndex = Math.Max(logGrid.RowCount - displayedRows, 0);
        autoFollowLogs = true;
    }

    private int GetFirstDisplayedLogRow()
    {
        return logGrid.RowCount == 0 ? 0 : logGrid.FirstDisplayedScrollingRowIndex;
    }

    private void RestoreFirstDisplayedLogRow(int rowIndex)
    {
        if (logGrid.RowCount == 0) return;
        logGrid.FirstDisplayedScrollingRowIndex = Math.Clamp(rowIndex, 0, logGrid.RowCount - 1);
    }

    private bool IsLogViewAtBottom()
    {
        if (logGrid.RowCount == 0) return true;
        int displayedRows = logGrid.DisplayedRowCount(includePartialRow: false);
        int lastVisibleIndex = logGrid.FirstDisplayedScrollingRowIndex + Math.Max(displayedRows, 1);
        return lastVisibleIndex >= logGrid.RowCount - 1;
    }

    private AndroidDevice? GetSelectedDevice()
    {
        return deviceCombo.SelectedItem as AndroidDevice;
    }

    private void SetPackageName(string packageName)
    {
        if (!packageComboBox.Items.Contains(packageName)) packageComboBox.Items.Insert(0, packageName);
        packageComboBox.Text = packageName;
    }

    private static bool TryGetApkPath(DragEventArgs e, out string apkPath)
    {
        apkPath = string.Empty;
        IDataObject? data = e.Data;
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
        string[] files = (string[])data.GetData(DataFormats.FileDrop)!;
        apkPath = files.FirstOrDefault(file => string.Equals(Path.GetExtension(file), ".apk", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(apkPath);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        refreshDevicesButton.Enabled = !busy;
        uninstallButton.Enabled = !busy;
        dropPanel.Enabled = !busy;
        if (!string.IsNullOrWhiteSpace(message)) SetStatus(message);
    }

    private void InitializeComponent()
    {

    }

    private void SetStatus(string message)
    {
        statusLabel.Text = message;
    }
}
