using System.Windows;

namespace MCSMServerManager;

/// <summary>远程文本文件编辑器。保存动作由主窗体调用 API 完成，本窗体只负责返回编辑后的文本。</summary>
public partial class FileEditorWindow : Window
{
    public FileEditorWindow(string path, string content)
    {
        InitializeComponent();
        PathText.Text = path;
        EditorBox.Text = content;
        StateText.Text = $"共 {content.Length} 个字符";
        EditorBox.CaretIndex = 0;
        Loaded += (_, _) => EditorBox.Focus();
    }

    /// <summary>用户在编辑器中修改后的文本内容。</summary>
    public string EditedText => EditorBox.Text;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}