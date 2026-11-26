using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace MCSMServerManager;

/// <summary>
/// 仿 PCL 启动器的错误弹窗：先给一句话结论，再按需展开完整接口信息与原始 JSON。
/// </summary>
public partial class DetailDialog : Window
{
    private string _fullDetail = "";

    public DetailDialog(string headline, string subline, string detail,
                        bool isWarning = false, bool isError = true)
    {
        InitializeComponent();

        HeadlineText.Text = headline;
        SublineText.Text = subline;
        _fullDetail = detail;
        BodyBox.Text = detail;

        IconBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            isWarning ? "#D97706" : "#DC2626"));
        IconGlyph.Text = isWarning ? "" : "";

        // 有原始响应时默认展开，让用户一眼看到问题所在
        DetailExpander.IsExpanded = detail.Length > 0;
    }

    /// <summary>用一次接口往返构造错误弹窗，自动做 JSON 初步解析。</summary>
    public static void ShowExchange(Window? owner, McsmClient.ApiExchange exchange, string headline)
    {
        var parsed = JsonTools.Describe(exchange.Body);
        var dialog = new DetailDialog(
            headline,
            $"{exchange.Method} {exchange.Path} · {exchange.Status} {exchange.StatusText} · 耗时 {exchange.ElapsedMs} 毫秒",
            parsed.Pretty,
            isError: !exchange.Ok);

        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private void CopyDetailButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_fullDetail);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.LogDirectory);
            Process.Start(new ProcessStartInfo(AppConfig.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开日志目录失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}