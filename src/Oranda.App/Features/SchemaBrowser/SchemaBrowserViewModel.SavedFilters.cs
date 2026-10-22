using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell.Settings;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;

namespace Oranda.App.Features.SchemaBrowser;

// SchemaBrowserViewModel 의 일부: 저장 필터(오렌지 Schema Browser Filter, TSK-13-10). 목록은 모든 접속 공용, 선택은 접속별.
public sealed partial class SchemaBrowserViewModel
{
    private SavedFilterMatcher? _matcher;
    private IReadOnlyList<string> _allSchemas = [];
    private IReadOnlyList<SchemaBrowserFilter> _localFilters = [];

    /// <summary>지금 접속에서 고른 저장 필터. 없으면 null(필터 없음).</summary>
    [ObservableProperty]
    private SchemaBrowserFilter? _activeSavedFilter;

    private SettingsService? Settings => _services?.GetService(typeof(SettingsService)) as SettingsService;

    /// <summary>저장한 필터(모든 접속 공용). 설정 서비스가 없으면(디자이너·일부 시험) 이 창 안에서만 둔다.</summary>
    public IReadOnlyList<SchemaBrowserFilter> SavedFilters => Settings?.Current.SchemaBrowser.Filters ?? _localFilters;

    public bool HasSavedFilter => ActiveSavedFilter is not null;

    /// <summary>지금 접속의 모든 스키마 이름(편집 대화상자의 스키마 고르기 후보).</summary>
    public IReadOnlyList<string> AllSchemaNames => _allSchemas;

    /// <summary><c>oranda.schema.editFilters</c> 가 편집 대화상자를 열라고 알린다(창 주인은 View 가 안다).</summary>
    public event EventHandler? FilterEditorRequested;

    public void RequestFilterEditor() => FilterEditorRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>대화상자의 「저장」: 목록만 바꾸고, 지금 고른 필터가 남아 있으면 그대로 고른다.</summary>
    public Task SaveFiltersKeepingChoiceAsync(IReadOnlyList<SchemaBrowserFilter> filters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var keep = ActiveSavedFilter is { } current && filters.Any(f => f.Id == current.Id) ? current.Id : null;
        return SaveFiltersAsync(filters, keep, ct);
    }

    partial void OnActiveSavedFilterChanged(SchemaBrowserFilter? value) => OnPropertyChanged(nameof(HasSavedFilter));

    private static string ProfileKey(ISession session) => session.Profile.Id.ToString();

    /// <summary>설정에서 이 접속이 고른 필터를 찾는다. 설정 서비스가 없으면 지금 값을 둔다.</summary>
    private SchemaBrowserFilter? ResolveActiveFilter(ISession session)
    {
        if (Settings is not { } settings)
        {
            return ActiveSavedFilter;
        }

        var sb = settings.Current.SchemaBrowser;
        return sb.ActiveFilters.TryGetValue(ProfileKey(session), out var id) ? sb.Filters.FirstOrDefault(f => f.Id == id) : null;
    }

