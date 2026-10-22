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
            Filters.Add(FilterDraft.From(f));
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

    /// <summary>확인 뒤 이 접속이 고를 필터 id. 고른 필터가 없으면 null(필터 없음).</summary>
    public string? SelectedId => Selected?.Id;

    public void New()
    {
        var name = NewName;
        for (var i = 2; Filters.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{NewName} ({i})";
        }

        var draft = FilterDraft.From(new SchemaBrowserFilter { Name = name });
        Filters.Add(draft);
        Selected = draft;
    }

    public void Delete()
    {
        if (Selected is not { } current)
        {
            return;
        }

        var index = Filters.IndexOf(current);
        Filters.Remove(current);
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
    private FilterDraft(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllScope), nameof(IsLogonScope), nameof(IsWithObjectsScope), nameof(IsSelectedScope))]
    private SchemaFilterScope _scope;

    [ObservableProperty]
    private bool _underscoreIsWildcard;

    [ObservableProperty]
    private bool _wrapWithPercent = true;

    public ObservableCollection<string> Schemas { get; } = [];

    public ObservableCollection<FilterTypeRow> Types { get; } = [];

    // 라디오 단추 넷이 한 값(Scope)을 나눠 쓴다. 꺼짐(false)은 다른 단추가 켜지며 생기므로 무시한다.
    public bool IsAllScope { get => Scope == SchemaFilterScope.All; set => SetScope(value, SchemaFilterScope.All); }

    public bool IsLogonScope { get => Scope == SchemaFilterScope.LogonSchema; set => SetScope(value, SchemaFilterScope.LogonSchema); }

    public bool IsWithObjectsScope { get => Scope == SchemaFilterScope.SchemasWithObjects; set => SetScope(value, SchemaFilterScope.SchemasWithObjects); }

    public bool IsSelectedScope { get => Scope == SchemaFilterScope.Selected; set => SetScope(value, SchemaFilterScope.Selected); }

    private void SetScope(bool on, SchemaFilterScope scope)
    {
        if (on)
        {
            Scope = scope;
        }
    }

    public static FilterDraft From(SchemaBrowserFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var d = new FilterDraft(filter.Id)
        {
            Name = filter.Name,
            Scope = filter.Scope,
            UnderscoreIsWildcard = filter.UnderscoreIsWildcard,
            WrapWithPercent = filter.WrapWithPercent,
        };
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
