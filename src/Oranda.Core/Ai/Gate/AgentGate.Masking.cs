using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Core.Ai.Gate;

// 결과 가림(설계 §5.4): 실제 열 값이 들어가는 필드(AgentFields.RowValueFields)를 허용 수준과 민감 컬럼 규칙에 따라 지운다.
public sealed partial class AgentGate
{
    /// <summary>행 값 필드 이름을 <see cref="NormalizeName"/> 으로 정규화한 집합(endpointValue·EndpointValue·endpoint_value 를 한 이름으로 본다).</summary>
    private static readonly HashSet<string> RowValueNames = [.. AgentFields.RowValueFields.Select(NormalizeName)];

    /// <summary>열 신원이 되는 속성 이름(정규화 뒤): 값이 열 이름 글이거나, 열 이름 글을 가진 객체다.</summary>
    private static readonly HashSet<string> ColumnIdentityNames = [.. AgentFields.ColumnIdentityFields.Select(NormalizeName)];

    /// <summary>
    /// 결과에서 행 값 필드를 지우고 지운 속성 수를 돌려준다(<paramref name="result"/> 를 직접 고치며, 결과 루트가 JSON 글이면 교체될 수 있다).
    /// <list type="bullet">
    /// <item><description><see cref="AiAccessLevel.QueryRows"/> 미만: 모든 깊이에서 <see cref="AgentFields.RowValueFields"/> 속성을 지운다.</description></item>
    /// <item><description><see cref="AiAccessLevel.QueryRows"/>: <c>get_histogram</c> 의 <c>column</c> 인자가 민감하면 결과 전체에서, 그 밖에는 열 이름이 민감 컬럼으로 식별되는 객체 안에서만 지운다.</description></item>
    /// </list>
    /// 문자열 값이 JSON 객체·배열 글이면(모든 수준에서) 파싱해 같은 규칙을 적용하고, 바뀐 것만 문자열로 되돌려 넣는다.
    /// </summary>
    private static int MaskRowValues(ref JsonNode result, string toolName, AiAccessLevel level, JsonObject arguments)
    {
        Func<JsonNode?, int> strip = StripSensitiveColumns;
        if (level < AiAccessLevel.QueryRows
            || (toolName == AgentToolNames.GetHistogram
                && TryGetString(arguments, AgentFields.Column, out var column)
                && SensitiveColumns.IsSensitive(column)))
        {
            strip = StripRowValues;
        }

        // 루트도 문자열일 수 있어 배열에 담아 같은 방식으로 다룬다.
        var holder = new JsonArray { result };
        var expansions = new List<Expansion>();
        ExpandEmbeddedJson(holder, expansions);

        var removed = strip(holder);

        // 안쪽(나중에 펼친 것)부터 문자열로 되돌린다. 값이 바뀌지 않았으면 원래 글 그대로.
        for (var i = expansions.Count - 1; i >= 0; i--)
        {
            expansions[i].Restore();
        }

        // 결과를 다음 단계(SQL 글 가림)가 다시 배열에 담을 수 있도록 임시 배열에서 뗀다(부모가 있는 노드는 다른 곳에 담을 수 없다).
        result = holder[0]!;
        holder.RemoveAt(0);
        return removed;
    }

    /// <summary>SQL 글·술어 필드 이름을 <see cref="NormalizeName"/> 으로 정규화한 집합(<c>access_predicates</c>·<c>accessPredicates</c> 를 한 이름으로 본다).</summary>
    private static readonly HashSet<string> SqlTextNames = [.. AgentFields.SqlTextFields.Select(NormalizeName)];

