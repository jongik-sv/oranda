using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Settings;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>
/// 「스키마 브라우저 필터」 대화상자(TSK-13-10, 오렌지 Schema Browser Filter). 필터마다 고칠 수 있는 사본(<see cref="FilterDraft"/>)을 두고,
/// 이름 콤보로 바꿔 가며 고친다(바꿔도 고친 내용은 남는다). 저장은 목록을 넘기고 창을 닫지 않으며, 확인은 검사를 통과해야 닫는다.
/// </summary>
public sealed partial class SchemaFilterEditorViewModel : ObservableObject
{
    private const string NewName = "새 필터";
    private readonly Func<IReadOnlyList<SchemaBrowserFilter>, Task>? _persist;

    public SchemaFilterEditorViewModel(
        IReadOnlyList<SchemaBrowserFilter> filters,
        string? selectedId,
        IReadOnlyList<string> allSchemas,
        Func<IReadOnlyList<SchemaBrowserFilter>, Task>? persist = null)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(allSchemas);
        AllSchemas = allSchemas;
        _persist = persist;
        foreach (var f in filters)
        {
            AddDraft(FilterDraft.From(f, allSchemas));
        }

        Selected = Filters.FirstOrDefault(d => d.Id == selectedId) ?? Filters.FirstOrDefault();
    }

    public ObservableCollection<FilterDraft> Filters { get; } = [];

    /// <summary>스키마 탭 콤보의 후보(지금 접속의 스키마 목록).</summary>
    public IReadOnlyList<string> AllSchemas { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private FilterDraft? _selected;

    [ObservableProperty]
    private string? _schemaToAdd;

    /// <summary>객체 탭 아래 「필터」 칸: 모든 유형에 적용할 이름 패턴.</summary>
    [ObservableProperty]
    private string _globalPattern = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasSelection => Selected is not null;

    public bool HasError => Error is not null;

    /// <summary>저장·확인 단추가 켜지는 조건: 어떤 필터에도 잘못된 스키마 정규식이 없다.</summary>
    public bool CanConfirm => Filters.All(f => f.PatternError is null);

    /// <summary>확인 뒤 이 접속이 고를 필터 id. 고른 필터가 없으면 null(필터 없음).</summary>
    public string? SelectedId => Selected?.Id;

    public void New()
    {
        var name = NewName;
        for (var i = 2; Filters.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{NewName} ({i})";
        }

        var draft = FilterDraft.From(new SchemaBrowserFilter { Name = name }, AllSchemas);
        AddDraft(draft);
        Selected = draft;
    }

    private void AddDraft(FilterDraft draft)
    {
        draft.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilterDraft.PatternError))
            {
                OnPropertyChanged(nameof(CanConfirm));
            }
        };
        Filters.Add(draft);
        OnPropertyChanged(nameof(CanConfirm));
    }

    public void Delete()
    {
        if (Selected is not { } current)
        {
            return;
        }

        var index = Filters.IndexOf(current);
        Filters.Remove(current);
        OnPropertyChanged(nameof(CanConfirm));
        Selected = Filters.Count == 0 ? null : Filters[Math.Min(index, Filters.Count - 1)];
    }

    public void AddSchema()
    {
        var name = SchemaToAdd?.Trim();
        if (Selected is not { } d || string.IsNullOrEmpty(name))
        {
            return;
        }

        if (!d.Schemas.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            d.Schemas.Add(name);
        }

        d.IsSelectedScope = true;
    }

    public void RemoveSchema(string? name)
    {
        if (Selected is { } d && name is not null)
        {
            d.Schemas.Remove(name);
        }
    }

    public void SelectAll() => SetShown(true);

    public void DeselectAll() => SetShown(false);

    private void SetShown(bool shown)
    {
        foreach (var t in Selected?.Types ?? [])
        {
            t.IsShown = shown;
        }
    }

    public void AssignGlobally()
    {
        foreach (var t in Selected?.Types ?? [])
        {
            t.Pattern = GlobalPattern;
        }
    }

    /// <summary>객체 탭을 처음 상태로: 모든 유형 보이기, 패턴 없음.</summary>
    public void Reset()
    {
        foreach (var t in Selected?.Types ?? [])
        {
            t.IsShown = true;
            t.Pattern = "";
        }

        GlobalPattern = "";
    }

    /// <summary>이름이 비었거나 겹치면 오류를 보이고 false.</summary>
    public bool Validate()
    {
        var blank = Filters.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Name));
        if (blank is not null)
        {
            Selected = blank;
            Error = "필터 이름을 넣어 주세요";
            return false;
        }

        var duplicate = Filters.GroupBy(f => f.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            Error = $"같은 이름의 필터가 있습니다: {Selected?.Name.Trim() ?? duplicate.Key}";
            return false;
        }

        var badPattern = Filters.FirstOrDefault(f => f.PatternError is not null);
        if (badPattern is not null)
        {
            Selected = badPattern;
            Error = $"스키마 패턴이 올바르지 않습니다: {badPattern.PatternError}";
            return false;
        }

        Error = null;
        return true;
    }

    public IReadOnlyList<SchemaBrowserFilter> BuildFilters() => [.. Filters.Select(f => f.ToFilter())];

    /// <summary>「저장」: 검사를 통과하면 목록을 넘긴다(창은 닫지 않는다).</summary>
    public async Task<bool> SaveAsync()
    {
        if (!Validate())
        {
            return false;
        }

        if (_persist is not null)
        {
            await _persist(BuildFilters()).ConfigureAwait(true);
        }

        return true;
    }
}

