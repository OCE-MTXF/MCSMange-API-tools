using System.Windows;
using System.Windows.Controls;

namespace MCSMServerManager;

/// <summary>设置页：分组导航 + 中文设置项，修改后写回安装目录的 config.json。</summary>
public partial class SettingsWindow : Window
{
    private bool _loading;
    private readonly AppConfig _cfg;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _cfg = config;

        ConfigPathText.Text = "配置文件位置：" + AppConfig.ConfigPath;
        LoadFromConfig();

        _loading = true;
        GroupList.SelectedIndex = 0;
        _loading = false;
        ApplyGroup("conn");
    }

    private void LoadFromConfig()
    {
        SavePanelUrlBox.IsChecked = _cfg.SavePanelUrl;
        SaveApiKeyBox.IsChecked = _cfg.SaveApiKey;
        AutoConnectBox.IsChecked = _cfg.AutoConnectOnStart;
        InsecureCertBox.IsChecked = _cfg.IgnoreCertificateErrors;

        LogEnabledBox.IsChecked = _cfg.LogEnabled;
        SelectCombo(LogLevelBox, _cfg.LogLevel);
        LogHttpBodyBox.IsChecked = _cfg.LogHttpBody;
        LogMaxSizeBox.Text = _cfg.LogMaxSizeMb.ToString();
        LogKeepBox.Text = _cfg.LogKeepFiles.ToString();

        PerfEnabledBox.IsChecked = _cfg.PerformanceMonitorEnabled;
        PerfValueBox.Text = _cfg.PerformanceIntervalValue.ToString();
        SelectCombo(PerfUnitBox, _cfg.PerformanceIntervalUnit.ToString());

        ShowErrorDetailBox.IsChecked = _cfg.ShowErrorDetailDialog;
        InstancePageSizeBox.Text = _cfg.InstancePageSize.ToString();
        FilePageSizeBox.Text = _cfg.FilePageSize.ToString();
        LogViewerLinesBox.Text = _cfg.LogViewerLines.ToString();

        UpdatePreview();
    }

    private static void SelectCombo(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag) { box.SelectedItem = item; return; }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GroupList.SelectedItem is ListBoxItem item && item.Tag is string tag) ApplyGroup(tag);
    }

    private void ApplyGroup(string tag)
    {
        PageTitle.Text = tag switch
        {
            "log" => "日志",
            "perf" => "性能监控",
            "ui" => "界面",
            _ => "连接"
        };

        LogCard.Visibility = tag == "log" ? Visibility.Visible : Visibility.Collapsed;
        PerfCard.Visibility = tag == "perf" ? Visibility.Visible : Visibility.Collapsed;
        UiCard.Visibility = tag == "ui" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LogLevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdatePreview();
    }

    private void PerfUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void PerfValueBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PerfPreviewText is null) return;
        var value = int.TryParse(PerfValueBox?.Text, out var v) && v > 0 ? v : 1;
        var unit = TagOf(PerfUnitBox) ?? "秒";
        var seconds = unit switch
        {
            "分" => value * 60,
            "时" => value * 3600,
            _ => value
        };
        PerfPreviewText.Text = $"= {seconds} 秒";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _cfg.SavePanelUrl = SavePanelUrlBox.IsChecked == true;
        _cfg.SaveApiKey = SaveApiKeyBox.IsChecked == true;
        _cfg.AutoConnectOnStart = AutoConnectBox.IsChecked == true;
        _cfg.IgnoreCertificateErrors = InsecureCertBox.IsChecked == true;

        _cfg.LogEnabled = LogEnabledBox.IsChecked == true;
        _cfg.LogLevel = TagOf(LogLevelBox) ?? "信息";
        _cfg.LogHttpBody = LogHttpBodyBox.IsChecked == true;
        _cfg.LogMaxSizeMb = Clamp(LogMaxSizeBox.Text, 1, 512);
        _cfg.LogKeepFiles = Clamp(LogKeepBox.Text, 1, 200);

        _cfg.PerformanceMonitorEnabled = PerfEnabledBox.IsChecked == true;
        _cfg.PerformanceIntervalValue = Math.Max(1, int.TryParse(PerfValueBox.Text, out var pv) ? pv : 10);
        _cfg.PerformanceIntervalUnit = Enum.TryParse<IntervalUnit>(TagOf(PerfUnitBox), out var unit) ? unit : IntervalUnit.秒;

        _cfg.ShowErrorDetailDialog = ShowErrorDetailBox.IsChecked == true;
        _cfg.InstancePageSize = Clamp(InstancePageSizeBox.Text, 10, 50);
        _cfg.FilePageSize = Clamp(FilePageSizeBox.Text, 10, 100);
        _cfg.LogViewerLines = Clamp(LogViewerLinesBox.Text, 100, 200000);

        _cfg.Save();
        AppLogger.Info("设置已保存：" + AppConfig.ConfigPath);
        DialogResult = true;
    }

    private static int Clamp(string text, int min, int max)
        => int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : min;

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "确定要把所有设置恢复为默认值吗？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var fresh = new AppConfig();
        CopyInto(fresh, _cfg);
        _cfg.Save();

        _loading = true;
        LoadFromConfig();
        _loading = false;
        AppLogger.Info("设置已恢复默认值");
    }

    private static void CopyInto(AppConfig from, AppConfig to)
    {
        to.SavePanelUrl = from.SavePanelUrl;
        to.SaveApiKey = from.SaveApiKey;
        to.AutoConnectOnStart = from.AutoConnectOnStart;
        to.IgnoreCertificateErrors = from.IgnoreCertificateErrors;
        to.LogEnabled = from.LogEnabled;
        to.LogLevel = from.LogLevel;
        to.LogHttpBody = from.LogHttpBody;
        to.LogMaxSizeMb = from.LogMaxSizeMb;
        to.LogKeepFiles = from.LogKeepFiles;
        to.PerformanceMonitorEnabled = from.PerformanceMonitorEnabled;
        to.PerformanceIntervalValue = from.PerformanceIntervalValue;
        to.PerformanceIntervalUnit = from.PerformanceIntervalUnit;
        to.ShowErrorDetailDialog = from.ShowErrorDetailDialog;
        to.InstancePageSize = from.InstancePageSize;
        to.FilePageSize = from.FilePageSize;
        to.LogViewerLines = from.LogViewerLines;
    }

    private void OpenConfigButton_Click(object sender, RoutedEventArgs e) => AppConfig.OpenConfigFolder();

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}