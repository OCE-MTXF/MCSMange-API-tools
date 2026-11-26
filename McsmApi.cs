namespace MCSMServerManager;

/// <summary>MCSManager 面板 API 的强类型封装，端点依据官方 API 文档。</summary>
public sealed class McsmApi
{
    private readonly McsmClient _client;

    public McsmApi(McsmClient client) => _client = client;

    public McsmClient Client => _client;

    // ---------- 仪表盘 / 概览 ----------
    public Task<PanelOverview> OverviewAsync(CancellationToken ct = default)
        => _client.GetAsync<PanelOverview>("/api/overview", null, ct);

    // ---------- 认证 / 账户 ----------
    public Task<string> LoginAsync(string username, string password, CancellationToken ct = default)
        => _client.PostAsync<string>("/api/auth/login", new { username, password }, null, ct);

    public Task<UserInfo> CurrentUserAsync(CancellationToken ct = default)
        => _client.GetAsync<UserInfo>("/api/auth", null, ct);

    public Task<string> TokenAsync(CancellationToken ct = default)
        => _client.GetAsync<string>("/api/auth/token", null, ct);

    public Task<string> LogoutAsync(CancellationToken ct = default)
        => _client.GetAsync<string>("/api/auth/logout", null, ct);

    public Task<UserResult> SearchUsersAsync(string? keyword, int page, int pageSize, string? role, CancellationToken ct = default)
        => _client.GetAsync<UserResult>("/api/auth/search", new Dictionary<string, string?>
        {
            ["userName"] = keyword ?? string.Empty,
            ["page"] = page.ToString(),
            ["page_size"] = pageSize.ToString(),
            ["role"] = role ?? string.Empty
        }, ct);

    public Task<object> CreateUserAsync(string username, string password, int permission, CancellationToken ct = default)
        => _client.PostAsync<object>("/api/auth", new { username, password, permission }, null, ct);

    public Task<bool> DeleteUsersAsync(IEnumerable<string> uuids, CancellationToken ct = default)
        => _client.DeleteAsync<bool>("/api/auth", uuids.ToArray(), null, ct);

    // ---------- 节点 ----------
    public Task<List<NodeStatus>> NodesAsync(CancellationToken ct = default)
        => _client.GetAsync<List<NodeStatus>>("/api/service/remote_services_list", null, ct);

    public Task<List<RemoteOverview>> NodesWithInstancesAsync(CancellationToken ct = default)
        => _client.GetAsync<List<RemoteOverview>>("/api/service/remote_services", null, ct);

    public Task<string> AddNodeAsync(string ip, int port, string remarks, string apiKey, string prefix = "", CancellationToken ct = default)
        => _client.PostAsync<string>("/api/service/remote_service", new { ip, port, prefix, remarks, apiKey }, null, ct);

    public Task<bool> DeleteNodeAsync(string uuid, CancellationToken ct = default)
        => _client.DeleteAsync<bool>("/api/service/remote_service", null, new Dictionary<string, string?> { ["uuid"] = uuid }, ct);

    public Task<bool> ConnectNodeAsync(string uuid, CancellationToken ct = default)
        => _client.GetAsync<bool>("/api/service/link_remote_service", new Dictionary<string, string?> { ["uuid"] = uuid }, ct);

