using System.Windows;

namespace MCSMServerManager;

/// <summary>双输入对话框，用于 URL 下载（下载地址 + 保存路径）这类需要一次填完两个字段的场景。</summary>
public partial class Prompt2Window : Window
{
    public Prompt2Window(string title, string prompt1, string initial1, string prompt2, string initial2)
    {
        InitializeComponent();
        Title = App.BrandedTitle(title);
        Prompt1Text.Text = prompt1;
        InputBox1.Text = initial1;
        InputBox1.CaretIndex = initial1.Length;
        Prompt2Text.Text = prompt2;
        InputBox2.Text = initial2;
        InputBox2.CaretIndex = initial2.Length;
        Loaded += (_, _) => InputBox1.Focus();
    }

    /// <summary>第一个输入框的内容。</summary>
    public string Value1 => InputBox1.Text.Trim();

    /// <summary>第二个输入框的内容。</summary>
    public string Value2 => InputBox2.Text.Trim();

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}