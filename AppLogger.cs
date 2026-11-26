using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace MCSMServerManager;

public enum LogLevel
{
    调试,
    信息,
    警告,
    错误
}

/// <summary>
/// 文件日志。程序启动、用户操作、HTTP 请求与响应、异常都会写入安装目录 logs/ 下。
/// 所有写入前都会脱敏，确保 API Key 不会落盘。
/// </summary>
public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Recent = new();
    private static AppConfig? _config;
    private static string? _currentPath;
    private static string _secret = "";

    /// <summary>日志级别变更事件，供界面实时刷新。</summary>
    public static event Action<string>? LineAppended;

    /// <summary>供界面显示的最近日志行。</summary>
    public static IReadOnlyList<string> RecentLines => Recent.ToArray();

    /// <summary>今日日志文件路径。</summary>
    public static string CurrentLogPath => _currentPath ?? Path.Combine(AppConfig.LogDirectory, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Init(AppConfig config)
    {
        _config = config;
        lock (Gate)
        {
            Recent.Clear();
            try
            {
                Directory.CreateDirectory(AppConfig.LogDirectory);
                _currentPath = Path.Combine(AppConfig.LogDirectory, $"app-{DateTime.Now:yyyy-MM-dd}.log");
                RotateIfNeeded();
            }
            catch
            {
                _currentPath = null;
            }
        }
    }

    /// <summary>登记需要脱敏的密钥，之后所有日志都会替换为 ***。</summary>
    public static void RegisterSecret(string? secret)
    {
        lock (Gate) { _secret = secret ?? ""; }
    }

    public static void Debug(string message) => Write(LogLevel.调试, message, null);
    public static void Info(string message) => Write(LogLevel.信息, message, null);
    public static void Warn(string message) => Write(LogLevel.警告, message, null);

    public static void Error(string message, Exception? ex = null)
        => Write(LogLevel.错误, message, ex);

    /// <summary>记录一次用户操作，便于事后回溯。</summary>
    public static void Action(string description)
        => Write(LogLevel.信息, "[操作] " + description, null);

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        var cfg = _config;
        if (cfg is null || !cfg.LogEnabled) return;

        if (level < ParseLevel(cfg.LogLevel)) return;

        var text = new StringBuilder();
        text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        text.Append(" [").Append(level).Append("] ");
        text.Append(Sanitize(message));

        if (ex is not null)
        {
            text.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(Sanitize(ex.Message));
            if (cfg.LogLevel == LogLevel.调试.ToString())
                text.AppendLine().Append(ex.StackTrace);
        }

        var line = text.ToString();

        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > 2000) Recent.TryDequeue(out _);

            if (_currentPath is null) return;
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_currentPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // 日志写入失败不应影响主流程
            }
        }

        LineAppended?.Invoke(line);
    }

    private static LogLevel ParseLevel(string? text) => text switch
    {
        "调试" => LogLevel.调试,
        "警告" => LogLevel.警告,
        "错误" => LogLevel.错误,
        _ => LogLevel.信息
    };

    /// <summary>把 API Key 等敏感串替换掉。</summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 先遮蔽查询串里的敏感参数：即便没有登记密钥，也能防止 URL 泄露。
        // 这里必须用 MaskQueryValue 而不是字面量替换，否则会与下面的密钥替换叠加，
        // 把 apikey=*** 变成 apikey=******* 这种随层级增长的花括号串。
        text = MaskQueryValue(text, "apikey=");
        text = MaskQueryValue(text, "token=");

        // 控制台命令经由 ?command= 传输，可能含 op / login / password 等凭据
        text = MaskQueryValue(text, "command=");

        // 最后按已登记的密钥值做全局替换，覆盖响应正文、日志等非 URL 位置。
        var secret = _secret;
        if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "***", StringComparison.Ordinal);

        return text;
    }

    /// <summary>
    /// 把查询串中指定参数的值替换为 ***，保留参数名以便排查。
    /// 实现上单向推进游标而非「重新从头搜索」，因此本函数是幂等的：
    /// 对已遮蔽的文本再次调用不会原地打转（早期实现用 while + 重新 IndexOf，
    /// 会反复匹配自己刚写下的 *** 而死循环）。
    /// </summary>
    public static string MaskQueryValue(string text, string key)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return text;

        var sb = new StringBuilder(text.Length);
        var searchFrom = 0;
        var copiedTo = 0;

        while (searchFrom <= text.Length - key.Length)
        {
            var at = text.IndexOf(key, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;

            var valueStart = at + key.Length;
            var valueEnd = text.IndexOfAny(MaskDelimiters, valueStart);
            if (valueEnd < 0) valueEnd = text.Length;

            sb.Append(text, copiedTo, at - copiedTo).Append(key).Append("***");

            copiedTo = valueEnd;
            searchFrom = valueEnd > valueStart ? valueEnd : valueStart + 1;
        }

        sb.Append(text, copiedTo, text.Length - copiedTo);
        return sb.ToString();
    }

    private static readonly char[] MaskDelimiters = { '&', ' ', '\r', '\n', '"', '\'', '}', ']' };

    /// <summary>超过体积上限时按日期改名，并清理过多历史文件。</summary>
    private static void RotateIfNeeded()
    {
        var cfg = _config;
        if (cfg is null || _currentPath is null) return;

        try
        {
            if (File.Exists(_currentPath))
            {
                var sizeMb = new FileInfo(_currentPath).Length / 1024d / 1024d;
                if (sizeMb >= Math.Max(1, cfg.LogMaxSizeMb))
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    var rotated = Path.Combine(AppConfig.LogDirectory,
                        Path.GetFileNameWithoutExtension(_currentPath) + "-" + stamp + ".log");
                    File.Move(_currentPath, rotated, true);
                    CleanupOldLogs(cfg.LogKeepFiles);
                }
            }
        }
        catch
        {
            // 轮转失败不影响写入
        }
    }

    private static void CleanupOldLogs(int keep)
    {
        try
        {
            var keepCount = Math.Max(1, keep);
            var files = new DirectoryInfo(AppConfig.LogDirectory)
                .GetFiles("app-*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(keepCount);

            foreach (var f in files)
            {
                try { f.Delete(); } catch { /* 忽略单个文件删除失败 */ }
            }
        }
        catch
        {
            // 清理失败不影响运行
        }
    }

    /// <summary>读取全部历史日志，供日志查看器展示。</summary>
    public static string ReadAllLogs(int maxLines)
    {
        try
        {
            if (!Directory.Exists(AppConfig.LogDirectory)) return "（暂无日志）";

            var files = new DirectoryInfo(AppConfig.LogDirectory)
                .GetFiles("app-*.log")
                .OrderByDescending(f => f.Name)
                .ToList();

            var sb = new StringBuilder();
            foreach (var f in files)
            {
                sb.Append("===== ").Append(f.Name).Append(" =====").AppendLine();
                try
                {
                    var lines = File.ReadAllLines(f.FullName, Encoding.UTF8);
                    foreach (var l in lines.Take(Math.Max(1, maxLines))) sb.AppendLine(l);
                }
                catch (Exception ex)
                {
                    sb.AppendLine("(读取失败: " + ex.Message + ")");
                }
            }
            return sb.Length == 0 ? "（暂无日志）" : sb.ToString();
        }
        catch (Exception ex)
        {
            return "读取日志失败：" + ex.Message;
        }
    }
}
