using System.Windows;

namespace MCSMServerManager;

public partial class LogWindow : Window
{
    public LogWindow(string title, string log)
    {
        InitializeComponent();
        TitleText.Text = $"实例日志 - {title}";
        LogBox.Text = log;
        LogBox.CaretIndex = LogBox.Text.Length;
        LogBox.ScrollToEnd();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(LogBox.Text)) return;
        try
        {
            Clipboard.SetText(LogBox.Text);
        }
        catch (Exception ex)
        {
            // 剪贴板常被其它进程短暂占用，直接抛 COM 异常会让整个复制功能不可用。
            MessageBox.Show(this, "复制失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
