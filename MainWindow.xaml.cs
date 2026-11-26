using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MCSMServerManager;

public partial class MainWindow : Window
{
    /// <summary>在线编辑允许打开的最大字节数，超过则拒绝，避免界面卡死。</summary>
    private const long MaxEditableBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 允许在线编辑的扩展名白名单。名单外的文件默认按二进制处理，阻止不可逆的损坏。
    /// </summary>
    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".yaml", ".yml", ".xml", ".toml", ".ini", ".cfg", ".conf",
        ".properties", ".log", ".sh", ".bash", ".bat", ".ps1", ".py", ".js", ".ts", ".java",
        ".csv", ".env", ".sql", ".gitignore", ".mcmeta", ".snbt", ".mcfunction", ".lang"
    };

    private readonly McsmClient _client = new();
    private readonly DispatcherTimer _perfTimer = new();

    private McsmApi _api = null!;
    private AppConfig _cfg = null!;
    private List<NodeStatus> _nodes = new();
    private List<InstanceDetail> _instances = new();
    private List<RemoteOverview> _remotes = new();
    private InstanceDetail? _selectedInstance;
    private string _currentPath = "/";
    private string _currentView = "overview";
    private bool _connected;
    private bool _userBusy;
    private bool _suppressComboLoad;
    private bool _insecureHttpAcknowledged;
    private int _busyDepth;

    private int _instancePage = 1;
    private int _instanceMaxPage = 1;
    private int _filePage;
    private int _fileMaxPage = 1;
    private int _userPage = 1;
    private int _userMaxPage = 1;

    public MainWindow()
    {
        InitializeComponent();

        _cfg = App.Config;
        _api = new McsmApi(_client);

        _client.IgnoreCertificateErrors = _cfg.IgnoreCertificateErrors;
        _client.LogHttpBody = _cfg.LogHttpBody;
        _client.ApiKeyFallbackEngaged += OnApiKeyFallbackEngaged;
        AppLogger.RegisterSecret(_cfg.LastApiKey);

        InsecureCertCheck.IsChecked = _cfg.IgnoreCertificateErrors;
        AddressBox.Text = string.IsNullOrWhiteSpace(_cfg.LastPanelUrl) ? "http://localhost:23333" : _cfg.LastPanelUrl;
        if (_cfg.SaveApiKey && _cfg.LastApiKey.Length > 0) ApiKeyBox.Password = _cfg.LastApiKey;

        NavList.SelectedIndex = 0;
        Closing += (_, _) => ShutdownApp();

        AppLogger.LineAppended += OnLogLineAppended;
        AppLogger.Action("主界面已加载");

        Loaded += (_, _) =>
        {
            if (App.ConfigWarning is not null) Warn(App.ConfigWarning);
            AppLogPathText.Text = AppLogger.CurrentLogPath;
            StartPerfTimer();

            if (_cfg.AutoConnectOnStart && ApiKeyBox.Password.Length > 0)
            {
                AppLogger.Info("按配置自动发起连接");
                ConnectButton_Click(this, new RoutedEventArgs());
            }
        };

        // 每 5 秒把新日志同步到「最近日志」与日志页
        var logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        logTimer.Tick += (_, _) => RefreshLogBoxes();
        logTimer.Start();
    }

    private void ShutdownApp()
    {
        AppLogger.LineAppended -= OnLogLineAppended;
        _perfTimer.Stop();
        _client.Dispose();

        if (_cfg.SaveApiKey) _cfg.LastApiKey = ApiKeyBox.Password;
        if (_cfg.SavePanelUrl) _cfg.LastPanelUrl = AddressBox.Text;
        _cfg.IgnoreCertificateErrors = _client.IgnoreCertificateErrors;
        _cfg.Save();
        AppLogger.Info("已保存配置并退出");
    }

    // ---------- 通用 UI 辅助 ----------

    private void SetStatus(string text, Color color)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
    }

    private void Info(string message) => MessageBox.Show(this, message, "提示",
        MessageBoxButton.OK, MessageBoxImage.Information);

    private void Warn(string message) => MessageBox.Show(this, message, "提示",
        MessageBoxButton.OK, MessageBoxImage.Warning);

    private void Error(string message) => MessageBox.Show(this, message, "错误",
        MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>
    /// 出错时的统一出口：按配置弹出可展开详情的 PCL 风格弹窗，
    /// 详情里带最近一次接口往返的原始 JSON。
    /// </summary>
    private void ReportError(McsmException ex)
    {
        AppLogger.Error("操作失败：" + ex.Message, ex);

        if (!_cfg.ShowErrorDetailDialog)
        {
            Error(ex.Message);
            return;
        }

        var exchange = _client.LastExchange;
        var detail = exchange is null
            ? ex.Message
            : JsonTools.Describe(exchange.Body).Pretty;

        var dialog = new DetailDialog(ex.Message,
            exchange is null
                ? "本次操作没有可展示的接口返回。"
                : $"{exchange.Method} {exchange.Path} · {exchange.Status} {exchange.StatusText} · 耗时 {exchange.ElapsedMs} 毫秒",
            detail) { Owner = this };

        dialog.ShowDialog();
    }

    /// <summary>用户主动发起的操作：带互斥与可见反馈，避免重复点击静默失效。</summary>
    private async Task RunAsync(Func<Task> action)
    {
        if (_userBusy)
        {
            SetStatus("操作进行中，请稍候…", Colors.Gray);
            return;
        }

        _userBusy = true;
        BeginBusy();
        try
        {
            await action();
        }
        catch (McsmException ex)
        {
            ReportError(ex);
        }
        catch (Exception ex)
        {
            AppLogger.Error("操作失败：" + ex.Message, ex);
            Error("操作失败: " + ex.Message);
        }
        finally
        {
            _userBusy = false;
            EndBusy();
        }
    }

    /// <summary>
    /// 导航切换与定时刷新触发的内部加载：不占用用户互斥（否则会被上层静默吞掉），
    /// 但必须捕获异常——丢弃的 Task 失败时不会冒泡，界面会静默停在旧数据。
    /// </summary>
    private async Task LoadAsync(Func<Task> action, bool silent = false)
    {
        BeginBusy();
        try
        {
            await action();
        }
        catch (McsmException ex)
        {
            AppLogger.Error("加载失败：" + ex.Message, ex);
            if (!silent) ReportError(ex);
            SetStatus("加载失败", Color.FromRgb(0xDC, 0x26, 0x26));
        }
        catch (Exception ex)
        {
            AppLogger.Error("加载失败：" + ex.Message, ex);
            if (!silent) Error("操作失败: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private void BeginBusy()
    {
        _busyDepth++;
        Mouse.OverrideCursor = Cursors.Wait;
        ContentRoot.IsEnabled = false;
    }

    private void EndBusy()
    {
        _busyDepth = Math.Max(0, _busyDepth - 1);
        if (_busyDepth != 0) return;
        Mouse.OverrideCursor = null;
        ContentRoot.IsEnabled = true;
    }

    private void ShowView(string view)
    {
        _currentView = view;
        OverviewView.Visibility = view == "overview" ? Visibility.Visible : Visibility.Collapsed;
        InstancesView.Visibility = view == "instances" ? Visibility.Visible : Visibility.Collapsed;
        NodesView.Visibility = view == "nodes" ? Visibility.Visible : Visibility.Collapsed;
        UsersView.Visibility = view == "users" ? Visibility.Visible : Visibility.Collapsed;
        FilesView.Visibility = view == "files" ? Visibility.Visible : Visibility.Collapsed;
        AppLogView.Visibility = view == "applog" ? Visibility.Visible : Visibility.Collapsed;
        PerfView.Visibility = view == "perf" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not string tag) return;
        ShowView(tag);

        if (tag == "applog")
        {
            RefreshLogBoxes();
            return;
        }

        if (!_connected) return;
        _ = tag switch
        {
            "overview" => LoadAsync(LoadOverviewAsync),
            "nodes" => LoadAsync(LoadNodesAsync),
            "instances" => LoadAsync(EnsureInstanceNodesAsync),
            "files" => LoadAsync(PrepareFilesAsync),
            "users" => LoadAsync(LoadUsersAsync),
            "perf" => LoadAsync(LoadPerformanceAsync),
            _ => Task.CompletedTask
        };
    }
    /// <summary>
    /// 回退到 ?apikey= 查询串意味着密钥进入 URL，可能被面板前置的 Nginx / 反向代理记入访问日志。
    /// McsmClient 仅在某个面板首次触发回退时发出本事件，且换面板时会重置该状态，
    /// 因此这里无需再去重：同一面板只提醒一次，换面板则重新提醒。
    /// </summary>
    private void OnApiKeyFallbackEngaged()
    {
        Warn(
            "面板不接受通过请求头传递 API Key，程序已回退为 ?apikey= 查询串方式以兼容旧版面板。\n\n" +
            "这意味着 API Key 会出现在请求 URL 中，可能被面板前置的 Nginx / 反向代理写入访问日志，" +
            "也可能残留在浏览器历史或代理缓存里。\n\n" +
            "建议将 MCSManager 面板升级到 10.x —— 该版本支持请求头认证，可彻底避免此问题。");
    }
    private void InsecureCertCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (InsecureCertCheck is null) return;
        _client.IgnoreCertificateErrors = InsecureCertCheck.IsChecked == true;
        _cfg.IgnoreCertificateErrors = _client.IgnoreCertificateErrors;
        InsecureWarnBadge.Visibility = _connected && _client.IgnoreCertificateErrors
            ? Visibility.Visible : Visibility.Collapsed;
        AppLogger.Warn("证书校验设置已变更：忽略证书错误 = " + _client.IgnoreCertificateErrors);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var url = McsmClient.NormalizeBaseUrl(AddressBox.Text);
            if (url.Length == 0)
            {
                Warn("请填写面板地址。");
                return;
            }

            // 明文 HTTP 下 API Key 会以管理员权限裸奔，必须让用户知情后再继续
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && _client.ApiKey.Length > 0
                && !_insecureHttpAcknowledged)
            {
                AppLogger.Warn("通过明文 HTTP 连接，API Key 未加密传输：" + url);

                var ok = MessageBox.Show(this,
                    "你正在通过明文 HTTP 连接面板。\n\n" +
                    "API Key 与面板账号同权限，经明文传输可能被同网络内的攻击者截获。\n\n" +
                    "建议：给面板配置 HTTPS 后改用 https:// 地址。\n" +
                    "若面板位于本机或可信内网，可选择「仍然连接」。",
                    "连接方式不安全", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (ok != MessageBoxResult.Yes) return;

                if (MessageBox.Show(this,
                    "是否在本次运行中记住这个选择？\n\n选择「是」后本次运行不再提示，重启程序后会再次提示。",
                    "记住选择", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _insecureHttpAcknowledged = true;
                }
            }

            AppLogger.Action("点击连接，面板地址 " + url);
            _client.BaseUrl = url;
            _client.ApiKey = ApiKeyBox.Password.Trim();
            _client.Token = "";
            AppLogger.RegisterSecret(_client.ApiKey);

            SetStatus("连接中...", Colors.Gray);
            var overview = await _api.OverviewAsync();

            _connected = true;
            SetStatus("已连接", Color.FromRgb(0x16, 0xA3, 0x4A));
            InsecureWarnBadge.Visibility = _client.IgnoreCertificateErrors
                ? Visibility.Visible : Visibility.Collapsed;
            ApplyOverview(overview);
            StartPerfTimer();

            if (_cfg.SavePanelUrl) { _cfg.LastPanelUrl = url; _cfg.Save(); }
            if (_cfg.SaveApiKey) { _cfg.LastApiKey = _client.ApiKey; _cfg.Save(); }

            if (_currentView != "overview") ShowView(_currentView);
        });
    }
    // ---------- 概览 ----------

    private async Task LoadOverviewAsync()
    {
        var overview = await _api.OverviewAsync();
        ApplyOverview(overview);
    }

    private void ApplyOverview(PanelOverview overview)
    {
        PanelVersionText.Text = string.IsNullOrWhiteSpace(overview.Version) ? "-" : overview.Version;
        var total = overview.RemoteCount?.Total ?? overview.Remote?.Count ?? 0;
        var available = overview.RemoteCount?.Available ?? overview.Remote?.Count(x => x.Available) ?? 0;
        NodeCountText.Text = $"{available}/{total}";

        // 顶层 system 是面板自身：CPU 取 cpu，内存没有 memUsage，需由 freemem/totalmem 反推。
        CpuText.Text = overview.System is null ? "-" : $"{overview.System.CpuRatio * 100:0.#}%";
        MemText.Text = overview.System is null ? "-" : $"{overview.System.MemRatio * 100:0.#}%";

        var remotes = overview.Remote ?? new List<RemoteOverview>();
        _remotes = remotes;
        OverviewGrid.ItemsSource = remotes;
        PanelSummaryText.Text = $"面板 {overview.Version} · 在线节点 {available}/{total}";
    }

    // ---------- 实例（卡片） ----------

    private static InstanceDetail? InstanceOf(object sender)
        => sender is FrameworkElement { Tag: InstanceDetail instance } ? instance : null;

    /// <summary>把卡片上的实例换算成操作目标，并校验它确实属于当前节点。</summary>
    private (string DaemonId, string Uuid)? TargetOf(InstanceDetail instance)
    {
        if (InstanceNodeCombo.SelectedItem is not NodeStatus node)
        {
            Warn("请先选择节点。");
            return null;
        }

        if (!_instances.Any(i => i.InstanceUuid == instance.InstanceUuid))
        {
            Warn("该实例不属于当前节点，请先刷新实例列表。");
            return null;
        }

        return (node.Uuid, instance.InstanceUuid);
    }

    /// <summary>
    /// 控制台命令可能包含 op / login / password 等敏感内容，写入日志前只保留命令动词，
    /// 参数一律替换为 ***，避免凭据落入日志文件。
    /// </summary>
    private static string MaskConsoleCommand(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return "";

        var space = trimmed.IndexOf(' ');
        var verb = space < 0 ? trimmed : trimmed[..space];

        // 这类命令的参数一律视为凭据
        if (verb is "op" or "deop" or "whitelist" or "ban" or "ban-ip" or "pardon" or "pardon-ip"
            or "login" or "register" or "changepw" or "password" or "setpassword" or "opdone")
            return verb + " ***";

        return trimmed;
    }
    private (string DaemonId, string Uuid)? SelectedTarget()
    {
        if (_selectedInstance is null)
        {
            Warn("请先在实例卡片上选择一个实例。");
            return null;
        }
        return TargetOf(_selectedInstance);
    }

    private async Task EnsureInstanceNodesAsync()
    {
        if (_nodes.Count == 0) await LoadNodesAsync();
        if (InstanceNodeCombo.SelectedItem is NodeStatus node) await LoadInstancesAsync(node.Uuid);
    }

    private async Task LoadInstancesAsync(string daemonId)
    {
        var keyword = InstanceFilterBox.Text.Trim();
        var result = await _api.InstancesAsync(daemonId, _instancePage, _cfg.InstancePageSize,
            keyword.Length == 0 ? null : keyword);

        _instances = result.Data;
        InstanceCards.ItemsSource = _instances;
        _instanceMaxPage = Math.Max(1, result.MaxPage);

        if (_selectedInstance is not null && !_instances.Any(i => i.InstanceUuid == _selectedInstance.InstanceUuid))
            _selectedInstance = null;

        UpdateInstancePageText();
    }

    private void UpdateInstancePageText()
    {
        InstancePageText.Text = $"第 {_instancePage}/{_instanceMaxPage} 页 · 共 {_instances.Count} 项";
        InstanceCountText.Text = _instances.Count == 0 ? "" : $"共 {_instances.Count} 个实例";
    }

    private async void InstanceNodeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboLoad) return;
        if (!_connected || InstanceNodeCombo.SelectedItem is not NodeStatus node) return;

        _instancePage = 1;
        _selectedInstance = null;
        await LoadAsync(() => LoadInstancesAsync(node.Uuid));
    }

    private async void RefreshInstancesButton_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceNodeCombo.SelectedItem is not NodeStatus node)
        {
            Warn("请先选择节点。");
            return;
        }
        _instancePage = 1;
        await RunAsync(() => LoadInstancesAsync(node.Uuid));
    }

    private async void PrevInstancePage_Click(object sender, RoutedEventArgs e)
    {
        if (_instancePage <= 1 || InstanceNodeCombo.SelectedItem is not NodeStatus node) return;
        _instancePage--;
        await RunAsync(() => LoadInstancesAsync(node.Uuid));
    }

    private async void NextInstancePage_Click(object sender, RoutedEventArgs e)
    {
        if (_instancePage >= _instanceMaxPage || InstanceNodeCombo.SelectedItem is not NodeStatus node) return;
        _instancePage++;
        await RunAsync(() => LoadInstancesAsync(node.Uuid));
    }

    private async void CardStartButton_Click(object sender, RoutedEventArgs e) => await CardActionAsync(sender, "启动", (d, u) => _api.StartInstanceAsync(d, u));
    private async void CardStopButton_Click(object sender, RoutedEventArgs e) => await CardActionAsync(sender, "停止", (d, u) => _api.StopInstanceAsync(d, u));
    private async void CardRestartButton_Click(object sender, RoutedEventArgs e) => await CardActionAsync(sender, "重启", (d, u) => _api.RestartInstanceAsync(d, u));

    private async Task CardActionAsync(object sender, string verb, Func<string, string, Task> action)
    {
        var instance = InstanceOf(sender);
        if (instance is null) return;
        var target = TargetOf(instance);
        if (target is null) return;

        _selectedInstance = instance;
        AppLogger.Action($"{verb}实例「{instance.Nickname}」");

        await RunAsync(async () =>
        {
            await action(target.Value.DaemonId, target.Value.Uuid);
            await LoadInstancesAsync(target.Value.DaemonId);
        });
    }

    private async void CardLogButton_Click(object sender, RoutedEventArgs e)
    {
        var instance = InstanceOf(sender);
        if (instance is null) return;
        var target = TargetOf(instance);
        if (target is null) return;

        _selectedInstance = instance;
        await ShowInstanceLogAsync(target.Value.DaemonId, target.Value.Uuid, instance.Nickname);
    }

    private async void CardFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var instance = InstanceOf(sender);
        if (instance is null) return;
        var target = TargetOf(instance);
        if (target is null) return;

        _selectedInstance = instance;
        AppLogger.Action($"打开实例「{instance.Nickname}」的文件管理");
        ShowView("files");
        _currentPath = "/";
        _filePage = 0;
        await LoadAsync(LoadFilesAsync);
    }

    private async void CardDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var instance = InstanceOf(sender);
        if (instance is null) return;
        var target = TargetOf(instance);
        if (target is null) return;

        var withFile = MessageBox.Show(this,
            $"确定要删除实例「{instance.Nickname}」吗？\n\n选择「是」将同时删除该实例的全部文件，且不可恢复。",
            "确认删除", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (withFile == MessageBoxResult.Cancel) return;

        var deleteFile = withFile == MessageBoxResult.Yes;
        AppLogger.Action($"删除实例「{instance.Nickname}」，同时删除文件 = {deleteFile}");

        await RunAsync(async () =>
        {
            await _api.DeleteInstancesAsync(target.Value.DaemonId, new[] { instance.InstanceUuid }, deleteFile);
            if (_selectedInstance?.InstanceUuid == instance.InstanceUuid) _selectedInstance = null;
            await LoadInstancesAsync(target.Value.DaemonId);
        });
    }

    private async void BatchActionButton_Click(object sender, RoutedEventArgs e)
    {
        var running = _instances.Where(i => i.IsRunning).ToList();
        if (running.Count == 0)
        {
            Warn("当前没有正在运行的实例。");
            return;
        }

        var choice = MessageBox.Show(this,
            $"当前有 {running.Count} 个实例正在运行。\n\n「是」全部停止，「否」全部重启，「取消」返回。",
            "批量操作", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;

        // 必须使用用户实际的选择：此处曾被硬编码为 No，导致「停止」分支永远不可达。
        var restart = choice == MessageBoxResult.No;
        AppLogger.Action($"批量操作：{(restart ? "全部重启" : "全部停止")}，共 {running.Count} 个实例");

        await RunAsync(async () =>
        {
            var daemonId = InstanceNodeCombo.SelectedItem is NodeStatus node ? node.Uuid : null;
            if (daemonId is null) return;

            foreach (var inst in running)
            {
                if (restart) await _api.RestartInstanceAsync(daemonId, inst.InstanceUuid);
                else await _api.StopInstanceAsync(daemonId, inst.InstanceUuid);
            }
            await LoadInstancesAsync(daemonId);
        });
    }

    private async void StopAllButton_Click(object sender, RoutedEventArgs e)
    {
        var running = _instances.Where(i => i.IsRunning).ToList();
        if (running.Count == 0)
        {
            Warn("当前没有正在运行的实例。");
            return;
        }
        if (MessageBox.Show(this, $"确定停止全部 {running.Count} 个正在运行的实例吗？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        AppLogger.Action($"停止全部实例，共 {running.Count} 个");
        await RunAsync(async () =>
        {
            var daemonId = InstanceNodeCombo.SelectedItem is NodeStatus node ? node.Uuid : null;
            if (daemonId is null) return;
            foreach (var inst in running) await _api.StopInstanceAsync(daemonId, inst.InstanceUuid);
            await LoadInstancesAsync(daemonId);
        });
    }

    private async void SendCommandButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget();
        if (target is null) return;
        var command = CommandBox.Text.Trim();
        if (command.Length == 0)
        {
            Warn("请输入要发送的命令。");
            return;
        }

        AppLogger.Action("发送控制台命令：" + MaskConsoleCommand(command));
        await RunAsync(async () =>
        {
            await _api.SendCommandAsync(target.Value.DaemonId, target.Value.Uuid, command);
            CommandBox.Clear();
        });
    }

    private async void ViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget();
        if (target is null) return;
        await ShowInstanceLogAsync(target.Value.DaemonId, target.Value.Uuid,
            _selectedInstance?.Nickname ?? target.Value.Uuid);
    }

    private async Task ShowInstanceLogAsync(string daemonId, string uuid, string name)
    {
        string? log = null;
        await RunAsync(async () =>
        {
            log = await _api.InstanceLogAsync(daemonId, uuid);
        });
        if (log is null) return;

        // 模态窗口必须在 RunAsync 之外打开，否则等待光标与互斥会一直持续。
        var window = new LogWindow(name, log) { Owner = this };
        window.ShowDialog();
    }

    // ---------- 节点 ----------

    private async Task LoadNodesAsync()
    {
        _nodes = await _api.NodesAsync();
        NodesGrid.ItemsSource = _nodes;
        PopulateInstanceNodeCombo();
    }

    private void PopulateInstanceNodeCombo()
    {
        var selected = (InstanceNodeCombo.SelectedItem as NodeStatus)?.Uuid;
        InstanceNodeCombo.ItemsSource = _nodes;

        // 程序化改写 SelectedItem 会同步触发 SelectionChanged，导致重复请求，
        // 因此这里静默一次联动，由调用方自行决定要不要重新加载实例。
        _suppressComboLoad = true;
        try
        {
            InstanceNodeCombo.SelectedItem = _nodes.Count == 0
                ? null
                : selected is null
                    ? _nodes[0]
                    : _nodes.FirstOrDefault(n => n.Uuid == selected) ?? _nodes[0];
        }
        finally
        {
            _suppressComboLoad = false;
        }
    }

    private async void RefreshNodesButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Action("刷新节点列表");
        await RunAsync(LoadNodesAsync);
    }

    private async void ConnectNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (NodesGrid.SelectedItem is not NodeStatus node)
        {
            Warn("请先选择一个节点。");
            return;
        }
        AppLogger.Action("连接节点 " + node.DisplayName);
        await RunAsync(async () =>
        {
            await _api.ConnectNodeAsync(node.Uuid);
            Info($"已触发节点连接：{node.DisplayName}");
            await LoadNodesAsync();
        });
    }

    private async void AddNodeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NodeEditWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        AppLogger.Action($"添加节点 {dialog.Ip}:{dialog.Port}");
        await RunAsync(async () =>
        {
            await _api.AddNodeAsync(dialog.Ip, dialog.Port, dialog.Remarks, dialog.NodeApiKey, dialog.Prefix);
            Info("节点已添加，请点击「连接节点」完成握手。");
            await LoadNodesAsync();
        });
    }

    private async void DeleteNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (NodesGrid.SelectedItem is not NodeStatus node)
        {
            Warn("请先选择一个节点。");
            return;
        }
        if (MessageBox.Show(this, $"确定删除节点 {node.DisplayName} 吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        AppLogger.Action("删除节点 " + node.DisplayName);
        await RunAsync(async () =>
        {
            await _api.DeleteNodeAsync(node.Uuid);
            // 节点被删除后，其上的实例选中项必须一并作废，
            // 否则后续操作会把请求打到已删除的节点上。
            _selectedInstance = null;
            _instances = new();
            InstanceCards.ItemsSource = _instances;
            await LoadNodesAsync();
        });
    }
    // ---------- 用户 ----------

    private async Task LoadUsersAsync()
    {
        var keyword = UserFilterBox.Text.Trim();
        var result = await _api.SearchUsersAsync(keyword.Length == 0 ? null : keyword, _userPage, 50, null);
        UsersGrid.ItemsSource = result.Data;
        _userMaxPage = Math.Max(1, result.MaxPage);
        UpdateUserPageText();
    }

    private void UpdateUserPageText()
        => UserPageText.Text = $"第 {_userPage}/{_userMaxPage} 页 · 本页 {UsersGrid.Items.Count} 项";

    private async void UsersButton_Click(object sender, RoutedEventArgs e)
    {
        _userPage = 1;
        AppLogger.Action("搜索用户");
        await RunAsync(LoadUsersAsync);
    }

    private async void PrevUserPage_Click(object sender, RoutedEventArgs e)
    {
        if (_userPage <= 1) return;
        _userPage--;
        await RunAsync(LoadUsersAsync);
    }

    private async void NextUserPage_Click(object sender, RoutedEventArgs e)
    {
        if (_userPage >= _userMaxPage) return;
        _userPage++;
        await RunAsync(LoadUsersAsync);
    }

    // ---------- 文件管理 ----------

    private async Task PrepareFilesAsync()
    {
        if (_selectedInstance is null)
        {
            Warn("请先在「实例」页选择一个实例。");
            return;
        }
        _currentPath = "/";
        _filePage = 0;
        await LoadFilesAsync();
    }

    private async Task LoadFilesAsync()
    {
        var target = SelectedTarget();
        if (target is null) return;

        var result = await _api.ListFilesAsync(target.Value.DaemonId, target.Value.Uuid,
            _currentPath, _filePage, _cfg.FilePageSize);

        FilesGrid.ItemsSource = result.Items;
        CurrentPathText.Text = string.IsNullOrWhiteSpace(result.AbsolutePath) ? _currentPath : result.AbsolutePath;
        _fileMaxPage = result.Total <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(result.Total / (double)_cfg.FilePageSize));
        UpdateFilePageText();
    }

    private void UpdateFilePageText()
    {
        var shown = FilesGrid.Items.Count;
        FilePageText.Text = $"第 {_filePage + 1}/{_fileMaxPage} 页 · 本页 {shown} 项";
    }

    private async void RefreshFilesButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Action("刷新文件列表 " + _currentPath);
        await RunAsync(LoadFilesAsync);
    }

    private async void PrevFilePage_Click(object sender, RoutedEventArgs e)
    {
        if (_filePage <= 0) return;
        _filePage--;
        await RunAsync(LoadFilesAsync);
    }

    private async void NextFilePage_Click(object sender, RoutedEventArgs e)
    {
        if (_filePage + 1 >= _fileMaxPage) return;
        _filePage++;
        await RunAsync(LoadFilesAsync);
    }

    private async void UpDirButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is "" or "/")
        {
            _currentPath = "/";
            await RunAsync(LoadFilesAsync);
            return;
        }

        var trimmed = _currentPath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        _currentPath = index <= 0 ? "/" : trimmed[..index];
        _filePage = 0;
        await RunAsync(LoadFilesAsync);
    }

    private async void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileItem item || !item.IsDirectory) return;
        _currentPath = _currentPath.TrimEnd('/') + "/" + item.Name;
        _filePage = 0;
        AppLogger.Action("进入目录 " + _currentPath);
        await RunAsync(LoadFilesAsync);
    }

    private void ViewLastResponseButton_Click(object sender, RoutedEventArgs e)
    {
        var exchange = _client.LastExchange;
        if (exchange is null)
        {
            Warn("本次会话还没有发起过接口请求。");
            return;
        }

        var summary = JsonTools.Describe(exchange.Body);
        var dialog = new DetailDialog(summary.Note,
            $"{exchange.Method} {exchange.Path} · {exchange.Status} {exchange.StatusText} · 耗时 {exchange.ElapsedMs} 毫秒",
            summary.Pretty, isWarning: !exchange.Ok, isError: false) { Owner = this };

        dialog.ShowDialog();
    }

    // ---------- 文件操作 ----------

    private string PathOf(FileItem item)
    {
        var dir = _currentPath.TrimEnd('/');
        return dir.Length == 0 ? "/" + item.Name : dir + "/" + item.Name;
    }

    private FileItem? SelectedFile(bool requireFile = false)
    {
        if (FilesGrid.SelectedItem is not FileItem item)
        {
            Warn("请先在列表中选择一个项目。");
            return null;
        }
        if (requireFile && item.IsDirectory)
        {
            Warn("该操作只支持文件，请选择文件而不是文件夹。");
            return null;
        }
        return item;
    }

    private string? Ask(string title, string prompt, string initial = "")
    {
        var dialog = new PromptWindow(title, prompt, initial) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.InputBox.Text.Trim() : null;
    }

    /// <summary>
    /// 在线编辑前的准入检查：扩展名必须在白名单内，且体积不得超过上限。
    /// 返回非 null 表示拒绝打开，值为可展示给用户的理由。
    /// </summary>
    private string? GuardEditable(FileItem item)
    {
        var name = item.Name;
        var ext = Path.GetExtension(name);

        var whitelisted = EditableExtensions.Contains(ext) ||
                          string.Equals(name, ".gitignore", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(name, ".mcmeta", StringComparison.OrdinalIgnoreCase);

        if (!whitelisted)
        {
            return $"“{name}” 不在可编辑的文本扩展名白名单内。\n\n" +
                   "在线编辑按 UTF-8 文本回写，打开二进制文件（如 server.jar、图片、压缩包）会导致内容损坏且无法撤销。\n" +
                   "请改用 MCSManager 网页面板处理该文件。";
        }

        if (item.Size > MaxEditableBytes)
        {
            return $"“{name}” 大小为 {SizeUtil.Human(item.Size)}，超过 {SizeUtil.Human(MaxEditableBytes)} 的在线编辑上限。\n\n" +
                   "过大的文件会长时间阻塞界面，建议改用 MCSManager 网页面板编辑。";
        }

        return null;
    }

    /// <summary>读取后再按内容兜底判断是否为二进制，避免扩展名被伪装绕过。</summary>
    private static bool LooksBinary(string text)
    {
        var limit = Math.Min(text.Length, 8192);
        for (var i = 0; i < limit; i++)
        {
            var c = text[i];
            if (c == '\0' || c == '\uFFFD') return true;
        }
        return false;
    }

    private async void NewFolderButton_Click(object sender, RoutedEventArgs e)
        => await CreateEntryAsync("新建文件夹", "请输入文件夹名称", (d, u, path) => _api.MakeDirectoryAsync(d, u, path));

    private async void NewFileButton_Click(object sender, RoutedEventArgs e)
        => await CreateEntryAsync("新建文件", "请输入文件名", (d, u, path) => _api.TouchFileAsync(d, u, path));

    /// <summary>取名称后创建目录或文件，并刷新列表。</summary>
    private async Task CreateEntryAsync(string title, string prompt, Func<string, string, string, Task> create)
    {
        var target = SelectedTarget();
        if (target is null) return;

        var name = Ask(title, prompt);
        if (string.IsNullOrEmpty(name)) return;

        var path = _currentPath.TrimEnd('/') + "/" + name;
        AppLogger.Action($"{title} {path}");

        await RunAsync(async () =>
        {
            await create(target.Value.DaemonId, target.Value.Uuid, path);
            await LoadFilesAsync();
        });
    }
    private async void EditFileButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget();
        if (target is null) return;
        var item = SelectedFile(requireFile: true);
        if (item is null) return;

        var refusal = GuardEditable(item);
        if (refusal is not null)
        {
            AppLogger.Warn($"拒绝编辑「{item.Name}」：不在白名单或超限");
            Warn(refusal);
            return;
        }

        var path = PathOf(item);

        string? text = null;
        await RunAsync(async () =>
        {
            text = await _api.ReadFileAsync(target.Value.DaemonId, target.Value.Uuid, path);
        });
        if (text is null) return;

        if (LooksBinary(text))
        {
            Warn($"“{item.Name}” 的内容包含二进制特征，已拒绝打开。\n\n以文本方式读取再回写会损坏该文件，请改用网页面板。");
            return;
        }

        var editor = new FileEditorWindow(path, text) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var edited = editor.EditedText;

        AppLogger.Action($"保存文件 {path}（{edited.Length} 字符）");
        await RunAsync(async () =>
        {
            await _api.WriteFileAsync(target.Value.DaemonId, target.Value.Uuid, path, edited);
            await LoadFilesAsync();
        });
    }

    private async void RenameFileButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget();
        if (target is null) return;
        var item = SelectedFile();
        if (item is null) return;
        var name = Ask("重命名", $"为“{item.Name}”输入新名称", item.Name);
        if (string.IsNullOrEmpty(name) || name == item.Name) return;

        var from = PathOf(item);
        var to = _currentPath.TrimEnd('/') + "/" + name;
        AppLogger.Action($"重命名 {from} -> {to}");
        await RunAsync(async () =>
        {
            await _api.MoveFilesAsync(target.Value.DaemonId, target.Value.Uuid, new[] { new[] { from, to } });
            await LoadFilesAsync();
        });
    }

    private async void CopyFileButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget();
        if (target is null) return;
        var item = SelectedFile();
        if (item is null) return;
        var name = Ask("复制", $"将“{item.Name}”复制为", item.Name);
        if (string.IsNullOrEmpty(name) || name == item.Name) return;

        var from = PathOf(item);
        var to = _currentPath.TrimEnd('/') + "/" + name;
        AppLogger.Action($"复制 {from} -> {to}");
        await RunAsync(async () =>
        {
            await _api.CopyFilesAsync(target.Value.DaemonId, target.Value.Uuid, new[] { new[] { from, to } });
            await LoadFilesAsync();
        });
    }

    private async void CompressFileButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget();
        if (target is null) return;
        var item = SelectedFile();
        if (item is null) return;
        var archive = Ask("打包", "压缩包保存路径", PathOf(item) + ".zip");
        if (string.IsNullOrEmpty(archive)) return;

        AppLogger.Action($"打包 {item.Name} -> {archive}");
        await RunAsync(async () =>
        {
            await _api.CompressAsync(target.Value.DaemonId, target.Value.Uuid, 1, archive, new[] { PathOf(item) });
            await LoadFilesAsync();
        });
    }

    private async void DecompressFileButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget();
        if (target is null) return;
        var item = SelectedFile(requireFile: true);
        if (item is null) return;
        var dest = Ask("解压", $"将“{item.Name}”解压到目录", _currentPath.TrimEnd('/'));
        if (string.IsNullOrEmpty(dest)) return;

        AppLogger.Action($"解压 {item.Name} -> {dest}");
        await RunAsync(async () =>
        {
            await _api.CompressAsync(target.Value.DaemonId, target.Value.Uuid, 2, PathOf(item), dest);
            await LoadFilesAsync();
        });
    }

    private async void DownloadUrlButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget();
        if (target is null) return;

        // 一次填完地址与保存路径，避免两个串行模态框打断操作。
        var dialog = new Prompt2Window(
            "URL 下载", "下载地址", "",
            "保存路径 + 文件名", _currentPath.TrimEnd('/') + "/") { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var url = dialog.Value1;
        var fileName = dialog.Value2;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(fileName)) return;

        AppLogger.Action($"URL 下载 {url} -> {fileName}");
        await RunAsync(async () =>
        {
            await _api.DownloadFromUrlAsync(target.Value.DaemonId, target.Value.Uuid, url, fileName);
            Info("下载任务已提交，daemon 会在后台继续下载。");
        });
    }

    private async void DeleteFileButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget();
        if (target is null) return;
        var items = FilesGrid.SelectedItems.Cast<FileItem>().ToList();
        if (items.Count == 0)
        {
            Warn("请先选择要删除的项目。");
            return;
        }

        var names = string.Join("、", items.Take(5).Select(x => x.Name));
        if (items.Count > 5) names += $" 等 {items.Count} 项";
        if (MessageBox.Show(this, $"确定要删除以下内容吗？\n\n{names}\n\n文件夹及其全部内容都会被删除。", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        AppLogger.Action($"删除 {items.Count} 个项目");
        await RunAsync(async () =>
        {
            await _api.DeleteFilesAsync(target.Value.DaemonId, target.Value.Uuid, items.Select(PathOf));
            await LoadFilesAsync();
        });
    }

    // ---------- 运行日志 ----------

    private void OnLogLineAppended(string line)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                RecentLogBox.AppendText(line + Environment.NewLine);
                if (_currentView == "applog") AppLogBox.AppendText(line + Environment.NewLine);
            });
        }
        catch
        {
            // 界面关闭过程中忽略
        }
    }

    private void RefreshLogBoxes()
    {
        var lines = AppLogger.RecentLines;
        if (lines.Count == 0) return;

        var tail = lines.TakeLast(Math.Max(50, _cfg.LogViewerLines / 10));
        RecentLogBox.Text = string.Join(Environment.NewLine, tail);
        RecentLogBox.ScrollToEnd();

        if (_currentView == "applog") RefreshAppLogBox();
    }

    private void RefreshAppLogBox()
        => AppLogBox.Text = AppLogger.ReadAllLogs(_cfg.LogViewerLines);

    private void RefreshAppLogButton_Click(object sender, RoutedEventArgs e) => RefreshAppLogBox();

    private void OpenLogDirButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.LogDirectory);
            Process.Start(new ProcessStartInfo(AppConfig.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error("打开日志目录失败：" + ex.Message);
        }
    }

    private void CopyAppLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppLogBox.Text);
            Info("日志内容已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            Error("复制失败：" + ex.Message);
        }
    }

    // ---------- 性能监控 ----------

    private bool _suppressPerfEvents;

    private void StartPerfTimer()
    {
        _perfTimer.Stop();

        _suppressPerfEvents = true;
        PerfAutoCheck.IsChecked = _cfg.PerformanceMonitorEnabled;
        _suppressPerfEvents = false;

        UpdatePerfIntervalText();
        if (!_cfg.PerformanceMonitorEnabled) return;

        _perfTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _cfg.PerformanceIntervalSeconds));
        _perfTimer.Tick -= PerfTimer_Tick;
        _perfTimer.Tick += PerfTimer_Tick;
        _perfTimer.Start();
        AppLogger.Info($"定时性能刷新已启动，周期 {_cfg.PerformanceIntervalText}");
    }

    private void UpdatePerfIntervalText()
        => PerfIntervalText.Text = _cfg.PerformanceMonitorEnabled
            ? $"每 {_cfg.PerformanceIntervalValue} {_cfg.PerformanceIntervalUnit}（{_cfg.PerformanceIntervalSeconds} 秒）自动刷新"
            : "定时刷新已关闭";

    private async void PerfTimer_Tick(object? sender, EventArgs e)
    {
        if (!_connected || _userBusy) return;
        if (_currentView != "perf" && _currentView != "overview") return;
        await LoadAsync(LoadPerformanceAsync, silent: true);
    }

    private async Task LoadPerformanceAsync()
    {
        var overview = await _api.OverviewAsync();

        PerfCpuText.Text = overview.System is null ? "-" : $"{overview.System.CpuRatio * 100:0.#}%";
        PerfMemText.Text = overview.System is null ? "-" : $"{overview.System.MemRatio * 100:0.#}%";

        var total = overview.RemoteCount?.Total ?? overview.Remote?.Count ?? 0;
        var available = overview.RemoteCount?.Available ?? overview.Remote?.Count(x => x.Available) ?? 0;
        PerfNodeText.Text = $"{available}/{total}";
        PerfTimeText.Text = DateTime.Now.ToString("HH:mm:ss");
        PerfGrid.ItemsSource = overview.Remote ?? new List<RemoteOverview>();

        ApplyOverview(overview);
    }

    private async void PerfAutoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPerfEvents || PerfAutoCheck is null) return;
        _cfg.PerformanceMonitorEnabled = PerfAutoCheck.IsChecked == true;
        _cfg.Save();
        AppLogger.Action("定时性能刷新开关：" + _cfg.PerformanceMonitorEnabled);
        StartPerfTimer();
        if (_cfg.PerformanceMonitorEnabled && _connected) await LoadAsync(LoadPerformanceAsync);
    }

    private async void PerfRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Action("手动刷新性能数据");
        await LoadAsync(LoadPerformanceAsync);
    }

    private async void PerfSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (new SettingsWindow(_cfg) { Owner = this }.ShowDialog() == true)
        {
            _cfg.Save();
            StartPerfTimer();
            if (_connected) await LoadAsync(LoadPerformanceAsync);
        }
    }

    // ---------- 设置 / 关于 ----------

    private async void SettingsNavItem_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Action("打开设置");
        if (new SettingsWindow(_cfg) { Owner = this }.ShowDialog() == true)
        {
            _cfg.Save();
            _client.IgnoreCertificateErrors = _cfg.IgnoreCertificateErrors;
            _client.LogHttpBody = _cfg.LogHttpBody;
            InsecureCertCheck.IsChecked = _cfg.IgnoreCertificateErrors;
            InsecureWarnBadge.Visibility = _connected && _client.IgnoreCertificateErrors
                ? Visibility.Visible : Visibility.Collapsed;
            StartPerfTimer();
            if (_connected) await LoadAsync(LoadPerformanceAsync);
        }
    }

    private void AboutNavItem_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Action("打开关于与鸣谢");
        new AboutWindow { Owner = this }.ShowDialog();
    }
}