/// <summary>대화상자에서 고치는 필터 한 개의 사본.</summary>
public sealed partial class FilterDraft : ObservableObject
{
    private IReadOnlyList<string> _allSchemas = [];

    private FilterDraft(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllScope), nameof(IsLogonScope), nameof(IsWithObjectsScope), nameof(IsSelectedScope), nameof(IsPatternScope))]
    private SchemaFilterScope _scope;

    /// <summary>정규식으로 고르기에 적은 패턴. 바뀌면 오류·맞는 스키마 목록을 다시 구한다.</summary>
    [ObservableProperty]
    private string _schemaPattern = "";

    /// <summary>잘못된 스키마 정규식의 이유. 정규식 범위가 아니거나 올바르면 null(저장·확인을 막는 근거).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatternError))]
    private string? _patternError;

    /// <summary>패턴에 맞는 스키마 미리보기(지금 접속의 스키마 목록 기준).</summary>
    public ObservableCollection<string> PatternMatches { get; } = [];

    public bool HasPatternError => PatternError is not null;

    public string PatternMatchCountText => $"맞는 스키마 {PatternMatches.Count}/{_allSchemas.Count}개";

    partial void OnSchemaPatternChanged(string value) => RefreshPattern();

    partial void OnScopeChanged(SchemaFilterScope value) => RefreshPattern();

    private void RefreshPattern()
    {
        PatternMatches.Clear();
        if (Scope != SchemaFilterScope.Pattern)
        {
            PatternError = null;
            OnPropertyChanged(nameof(PatternMatchCountText));
            return;
        }

        var regex = SavedFilterMatcher.TryParseSchemaPattern(SchemaPattern, out var error);
        PatternError = error;
        if (error is null)
        {
            foreach (var schema in _allSchemas)
            {
                if (IsMatch(regex, schema))
                {
                    PatternMatches.Add(schema);
                }
            }
        }

        OnPropertyChanged(nameof(PatternMatchCountText));
    }

    // 시간 제한을 넘기면 그 스키마는 안 맞는 것으로 본다(매처와 같은 규칙).
    private static bool IsMatch(System.Text.RegularExpressions.Regex? regex, string schema)
    {
        try
        {
            return regex?.IsMatch(schema) ?? true;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [ObservableProperty]
    private bool _underscoreIsWildcard;

    [ObservableProperty]
    private bool _wrapWithPercent = true;

    public ObservableCollection<string> Schemas { get; } = [];

    public ObservableCollection<FilterTypeRow> Types { get; } = [];

    // 라디오 단추 다섯이 한 값(Scope)을 나눠 쓴다. 꺼짐(false)은 다른 단추가 켜지며 생기므로 무시한다.
    public bool IsAllScope { get => Scope == SchemaFilterScope.All; set => SetScope(value, SchemaFilterScope.All); }

    public bool IsLogonScope { get => Scope == SchemaFilterScope.LogonSchema; set => SetScope(value, SchemaFilterScope.LogonSchema); }

    public bool IsWithObjectsScope { get => Scope == SchemaFilterScope.SchemasWithObjects; set => SetScope(value, SchemaFilterScope.SchemasWithObjects); }

    public bool IsSelectedScope { get => Scope == SchemaFilterScope.Selected; set => SetScope(value, SchemaFilterScope.Selected); }

    public bool IsPatternScope { get => Scope == SchemaFilterScope.Pattern; set => SetScope(value, SchemaFilterScope.Pattern); }

    private void SetScope(bool on, SchemaFilterScope scope)
    {
        if (on)
        {
            Scope = scope;
        }
    }

    public static FilterDraft From(SchemaBrowserFilter filter, IReadOnlyList<string>? allSchemas = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        // _allSchemas 를 먼저 둬야 패턴·범위를 넣을 때 맞는 스키마 목록이 채워진다.
        var d = new FilterDraft(filter.Id)
        {
            _allSchemas = allSchemas ?? [],
            Name = filter.Name,
            SchemaPattern = filter.SchemaPattern ?? "",
            Scope = filter.Scope,
            UnderscoreIsWildcard = filter.UnderscoreIsWildcard,
            WrapWithPercent = filter.WrapWithPercent,
        };
        d.RefreshPattern();
        foreach (var s in filter.Schemas)
        {
            d.Schemas.Add(s);
        }

        // 트리 범주 순서대로, 그 뒤에 범주에 없는 유형(이 필터에만 적힌 것).
        var known = SchemaBrowserViewModel.CategoryTitles;
        var extra = filter.HiddenTypes.Concat(filter.NamePatterns.Keys)
            .Where(t => known.All(k => k.Type != t))
            .Distinct(StringComparer.Ordinal)
            .Select(t => (Type: t, Title: t));
        foreach (var (type, title) in known.Concat(extra))
        {
            d.Types.Add(new FilterTypeRow(type, title)
            {
                IsShown = !filter.HiddenTypes.Contains(type, StringComparer.Ordinal),
                Pattern = filter.NamePatterns.TryGetValue(type, out var p) ? p : "",
            });
        }

        return d;
    }

    public SchemaBrowserFilter ToFilter() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Scope = Scope,
        Schemas = [.. Schemas],
        SchemaPattern = string.IsNullOrWhiteSpace(SchemaPattern) ? null : SchemaPattern.Trim(),
        HiddenTypes = [.. Types.Where(t => !t.IsShown).Select(t => t.Type)],
        NamePatterns = Types.Where(t => !string.IsNullOrWhiteSpace(t.Pattern))
            .ToDictionary(t => t.Type, t => t.Pattern.Trim(), StringComparer.Ordinal),
        UnderscoreIsWildcard = UnderscoreIsWildcard,
        WrapWithPercent = WrapWithPercent,
    };
}

/// <summary>객체 탭의 한 줄: 유형 보이기와 그 유형의 이름 패턴.</summary>
public sealed partial class FilterTypeRow(string type, string title) : ObservableObject
{
    public string Type { get; } = type;

    public string Title { get; } = title;

    [ObservableProperty]
    private bool _isShown = true;

    [ObservableProperty]
    private string _pattern = "";
}
