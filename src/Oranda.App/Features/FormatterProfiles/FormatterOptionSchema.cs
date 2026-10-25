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
        ["keywordAlign"] = "절 키워드 맞춤(Left/Right)",
        ["clauseLayout"] = "절 배치(Indent/Column = 하우스 서식 7열)",
        ["operatorSpacing"] = "연산자 앞뒤 공백",
        ["parenInnerSpace"] = "괄호 안쪽 공백",
        ["lineBreaks.selectList"] = "SELECT 목록 배치",
        ["lineBreaks.groupOrderBy"] = "GROUP BY·ORDER BY 목록 배치",
        ["lineBreaks.orderBy"] = "ORDER BY 목록만 따로",
        ["lineBreaks.joinClause"] = "JOIN 줄바꿈",
        ["lineBreaks.andOr"] = "AND/OR 줄바꿈",
        ["lineBreaks.inListThreshold"] = "IN 목록 줄바꿈 개수(0=안 함)",
        ["lineBreaks.maxLineWidth"] = "최대 줄 너비",
        ["align.alias"] = "별칭 정렬",
        ["align.assignment"] = "대입 정렬",
        ["align.columnDefinition"] = "열 정의 정렬",
        ["align.equals"] = "= 정렬",
        ["align.comparison"] = "외부 조인·범위 비교 맞춤",
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

    // 화면(TSK-13-42) 묶음·짧은 라벨·단위·한국어 값 이름. 라벨(Labels)은 그대로 두고 툴팁·낭독기 이름으로 쓴다.
    private static readonly Dictionary<string, string> GroupOfPath = new(StringComparer.Ordinal)
    {
        ["case.keyword"] = CaseGroup,
        ["case.function"] = CaseGroup,
        ["case.identifier"] = CaseGroup,
        ["indent.useTabs"] = IndentGroup,
        ["indent.size"] = IndentGroup,
        ["indent.subquery"] = IndentGroup,
        ["indent.case"] = IndentGroup,
        ["indent.plsqlBlock"] = IndentGroup,
        ["lineBreaks.selectList"] = LineBreakGroup,
        ["lineBreaks.groupOrderBy"] = LineBreakGroup,
        ["lineBreaks.orderBy"] = LineBreakGroup,
        ["lineBreaks.andOr"] = LineBreakGroup,
        ["lineBreaks.joinClause"] = LineBreakGroup,
        ["lineBreaks.inListThreshold"] = LineBreakGroup,
        ["lineBreaks.maxLineWidth"] = LineBreakGroup,
        ["clauseLayout"] = CommaGroup,
        ["commaPosition"] = CommaGroup,
        ["keywordAlign"] = CommaGroup,
        ["align.alias"] = AlignGroup,
        ["align.assignment"] = AlignGroup,
        ["align.columnDefinition"] = AlignGroup,
        ["align.equals"] = AlignGroup,
        ["align.comparison"] = AlignGroup,
        ["operatorSpacing"] = AlignGroup,
        ["parenInnerSpace"] = AlignGroup,
        ["comments.preserve"] = EtcGroup,
        ["comments.maxBlankLines"] = EtcGroup,
        ["statementTerminator"] = EtcGroup,
        ["honorFormatterOff"] = EtcGroup,
    };

    private static readonly Dictionary<string, string> ShortLabels = new(StringComparer.Ordinal)
    {
        ["case.keyword"] = "키워드",
        ["case.function"] = "함수",
        ["case.identifier"] = "식별자",
        ["indent.useTabs"] = "탭 문자로 들여쓰기",
        ["indent.size"] = "칸 수",
        ["indent.subquery"] = "서브쿼리",
        ["indent.case"] = "CASE",
        ["indent.plsqlBlock"] = "PL/SQL 블록",
        ["commaPosition"] = "목록 쉼표",
        ["keywordAlign"] = "키워드 맞춤",
        ["clauseLayout"] = "절 배치",
        ["operatorSpacing"] = "연산자 앞뒤 공백",
        ["parenInnerSpace"] = "괄호 안쪽 공백",
        ["lineBreaks.selectList"] = "SELECT 목록",
        ["lineBreaks.groupOrderBy"] = "GROUP BY",
        ["lineBreaks.orderBy"] = "ORDER BY",
        ["lineBreaks.joinClause"] = "JOIN 앞에서 줄바꿈",
        ["lineBreaks.andOr"] = "AND·OR",
        ["lineBreaks.inListThreshold"] = "IN 목록 감기",
        ["lineBreaks.maxLineWidth"] = "최대 줄 너비",
        ["align.alias"] = "별칭 맞춤",
        ["align.assignment"] = "대입 := 맞춤",
        ["align.columnDefinition"] = "열 정의 맞춤",
        ["align.equals"] = "= 기호 맞춤",
        ["align.comparison"] = "비교 맞춤",
        ["comments.preserve"] = "주석 보존",
        ["comments.maxBlankLines"] = "빈 줄 최대",
        ["statementTerminator"] = "문장 끝 ;",
        ["honorFormatterOff"] = "@formatter:off 존중",
    };

    private static readonly Dictionary<string, string> Units = new(StringComparer.Ordinal)
    {
        ["lineBreaks.inListThreshold"] = "개 넘으면 · 0=안 함",
        ["lineBreaks.maxLineWidth"] = "자",
    };

    // 고르기 값의 화면 순서와 한국어 이름(값 이름은 FormatterOptions 열거형 그대로).
    private static readonly Dictionary<string, (string Name, string Label)[]> ChoiceLabels = new(StringComparer.Ordinal)
    {
        ["case.keyword"] = CaseChoices,
        ["case.function"] = CaseChoices,
        ["case.identifier"] = CaseChoices,
        ["commaPosition"] = [("Trailing", "뒤에"), ("Leading", "앞에")],
        ["keywordAlign"] = [("Left", "왼쪽"), ("Right", "오른쪽")],
        ["lineBreaks.selectList"] = [("OnePerLine", "한 줄에 하나"), ("FillToWidth", "너비까지 채움")],
        ["lineBreaks.groupOrderBy"] = [("SingleLine", "한 줄"), ("MultiLine", "여러 줄")],
        ["lineBreaks.orderBy"] = [("FollowGroupBy", "= GROUP BY"), ("SingleLine", "한 줄"), ("MultiLine", "여러 줄")],
        ["clauseLayout"] = [("Indent", "들여쓰기"), ("Column", "열 맞춤(하우스)")],
        ["lineBreaks.andOr"] = [("Before", "앞에서"), ("After", "뒤에서"), ("None", "안 함")],
        ["statementTerminator"] = [("Keep", "그대로"), ("Add", "붙임"), ("Remove", "뺌")],
    };

    private static IReadOnlyList<OptionDescriptor>? s_all;

    /// <summary>묶음 「대소문자」(키워드·함수·식별자 × 대문자·소문자·그대로 표).</summary>
    public const string CaseGroup = "대소문자";

    /// <summary>묶음 「들여쓰기」.</summary>
    public const string IndentGroup = "들여쓰기";

    /// <summary>묶음 「줄바꿈」.</summary>
    public const string LineBreakGroup = "줄바꿈";

    /// <summary>묶음 「쉼표·맞춤」.</summary>
    public const string CommaGroup = "쉼표·맞춤";

    /// <summary>묶음 「정렬·공백」.</summary>
    public const string AlignGroup = "정렬·공백";

    /// <summary>묶음 「주석·기타」.</summary>
    public const string EtcGroup = "주석·기타";

    /// <summary>화면 묶음(이 순서로 놓는다).</summary>
    public static IReadOnlyList<string> Groups { get; } = [CaseGroup, IndentGroup, LineBreakGroup, CommaGroup, AlignGroup, EtcGroup];

    private static (string Name, string Label)[] CaseChoices => [("Upper", "대문자"), ("Lower", "소문자"), ("AsIs", "그대로")];

    /// <summary>옵션의 화면 묶음. 모르는 경로면 「주석·기타」.</summary>
    public static string GroupOf(string path) => GroupOfPath.GetValueOrDefault(path, EtcGroup);

    /// <summary>묶음 안에서 보일 짧은 라벨(긴 라벨은 <see cref="OptionDescriptor.Label"/>).</summary>
    public static string ShortLabelOf(string path) => ShortLabels.GetValueOrDefault(path) ?? LabelOf(path);

    /// <summary>숫자 옵션 뒤에 붙는 단위·설명(없으면 null).</summary>
    public static string? UnitOf(string path) => Units.GetValueOrDefault(path);

    /// <summary>고르기 값(화면 순서)과 한국어 이름. 표에 없는 값은 값 이름 그대로 뒤에 붙인다.</summary>
    public static IReadOnlyList<(string Name, string Label)> ChoicesOf(OptionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var known = ChoiceLabels.GetValueOrDefault(descriptor.Path) ?? [];
        var list = known.Where(k => descriptor.Choices.Contains(k.Name)).ToList();
        list.AddRange(descriptor.Choices.Where(c => list.All(k => k.Name != c)).Select(c => (c, c)));
        return list;
    }

    /// <summary>
    /// 지금 값들로 보아 이 옵션이 출력에 효과가 없으면 그 이유(효과가 있으면 null). 절 배치가 열 맞춤(하우스 서식)이면 「키워드 맞춤」은 쓰지 않고,
    /// 「비교 맞춤」은 열 맞춤에서만 쓴다(TSK-13-43).
    /// </summary>
    public static string? InapplicableReason(string path, Func<string, string?> choiceOf)
    {
        ArgumentNullException.ThrowIfNull(choiceOf);
        var column = choiceOf("clauseLayout") == nameof(ClauseLayout.Column);
        return path switch
        {
            "keywordAlign" when column => "절 배치가 열 맞춤(하우스 서식)이면 쓰지 않습니다",
            "align.comparison" when !column => "절 배치가 열 맞춤(하우스 서식)일 때만 씁니다",
            _ => null,
        };
    }

    /// <summary>고르기 값의 한국어 이름(모르면 값 이름).</summary>
    public static string ChoiceLabelOf(string path, string? name)
        => (ChoiceLabels.GetValueOrDefault(path) ?? []).FirstOrDefault(k => k.Name == name).Label ?? name ?? string.Empty;

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