    /// <summary>
    /// 결과에서 SQL 글 필드(<see cref="AgentFields.SqlTextFields"/>)의 문자열을 <see cref="ISqlLiteralMasker.MaskPredicate"/> 로 가린다
    /// (<paramref name="result"/> 를 직접 고치며, 루트가 JSON 글이면 교체될 수 있다). 이름이 정해진 필드만 가린다 — 모든 문자열을 가리면 <c>note</c>·숫자가 망가진다.
    /// 주석도 프롬프트와 같은 사전의 주석 자리표시(<c>/*c1*/</c>)가 되어, LLM 이 프롬프트와 도구 결과에서 같은 주석을 같은 모양으로 본다.
    /// 문자열 값이 JSON 객체·배열 글이면 펼쳐서 같은 규칙을 적용한다. 필드 값이 배열·객체면 안의 문자열을 모두 가린다.
    /// </summary>
    private void MaskSqlTextFields(ref JsonNode result, LiteralVault vault)
    {
        var holder = new JsonArray { result };
        var expansions = new List<Expansion>();
        ExpandEmbeddedJson(holder, expansions);

        MaskSqlTexts(holder, vault);

        for (var i = expansions.Count - 1; i >= 0; i--)
        {
            expansions[i].Restore();
        }

        result = holder[0]!;
        holder.RemoveAt(0);
    }

    private void MaskSqlTexts(JsonNode? node, LiteralVault vault)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (SqlTextNames.Contains(NormalizeName(name)))
                    {
                        var item = obj[name];
                        var masked = MaskSqlValue(item, vault);
                        if (!ReferenceEquals(masked, item))
                        {
                            obj[name] = masked;
                        }
                    }
                    else
                    {
                        MaskSqlTexts(obj[name], vault);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    MaskSqlTexts(item, vault);
                }

