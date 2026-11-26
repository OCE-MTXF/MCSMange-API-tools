using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCSMServerManager;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}

/// <summary>MCSManager 统一响应包: { status, data, time }</summary>
public sealed class McsmEnvelope<T>
{
    public int Status { get; set; }
    public T? Data { get; set; }
    public long Time { get; set; }
}

public sealed class ProcessInfo
{
    public double Cpu { get; set; }
    public long Memory { get; set; }
    public string Cwd { get; set; } = "";

    /// <summary>上次启动时间（Unix 毫秒）。网页版「启动时间」展示的就是这个字段。</summary>
    public long LastDatetime { get; set; }

    /// <summary>实例创建时间（Unix 毫秒）。</summary>
    public long CreateDatetime { get; set; }
    public int Pid { get; set; }
    public int Ppid { get; set; }
    public long Ctime { get; set; }
    public long Elapsed { get; set; }
}

public sealed class SystemInfo
{
    public string Type { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Release { get; set; } = "";
    public string Version { get; set; } = "";
    public string Node { get; set; } = "";
    public double Uptime { get; set; }
    public string Cwd { get; set; } = "";

    /// <summary>上次启动时间（Unix 毫秒）。网页版「启动时间」展示的就是这个字段。</summary>
    public long LastDatetime { get; set; }

    /// <summary>实例创建时间（Unix 毫秒）。</summary>
    public long CreateDatetime { get; set; }
    public double[]? Loadavg { get; set; }
    public long Freemem { get; set; }
    public long Totalmem { get; set; }
    /// <summary>面板自身的 CPU 占用（0~1 小数）。守护节点结构中不存在此字段。</summary>
    public double Cpu { get; set; }
    public double CpuUsage { get; set; }
    public double MemUsage { get; set; }
    public long Time { get; set; }

    /// <summary>
    /// CPU 占比（0~1）。/api/overview 存在两套结构：顶层 system 是面板自身，字段名为 cpu；
    /// remote[].system 是守护节点，字段名为 cpuUsage。此处两者皆可。
    /// </summary>
    public double CpuRatio => CpuUsage > 0 ? CpuUsage : Cpu;

    /// <summary>
    /// 内存占比（0~1）。顶层 system 不提供 memUsage，用 1 - freemem/totalmem 计算；
    /// 该公式在守护节点结构下与 memUsage 一致。
    /// </summary>
    public double MemRatio => Totalmem > 0
        ? Math.Clamp(1.0 - (double)Freemem / Totalmem, 0, 1)
        : MemUsage;
}

public sealed class InstanceOverview
{
    public int Running { get; set; }
    public int Total { get; set; }
}

public sealed class CpuMemPoint
{
    public double Cpu { get; set; }
    public double Mem { get; set; }
}

public sealed class RemoteCount
{
    public int Available { get; set; }
    public int Total { get; set; }
}

public sealed class RemoteOverview
{
    public string Uuid { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string Prefix { get; set; } = "";
    public bool Available { get; set; }
    public string Remarks { get; set; } = "";
    public string Version { get; set; } = "";
    public ProcessInfo? Process { get; set; }
    public InstanceOverview? Instance { get; set; }
    public SystemInfo? System { get; set; }
    public List<CpuMemPoint>? CpuMemChart { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Remarks) ? $"{Ip}:{Port}" : Remarks;
    public string Endpoint => $"{Ip}:{Port}{Prefix}";
    public string StateText => Available ? "在线" : "离线";

    /// <summary>节点系统 CPU 占比。</summary>
    public string CpuText => System is null ? "-" : $"{System.CpuRatio * 100:0.#}%";

    /// <summary>节点系统内存占比。</summary>
    public string SysMemText => System is null ? "-" : $"{System.MemRatio * 100:0.#}%";

    /// <summary>节点上游戏进程的常驻内存。</summary>
    public string MemText => Process is null ? "-" : SizeUtil.Human(Process.Memory);

    /// <summary>运行中的实例数量。</summary>
    public string RunningText => Instance is null ? "-" : $"{Instance.Running}/{Instance.Total}";
}

public sealed class PanelOverview
{
    public string Version { get; set; } = "";
    public string SpecifiedDaemonVersion { get; set; } = "";
    public ProcessInfo? Process { get; set; }
    public SystemInfo? System { get; set; }
    public RemoteCount? RemoteCount { get; set; }
    public List<RemoteOverview>? Remote { get; set; }
}

public sealed class NodeStatus
{
    public string Uuid { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string Prefix { get; set; } = "";
    public bool Available { get; set; }
    public string Remarks { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Remarks) ? $"{Ip}:{Port}" : Remarks;
    public string Endpoint => $"{Ip}:{Port}{Prefix}";
    public string StateText => Available ? "在线" : "离线";
}

public sealed class InstanceConfig
{
    public string Nickname { get; set; } = "";
    public string Type { get; set; } = "";
    public string StartCommand { get; set; } = "";
    public string Cwd { get; set; } = "";

    /// <summary>上次启动时间（Unix 毫秒）。网页版「启动时间」展示的就是这个字段。</summary>
    public long LastDatetime { get; set; }

