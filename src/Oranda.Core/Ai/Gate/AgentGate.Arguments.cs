using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Core.Ai.Gate;

// 인자 정리(스키마에 선언된 키만 정확히 골라 새 객체로 복사)와 감사 기록용 인자 요약.
public sealed partial class AgentGate
{
    /// <summary>인자 문자열 한 개의 최대 길이. 넘으면 거부한다(분석기·DB 세션에 거대한 글을 보내지 않는다).</summary>
    private const int MaxArgumentChars = 20_000;

    private static readonly ConcurrentDictionary<string, ToolSchema> Schemas = new(StringComparer.Ordinal);

    /// <summary>
    /// 호출 인자를 도구 스키마(<see cref="AgentToolDefinition.InputSchemaJson"/>)에 맞춰 <paramref name="clean"/> 에 옮긴다.
    /// 스키마에 선언된 최상위 키를 대소문자까지 정확히 맞춰 복사본으로 담고, 선언되지 않은 키(대소문자만 다른 키 포함)·빠진 필수 키·틀린 타입은 거부한다.
    /// 거부해도 선언된 키는 <paramref name="clean"/> 에 남겨 감사 기록의 인자 요약에 쓴다. 통과하면 null.
    /// </summary>
    private static GateOutcome? CleanArguments(AgentToolDefinition tool, JsonNode? arguments, JsonObject clean)
    {
        JsonObject source;
        if (arguments is null)
        {
            source = [];
        }
        else if (arguments is JsonObject obj)
        {
            source = obj;
        }
        else
        {
            return Deny("invalid_arguments", "The tool arguments must be a JSON object.");
        }

        var schema = Schemas.GetOrAdd(tool.Name, _ => ToolSchema.Parse(tool.InputSchemaJson));
        var given = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        string? undeclared = null;
        foreach (var pair in source)
        {
            given[pair.Key] = pair.Value;
            if (undeclared is null && !schema.ByName.ContainsKey(pair.Key))
            {
                undeclared = pair.Key;
            }
        }

        // 스키마 순서대로 옮긴다. 선택 키의 null 은 「없음」으로 본다(모델이 비워 둔 칸을 null 로 채우는 일이 흔하다).
        foreach (var property in schema.Properties)
        {
            if (given.TryGetValue(property.Name, out var value) && value is not null)
            {
                clean[property.Name] = value.DeepClone();
            }
        }

        if (undeclared is not null)
        {
            return Deny("invalid_arguments", $"Unknown argument '{Clip(undeclared, 40)}'. Use only the arguments declared by the tool schema, spelled exactly.");
        }

        foreach (var property in schema.Properties)
        {
            if (!clean.TryGetPropertyValue(property.Name, out var value))
            {
                if (property.Required)
                {
                    return Deny("invalid_arguments", $"The argument '{property.Name}' is required.");
                }

                continue;
            }

            if (property.Validate(value) is { } problem)
            {
                return Deny("invalid_arguments", $"The argument '{property.Name}' {problem}.");
            }

            if (HasLongString(value))
            {
                return Deny("invalid_arguments", $"The argument '{property.Name}' contains a text longer than {MaxArgumentChars} characters.");
            }

            if (property.Name == AgentFields.SqlId && !(value is JsonValue id && id.TryGetValue<string>(out var idText) && SqlIdPattern().IsMatch(idText)))
            {
                return Deny("invalid_arguments", "The argument 'sql_id' must be 13 lowercase letters or digits.");
            }
        }

        return null;
    }