                break;
        }
    }

    /// <summary>SQL 글 필드의 값: 문자열은 가리고, 배열·객체는 안의 문자열을 모두 가린다. 그 밖의 값(숫자·불리언·null)은 그대로.</summary>
    private JsonNode? MaskSqlValue(JsonNode? value, LiteralVault vault)
    {
        switch (value)
        {
            case JsonValue text when text.TryGetValue<string>(out var s):
                return JsonValue.Create(_masker!.MaskPredicate(s, vault));
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    // 문자열만 새 노드로 바꾼다. 배열·객체·숫자는 제자리에 둔다(같은 노드를 다시 대입하면 부모가 있다는 예외가 난다).
                    var item = array[i];
                    var masked = MaskSqlValue(item, vault);
                    if (!ReferenceEquals(masked, item))
                    {
                        array[i] = masked;
                    }
                }

                return array;
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var item = obj[name];
                    var masked = MaskSqlValue(item, vault);
                    if (!ReferenceEquals(masked, item))
                    {
                        obj[name] = masked;
                    }
                }

                return obj;
            default:
                return value;
        }
    }

    /// <summary>행 값 필드를 가릴 수 있는 모양인가(허용 수준이 <see cref="AiAccessLevel.QueryRows"/> 미만일 때). 결과 루트는 객체나 배열이어야 한다.</summary>
    private static bool IsMaskableShape(JsonNode result) => result is JsonObject or JsonArray;

    /// <summary>문자열 값 하나가 JSON 글로 펼쳐진 기록. <see cref="Restore"/> 가 가린 결과를 문자열로 되돌린다.</summary>
    private sealed record Expansion(JsonNode Parent, string? Key, int Index, string Original, JsonNode Parsed, string Before)
    {
        public void Restore()
        {
            var after = Parsed.ToJsonString();
            var text = string.Equals(after, Before, StringComparison.Ordinal) ? Original : after;
            if (Parent is JsonObject obj)
            {
                obj[Key!] = JsonValue.Create(text);
            }
            else
            {
                ((JsonArray)Parent)[Index] = JsonValue.Create(text);
            }
        }
    }

    /// <summary>트리 안의 JSON 글 문자열을 실제 노드로 펼친다(<paramref name="expansions"/> 에 기록, 바깥쪽이 먼저). 파싱 실패·깊이 64 초과·JSON 이 아닌 글은 그대로 둔다.</summary>
    private static void ExpandEmbeddedJson(JsonNode? node, List<Expansion> expansions)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (TryParseEmbeddedJson(child, out var text, out var parsed))
                    {
                        expansions.Add(new Expansion(obj, key, -1, text, parsed, parsed.ToJsonString()));
                        obj[key] = parsed;
                        ExpandEmbeddedJson(parsed, expansions);
                    }
                    else
                    {
                        ExpandEmbeddedJson(child, expansions);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (TryParseEmbeddedJson(child, out var text, out var parsed))
                    {
                        expansions.Add(new Expansion(array, null, i, text, parsed, parsed.ToJsonString()));
                        array[i] = parsed;
                        ExpandEmbeddedJson(parsed, expansions);
                    }
                    else
                    {
                        ExpandEmbeddedJson(child, expansions);
                    }
                }

                break;
        }
    }

    private static bool TryParseEmbeddedJson(JsonNode? node, out string text, out JsonNode parsed)
    {
        text = string.Empty;
        parsed = null!;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var candidate))
        {
            return false;
        }

        var trimmed = candidate.AsSpan().TrimStart();
        if (trimmed.Length < 2 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            var result = JsonNode.Parse(candidate, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
            if (result is not (JsonObject or JsonArray))
            {
                return false;
            }

            text = candidate;
            parsed = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>모든 깊이에서 행 값 필드(이름 정규화 뒤 비교)를 지운다.</summary>
    private static int StripRowValues(JsonNode? node)
    {
        var removed = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (RowValueNames.Contains(NormalizeName(name)) && obj.Remove(name))
                    {
                        removed++;
                    }
                }

                foreach (var pair in obj)
                {
                    removed += StripRowValues(pair.Value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    removed += StripRowValues(item);
                }

                break;
        }

        return removed;
    }

    /// <summary>
    /// 열 이름이 민감 컬럼으로 식별되는 객체(와 그 안쪽)에서만 행 값 필드를 지운다. 속성 키 자체가 민감 컬럼 이름이면
    /// (<c>{"columns":{"JUMIN_NO":{…}}}</c> 같은 사전 모양) 그 값 아래의 행 값 필드를 모두 지운다.
    /// </summary>
    private static int StripSensitiveColumns(JsonNode? node)
    {
        var removed = 0;
        switch (node)
        {
            case JsonObject obj:
                if (IdentifiesSensitiveColumn(obj))
                {
                    return StripRowValues(obj);
                }

                foreach (var pair in obj.ToList())
                {
                    removed += SensitiveColumns.IsSensitive(pair.Key)
                        ? StripRowValues(pair.Value)
                        : StripSensitiveColumns(pair.Value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    removed += StripSensitiveColumns(item);
                }

                break;
        }

        return removed;
    }

    /// <summary>
    /// 객체 자신이나 자식이 민감 열을 가리키는가: <see cref="AgentFields.ColumnIdentityFields"/> 속성이 민감한 글이거나,
    /// 값이 객체이고 그 안의 같은 이름 속성이 민감한 글이다.
    /// </summary>
    private static bool IdentifiesSensitiveColumn(JsonObject obj)
    {
        foreach (var pair in obj)
        {
            if (!ColumnIdentityNames.Contains(NormalizeName(pair.Key)))
            {
                continue;
            }

            switch (pair.Value)
            {
                case JsonValue value when value.TryGetValue<string>(out var text) && SensitiveColumns.IsSensitive(text):
                    return true;
                case JsonObject inner:
                    foreach (var innerPair in inner)
                    {
                        if (ColumnIdentityNames.Contains(NormalizeName(innerPair.Key))
                            && innerPair.Value is JsonValue innerValue
                            && innerValue.TryGetValue<string>(out var innerText)
                            && SensitiveColumns.IsSensitive(innerText))
                        {
                            return true;
                        }
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>필드 이름 비교용: 소문자로 바꾸고 <c>_</c>·<c>-</c> 를 뺀다.</summary>
    private static string NormalizeName(string name) =>
        name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
