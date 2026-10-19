using System.Text.Json.Nodes;
using Oranda.Core.Composition;
using Oranda.Core.Settings;

namespace Oranda.App.Shell.Layout;

/// <summary>
/// layout.json 1 → 2: 예전 기본 모양 <c>세로[가로[왼쪽 묶음, 문서, …], 아래 묶음]</c> 을 새 기본 모양
/// <c>가로[왼쪽 묶음, 세로[문서·…, 아래 묶음]]</c> 으로 바꾼다(왼쪽 도구 창이 창 전체 높이를 쓰게). 다른 모양은 그대로 둔다.
/// 비율은 그대로 옮긴다(왼쪽 폭, 아래 높이).
/// </summary>
[OrandaService(typeof(ISettingsMigration))]
public sealed class LayoutLeftFullHeightMigration : ISettingsMigration
{
    public string FileKind => SettingsFileKinds.Layout;

    public int FromVersion => 1;

    public JsonNode Apply(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document["root"] is JsonObject root && Rebuild(root) is { } rebuilt)
        {
            document["root"] = rebuilt;
        }

        return document;
    }

    /// <summary>예전 기본 모양이면 새 모양, 아니면 null.</summary>
    internal static JsonObject? Rebuild(JsonObject root)
    {
        if (!Is(root, "Split", "Vertical") || root["children"] is not JsonArray outer || outer.Count != 2
            || outer[0] is not JsonObject middle || !Is(middle, "Split", "Horizontal") || middle["children"] is not JsonArray inner || inner.Count < 2
            || outer[1] is not JsonObject bottom || !Is(bottom, "ToolGroup", null)
            || inner[0] is not JsonObject left || !Is(left, "ToolGroup", null)
            || !inner.Skip(1).OfType<JsonObject>().Any(n => Is(n, "Documents", null)))
        {
            return null;
        }

        var leftShare = Share(left) ?? 0.22;
        var bottomShare = Share(bottom) ?? 0.28;
        var rest = inner.Skip(1).OfType<JsonObject>().Select(n => (JsonObject)n.DeepClone()).ToList();
        JsonObject top;
        if (rest.Count == 1)
        {
            top = rest[0];
        }
        else
        {
            // 문서·오른쪽 묶음의 비율은 왼쪽을 뺀 나머지 안의 몫으로 다시 잰다.
            foreach (var n in rest)
            {
                if (Share(n) is { } p)
                {
                    n["proportion"] = Math.Round(p / (1 - leftShare), 4);
                }
            }

            top = Split("Horizontal", null, [.. rest]);
        }

        top["proportion"] = 1 - bottomShare;
        var bottomCopy = (JsonObject)bottom.DeepClone();
        bottomCopy["proportion"] = bottomShare;
        var leftCopy = (JsonObject)left.DeepClone();
        leftCopy["proportion"] = leftShare;
        var column = Split("Vertical", 1 - leftShare, [top, bottomCopy]);
        return Split("Horizontal", Share(root), [leftCopy, column]);
    }

    private static bool Is(JsonObject node, string kind, string? orientation) =>
        string.Equals((string?)node["kind"], kind, StringComparison.Ordinal)
        && (orientation is null || string.Equals((string?)node["orientation"] ?? "Horizontal", orientation, StringComparison.Ordinal));

    private static double? Share(JsonObject node) => node["proportion"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static JsonObject Split(string orientation, double? proportion, JsonObject[] children)
    {
        var node = new JsonObject
        {
            ["kind"] = "Split",
            ["orientation"] = orientation,
            ["children"] = new JsonArray([.. children]),
            ["tools"] = new JsonArray(),
        };
        if (proportion is { } p)
        {
            node["proportion"] = p;
        }

        return node;
    }
}
