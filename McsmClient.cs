using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace MCSMServerManager;

/// <summary>
/// MCSManager 面板/守护进程 HTTP 客户端。
/// 所有响应统一为 { status, data, time } 包结构，status == 200 视为成功。
/// </summary>
public sealed class McsmClient : IDisposable
{
    /// <summary>MCSManager 权限中间件优先读取的 API Key 头（见 panel/src/app/middleware/permission.ts）。</summary>
    private const string ApiKeyHeader = "x-request-api-key";

    private readonly HttpClient _http;
    private string _baseUrl = "";
    private bool _ignoreCertificateErrors;

    /// <summary>
    /// 兼容旧版面板：部分版本不识别 <see cref="ApiKeyHeader"/>，首次收到 403 后
    /// 自动改用 ?apikey= 查询串重试一次，并在本次会话内记住该选择。
    /// </summary>
    private bool _apiKeyViaQuery;

    public string ApiKey { get; set; } = "";
    public string Token { get; set; } = "";

    /// <summary>
    /// 是否放行无效的 HTTPS 证书。默认关闭（等同系统校验）。
    /// 仅在面板使用自签名证书、且用户在连接栏主动勾选时才会置为 true。
    /// </summary>
    public bool IgnoreCertificateErrors
    {
        get => _ignoreCertificateErrors;
        set => _ignoreCertificateErrors = value;
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            var normalized = NormalizeBaseUrl(value);

            // 换面板时必须重置回退标记：新面板很可能支持请求头认证，
            // 若沿用旧面板留下的标记，就会把密钥继续放进 URL，白白承担泄露风险。
            if (!string.Equals(normalized, _baseUrl, StringComparison.OrdinalIgnoreCase))
                _apiKeyViaQuery = false;

            _baseUrl = normalized;
        }
    }

    public McsmClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        // 该回调一旦设置就会接管校验，因此默认分支必须显式放行 SslPolicyErrors.None，
        // 等价于系统校验；只有用户显式开启不安全模式才接受任意证书。
        handler.ServerCertificateCustomValidationCallback = (_, _, _, errors) =>
            _ignoreCertificateErrors || errors == SslPolicyErrors.None;

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _http.DefaultRequestHeaders.Add("User-Agent", "MCSMServerManager/1.0");
    }

    public static string NormalizeBaseUrl(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }
        return url.TrimEnd('/');
    }

    public async Task<T> GetAsync<T>(string path, IDictionary<string, string?>? query = null, CancellationToken ct = default)
        => await SendAsync<T>(HttpMethod.Get, path, null, query, ct).ConfigureAwait(false);

    public async Task<T> PostAsync<T>(string path, object? body = null, IDictionary<string, string?>? query = null, CancellationToken ct = default)
        => await SendAsync<T>(HttpMethod.Post, path, body, query, ct).ConfigureAwait(false);

    public async Task<T> PutAsync<T>(string path, object? body = null, IDictionary<string, string?>? query = null, CancellationToken ct = default)
        => await SendAsync<T>(HttpMethod.Put, path, body, query, ct).ConfigureAwait(false);

    public async Task<T> DeleteAsync<T>(string path, object? body = null, IDictionary<string, string?>? query = null, CancellationToken ct = default)
        => await SendAsync<T>(HttpMethod.Delete, path, body, query, ct).ConfigureAwait(false);

    /// <summary>一次完整的接口往返记录，供「查看原始响应」与日志使用。</summary>
    public sealed record ApiExchange(
        string Method,
        string Path,
        string Url,
        int Status,
        long ElapsedMs,
        string? Body,
        string? Error)
    {
        /// <summary>是否成功（HTTP 200 且信封 status 为 200）。</summary>
        public bool Ok => Error is null && Status == 200;

        /// <summary>HTTP 状态的中文描述。</summary>
        public string StatusText => Error is not null
            ? "请求失败"
            : Status switch
            {
                200 => "成功",
                400 => "请求参数不正确",
                403 => "权限不足或密钥被拒绝",
                404 => "接口不存在",
                >= 500 => "面板内部错误",
                _ => "HTTP " + Status
            };
    }

    /// <summary>最近一次接口往返。</summary>
    public ApiExchange? LastExchange { get; private set; }

    /// <summary>每次接口往返完成后触发，供界面实时刷新与详情弹窗使用。</summary>
    public event Action<ApiExchange>? ExchangeCompleted;

    private void Publish(HttpMethod method, string path, string url, int status, long elapsed, string? body, string? error)
    {
        var exchange = new ApiExchange(
            method.Method, path, AppLogger.Sanitize(url), status, elapsed,
            body is null ? null : AppLogger.Sanitize(body), error);

        LastExchange = exchange;

        var summary = exchange.Error is not null
            ? $"{exchange.Method} {exchange.Path} 失败：{exchange.Error}"
            : $"{exchange.Method} {exchange.Path} -> {exchange.Status} {exchange.StatusText}，耗时 {exchange.ElapsedMs} 毫秒";

        if (exchange.Ok) AppLogger.Info(summary);
        else AppLogger.Error(summary);

        if (body is not null && _logHttpBody)
            AppLogger.Debug("响应内容：" + AppLogger.Sanitize(body));

        ExchangeCompleted?.Invoke(exchange);
    }

    private bool _logHttpBody;

    /// <summary>是否把完整响应体写入日志，由配置文件控制。</summary>
    public bool LogHttpBody
    {
        get => _logHttpBody;
        set => _logHttpBody = value;
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body,
        IDictionary<string, string?>? query, CancellationToken ct)
    {
        if (BaseUrl.Length == 0) throw new McsmException("尚未配置面板地址。");

        var (status, text) = await SendOnceAsync(method, path, body, query, ct, _apiKeyViaQuery).ConfigureAwait(false);

        if ((int)status == 403 && !_apiKeyViaQuery && ApiKey.Length > 0)
        {
            _apiKeyViaQuery = true;

            // 回退意味着密钥进入 URL 查询串，可能被前置的 Nginx / 反向代理记入访问日志。
            // 这里必须留下明确痕迹，供界面提示与日志追溯。
            AppLogger.Warn(
                "面板拒绝了请求头认证，已回退为 ?apikey= 查询串发送密钥以兼容旧版面板。" +
                "此后该密钥可能出现在面板前置反向代理的访问日志中，建议升级面板以改用请求头。");
            ApiKeyFallbackEngaged?.Invoke();

            (status, text) = await SendOnceAsync(method, path, body, query, ct, true).ConfigureAwait(false);
        }

        return Interpret<T>(status, text, ct);
    }

    /// <summary>本次会话是否已回退到把 API Key 放进 URL 查询串的兼容模式。</summary>
    public bool ApiKeyViaQuery => _apiKeyViaQuery;

    /// <summary>首次回退到查询串发送密钥时触发，供界面提示用户该风险。</summary>
    public event Action? ApiKeyFallbackEngaged;

    private async Task<(HttpStatusCode Status, string Text)> SendOnceAsync(
        HttpMethod method, string path, object? body, IDictionary<string, string?>? query,
        CancellationToken ct, bool apiKeyViaQuery)
    {
        var url = BaseUrl + (path.StartsWith('/') ? path : "/" + path);

        var parameters = new List<string>();
        if (query != null)
        {
            foreach (var kv in query)
            {
                if (kv.Value is null) continue;
                parameters.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
            }
        }

        if (!string.IsNullOrEmpty(Token))
            parameters.Add("token=" + Uri.EscapeDataString(Token));
        if (apiKeyViaQuery && !string.IsNullOrEmpty(ApiKey))
            parameters.Add("apikey=" + Uri.EscapeDataString(ApiKey));

        if (parameters.Count > 0)
            url += (url.Contains('?') ? "&" : "?") + string.Join('&', parameters);

        using var request = new HttpRequestMessage(method, url);

        // 凭据优先走请求头，避免泄漏到面板侧访问日志 / 反向代理日志。
        if (!apiKeyViaQuery && !string.IsNullOrEmpty(ApiKey))
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, ApiKey);

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, Json.Options);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        else if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            request.Content = new StringContent("", Encoding.UTF8, "application/json");
        }

        AppLogger.Debug($"{method} {path} -> {AppLogger.Sanitize(url)}");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            watch.Stop();
            Publish(method, path, url, (int)response.StatusCode, watch.ElapsedMilliseconds, text, null);
            return (response.StatusCode, text);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            const string msg = "操作已取消。";
            Publish(method, path, url, 0, watch.ElapsedMilliseconds, null, msg);
            throw new McsmException(msg);
        }
        catch (TaskCanceledException)
        {
            var msg = $"请求超时：{_http.Timeout.TotalSeconds:0} 秒内没有收到面板响应。\n\n请确认面板已启动、地址与端口正确，且网络可达。";
            Publish(method, path, url, 0, watch.ElapsedMilliseconds, null, msg);
            throw new McsmException(msg);
        }
        catch (HttpRequestException ex)
        {
            var err = BuildTransportError(ex, ct);
            Publish(method, path, url, 0, watch.ElapsedMilliseconds, null, err.Message);
            throw err;
        }
        catch (IOException ex)
        {
            var err = BuildTransportError(ex, ct);
            Publish(method, path, url, 0, watch.ElapsedMilliseconds, null, err.Message);
            throw err;
        }
    }

    private T Interpret<T>(HttpStatusCode status, string text, CancellationToken ct)
    {
        McsmEnvelope<JsonElement>? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<McsmEnvelope<JsonElement>>(text, Json.Options);
        }
        catch (JsonException)
        {
            // 非 JSON 响应：权限中间件等场景会直接返回纯文本，交给下面的 HTTP 分支处理。
        }

        if (envelope is null)
            throw new McsmException(DescribeHttpFailure((int)status, text));

        if (envelope.Status != 200)
        {
            var message = envelope.Data.ValueKind == JsonValueKind.String
                ? envelope.Data.GetString()
                : envelope.Data.ValueKind != JsonValueKind.Undefined && envelope.Data.ValueKind != JsonValueKind.Null
                    ? envelope.Data.ToString()
                    : null;
            throw new McsmException($"接口返回错误 {envelope.Status}: {Redact(message) ?? "未知错误"}");
        }

        if (typeof(T) == typeof(string))
        {
            object? asString = envelope.Data.ValueKind == JsonValueKind.String
                ? envelope.Data.GetString()
                : envelope.Data.ValueKind == JsonValueKind.Undefined || envelope.Data.ValueKind == JsonValueKind.Null
                    ? null
                    : envelope.Data.ToString();
            return (T)(asString ?? (object)string.Empty);
        }

        if (typeof(T) == typeof(bool))
        {
            bool ok = envelope.Data.ValueKind == JsonValueKind.True;
            return (T)(object)ok;
        }

        if (envelope.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default!;

        var result = envelope.Data.Deserialize<T>(Json.Options);
        if (result is null) throw new McsmException("接口返回数据为空。");
        return result;
    }

    /// <summary>
    /// 把传输层异常翻译成可执行的排查提示，并剥掉可能含 apikey 的原始文本。
    /// .NET 9 的 <see cref="HttpRequestException"/> 会把底层套接字错误包在内层，
    /// 且其 Message 恒为英文占位文案，因此必须沿异常链下钻，并结合
    /// <see cref="HttpRequestError"/> 判定真正原因。
    /// </summary>
    private McsmException BuildTransportError(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new McsmException("操作已取消。");

        // 先在整条异常链上找最具体的错误：SocketException 能给出端口/防火墙级别的提示。
        for (var probe = ex; probe is not null; probe = probe.InnerException)
        {
            if (probe is SocketException socket) return new McsmException(DescribeSocket(socket));
        }

        if (ex is HttpRequestException http)
        {
            var reason = DescribeHttpErrorKind(http.HttpRequestError);
            var detail = LooksLikePlaceholder(http.Message) ? "" : "\n\n技术细节：" + Redact(http.Message);
            return new McsmException(reason + detail);
        }

        var root = ex;
        while (root.InnerException is not null) root = root.InnerException;

        if (root is AuthenticationException)
            return new McsmException(
                "TLS 握手失败：" + Redact(root.Message) +
                "\n\n若面板使用自签名证书，请勾选「忽略证书错误（不安全）」后重试。");

        var fallback = Redact(root.Message);
        return new McsmException(
            LooksLikePlaceholder(fallback) || string.IsNullOrWhiteSpace(fallback)
                ? DescribeHttpErrorKind(HttpRequestError.Unknown)
                : "无法连接面板。\n\n技术细节：" + fallback);
    }

    /// <summary>把 .NET 的请求错误分类翻译成中文排查提示。</summary>
    private string DescribeHttpErrorKind(HttpRequestError kind)
    {
        var target = string.IsNullOrEmpty(_baseUrl) ? "面板" : _baseUrl;

        return kind switch
        {
            HttpRequestError.NameResolutionError =>
                $"找不到主机 {target}。请检查面板地址是否拼写正确，或本机 DNS 不可用。",

            HttpRequestError.ConnectionError =>
                $"无法连接到 {target}。\n\n请确认：\n" +
                "· MCSManager 面板已经启动\n" +
                "· 端口填写正确（面板默认 23333）\n" +
                "· 地址需带端口，例如 http://192.168.1.5:23333\n" +
                "· 若面板在另一台机器上，请确认防火墙放行了该端口",

            HttpRequestError.SecureConnectionError =>
                $"与 {target} 的 TLS 连接失败。\n\n若面板使用自签名证书，" +
                "请勾选「忽略证书错误（不安全）」后重试。",

            HttpRequestError.HttpProtocolError =>
                $"{target} 返回了无法解析的响应。\n\n" +
                "请确认该地址确实是 MCSManager 面板，而不是反向代理或普通网站。",

            HttpRequestError.ExtendedConnectNotSupported =>
                $"连接 {target} 失败：目标主机可达，但拒绝了 TCP 连接。请确认端口填写正确，且面板正在监听。",

            HttpRequestError.VersionNegotiationError =>
                $"与 {target} 协商 HTTP 协议版本失败。请确认面板版本不低于 9.x。",

            HttpRequestError.UserAuthenticationError =>
                $"面板要求用户身份验证。该地址可能是 MCSManager 的网页登录页，而不是 API 接口。",

            HttpRequestError.ProxyTunnelError =>
                $"通过代理连接 {target} 失败。若你并未配置代理，请在系统设置中关闭代理后重试。",

            HttpRequestError.InvalidResponse =>
                $"{target} 返回的响应格式非法。该端口上的服务可能不是 MCSManager 面板。",

            HttpRequestError.ResponseEnded =>
                $"与 {target} 的连接在收到完整响应前被中断。面板可能在处理请求时崩溃。",

            HttpRequestError.ConfigurationLimitExceeded =>
                $"请求 {target} 超出客户端限制（超时或重定向次数）。请稍后重试。",

            _ => $"无法连接 {target}。请确认面板已启动、地址与端口正确，且网络可达。"
        };
    }

    /// <summary>判断框架异常文案是否为无信息量的英文占位串，这类文本不展示给用户。</summary>
    private static bool LooksLikePlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        return text.Contains("An error occurred while sending the request", StringComparison.OrdinalIgnoreCase)
            || text.Contains("The SSL connection could not be established", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Error while copying content to a stream", StringComparison.OrdinalIgnoreCase)
            || text.Contains("The response ended prematurely", StringComparison.OrdinalIgnoreCase);
    }
    private string DescribeSocket(SocketException socket)
    {
        var target = string.IsNullOrEmpty(_baseUrl) ? "面板" : _baseUrl;

        return socket.SocketErrorCode switch
        {
            SocketError.ConnectionRefused =>
                $"连接被拒绝：{target} 上没有服务在监听。\n\n请确认：\n" +
                "· MCSManager 面板已经启动\n" +
                "· 端口填写正确（面板默认 23333）\n" +
                "· 地址需带端口，例如 http://192.168.1.5:23333",

            SocketError.TimedOut =>
                $"连接 {target} 超时。面板可能在另一台机器上且被防火墙拦截，或当前网络不可达。",

            SocketError.HostNotFound =>
                $"找不到主机 {target}。请检查面板地址是否拼写正确。",

            SocketError.NetworkUnreachable =>
                $"网络不可达，无法访问 {target}。",

            SocketError.ConnectionReset =>
                $"连接被 {target} 重置。请确认面板仍在运行。",

            SocketError.ConnectionAborted =>
                $"与 {target} 的连接被中止。多半是面板在返回完整响应前就断开了连接，" +
                $"例如面板进程崩溃、被反向代理超时切断，或响应体过大。",

            SocketError.HostUnreachable =>
                $"主机 {target} 不可达。请检查本机网络、VPN 与路由设置。",

            SocketError.TryAgain or SocketError.NoRecovery =>
                $"连接 {target} 失败：套接字层报错 {socket.SocketErrorCode}。" +
                $"这通常是临时性网络问题，请稍后重试。",

            // 兜底分支刻意以中文开头，并先给出与系统语言无关的枚举名；
            // OS 返回的 Message 受系统语言影响，只作为补充细节附在后面。
            _ => $"无法连接 {target}：套接字错误 {socket.SocketErrorCode}" +
                 $"系统信息：{Redact(socket.Message)}"
        };
    }

    private string DescribeHttpFailure(int status, string body)
    {
        var snippet = Redact(body.Length > 180 ? body[..180] + "…" : body);

        if (status == 403)
        {
            if (body.Contains("未开启API密钥创建功能") || body.Contains("API key creation feature", StringComparison.OrdinalIgnoreCase))
            {
                return "面板未启用 API 密钥功能。\n\n" +
                       "请在 MCSManager 面板的「系统设置」中开启 API 密钥创建功能，\n" +
                       "并确认该密钥属于管理员账号。";
            }

            if (body.Contains("密钥不正确") || body.Contains("权限不足", StringComparison.Ordinal))
                return "API 密钥被拒绝：密钥不正确或所属账号权限不足。\n\n请确认密钥完整无误，且所属账号权限满足该操作要求。";

            return $"面板拒绝访问（HTTP 403）。{snippet}";
        }

        if (status == 404)
            return $"接口不存在（HTTP 404）：{snippet}\n\n面板版本可能过旧，建议升级 MCSManager。";

        if (status >= 500)
            return $"面板内部错误（HTTP {status}）：{snippet}";

        return $"面板返回 HTTP {status}：{snippet}";
    }

    /// <summary>防止异常文本把 API Key 带进对话框或日志。</summary>
    private string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // 与 AppLogger.Sanitize 共用同一套遮蔽逻辑，避免两处实现出现不一致的漏网情况。
        text = AppLogger.MaskQueryValue(text, "apikey=");
        text = AppLogger.MaskQueryValue(text, "token=");

        if (ApiKey.Length > 0) text = text.Replace(ApiKey, "***", StringComparison.Ordinal);
        if (Token.Length > 0) text = text.Replace(Token, "***", StringComparison.Ordinal);
        return text;
    }

    public void Dispose() => _http.Dispose();
}