    /// <summary>实例创建时间（Unix 毫秒）。</summary>
    public long CreateDatetime { get; set; }
    public string Ie { get; set; } = "utf-8";
    public string Oe { get; set; } = "utf-8";
    public string StopCommand { get; set; } = "";
    public int Crlf { get; set; }
    public List<string>? Tag { get; set; }
}

public sealed class InstanceInfo
{
    public bool McPingOnline { get; set; }
    public int CurrentPlayers { get; set; } = -1;
    public int MaxPlayers { get; set; } = -1;
    public string Version { get; set; } = "";
    public int Latency { get; set; }
    public int FileLock { get; set; }
}

public sealed class InstanceDetail
{
    public string InstanceUuid { get; set; } = "";
    public int Status { get; set; }
    public int Started { get; set; }
    public int AutoRestarted { get; set; }
    public long Space { get; set; }
    public InstanceConfig? Config { get; set; }
    public InstanceInfo? Info { get; set; }
    public ProcessInfo? ProcessInfo { get; set; }

    public string Nickname => string.IsNullOrWhiteSpace(Config?.Nickname) ? InstanceUuid : Config!.Nickname;
    public string TypeText => string.IsNullOrWhiteSpace(Config?.Type) ? "-" : Config!.Type;
    public string StatusText => InstanceStatus.ToText(Status);
    public string SpaceText => SizeUtil.Human(Space);
    public string PlayersText => Info is null || Info.MaxPlayers < 0 ? "-" : $"{Info.CurrentPlayers}/{Info.MaxPlayers}";
    public string CpuText => ProcessInfo is null ? "-" : $"{ProcessInfo.Cpu:0.##}";
    public string MemoryText => ProcessInfo is null ? "-" : SizeUtil.Human(ProcessInfo.Memory);

    /// <summary>实例类型的中文名称，与 MCSManager 网页版保持一致的表述。</summary>
    public string FriendlyType => Config?.Type switch
    {
        "minecraft/java" => "MC Java 版服务端",
        "minecraft/java/paper" => "PaperMC 服务端",
        "minecraft/java/folia" => "Folia 服务端",
        "minecraft/java/mohist" => "Mohist 服务端",
        "minecraft/java/bukkit" => "Bukkit 服务端",
        "minecraft/java/spigot" => "Spigot 服务端",
        "minecraft/java/forge" => "MC Forge 服务端",
        "minecraft/java/fabric" => "MC Fabric 服务端",
        "minecraft/bedrock" => "MC 基岩版服务端",
        "steam" => "Steam 服务端",
        null or "" => "-",
        var other => other
    };

    /// <summary>是否处于运行中状态。</summary>
    public bool IsRunning => Status == 3;

    /// <summary>上次启动时间，取 config.lastDatetime（注意 started 只是 0/1 标志，不是时间戳）。</summary>
    public string LastStartedText
    {
        get
        {
            var ms = Config?.LastDatetime ?? 0;
            if (ms <= 0) return "-";
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                return "-";
            }
        }
    }
}

public sealed class PagedInstances
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int MaxPage { get; set; }
    public List<string>? AllTags { get; set; }
    public List<InstanceDetail> Data { get; set; } = new();
}

public sealed class FileItem
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Time { get; set; } = "";
    public int Mode { get; set; }
    /// <summary>0 = 文件夹, 1 = 文件</summary>
    public int Type { get; set; }

    public bool IsDirectory => Type == 0;
    public string TypeText => IsDirectory ? "文件夹" : "文件";
    public string SizeText => IsDirectory ? "-" : SizeUtil.Human(Size);
}

public sealed class FileListResult
{
    public List<FileItem> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
    public string AbsolutePath { get; set; } = "";
}

public sealed class UserInstanceRef
{
    public string InstanceUuid { get; set; } = "";
    public string DaemonId { get; set; } = "";
}

public sealed class UserInfo
{
    public string Uuid { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Permission { get; set; }
    public string RegisterTime { get; set; } = "";
    public string LoginTime { get; set; } = "";
    public List<UserInstanceRef>? Instances { get; set; }
    public bool Open2FA { get; set; }
    public bool SsoBound { get; set; }

    public string PermissionText => Permission switch
    {
        10 => "管理员",
        1 => "用户",
        -1 => "已封禁",
        0 => "访客",
        _ => Permission.ToString()
    };

    public int InstanceCount => Instances?.Count ?? 0;
    public string TwoFactorText => Open2FA ? "已开启" : "未开启";
}

public sealed class UserResult
{
    public List<UserInfo> Data { get; set; } = new();
    public int MaxPage { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}

public static class InstanceStatus
{
    public static string ToText(int status) => status switch
    {
        -1 => "忙碌",
        0 => "已停止",
        1 => "停止中",
        2 => "启动中",
        3 => "运行中",
        _ => $"未知({status})"
    };

    public static bool IsRunning(int status) => status == 3 || status == 2 || status == -1;
}

public static class SizeUtil
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Human(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {Units[unit]}";
    }
}

public sealed class McsmException : Exception
{
    public McsmException(string message) : base(message) { }
}