/// <summary>
/// 옵션 하나의 편집 행. 값이 바뀌면 <see cref="Changed"/>. 프로필을 바꿔도 행은 그대로 두고 값만 다시 읽는다(<see cref="Load"/>).
/// 기준(Oranda 기본)·저장한 값과 비교해 「기본과 다름」(<see cref="IsChanged"/>)·「저장 안 한 변경」(<see cref="IsDirty"/>)을 알린다.
/// 고칠 수 없는 행(기본 제공 프로필)은 값을 바꾸려 해도 그대로 두고 화면을 원래 값으로 되돌린다.
/// </summary>
public sealed class OptionRow : ObservableObject
{
    private bool _boolValue;
    private decimal _numberValue;
    private string? _choiceValue;
    private bool _isEditable;
    private bool _isChanged;
    private bool _isDirty;
    private JsonNode? _baseline;
    private JsonNode? _saved;
    private string? _inapplicableReason;

    public OptionRow(OptionDescriptor descriptor, bool isEditable)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        _isEditable = isEditable;
        ChoiceItems = descriptor.Kind == OptionKind.Choice
            ? FormatterOptionSchema.ChoicesOf(descriptor).Select(c => new ChoiceItem(this, c.Name, c.Label)).ToList()
            : [];
    }

    public OptionDescriptor Descriptor { get; }

    public string Label => Descriptor.Label;

    public string Path => Descriptor.Path;

    /// <summary>묶음 안에서 보일 짧은 라벨.</summary>
    public string ShortLabel => FormatterOptionSchema.ShortLabelOf(Path);

    /// <summary>화면 묶음 이름.</summary>
    public string Group => FormatterOptionSchema.GroupOf(Path);

    /// <summary>숫자 뒤 단위·설명(없으면 null).</summary>
    public string? Unit => FormatterOptionSchema.UnitOf(Path);

    /// <summary>고르기 값(화면 순서, 한국어 이름). 고르기 옵션이 아니면 비어 있다.</summary>
    public IReadOnlyList<ChoiceItem> ChoiceItems { get; }

    public bool IsEditable
    {
        get => _isEditable;
        set => SetProperty(ref _isEditable, value);
    }

    public bool IsBool => Descriptor.Kind == OptionKind.Bool;

    public bool IsInt => Descriptor.Kind == OptionKind.Number;

    public bool IsChoice => Descriptor.Kind == OptionKind.Choice;

    public IReadOnlyList<string> Choices => Descriptor.Choices;

    public decimal Minimum => Descriptor.Min;

    public decimal Maximum => Descriptor.Max;

    /// <summary>기준 프로필(Oranda 기본)과 값이 다르다(파란 점).</summary>
    public bool IsChanged
    {
        get => _isChanged;
        private set
        {
            if (SetProperty(ref _isChanged, value))
            {
                OnMarkerChanged();
            }
        }
    }

    /// <summary>저장한 값과 다르다(주황 점).</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnMarkerChanged();
            }
        }
    }

    /// <summary>라벨을 굵게(기본과 다르거나 저장 안 한 변경).</summary>
    public bool IsMarked => _isChanged || _isDirty;

    /// <summary>파란 점을 보인다(저장 안 한 변경이 아니고 기본과 다름).</summary>
    public bool ShowsChanged => _isChanged && !_isDirty;

    /// <summary>점의 툴팁.</summary>
    public string? MarkerTip => _isDirty ? "저장 안 한 변경" : _isChanged ? "기본과 다름" : null;

    /// <summary>다른 옵션 값 때문에 이 옵션이 효과가 없으면 그 이유(효과가 있으면 null). 화면은 흐리게 두고 툴팁으로 보인다.</summary>
    public string? InapplicableReason
    {
        get => _inapplicableReason;
        set
        {
            if (SetProperty(ref _inapplicableReason, value))
            {
                OnPropertyChanged(nameof(IsApplicable));
                OnPropertyChanged(nameof(Tip));
            }
        }
    }

    /// <summary>지금 값들로 보아 출력에 효과가 있다.</summary>
    public bool IsApplicable => _inapplicableReason is null;

    /// <summary>툴팁: 긴 라벨(효과가 없으면 「 — 이유」를 붙인다).</summary>
    public string Tip => _inapplicableReason is { } why ? Label + " — " + why : Label;

    /// <summary>지금 값의 한국어 글(대문자·켬·4 …).</summary>
    public string DisplayValue => Descriptor.Kind switch
    {
        OptionKind.Bool => _boolValue ? "켬" : "끔",
        OptionKind.Number => ((int)_numberValue).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => FormatterOptionSchema.ChoiceLabelOf(Path, _choiceValue),
    };

    public event Action<OptionRow>? Changed;

    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (!_isEditable)
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _boolValue, value))
            {
                AfterChange();
            }
        }
    }

    public decimal NumberValue
    {
        get => _numberValue;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), Descriptor.Min, Descriptor.Max);
            if (!_isEditable)
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _numberValue, clamped))
            {
                AfterChange();
            }
            else if (clamped != value)
            {
                OnPropertyChanged(); // 범위 밖 값을 넣으면 칸을 맞춘 값으로 되돌린다.
            }
        }
    }

    public string? ChoiceValue
    {
        get => _choiceValue;
        set
        {
            if (!_isEditable)
            {
                OnPropertyChanged();
                RefreshChoices();
                return;
            }

            if (value is not null && SetProperty(ref _choiceValue, value))
            {
                AfterChange();
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

    /// <summary>노드 값을 읽어 넣는다. 화면에는 알리지만 <see cref="Changed"/> 는 올리지 않는다.</summary>
    public void Load(JsonNode? node)
    {
        switch (Descriptor.Kind)
        {
            case OptionKind.Bool:
                _boolValue = node is JsonValue b && b.TryGetValue<bool>(out var bv) && bv;
                OnPropertyChanged(nameof(BoolValue));
                break;
            case OptionKind.Number:
                _numberValue = node is JsonValue n && n.TryGetValue<int>(out var nv) ? nv : 0;
                OnPropertyChanged(nameof(NumberValue));
                break;
            default:
                _choiceValue = node is JsonValue c && c.TryGetValue<string>(out var cv) ? cv : Descriptor.Choices[0];
                OnPropertyChanged(nameof(ChoiceValue));
                RefreshChoices();
                break;
        }

        OnPropertyChanged(nameof(DisplayValue));
        Recompute();
    }

    /// <summary>비교 기준: <paramref name="baseline"/> = Oranda 기본 값, <paramref name="saved"/> = 저장한 값.</summary>
    public void SetReference(JsonNode? baseline, JsonNode? saved)
    {
        _baseline = baseline?.DeepClone();
        _saved = saved?.DeepClone();
        Recompute();
    }

    private void AfterChange()
    {
        OnPropertyChanged(nameof(DisplayValue));
        RefreshChoices();
        Recompute();
        Changed?.Invoke(this);
    }

    private void Recompute()
    {
        var now = ToNode();
        IsChanged = _baseline is not null && !JsonNode.DeepEquals(now, _baseline);
        IsDirty = _saved is not null && !JsonNode.DeepEquals(now, _saved);
    }

    private void RefreshChoices()
    {
        foreach (var c in ChoiceItems)
        {
            c.Refresh();
        }
    }

    private void OnMarkerChanged()
    {
        OnPropertyChanged(nameof(IsMarked));
        OnPropertyChanged(nameof(ShowsChanged));
        OnPropertyChanged(nameof(MarkerTip));
    }
}

/// <summary>고르기 옵션의 값 하나(라디오 단추 하나). 켜면 그 값을 고른다.</summary>
public sealed class ChoiceItem : ObservableObject
{
    private readonly OptionRow _row;

    internal ChoiceItem(OptionRow row, string name, string label)
    {
        _row = row;
        Name = name;
        Label = label;
    }

    /// <summary>값 이름(FormatterOptions 열거형 이름).</summary>
    public string Name { get; }

    /// <summary>한국어 이름.</summary>
    public string Label { get; }

    public bool IsChecked
    {
        get => _row.ChoiceValue == Name;
        set
        {
            // 끄기는 무시한다: 다른 값을 켜면 그 값이 고른 값이 된다.
            if (value)
            {
                _row.ChoiceValue = Name;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsChecked));
}
