using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCSMServerManager;

/// <summary>轮询周期的单位。序列化为字符串，便于手工编辑配置文件。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum IntervalUnit
{
    秒,
    分,
    时
}

/// <summary>
/// 安装目录下的 config.json 配置。
/// 约定：配置文件路径固定为程序所在目录，读取失败时回退到默认值并给出提示。
/// </summary>
public sealed class AppConfig
{
    [JsonIgnore]
    public static string ConfigPath { get; } = Path.Combine(AppContext.BaseDirectory, "config.json");

    [JsonIgnore]
    public static string LogDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "logs");

    // ---------- 连接 ----------
    /// <summary>是否记住面板地址。</summary>
    public bool SavePanelUrl { get; set; } = true;

    /// <summary>是否把 API Key 记住到配置文件。仅在用户明确同意时开启。</summary>
    public bool SaveApiKey { get; set; }

    /// <summary>记住的面板地址。</summary>
    public string LastPanelUrl { get; set; } = "http://localhost:23333";

    /// <summary>
    /// 记住的 API Key 明文，仅存在于内存中，绝不序列化到磁盘。
    /// 落盘请使用 <see cref="LastApiKeyProtected"/>。
    /// </summary>
    [JsonIgnore]
    public string LastApiKey { get; set; } = "";

    /// <summary>
    /// 落盘的 API Key：Windows DPAPI（CryptProtectData）加密后的 Base64。
    /// 只有当前 Windows 用户能解密，换用户或重置账户后会自动失效并要求重新填写。
    /// </summary>
    public string LastApiKeyProtected { get; set; } = "";

    /// <summary>忽略 HTTPS 证书校验（不安全）。</summary>
    public bool IgnoreCertificateErrors { get; set; }

    // ---------- 日志 ----------
    public bool LogEnabled { get; set; } = true;

    /// <summary>debug / info / warn / error。</summary>
    public string LogLevel { get; set; } = "信息";

    /// <summary>单个日志文件最大体积（MB），超出后轮转。</summary>
    public int LogMaxSizeMb { get; set; } = 8;

    /// <summary>最多保留多少个历史日志文件。</summary>
    public int LogKeepFiles { get; set; } = 14;

    /// <summary>是否把每次 HTTP 请求的完整响应写入日志。</summary>
    public bool LogHttpBody { get; set; }

    // ---------- 性能轮询 ----------
    public bool PerformanceMonitorEnabled { get; set; } = true;

    public IntervalUnit PerformanceIntervalUnit { get; set; } = IntervalUnit.秒;

    public int PerformanceIntervalValue { get; set; } = 10;

    // ---------- 界面 ----------
    /// <summary>接口出错时是否自动弹出可展开详情的错误对话框。</summary>
    public bool ShowErrorDetailDialog { get; set; } = true;

    /// <summary>启动时自动用记住的面板地址与密钥连接。</summary>
    public bool AutoConnectOnStart { get; set; }

    public int InstancePageSize { get; set; } = 20;

    public int FilePageSize { get; set; } = 50;

    /// <summary>每页显示的日志行数上限。</summary>
    public int LogViewerLines { get; set; } = 5000;

    /// <summary>按秒/分/时把自定义周期换算成实际秒数，最小 1 秒。</summary>
    [JsonIgnore]
    public int PerformanceIntervalSeconds
    {
        get
        {
            var v = PerformanceIntervalValue <= 0 ? 1 : PerformanceIntervalValue;
            return PerformanceIntervalUnit switch
            {
                IntervalUnit.分 => v * 60,
                IntervalUnit.时 => v * 3600,
                _ => v
            };
        }
    }

    /// <summary>周期的中文描述，例如「每 10 秒」。</summary>
    [JsonIgnore]
    public string PerformanceIntervalText => $"每 {PerformanceIntervalValue} {PerformanceIntervalUnit}";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>读取配置文件；不存在或损坏时返回默认配置并记录原因。</summary>
    public static AppConfig Load(out string? warning)
    {
        var notices = new List<string>();
        warning = null;

        try
        {
            if (!File.Exists(ConfigPath))
            {
                var fresh = new AppConfig();
                fresh.Save();
                return fresh;
            }

            var text = File.ReadAllText(ConfigPath, Encoding.UTF8);
            var cfg = JsonSerializer.Deserialize<AppConfig>(text, Options);
            if (cfg is null)
            {
                notices.Add("配置文件内容为空，已回退到默认配置。");
                return new AppConfig();
            }

            cfg.RestoreApiKey(text, notices);
            warning = notices.Count == 0 ? null : string.Join("\n", notices);
            return cfg;
        }
        catch (JsonException ex)
        {
            notices.Add($"配置文件格式错误（{ex.Message}），已回退到默认配置。");
            warning = string.Join("\n", notices);
            return new AppConfig();
        }
        catch (Exception ex)
        {
            notices.Add("读取配置文件失败：" + ex.Message);
            warning = string.Join("\n", notices);
            return new AppConfig();
        }
    }

    /// <summary>
    /// 恢复 API Key：
    /// 优先读 DPAPI 密文；读不到时再按旧版格式从原始 JSON 里找明文并立即迁移加密。
    /// </summary>
    private void RestoreApiKey(string rawJson, List<string> notices)
    {
        if (!SaveApiKey)
        {
            LastApiKey = "";
            LastApiKeyProtected = "";
            return;
        }

        if (!string.IsNullOrEmpty(LastApiKeyProtected))
        {
            var plain = Dpapi.Unprotect(LastApiKeyProtected);
            if (!string.IsNullOrEmpty(plain))
            {
                LastApiKey = plain;
                return;
            }

            LastApiKeyProtected = "";
            LastApiKey = "";
            notices.Add("保存的 API Key 无法解密（可能更换了 Windows 用户或系统账户被重置），请重新填写。");
            return;
        }

        var legacy = TryReadLegacyPlainKey(rawJson);
        if (string.IsNullOrEmpty(legacy))
        {
            LastApiKey = "";
            return;
        }

        LastApiKey = legacy;
        notices.Add("检测到旧版本以明文保存的 API Key，已改用 Windows DPAPI 加密保存。");
        // 立刻重写一次配置，把磁盘上的明文换成密文。
        Save();
    }

    /// <summary>从旧版配置文本里取出明文 "LastApiKey"。</summary>
    private static string? TryReadLegacyPlainKey(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "LastApiKey", StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) return null;
                return prop.Value.GetString();
            }
        }
        catch (JsonException)
        {
            // 配置损坏时已经在外层提示过了，这里不重复报错。
        }
        return null;
    }

    /// <summary>
    /// 写回配置文件。关闭「保存 API Key」时不会把密钥落盘；
    /// 开启时只写 DPAPI 密文，DPAPI 不可用则宁可不记，也绝不回退成明文。
    /// </summary>
    public void Save()
    {
        try
        {
            if (SaveApiKey)
            {
                var cipher = Dpapi.Protect(LastApiKey);
                if (string.IsNullOrEmpty(cipher))
                {
                    LastApiKeyProtected = "";
                    AppLogger.Warn("API Key 加密失败，本次不会把密钥写入配置文件。");
                }
                else
                {
                    LastApiKeyProtected = cipher;
                }
            }
            else
            {
                LastApiKeyProtected = "";
                LastApiKey = "";
            }

            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("写入配置文件失败：" + ex.Message);
        }
    }

    /// <summary>打开配置所在文件夹。</summary>
    public static void OpenConfigFolder()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("打开安装目录失败：" + ex.Message);
        }
    }
}
