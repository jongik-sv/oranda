using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// <c>--output-schema</c> 용 변환. 모델 서버의 strict 구조 출력은 모든 속성이 <c>required</c> 이어야 하고 <c>additionalProperties:false</c> 여야 한다.
/// 요청 스키마(<c>TuningResult.SchemaJson</c> 등)는 선택 칸이 있으므로 여기서 바꾼다(원본 스키마는 고치지 않는다):
/// 선택 칸은 required 에 올리되 null 을 허용한다. 결과에서는 원래 선택이던 칸의 null 을 지워 Claude 경로와 같은 모양으로 돌려준다.
/// </summary>
internal static class CodexOutputSchema
{
    public static string ToStrict(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText()) ?? throw new ArgumentException("스키마가 비어 있습니다.", nameof(schema));
        Strictify(node);
        return node.ToJsonString();
    }

    /// <summary>모델이 낸 결과에서 원래 선택 칸이던 null 을 지운다(원본 스키마 기준).</summary>
    public static JsonElement StripOptionalNulls(JsonElement result, JsonElement originalSchema)
    {
        var data = JsonNode.Parse(result.GetRawText());
        var schema = JsonNode.Parse(originalSchema.GetRawText());
        Strip(data, schema);
        return JsonSerializer.SerializeToElement(data);
    }

    private static void Strictify(JsonNode? node)
    {
        if (node is not JsonObject schema)
        {
            return;
        }

        if (schema["properties"] is JsonObject properties)
        {
            var required = schema["required"] is JsonArray existing
                ? existing.Select(n => n?.GetValue<string>()).Where(n => n is not null).Cast<string>().ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var (name, property) in properties.ToList())
            {
                Strictify(property);
                if (!required.Contains(name))
                {
                    AllowNull(property);
                }
            }

            schema["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
            schema["additionalProperties"] = false;
        }

        if (schema["items"] is JsonNode items)
        {
            Strictify(items);
        }
    }

    private static void AllowNull(JsonNode? property)
    {
        if (property is not JsonObject obj)
        {
            return;
        }

        switch (obj["type"])
        {
            case JsonValue single when single.TryGetValue<string>(out var type):
                if (type != "null")
                {
                    obj["type"] = new JsonArray(type, "null");
                }

                break;
            case JsonArray types when !types.Any(t => t?.GetValue<string>() == "null"):
                types.Add("null");
                break;
        }

        if (obj["enum"] is JsonArray values && !values.Any(v => v is null))
        {
            values.Add(null);
        }
    }

    private static void Strip(JsonNode? data, JsonNode? schema)
    {
        if (schema is not JsonObject schemaObject)
        {
            return;
        }

        if (data is JsonObject obj && schemaObject["properties"] is JsonObject properties)
        {
            var required = schemaObject["required"] is JsonArray list
                ? list.Select(n => n?.GetValue<string>()).Where(n => n is not null).Cast<string>().ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var (name, value) in obj.ToList())
            {
                if (value is null && !required.Contains(name))
                {
                    obj.Remove(name);
                }
                else if (properties[name] is { } propertySchema)
                {
                    Strip(value, propertySchema);
                }
            }
        }
        else if (data is JsonArray array && schemaObject["items"] is { } itemSchema)
        {
            foreach (var item in array)
            {
                Strip(item, itemSchema);
            }
        }
    }
}
