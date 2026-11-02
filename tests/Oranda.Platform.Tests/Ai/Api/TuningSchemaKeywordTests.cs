using System.Text.Json;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Platform.Tests.Ai.Api;

/// <summary>
/// 튜닝 스키마는 CLI 경로의 <c>--json-schema</c> 로도 그대로 가고 설치된 claude 가 스키마를 변환한다. 변환이 모르는 키워드(minLength 등)가 들어가면
/// 구독 경로의 동작이 바뀌므로 지금 쓰는 키워드만 허용하도록 고정한다.
/// </summary>
public sealed class TuningSchemaKeywordTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "items", "enum", "description", "additionalProperties",
    };

    [Fact]
    public void The_tuning_schema_uses_only_the_keywords_the_cli_path_already_sent()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        Walk(TuningResult.Schema(), used, insideProperties: false);

        Assert.Empty(used.Where(k => !Allowed.Contains(k)));
    }

    // properties 의 자식 이름은 키워드가 아니라 칸 이름이므로 건너뛴다.
    private static void Walk(JsonElement node, HashSet<string> used, bool insideProperties)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (!insideProperties)
                {
                    used.Add(property.Name);
                }

                Walk(property.Value, used, insideProperties: !insideProperties && property.Name == "properties");
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                Walk(item, used, insideProperties: false);
            }
        }
    }
}