    [GeneratedRegex(@"\A[0-9a-z]{13}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SqlIdPattern();

    /// <summary>SQL 글을 인자로 받거나(<c>sql</c>·<c>variants[].sql</c>) 결과에 SQL 글·술어를 싣는 도구. 리터럴 가림이 필요하면 사전이 있어야 쓸 수 있다.</summary>
    private static bool TouchesSql(string toolName) =>
        toolName is AgentToolNames.ExplainPlan or AgentToolNames.GetCursorPlan or AgentToolNames.GetTuningContext or AgentToolNames.BenchmarkQueries;

    /// <summary>
    /// <c>sql</c>·<c>variants[].sql</c> 의 자리표시(<c>:v1</c>)를 원래 글로 되돌려 <paramref name="clean"/> 에 넣는다.
    /// 사전의 모양인데 사전에 없는 자리표시가 있으면 <c>literal_unknown</c> 으로 거부한다(LLM 이 지어낸 값을 실행하지 않는다).
    /// </summary>
    private GateOutcome? RestoreLiterals(CallState state, LiteralVault vault)
    {
        var clean = state.Clean;
        state.PlaceholderCount = 0;
        if (TryGetString(clean, AgentFields.Sql, out var sql))
        {
            // 감사 요약은 LLM 이 보낸 가린 글 기준이다: 되돌리기 전에 길이와 자리표시 사용 수를 적어 둔다.
            state.MaskedSqlLength = sql.Length;
            state.PlaceholderCount += CountPlaceholders(sql, vault);
            var restored = _masker!.Restore(sql, vault);
            if (!restored.Succeeded)
            {
                return UnknownLiteral(restored);
            }

            clean[AgentFields.Sql] = JsonValue.Create(restored.Text);
        }

        if (clean[AgentFields.Variants] is JsonArray variants)
        {
            foreach (var item in variants)
            {
                if (item is JsonObject variant && TryGetString(variant, AgentFields.Sql, out var variantSql))
                {
                    state.PlaceholderCount += CountPlaceholders(variantSql, vault);
                    var restored = _masker!.Restore(variantSql, vault);
                    if (!restored.Succeeded)
                    {
                        return UnknownLiteral(restored);
                    }

                    variant[AgentFields.Sql] = JsonValue.Create(restored.Text);
                }
            }
        }

        return null;
    }

    /// <summary>글에 나온 이 사전의 자리표시(<c>:v1</c> 꼴) 개수. 같은 자리표시가 여러 번 나오면 나온 만큼 센다.</summary>
    private static int CountPlaceholders(string text, LiteralVault vault) =>
        Regex.Count(text, @"(?<![\w:]):" + Regex.Escape(vault.Prefix) + @"\d+(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static GateOutcome UnknownLiteral(SqlRestoreResult restored) =>
        Deny(
            "literal_unknown",
            $"The SQL uses the placeholder '{Clip(restored.UnknownPlaceholder, 40)}' that this request did not create; use only the placeholders that appear in the statement you were given.");

    private static bool HasLongString(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value:
                return value.TryGetValue<string>(out var text) && text.Length > MaxArgumentChars;
            case JsonArray array:
                return array.Any(HasLongString);
            case JsonObject obj:
                return obj.Any(pair => HasLongString(pair.Value));
            default:
                return false;
        }
    }

    /// <summary>
    /// 감사 기록 <c>arguments</c> 열의 요약: <c>키=값</c> 쉼표 나열. SQL 글은 절대 넣지 않고 <c>sql=&lt;N chars&gt;</c> 로 길이만, 변형 배열은 개수만 적는다.
    /// 객체 인자는 해석된 <c>OWNER.NAME</c>(없으면 원래 값 앞 100자)이다. 전체 400자 제한.
    /// </summary>
    private static string? SummarizeArguments(CallState state)
    {
        if (state.Clean.Count == 0 && !state.LiteralMasking)
        {
            return null;
        }

        // 가림 표시는 400자 제한에 잘리지 않도록 맨 앞에 둔다.
        var parts = new List<string>();
        if (state.LiteralMasking)
        {
            parts.Add("literals=masked");
            if (state.PlaceholderCount is { } placeholders)
            {
                parts.Add("placeholders=" + placeholders.ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (var pair in state.Clean)
        {
            var key = pair.Key;
            var value = pair.Value;
            string text;
            if (key == AgentFields.Sql)
            {
                // 되돌린 뒤의 길이는 원문 길이를 드러낸다: 가림이 켜져 있으면 LLM 이 보낸 가린 글의 길이를 적는다.
                text = state.MaskedSqlLength is { } maskedLength ? $"<{maskedLength} chars>"
                    : value is JsonValue v && v.TryGetValue<string>(out var sql) ? $"<{sql.Length} chars>" : "<invalid>";
            }
            else if (key == AgentFields.Variants)
            {
                text = value is JsonArray array ? array.Count.ToString(CultureInfo.InvariantCulture) : "<invalid>";
            }
            else if ((key == AgentFields.ObjectName || key == AgentFields.Table) && state.Resolved.TryGetValue(key, out var resolved))
            {
                text = Clip(resolved.Owner + "." + resolved.Name, AuditValueMaxChars);
            }
            else
            {
                text = Clip(Render(value), AuditValueMaxChars);
            }

            parts.Add(key + "=" + text);
        }

        var summary = string.Join(", ", parts);
        return summary.Length > AuditArgumentsMaxChars ? summary[..AuditArgumentsMaxChars] : summary;
    }

    private static string Render(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonArray array => string.Join('|', array.Select(Render)),
        _ => node.ToJsonString(),
    };

    /// <summary>스키마에서 게이트가 쓰는 부분: 최상위 속성의 타입·필수·범위.</summary>
    private sealed class ToolSchema
    {
        private ToolSchema(IReadOnlyList<SchemaProperty> properties)
        {
            Properties = properties;
            ByName = properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
        }

        public IReadOnlyList<SchemaProperty> Properties { get; }

        public Dictionary<string, SchemaProperty> ByName { get; }

        public static ToolSchema Parse(string json)
        {
            var root = JsonNode.Parse(json)!.AsObject();
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (root["required"] is JsonArray requiredArray)
            {
                foreach (var item in requiredArray)
                {
                    required.Add(item!.GetValue<string>());
                }
            }

            var properties = new List<SchemaProperty>();
            foreach (var pair in root["properties"]!.AsObject())
            {
                properties.Add(SchemaProperty.Parse(pair.Key, pair.Value!.AsObject(), required.Contains(pair.Key)));
            }

            return new ToolSchema(properties);
        }
    }

    private sealed class SchemaProperty
    {
        private SchemaProperty(string name, string type, bool required)
        {
            Name = name;
            Type = type;
            Required = required;
        }

        public string Name { get; }

        public string Type { get; }

        public bool Required { get; }

        private string? ItemType { get; init; }

        private long? Minimum { get; init; }

        private long? Maximum { get; init; }

        private int? MinItems { get; init; }

        private int? MaxItems { get; init; }

        public static SchemaProperty Parse(string name, JsonObject node, bool required) =>
            new(name, node["type"]?.GetValue<string>() ?? "string", required)
            {
                ItemType = node["items"]?["type"]?.GetValue<string>(),
                Minimum = node["minimum"]?.GetValue<long>(),
                Maximum = node["maximum"]?.GetValue<long>(),
                MinItems = node["minItems"]?.GetValue<int>(),
                MaxItems = node["maxItems"]?.GetValue<int>(),
            };

        /// <summary>값이 스키마에 맞는지 본다. 어긋나면 「…해야 한다」 꼴의 영어 구절, 맞으면 null.</summary>
        public string? Validate(JsonNode? value)
        {
            switch (Type)
            {
                case "string":
                    return IsString(value) ? null : "must be a string";
                case "integer":
                    if (!TryGetInteger(value, out var number))
                    {
                        return "must be an integer";
                    }

                    return (Minimum is { } min && number < min) || (Maximum is { } max && number > max)
                        ? $"is out of range ({Minimum?.ToString(CultureInfo.InvariantCulture)}..{Maximum?.ToString(CultureInfo.InvariantCulture)})"
                        : null;
                case "array":
                    return ValidateArray(value);
                case "object":
                    return value is JsonObject ? null : "must be an object";
                default:
                    return null;
            }
        }

        private string? ValidateArray(JsonNode? value)
        {
            if (value is not JsonArray array)
            {
                return "must be an array";
            }

            if ((MinItems is { } min && array.Count < min) || (MaxItems is { } max && array.Count > max))
            {
                return $"must have between {MinItems} and {MaxItems} items";
            }

            if (ItemType == "string" && array.Any(item => !IsString(item)))
            {
                return "must contain only strings";
            }

            // benchmark_queries 의 변형: { label, sql } 만, 둘 다 문자열.
            if (Name == AgentFields.Variants)
            {
                foreach (var item in array)
                {
                    if (item is not JsonObject variant
                        || variant.Count != 2
                        || !IsString(variant["label"])
                        || !IsString(variant[AgentFields.Sql]))
                    {
                        return "must contain objects with exactly the string fields 'label' and 'sql'";
                    }
                }
            }

            return null;
        }

        private static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

        private static bool TryGetInteger(JsonNode? node, out long number)
        {
            number = 0;
            return node is JsonValue v
                && v.GetValueKind() == JsonValueKind.Number
                && long.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
        }
    }
}
