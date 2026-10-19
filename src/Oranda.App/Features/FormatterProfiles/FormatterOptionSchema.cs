using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Formatting;

namespace Oranda.App.Features.FormatterProfiles;

public enum OptionKind
{
    Bool,
    Number,
    Choice,
}

/// <summary>옵션 하나의 설명(JSON 경로 = data-model 2.3 의 필드 이름, 예: <c>case.keyword</c>).</summary>
public sealed record OptionDescriptor(string Path, string Label, OptionKind Kind, IReadOnlyList<string> Choices, int Min, int Max);

/// <summary>
/// <see cref="FormatterOptions"/> 계약(속성·형식)을 읽어 화면에 보일 옵션 목록을 만든다. 옵션이 늘면 화면도 따라 늘어난다.
/// 라벨과 숫자 범위만 여기 표로 둔다(data-model 2.3).
/// </summary>
public static class FormatterOptionSchema
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["case.keyword"] = "키워드 대소문자",
        ["case.function"] = "함수 대소문자",
        ["case.identifier"] = "식별자 대소문자",
        ["indent.useTabs"] = "탭 문자로 들여쓰기",
        ["indent.size"] = "들여쓰기 칸 수",
        ["indent.subquery"] = "서브쿼리 들여쓰기 단계",
        ["indent.case"] = "CASE 들여쓰기 단계",
        ["indent.plsqlBlock"] = "PL/SQL 블록 들여쓰기 단계",
        ["commaPosition"] = "쉼표 위치",
        ["operatorSpacing"] = "연산자 앞뒤 공백",
        ["parenInnerSpace"] = "괄호 안쪽 공백",
        ["lineBreaks.selectList"] = "SELECT 목록 배치",
        ["lineBreaks.joinClause"] = "JOIN 줄바꿈",
        ["lineBreaks.andOr"] = "AND/OR 줄바꿈",
        ["lineBreaks.inListThreshold"] = "IN 목록 줄바꿈 개수(0=안 함)",
        ["lineBreaks.maxLineWidth"] = "최대 줄 너비",
        ["align.alias"] = "별칭 정렬",
        ["align.assignment"] = "대입 정렬",
        ["align.columnDefinition"] = "열 정의 정렬",
        ["align.equals"] = "= 정렬",
        ["comments.preserve"] = "주석 보존",
        ["comments.maxBlankLines"] = "연속 빈 줄 최대",
        ["statementTerminator"] = "문장 끝 ;",
        ["honorFormatterOff"] = "@formatter:off 구간 존중",
    };

    private static readonly Dictionary<string, (int Min, int Max)> Ranges = new(StringComparer.Ordinal)
    {
        ["indent.size"] = (1, 16),
        ["indent.subquery"] = (0, 4),
        ["indent.case"] = (0, 4),
        ["indent.plsqlBlock"] = (0, 4),
        ["lineBreaks.inListThreshold"] = (0, 1000),
        ["lineBreaks.maxLineWidth"] = (40, 400),
        ["comments.maxBlankLines"] = (0, 5),
    };

    private static IReadOnlyList<OptionDescriptor>? s_all;

    /// <summary>모든 옵션(선언 순서).</summary>
    public static IReadOnlyList<OptionDescriptor> All => s_all ??= Build(typeof(FormatterOptions), string.Empty).ToList();

    /// <summary>옵션 노드(<c>options</c> 객체)에서 스키마에 없는 키의 경로.</summary>
    public static IReadOnlyList<string> FindUnknown(JsonNode? options)
    {
        var unknown = new List<string>();
        Walk(options as JsonObject, typeof(FormatterOptions), string.Empty, unknown);
        return unknown;
    }

    /// <summary>경로로 노드 값을 읽는다. 없으면 null.</summary>
    public static JsonNode? Get(JsonObject options, string path)
    {
        JsonNode? node = options;
        foreach (var part in path.Split('.'))
        {
            node = (node as JsonObject)?[part];
        }

        return node;
    }

    /// <summary>경로에 값을 넣는다(중간 객체가 없으면 만든다).</summary>
    public static void Set(JsonObject options, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var obj = options;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (obj[parts[i]] is not JsonObject next)
            {
                next = [];
                obj[parts[i]] = next;
            }

            obj = next;
        }

        obj[parts[^1]] = value;
    }

    private static IEnumerable<OptionDescriptor> Build(Type type, string prefix)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (p.GetCustomAttribute<JsonIgnoreAttribute>() is not null || p.GetMethod is null)
            {
                continue;
            }

            var path = prefix + JsonName(p);
            var t = p.PropertyType;
            if (t == typeof(bool))
            {
                yield return new OptionDescriptor(path, LabelOf(path), OptionKind.Bool, [], 0, 1);
            }
            else if (t == typeof(int))
            {
                var (min, max) = Ranges.GetValueOrDefault(path, (0, 1000));
                yield return new OptionDescriptor(path, LabelOf(path), OptionKind.Number, [], min, max);
            }
            else if (t.IsEnum)
            {
                yield return new OptionDescriptor(path, LabelOf(path), OptionKind.Choice, Enum.GetNames(t), 0, 0);
            }
            else if (t.IsClass && t != typeof(string))
            {
                foreach (var d in Build(t, path + "."))
                {
                    yield return d;
                }
            }
        }
    }

    private static void Walk(JsonObject? node, Type type, string prefix, List<string> unknown)
    {
        if (node is null)
        {
            return;
        }

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .ToDictionary(JsonName, p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in node)
        {
            if (!props.TryGetValue(key, out var p))
            {
                unknown.Add(prefix + key);
            }
            else if (p.PropertyType.IsClass && p.PropertyType != typeof(string))
            {
                Walk(value as JsonObject, p.PropertyType, prefix + key + ".", unknown);
            }
        }
    }

    private static string JsonName(PropertyInfo p)
        => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);

    private static string LabelOf(string path) => Labels.GetValueOrDefault(path, path);
}