    // ---------- 实例 ----------
    public Task<PagedInstances> InstancesAsync(string daemonId, int page, int pageSize, string? keyword = null, CancellationToken ct = default)
        => _client.GetAsync<PagedInstances>("/api/service/remote_service_instances", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["page"] = page.ToString(),
            ["page_size"] = pageSize.ToString(),
            ["instance_name"] = string.IsNullOrWhiteSpace(keyword) ? null : keyword
        }, ct);

    public Task<InstanceDetail> InstanceAsync(string daemonId, string uuid, CancellationToken ct = default)
        => _client.GetAsync<InstanceDetail>("/api/instance", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid
        }, ct);

    public Task<object> StartInstanceAsync(string daemonId, string uuid, CancellationToken ct = default)
        => _client.GetAsync<object>("/api/protected_instance/open", Pair(daemonId, uuid), ct);

    public Task<object> StopInstanceAsync(string daemonId, string uuid, CancellationToken ct = default)
        => _client.GetAsync<object>("/api/protected_instance/stop", Pair(daemonId, uuid), ct);

    public Task<object> RestartInstanceAsync(string daemonId, string uuid, CancellationToken ct = default)
        => _client.GetAsync<object>("/api/protected_instance/restart", Pair(daemonId, uuid), ct);

    public Task<object> KillInstanceAsync(string daemonId, string uuid, CancellationToken ct = default)
        => _client.GetAsync<object>("/api/protected_instance/kill", Pair(daemonId, uuid), ct);

    public Task<bool> SendCommandAsync(string daemonId, string uuid, string command, CancellationToken ct = default)
        => _client.GetAsync<bool>("/api/protected_instance/command", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid,
            ["command"] = command
        }, ct);

    public Task<string> InstanceLogAsync(string daemonId, string uuid, CancellationToken ct = default)
        => _client.GetAsync<string>("/api/protected_instance/outputlog", Pair(daemonId, uuid), ct);

    public Task<List<string>> DeleteInstancesAsync(string daemonId, IEnumerable<string> uuids, bool deleteFile, CancellationToken ct = default)
        => _client.DeleteAsync<List<string>>("/api/instance", new { uuids = uuids.ToArray(), deleteFile },
            new Dictionary<string, string?> { ["daemonId"] = daemonId }, ct);

    // ---------- 文件管理 ----------
    public Task<FileListResult> ListFilesAsync(string daemonId, string uuid, string target, int page, int pageSize, string? filter = null, CancellationToken ct = default)
        => _client.GetAsync<FileListResult>("/api/files/list", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid,
            ["target"] = target,
            ["page"] = page.ToString(),
            ["page_size"] = pageSize.ToString(),
            // 实测 10.18.3：该参数必须存在（空串即可）。省略时面板会返回
            // {"items":[],"total":0} 且 HTTP 200——静默空列表，不会报错。
            ["file_name"] = filter ?? ""
        }, ct);

    public Task<string> ReadFileAsync(string daemonId, string uuid, string target, CancellationToken ct = default)
        => _client.PutAsync<string>("/api/files/", new { target },
            new Dictionary<string, string?> { ["daemonId"] = daemonId, ["uuid"] = uuid }, ct);

    public Task<bool> WriteFileAsync(string daemonId, string uuid, string target, string text, CancellationToken ct = default)
        => _client.PutAsync<bool>("/api/files/", new { target, text },
            new Dictionary<string, string?> { ["daemonId"] = daemonId, ["uuid"] = uuid }, ct);

    public Task<bool> DeleteFilesAsync(string daemonId, string uuid, IEnumerable<string> targets, CancellationToken ct = default)
        => _client.DeleteAsync<bool>("/api/files", new { targets = targets.ToArray() },
            new Dictionary<string, string?> { ["daemonId"] = daemonId, ["uuid"] = uuid }, ct);

    public Task<bool> MakeDirectoryAsync(string daemonId, string uuid, string target, CancellationToken ct = default)
        => _client.PostAsync<bool>("/api/files/mkdir", new { target },
            new Dictionary<string, string?> { ["daemonId"] = daemonId, ["uuid"] = uuid }, ct);

    public Task<bool> TouchFileAsync(string daemonId, string uuid, string target, CancellationToken ct = default)
        => _client.PostAsync<bool>("/api/files/touch", new { target },
            new Dictionary<string, string?> { ["daemonId"] = daemonId, ["uuid"] = uuid }, ct);

    public Task<bool> MoveFilesAsync(string daemonId, string uuid, IEnumerable<string[]> targets, CancellationToken ct = default)
        => _client.PutAsync<bool>("/api/files/move", new { targets = targets.ToArray() },
            new Dictionary<string, string?> { ["daemonId"] = daemonId, ["uuid"] = uuid }, ct);

    public Task<bool> CopyFilesAsync(string daemonId, string uuid, IEnumerable<string[]> targets, CancellationToken ct = default)
        => _client.PostAsync<bool>("/api/files/copy", new { targets = targets.ToArray() },
            new Dictionary<string, string?> { ["daemonId"] = daemonId, ["uuid"] = uuid }, ct);
    public Task<bool> CompressAsync(string daemonId, string uuid, int type, string source, object targets, string code = "utf-8", CancellationToken ct = default)
        => _client.PostAsync<bool>("/api/files/compress", new { type, code, source, targets },
            new Dictionary<string, string?> { ["daemonId"] = daemonId, ["uuid"] = uuid }, ct);

    public Task<object> DownloadFromUrlAsync(string daemonId, string uuid, string url, string fileName, CancellationToken ct = default)
        => _client.PostAsync<object>("/api/files/download_from_url", new { url, file_name = fileName },
            new Dictionary<string, string?> { ["daemonId"] = daemonId, ["uuid"] = uuid }, ct);

    private static Dictionary<string, string?> Pair(string daemonId, string uuid)
        => new() { ["daemonId"] = daemonId, ["uuid"] = uuid };
}
