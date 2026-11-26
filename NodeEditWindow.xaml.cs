using System.Windows;

namespace MCSMServerManager;

/// <summary>添加守护节点对话框，字段对应 POST /api/service/remote_service 的请求体。</summary>
public partial class NodeEditWindow : Window
{
    public NodeEditWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => IpBox.Focus();
    }

    /// <summary>节点 IP 或域名。</summary>
    public string Ip => IpBox.Text.Trim();

    /// <summary>守护进程端口。</summary>
    public int Port => int.TryParse(PortBox.Text.Trim(), out var port) ? port : -1;

    /// <summary>备注名称，为空时列表会回退显示 IP:端口。</summary>
    public string Remarks => RemarksBox.Text.Trim();

    /// <summary>实例前缀，可为空。</summary>
    public string Prefix => PrefixBox.Text.Trim();

    /// <summary>守护进程 daemon 侧的 ipc 密码。</summary>
    public string NodeApiKey => ApiKeyBox.Password.Trim();

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (Ip.Length == 0)
        {
            MessageBox.Show(this, "请填写节点 IP 或域名。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Port is < 1 or > 65535)
        {
            MessageBox.Show(this, "端口需为 1-65535 之间的整数。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (NodeApiKey.Length == 0)
        {
            MessageBox.Show(this, "请填写节点密钥（daemon 配置中的 ipc 密码）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}