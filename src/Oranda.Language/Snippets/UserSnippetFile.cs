using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oranda.Language.Snippets;

/// <summary>
/// 사용자 조각 파일 <c>{data}/snippets.json</c> 의 읽기·쓰기(순수 변환, 파일 접근 없음).
/// 형식: <c>{ "schemaVersion": 1, "snippets": [ { "prefix", "name", "description", "body" } ] }</c>.
/// </summary>
public static class UserSnippetFile
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JSON 글자를 조각 목록으로. 형식이 틀리거나 새 버전이면 <see cref="InvalidDataException"/>. 줄임말·본문이 빈 항목은 건너뛴다.</summary>
    public static IReadOnlyList<Snippet> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("사용자 조각 파일 형식이 올바르지 않다: " + ex.Message, ex);
        }

        if (root is not JsonObject obj)
        {
            throw new InvalidDataException("사용자 조각 파일 형식이 올바르지 않다.");
        }

        var version = obj["schemaVersion"]?.GetValue<int>() ?? 1;
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidDataException($"사용자 조각 파일의 schemaVersion {version} 은 지원하지 않는다.");
        }

        var list = new List<Snippet>();
        if (obj["snippets"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                var prefix = item["prefix"]?.GetValue<string>()?.Trim();
                var body = item["body"]?.GetValue<string>();
                if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(body))
                {
                    continue;
                }

                var name = item["name"]?.GetValue<string>() ?? prefix;
                var description = item["description"]?.GetValue<string>() ?? string.Empty;
                list.Add(new Snippet(prefix, name, description, body, SnippetKind.Snippet, IsUser: true));
            }
        }

        return list;
    }

    /// <summary>조각 목록을 JSON 글자로(들여쓰기 2칸).</summary>
    public static string Serialize(IEnumerable<Snippet> snippets)
    {
        ArgumentNullException.ThrowIfNull(snippets);
        var array = new JsonArray();
        foreach (var s in snippets.Where(x => x.Kind == SnippetKind.Snippet))
        {
            array.Add(new JsonObject
            {
                ["prefix"] = s.Prefix,
                ["name"] = s.Name,
                ["description"] = s.Description,
                ["body"] = s.Body,
            });
        }

        var root = new JsonObject { ["schemaVersion"] = CurrentSchemaVersion, ["snippets"] = array };
        return root.ToJsonString(Indented);
    }
}
