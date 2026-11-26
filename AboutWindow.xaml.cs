using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MCSMServerManager;

/// <summary>关于、开源协议与鸣谢。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        TitleText.Text = App.AppTitle;
        VersionText.Text = $"版本 {App.Version} · {App.Brand}";
    }

    private static string LicenseSummary => string.Join(Environment.NewLine, new[]
    {
        "MCSM 服务器管理器 v" + App.Version,
        "作者：OCE_MTXF（oce_mtxf@outlook.com）",
        "许可证：Apache License 2.0",
        "",
        "本软件依赖 MCSManager 提供的公开 HTTP 接口（MCSManager 采用 Apache-2.0 协议）。",
        "界面框架：.NET 9 / WPF（MIT，© Microsoft Corporation）。",
        "界面布局与交互参考 MCSManager 网页版面板。",
        "",
        "可展开的错误弹窗交互思路参考自 PCL（Plain Craft Launcher）。",
        "PCL 由成都瓜皮龙科技有限公司（Meloong）以《PCL 分发有限许可》发布，该许可并非开源许可。",
        "依《PCL 存储库合理使用指南》，本软件属于轻度使用：仅参考了极小部分内容，",
        "且实质功能（通过 MCSManager 接口管理游戏服务器）与 PCL（启动 Minecraft）无重叠。",
        "轻度使用要求署名且不得混淆：致谢 PCL；同时本软件不使用 PCL 的名称与品牌标识，",
        "也不是 PCL 的分支、魔改或二次创作产物。"
    });
    private void OpenLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        if (File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                Warn("打开失败：" + ex.Message);
                return;
            }
        }

        MessageBox.Show(this,
            "安装目录中没有找到 LICENSE 文件。\n\n完整许可证文本可在项目源码目录中查看。",
            "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LicenseSummary);
            MessageBox.Show(this, "许可证信息已复制到剪贴板。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Warn("复制失败：" + ex.Message);
        }
    }

    private static void Warn(string text) => MessageBox.Show(text, "提示",
        MessageBoxButton.OK, MessageBoxImage.Warning);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
