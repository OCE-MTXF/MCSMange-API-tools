using System.Windows;
using System.Windows.Threading;

namespace MCSMServerManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>产品名称，显示在各窗口标题栏。</summary>
    public const string AppTitle = "MCSM 服务器管理器";

    /// <summary>标题栏署名。</summary>
    public const string Brand = "by OCE_MTXF BUG联系oce_mtxf@outlook.com";

    /// <summary>应用版本号，以程序集版本为准，避免与 csproj 漂移。</summary>
    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";

    /// <summary>
    /// 生成带署名的窗口标题。传入前缀时形如
    /// 「实例日志 - MCSM 服务器管理器 by OCE_MTXF BUG联系oce_mtxf@outlook.com」，
    /// 不传时形如「MCSM 服务器管理器 by OCE_MTXF BUG联系oce_mtxf@outlook.com」。
    /// </summary>
    public static string BrandedTitle(string? prefix = null)
        => string.IsNullOrWhiteSpace(prefix) ? $"{AppTitle} {Brand}" : $"{prefix} - {AppTitle} {Brand}";

    /// <summary>全局配置，启动时从安装目录的 config.json 读取。</summary>
    public static AppConfig Config { get; private set; } = new();

    /// <summary>配置文件读取过程中的告警，由主窗体在启动后提示一次。</summary>
    public static string? ConfigWarning { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? warning;
        Config = AppConfig.Load(out warning);
        ConfigWarning = warning;
        AppLogger.Init(Config);
        AppLogger.RegisterSecret(Config.LastApiKey);

        AppLogger.Info($"{AppTitle} {Brand} v{Version} 启动");
        AppLogger.Info("程序目录：" + AppContext.BaseDirectory);
        AppLogger.Info("配置文件：" + AppConfig.ConfigPath);
        AppLogger.Info("日志文件：" + AppLogger.CurrentLogPath);
        if (ConfigWarning is not null) AppLogger.Warn(ConfigWarning);

        // 把没被捕获的异常也写进日志，避免「什么都没发生」的静默失败
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("界面线程未处理异常：" + args.Exception.Message, args.Exception);
            args.Handled = true;
            MessageBox.Show(
                "程序遇到未处理的错误，详情已写入日志。\n\n" + args.Exception.Message,
                "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLogger.Error("后台线程未处理异常：" + args.ExceptionObject, args.ExceptionObject as Exception);
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("程序退出。");
        base.OnExit(e);
    }
}