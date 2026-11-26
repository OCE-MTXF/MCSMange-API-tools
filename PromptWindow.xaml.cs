using System.Windows;

namespace MCSMServerManager;

/// <summary>轻量单行输入对话框，用于新建文件/文件夹、重命名、下载链接等场景。</summary>
public partial class PromptWindow : Window
{
    public PromptWindow(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = App.BrandedTitle(title);
        PromptText.Text = prompt;
        InputBox.Text = initial;
        InputBox.CaretIndex = initial.Length;
        InputBox.SelectAll();
        Loaded += (_, _) => InputBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}