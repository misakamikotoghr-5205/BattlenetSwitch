using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuickSwitch;

public sealed class LauncherConfig
{
    private readonly JsonObject root;
    private readonly JsonObject client;
    private readonly JsonObject? loginSettings;

    public static readonly string[] Regions = ["CN", "US", "EU", "KR", "TW"];

    public LauncherConfig(byte[] bytes)
    {
        try
        {
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject ?? throw new JsonException();
            client = root["Client"] as JsonObject ?? throw new JsonException();
            loginSettings = client["LoginSettings"] as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("无法识别战网配置。请先打开战网并完成一次登录。", ex);
        }
    }

    public string[] Names
    {
        get
        {
            var saved = client["SavedAccountNames"];
            return saved switch
            {
                null => [],
                JsonValue value when value.TryGetValue<string>(out var names) =>
                    names.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                JsonArray array => array.Select(NodeText).Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!.Trim()).ToArray(),
                _ => throw new InvalidDataException("战网账号列表格式无法识别。")
            };
        }
    }

    public string? Region => NormalizeRegion(FindText(root, "LastLoginRegion"))
        ?? NormalizeRegion(NodeText(loginSettings?["SelectedRegion"]));

    public static string? NormalizeRegion(string? value)
    {
        var code = value?.Trim().ToUpperInvariant();
        return Regions.Contains(code, StringComparer.Ordinal) ? code : null;
    }

    public byte[] SelectAccount(string? account, string region, IEnumerable<string> remembered)
    {
        var names = account is null ? Array.Empty<string>() :
            remembered.Prepend(account).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        client["SavedAccountNames"] = client["SavedAccountNames"] is JsonArray
            ? new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray())
            : JsonValue.Create(string.Join(",", names));
        client["AutoLogin"] = NodeText(client["AutoLogin"]) is null
            ? JsonValue.Create(account is not null)
            : JsonValue.Create(account is null ? "false" : "true");
        var settings = loginSettings ?? new JsonObject();
        if (loginSettings is null) client["LoginSettings"] = settings;
        settings["SelectedRegion"] = region;
        ReplaceRegion(root, region);
        return JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? NodeText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? FindText(JsonNode node, string key)
    {
        if (node is JsonObject obj)
        {
            if (NodeText(obj[key]) is { } value) return value;
            foreach (var item in obj)
                if (item.Value is not null && FindText(item.Value, key) is { } found) return found;
        }
        else if (node is JsonArray list)
        {
            foreach (var item in list)
                if (item is not null && FindText(item, key) is { } found) return found;
        }
        return null;
    }

    private static void ReplaceRegion(JsonNode node, string region)
    {
        if (node is JsonObject obj)
        {
            if (obj["LastLoginRegion"] is JsonValue) obj["LastLoginRegion"] = region;
            foreach (var item in obj)
                if (item.Value is not null) ReplaceRegion(item.Value, region);
        }
        else if (node is JsonArray list)
        {
            foreach (var item in list)
                if (item is not null) ReplaceRegion(item, region);
        }
    }
}