    /// <summary>저장 필터를 고른다(null = 필터 없음). 이 접속의 선택으로 저장하고 트리를 다시 거른다.</summary>
    public async Task SelectSavedFilterAsync(string? filterId, CancellationToken ct = default)
    {
        if (CurrentSession is { } session && Settings is { } settings)
        {
            var key = ProfileKey(session);
            await settings.UpdateAsync(
                s => s with { SchemaBrowser = s.SchemaBrowser with { ActiveFilters = WithChoice(s.SchemaBrowser.ActiveFilters, key, filterId) } },
                ct).ConfigureAwait(true);
        }

        ActiveSavedFilter = filterId is null ? null : SavedFilters.FirstOrDefault(f => f.Id == filterId);
        await RefreshSavedFilterAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 편집 대화상자의 확인: 필터 목록을 통째로 바꾸고 <paramref name="selectId"/> 를 이 접속의 선택으로 둔다.
    /// 지운 필터를 고르고 있던 다른 접속의 선택도 함께 지운다.
    /// </summary>
    public async Task SaveFiltersAsync(IReadOnlyList<SchemaBrowserFilter> filters, string? selectId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var ids = filters.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var key = CurrentSession is { } session ? ProfileKey(session) : null;
        if (Settings is { } settings)
        {
            await settings.UpdateAsync(
                s =>
                {
                    IReadOnlyDictionary<string, string> choices = s.SchemaBrowser.ActiveFilters
                        .Where(kv => ids.Contains(kv.Value))
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                    if (key is not null)
                    {
                        choices = WithChoice(choices, key, selectId);
                    }

                    return s with { SchemaBrowser = s.SchemaBrowser with { Filters = [.. filters], ActiveFilters = choices } };
                },
                ct).ConfigureAwait(true);
        }
        else
        {
            _localFilters = [.. filters];
        }

        OnPropertyChanged(nameof(SavedFilters));
        ActiveSavedFilter = selectId is null ? null : filters.FirstOrDefault(f => f.Id == selectId);
        await RefreshSavedFilterAsync(ct).ConfigureAwait(true);
    }

    private static Dictionary<string, string> WithChoice(IReadOnlyDictionary<string, string> choices, string key, string? filterId)
    {
        var next = choices.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (filterId is null)
        {
            next.Remove(key);
        }
        else
        {
            next[key] = filterId;
        }

        return next;
    }

    /// <summary>고른 필터가 바뀌었다: 보일 스키마를 맞추고(펼친 스키마는 그 노드 그대로) 펼친 것을 새 필터로 다시 읽는다.</summary>
    private async Task RefreshSavedFilterAsync(CancellationToken ct)
    {
        var session = CurrentSession;
        if (session is null || _meta is null || IsBusy)
        {
            return; // 짓는 중이면 짓기가 설정에서 다시 읽는다
        }

        if (_allSchemas.Count == 0)
        {
            await LoadAsync(ct).ConfigureAwait(true);
            return;
        }

        var gen = _buildGeneration;
        BeginLoad();
        try
        {
            var matcher = await BuildMatcherAsync(session, ct).ConfigureAwait(true);
            if (gen != _buildGeneration)
            {
                return;
            }

            _matcher = matcher;
            SyncSchemaRoots(session);
        }
        finally
        {
            EndLoad();
        }

        await ApplyFilterAsync(ct).ConfigureAwait(true);
    }

    /// <summary>고른 필터의 매처. 「객체를 가진 스키마만」 이면 객체 소유자를 UI 스레드 밖에서 모은다.</summary>
    private async Task<SavedFilterMatcher?> BuildMatcherAsync(ISession session, CancellationToken ct)
    {
        if (ActiveSavedFilter is not { } filter)
        {
            return null;
        }

        IReadOnlySet<string>? owners = null;
        if (filter.Scope == SchemaFilterScope.SchemasWithObjects && _meta is { } meta)
        {
            var all = await meta.ListObjectsAsync(session, new ObjectQuery(), ct).ConfigureAwait(true);
            owners = await Task.Run(() => (IReadOnlySet<string>)all.Select(o => o.Owner).ToHashSet(StringComparer.Ordinal), ct).ConfigureAwait(true);
        }

        return new SavedFilterMatcher(filter, session.Profile.User, owners);
    }

    /// <summary>필터가 보이는 스키마로 뿌리 노드를 맞춘다. 남는 스키마는 노드를 그대로 두어 펼침·선택이 유지된다.</summary>
    private void SyncSchemaRoots(ISession session)
    {
        var wanted = VisibleSchemas(session);
        var keep = wanted.ToHashSet(StringComparer.Ordinal);
        foreach (var gone in Roots.Where(r => r.Kind == SchemaNodeKind.Schema && !keep.Contains(r.Owner!)).ToList())
        {
            Roots.Remove(gone);
        }

        var offset = _favoritesNode is not null && Roots.Contains(_favoritesNode) ? 1 : 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = offset + i;
            var existing = Roots.FirstOrDefault(r => r.Kind == SchemaNodeKind.Schema && r.Owner == wanted[i]);
            if (existing is null)
            {
                Roots.Insert(at, NewSchemaNode(wanted[i]));
            }
            else if (Roots.IndexOf(existing) != at)
            {
                Roots.Move(Roots.IndexOf(existing), at);
            }
        }

        StatusText = SchemaCountText(wanted.Count);
    }

    /// <summary>필터를 지난 스키마. 내 스키마(접속 사용자)를 맨 위에 둔다(SQL Developer·Orange 처럼). 나머지는 이름 순서 그대로.</summary>
    private List<string> VisibleSchemas(ISession session) =>
        [.. _allSchemas
            .Where(n => _matcher?.ShowsSchema(n) ?? true)
            .OrderBy(n => string.Equals(n, session.Profile.User, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];

    private SchemaNode NewSchemaNode(string name)
    {
        var n = new SchemaNode(this, SchemaNodeKind.Schema, name) { Owner = name };
        n.ResetChildren();
        return n;
    }

    // 스키마 정규식 경고는 VisibleSchemas 가 정규식을 돌린 뒤에야 정해지므로 호출 순서가 그 뒤여야 한다. 맞추기마다 한 번만 보인다.
    private string SchemaCountText(int shown) => (ActiveSavedFilter is { } f
        ? $"필터 「{f.Name}」 · 스키마 {shown}/{_allSchemas.Count}개"
        : $"스키마 {shown}개")
        + (_matcher?.SchemaPatternWarning is { } warning ? " · " + warning : "");
}
