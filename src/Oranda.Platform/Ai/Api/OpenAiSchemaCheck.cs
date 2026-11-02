using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oranda.Platform.Ai.Api;

/// <summary>
/// 구조 결과(StructuredOutput 인자)를 요청에 실은 스키마로 검사한다. 모델이 다시 제출할 수 있게 어긋난 칸의 경로를 돌려준다.
/// 보는 것: <c>type</c>(문자열 또는 배열), <c>required</c>, <c>properties</c>, <c>items</c>, <c>enum</c>. 그 밖의 키워드는 무시한다.
/// <c>type</c> 에 <c>null</c> 이 들어 있는 칸만 null 을 받는다.
/// </summary>
internal static class OpenAiSchemaCheck
{
    private const int MaxIssues = 5;

    /// <summary>어긋난 곳의 설명(최대 몇 개). 모두 맞으면 빈 목록.</summary>
    public static IReadOnlyList<string> Validate(JsonElement schema, JsonNode? value)
    {
        var issues = new List<string>();
        Check(schema, value, "$", issues);
        return issues;
    }

    private static void Check(JsonElement schema, JsonNode? value, string path, List<string> issues)
    {
        if (issues.Count >= MaxIssues || schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!TypeMatches(schema, value))
        {
            issues.Add($"{path}: 형식이 맞지 않습니다({Describe(schema)} 이어야 함)");
            return;
        }

        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array && value is not null
            && !allowed.EnumerateArray().Any(a => string.Equals(a.GetRawText(), value.ToJsonString(), StringComparison.Ordinal)))
        {
            issues.Add($"{path}: 허용된 값이 아닙니다({string.Join(", ", allowed.EnumerateArray().Select(a => a.GetRawText()))} 중 하나)");
            return;
        }

        switch (value)
        {
            case JsonObject obj:
                if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                {
                    foreach (var name in required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!))
                    {
                        if (!obj.TryGetPropertyValue(name, out _))
                        {
                            issues.Add($"{Join(path, name)}: 필수 칸이 없습니다");
                        }
                    }
                }

                if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in properties.EnumerateObject())
                    {
                        if (obj.TryGetPropertyValue(property.Name, out var child))
                        {
                            Check(property.Value, child, Join(path, property.Name), issues);
                        }
                    }
                }

                break;
            case JsonArray array:
                if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                {
                    for (var i = 0; i < array.Count && issues.Count < MaxIssues; i++)
                    {
                        Check(items, array[i], $"{path}[{i}]", issues);
                    }
                }

                break;
        }
    }

    private static string Join(string path, string name) => path == "$" ? name : $"{path}.{name}";

    private static List<string> TypesOf(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList(),
            _ => [],
        };
    }

    private static string Describe(JsonElement schema) => string.Join("|", TypesOf(schema));

    private static bool TypeMatches(JsonElement schema, JsonNode? value)
    {
        var types = TypesOf(schema).ToList();
        if (types.Count == 0)
        {
            return true;
        }

        return types.Any(t => t switch
        {
            "null" => value is null,
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value is JsonValue s && s.GetValueKind() == JsonValueKind.String,
            "boolean" => value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "number" => value is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
            "integer" => value is JsonValue i && i.GetValueKind() == JsonValueKind.Number && IsInteger(i),
            _ => true,
        });
    }

    private static bool IsInteger(JsonValue value) =>
        value.TryGetValue<long>(out _) || (value.TryGetValue<double>(out var d) && d == Math.Floor(d));
}
