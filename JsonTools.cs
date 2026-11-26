using System.Text;
using System.Text.Json;

namespace MCSMServerManager;

/// <summary>一次 JSON 初步解析的结果。</summary>
public sealed class JsonSummary
{
    /// <summary>结论概述，显示在详情顶部。</summary>
    public string Note { get; init; } = "";

    /// <summary>格式化后的完整内容，可直接查看。</summary>
    public string Pretty { get; init; } = "";

    /// <summary>响应是否为合法 JSON。</summary>
    public bool IsJson { get; init; }
}

/// <summary>
/// 对接口返回的 JSON 做初步解析：识别 MCSManager 的 { status, data, time } 信封，
/// 给出中文结论，并把完整内容格式化后供「完整查看」入口使用。
/// </summary>
public static class JsonTools
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonSummary Describe(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new JsonSummary { Note = "响应内容为空。", Pretty = "（空响应）", IsJson = false };

        var trimmed = body.TrimStart();

        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return new JsonSummary
            {
                Note = "响应不是 JSON，通常是面板直接返回的纯文本错误信息。",
                Pretty = body,
                IsJson = false
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var note = DescribeEnvelope(doc.RootElement);
            var pretty = JsonSerializer.Serialize(doc.RootElement, Options);
            return new JsonSummary { Note = note, Pretty = pretty, IsJson = true };
        }
        catch (JsonException ex)
        {
            return new JsonSummary
            {
                Note = "JSON 解析失败：" + ex.Message,
                Pretty = body,
                IsJson = false
            };
        }
    }

    private static string DescribeEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "顶层是 " + Kind(root.ValueKind) + "，长度 " + (root.GetArrayLength()) + " 项。";

        // MCSManager 统一响应信封
        if (root.TryGetProperty("status", out var status))
        {
            var code = status.ValueKind == JsonValueKind.Number ? status.ToString() : status.ToString();
            var sb = new StringBuilder();
            sb.Append("识别到 MCSManager 响应信封：status = ").Append(code);

            if (root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number)
            {
                if (time.TryGetInt64(out var ms))
                    sb.Append("，time = ").Append(DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            }

            if (root.TryGetProperty("data", out var data))
            {
                sb.Append("，data 为").Append(Kind(data.ValueKind));
                sb.Append(DescribeData(data));
            }
            else
            {
                sb.Append("，响应中没有 data 字段");
            }

            return sb.ToString();
        }

        var keys = root.EnumerateObject().Select(p => p.Name).Take(8).ToArray();
        return "JSON 对象，顶层字段：" + (keys.Length == 0 ? "（无）" : string.Join("、", keys));
    }

    private static string DescribeData(JsonElement data) => data.ValueKind switch
    {
        JsonValueKind.Array => $"，共 {data.GetArrayLength()} 项",
        JsonValueKind.Object => "，字段：" + string.Join("、", data.EnumerateObject().Select(p => p.Name).Take(8)),
        JsonValueKind.String => "，内容为 \"" + Truncate(data.GetString(), 60) + "\"",
        JsonValueKind.Null or JsonValueKind.Undefined => "，内容为空",
        _ => "，内容为 " + Truncate(data.ToString(), 60)
    };

    private static string Kind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "对象",
        JsonValueKind.Array => "数组",
        JsonValueKind.String => "字符串",
        JsonValueKind.Number => "数字",
        JsonValueKind.True or JsonValueKind.False => "布尔值",
        JsonValueKind.Null => "null",
        _ => "未知"
    };

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>把任意内容格式化为可读 JSON；不是 JSON 就原样缩进返回。</summary>
    public static string Pretty(string? body)
    {
        var summary = Describe(body);
        return summary.Pretty;
    }
}