/// <summary>옵션 하나의 편집 행. 값이 바뀌면 <see cref="Changed"/>.</summary>
public sealed class OptionRow : ObservableObject
{
    private bool _boolValue;
    private decimal _numberValue;
    private string? _choiceValue;

    public OptionRow(OptionDescriptor descriptor, bool isEditable)
    {
        Descriptor = descriptor;
        IsEditable = isEditable;
    }

    public OptionDescriptor Descriptor { get; }

    public string Label => Descriptor.Label;

    public string Path => Descriptor.Path;

    public bool IsEditable { get; }

    public bool IsBool => Descriptor.Kind == OptionKind.Bool;

    public bool IsInt => Descriptor.Kind == OptionKind.Number;

    public bool IsChoice => Descriptor.Kind == OptionKind.Choice;

    public IReadOnlyList<string> Choices => Descriptor.Choices;

    public decimal Minimum => Descriptor.Min;

    public decimal Maximum => Descriptor.Max;

    public event Action<OptionRow>? Changed;

    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (SetProperty(ref _boolValue, value))
            {
                Changed?.Invoke(this);
            }
        }
    }

    public decimal NumberValue
    {
        get => _numberValue;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), Descriptor.Min, Descriptor.Max);
            if (SetProperty(ref _numberValue, clamped))
            {
                Changed?.Invoke(this);
            }
        }
    }

    public string? ChoiceValue
    {
        get => _choiceValue;
        set
        {
            if (value is not null && SetProperty(ref _choiceValue, value))
            {
                Changed?.Invoke(this);
            }
        }
    }

    /// <summary>현재 값을 JSON 노드로.</summary>
    public JsonNode ToNode()
    {
        switch (Descriptor.Kind)
        {
            case OptionKind.Bool:
                return JsonValue.Create(_boolValue);
            case OptionKind.Number:
                return JsonValue.Create((int)_numberValue);
            default:
                return JsonValue.Create(_choiceValue ?? Descriptor.Choices[0])!;
        }
    }

    /// <summary>알림 없이 노드 값을 읽어 넣는다.</summary>
    public void Load(JsonNode? node)
    {
        switch (Descriptor.Kind)
        {
            case OptionKind.Bool:
                _boolValue = node is JsonValue b && b.TryGetValue<bool>(out var bv) && bv;
                break;
            case OptionKind.Number:
                _numberValue = node is JsonValue n && n.TryGetValue<int>(out var nv) ? nv : 0;
                break;
            default:
                _choiceValue = node is JsonValue c && c.TryGetValue<string>(out var cv) ? cv : Descriptor.Choices[0];
                break;
        }
    }
}
