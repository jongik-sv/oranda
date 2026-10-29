using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.SchemaBrowser;

// SchemaBrowserViewModel 의 일부: 트리 로딩·필터·더 보기(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SchemaBrowserViewModel
{
    partial void OnFilterTextChanged(string value) => ScheduleFilter();

    private void OnTypesChanged(object? sender, EventArgs e)
    {
        RememberTypes();
        ScheduleFilter();
    }

    partial void OnInvalidOnlyChanged(bool value)
    {
        _invalidByOwner.Clear(); // 켜고 끌 때는 INVALID 목록을 서버에서 새로 읽는다
        ScheduleFilter();
    }

    partial void OnFavoritesOnlyChanged(bool value) => ScheduleFilter();

    private void ScheduleFilter()
    {
        if (_suppressFilterSchedule)
        {
            return;
        }

        _debounce?.Cancel();
        if (DebounceMilliseconds <= 0)
        {
            _ = ApplyFilterCoreAsync(_filter.InvalidOnly == InvalidOnly, default);
            return;
        }

        var cts = _debounce = new CancellationTokenSource();
        _ = DelayedApplyAsync(cts.Token);
    }

    private async Task DelayedApplyAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, ct);
            await ApplyFilterCoreAsync(_filter.InvalidOnly == InvalidOnly, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>스키마 목록을 다시 읽어 트리를 처음부터 만든다(새로 고침 · 접속 바뀜).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        ResolveServices();
        FollowConnections(); // 첫 접속 뒤에 만든 창도 지금 접속을 따라간다
        CurrentSession ??= ConnectionManager.SessionRouter.CurrentOf(_services);
        // 트리 짓기는 자기 세대로 낡음을 판단한다: 짓는 사이 필터가 다시 걸려도(_generation) 짓기를 멈추지 않는다.
        var gen = Interlocked.Increment(ref _buildGeneration);
        Interlocked.Increment(ref _generation); // 옛 트리 노드의 읽기는 버린다
        Roots.Clear();
        _favoritesNode = null;
        SelectedNode = null;
        OnPropertyChanged(nameof(HasSession));
        var session = CurrentSession;
        if (session is null || _meta is null)
        {
            StatusText = "접속하면 객체 트리가 나타난다";
            return;
        }

        IsBusy = true;
        try
        {
            var schemas = await _meta.ListSchemasAsync(session, ct);
            var favs = await _favoriteStore.LoadAsync(session.Profile.Id, ct);
            ActiveSavedFilter = ResolveActiveFilter(session);
            Types.Restore(ResolveTypes(session));
            var matcher = await BuildMatcherAsync(session, ct);
            if (gen != _buildGeneration)
            {
                return;
            }

            _favorites = favs;
            _allSchemas = schemas;
            _matcher = matcher;
            _filter = BuildFilter();
            _favoritesNode = new SchemaNode(this, SchemaNodeKind.Favorites, "즐겨찾기");
            _favoritesNode.ResetChildren();
            Roots.Add(_favoritesNode);
            var visible = VisibleSchemas(session);
            foreach (var name in visible)
            {
                Roots.Add(NewSchemaNode(name));
            }

            StatusText = SchemaCountText(visible.Count);
            var own = Roots.FirstOrDefault(r => r.Kind == SchemaNodeKind.Schema
                && string.Equals(r.Owner, session.Profile.User, StringComparison.OrdinalIgnoreCase));
            if (own is not null)
            {
                // 읽기 전에 펼친 것으로 둔다: 읽는 사이 필터가 다시 걸리면 펼친 스키마로 보고 새 필터로 다시 읽는다.
                own.IsExpandedSilently = true;
                await ExpandAsync(own, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = "스키마 목록을 읽지 못했다: " + ex.Message;
        }
        finally
        {
            if (gen == _buildGeneration)
            {
                IsBusy = false;
            }
        }
    }

    private SchemaFilter BuildFilter() => new(FilterText, Types.SelectedTypes, InvalidOnly, FavoritesOnly);

    /// <summary>필터를 적용한다: 이미 불러온 스키마 노드를 비우고 펼쳐 있던 것만 다시 읽는다.</summary>
    public Task ApplyFilterAsync(CancellationToken ct = default) => ApplyFilterCoreAsync(reuseInvalid: false, ct);

    /// <param name="reuseInvalid">참이면 앞서 읽은 INVALID 목록을 다시 쓴다(이름·유형 거르기만 바뀐 때: 서버를 한 번 덜 부른다).</param>
    private async Task ApplyFilterCoreAsync(bool reuseInvalid, CancellationToken ct)
    {
        _filter = BuildFilter();
        _reuseInvalid = reuseInvalid;
        var gen = Interlocked.Increment(ref _generation);
        var reload = new List<SchemaNode>();

        // 다시 읽어도 펼쳐 둔 범주와 고른 객체는 그대로 둔다(컴파일·DDL 뒤 트리가 접히지 않게).
        var openCategories = new Dictionary<SchemaNode, HashSet<string>>();
        var selectedKey = SelectedNode?.Ref is { } picked ? SchemaSql.FavoriteKey(picked) : null;
        foreach (var root in Roots.Where(r => r.Kind == SchemaNodeKind.Schema))
        {
            if (root.Loaded || root.IsExpanded)
            {
                reload.Add(root);
                openCategories[root] = root.Children
                    .Where(c => c.Kind == SchemaNodeKind.Category && c.IsExpanded && c.CategoryType is not null)
                    .Select(c => c.CategoryType!)
                    .ToHashSet(StringComparer.Ordinal);
                // 읽는 동안 옛 트리를 그대로 둔다: 지우고 「…」 를 보였다가 다시 짓는 깜박임·접힘·스크롤 튐을 없앤다.
                // 새 결과는 LoadSchemaAsync 가 한 번에 바꿔 끼운다.
                root.Loaded = false;
                continue;
            }

            root.ResetChildren();
        }

        foreach (var n in reload)
        {
            if (gen != _generation)
            {
                return;
            }

            await ExpandAsync(n, ct);
            foreach (var cat in n.Children.Where(c => c.Kind == SchemaNodeKind.Category && c.CategoryType is { } t && openCategories[n].Contains(t)).ToList())
            {
                await ExpandAsync(cat, ct);
                cat.IsExpandedSilently = true;
                if (selectedKey is not null
                    && cat.Children.FirstOrDefault(c => c.Ref is { } r && SchemaSql.FavoriteKey(r) == selectedKey) is { } again)
                {
                    SelectedNode = again;
                }
            }
        }
    }

    /// <summary>노드가 펼쳐질 때 자식을 읽는다. 이미 읽었거나 읽는 중이면 아무것도 안 한다.</summary>
    public async Task ExpandAsync(SchemaNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        // 같은 세대의 읽기가 이미 돌고 있으면 기다리지 않는다. 앞 세대의 읽기는 끝나도 버려지므로 새로 읽는다.
        if (node.Loaded || (node.Loading && node.LoadGeneration == _generation) || !node.HasChildrenToLoad)
        {
            return;
        }

        var session = CurrentSession;
        if (session is null || _meta is null)
        {
            return;
        }

        var gen = _generation;
        node.Loading = true;
        node.LoadGeneration = gen;
        BeginLoad();
        try
        {
            switch (node.Kind)
            {
                case SchemaNodeKind.Schema:
                    await LoadSchemaAsync(node, session, ct);
                    break;
                case SchemaNodeKind.Category:
                    node.Children.Clear();
                    node.Shown = 0;
                    AddPage(node);
                    break;
                case SchemaNodeKind.Item:
                    AddFolders(node);
                    break;
                case SchemaNodeKind.Folder:
                    await LoadFolderAsync(node, session, ct);
                    break;
                case SchemaNodeKind.Favorites:
                    LoadFavorites(node);
                    break;
            }

            if (gen == _generation)
            {
                node.Loaded = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (gen == _generation)
        {
            node.Children.Clear();
            node.Children.Add(new SchemaNode(null, SchemaNodeKind.Message, "읽지 못했다: " + ex.Message));
        }
        catch (Exception)
        {
            // 버려진 세대의 실패는 새 읽기의 결과를 덮지 않는다.
        }
        finally
        {
            if (node.LoadGeneration == gen)
            {
                node.Loading = false;
            }

            EndLoad();
        }
    }

    /// <summary>읽기 하나가 시작됐다(트리 짓기·노드 펼치기). 읽는 동안 패널이 진행 표시를 보인다.</summary>
    private void BeginLoad()
    {
        _loadsInFlight++;
        IsLoading = true;
    }

    /// <summary>읽기 하나가 끝났다(버려진 읽기 포함). 남은 읽기와 트리 짓기가 없으면 진행 표시를 끈다.</summary>
    private void EndLoad()
    {
        _loadsInFlight--;
        IsLoading = _loadsInFlight > 0 || IsBusy;
    }

    partial void OnIsBusyChanged(bool value) => IsLoading = value || _loadsInFlight > 0;

    private async Task LoadSchemaAsync(SchemaNode node, ISession session, CancellationToken ct)
    {
        var f = _filter;
        var query = new ObjectQuery
        {
            Owner = node.Owner,
            ObjectTypes = f.Types,
            NamePattern = SchemaFilter.ToLikePattern(f.Text),
            InvalidOnly = f.InvalidOnly,
        };
        var gen = _generation;

        // 목록 조회 결과에는 상태가 없으므로 INVALID 객체만 먼저 따로 읽어 트리에서 빨갛게 보인다(Orange 처럼 깨진 객체가 바로 보이게).
        HashSet<string>? invalid = null;
        if (!f.InvalidOnly)
        {
            invalid = _reuseInvalid && node.Owner is { } ownerKey && _invalidByOwner.TryGetValue(ownerKey, out var kept)
                ? kept
                : (await _meta!.ListObjectsAsync(session, query with { InvalidOnly = true, ObjectTypes = [], NamePattern = null }, ct))
                    .Select(SchemaSql.FavoriteKey).ToHashSet(StringComparer.Ordinal);
        }

        if (node.Owner is { } owner && invalid is not null)
        {
            _invalidByOwner[owner] = invalid;
        }

        var objects = await _meta!.ListObjectsAsync(session, query, ct);

        // Java 저장 객체는 객체 목록 캐시와 따로 이 소유자만 묻는다(행 수 제한, JVM 이 없으면 빈 목록). 실패해도 다른 객체는 보인다.
        var (java, javaCut) = await ListJavaAsync(session, query, ct);
        if (java.Count > 0)
        {
            foreach (var j in java)
            {
                _javaNames[j.Ref] = j.LongName;
                if (invalid is not null && j.Status is { } st && !string.Equals(st, "VALID", StringComparison.Ordinal))
                {
                    invalid.Add(SchemaSql.FavoriteKey(j.Ref));
                }
            }

            objects = [.. objects, .. java.Select(j => j.Ref)];
        }

        var javaNames = new Dictionary<DbObjectRef, string>(_javaNames);
        var favorites = _favorites;
        var matcher = _matcher;
        var groups = await Task.Run(
            () =>
            {
                IEnumerable<DbObjectRef> src = objects;
                if (f.FavoritesOnly)
                {
                    src = src.Where(o => favorites.Contains(SchemaSql.FavoriteKey(o)));
                }

                if (matcher is not null)
                {
                    src = src.Where(matcher.Shows); // 저장 필터: 숨긴 유형·유형별 이름 패턴
                }

                return src.GroupBy(o => o.ObjectType, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<DbObjectRef>)[.. g.OrderBy(o => javaNames.GetValueOrDefault(o, o.Name), StringComparer.Ordinal)], StringComparer.Ordinal);
            },
            ct);
        if (gen != _generation)
        {
            return;
        }

        node.Children.Clear();
        var known = new HashSet<string>(Categories.Select(c => c.Type), StringComparer.Ordinal);
        var ordered = Categories.Select(c => (c.Type, c.Title))
            .Concat(groups.Keys.Where(k => !known.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).Select(k => (Type: k, Title: k)));
        foreach (var (type, title) in ordered)
        {
            if (!groups.TryGetValue(type, out var items) || items.Count == 0)
            {
                continue;
            }

            var cat = new SchemaNode(this, SchemaNodeKind.Category, $"{title} ({items.Count})")
            {
                Owner = node.Owner,
                CategoryType = type,
                Parent = node,
                Items = items,
            };
            cat.Children.Add(new SchemaNode(null, SchemaNodeKind.Placeholder, "…"));
            node.Children.Add(cat);
        }

        if (node.Children.Count == 0)
        {
            node.Children.Add(new SchemaNode(null, SchemaNodeKind.Message, "해당하는 객체가 없다"));
        }

        StatusText = $"{node.Owner}: 객체 {objects.Count}개" + (ActiveSavedFilter is { } saved ? $" · 필터 「{saved.Name}」" : "")
            + (javaCut ? $" · Java 객체는 처음 {JavaListLimit}개만" : "");
    }

    /// <summary>스키마 하나에서 보이는 Java 객체 최대 수(SYS 쪽 Java 클래스는 수만 개다). 넘으면 상태 줄에 알린다.</summary>
    public const int JavaListLimit = 5000;

    /// <summary>Java 객체의 긴 이름(트리 제목). 키는 짧은 이름의 참조.</summary>
    private readonly Dictionary<DbObjectRef, string> _javaNames = [];

    /// <summary>스키마의 Java 객체(한 개 더 읽어 잘렸는지 안다). 제공자가 실패하면 빈 목록.</summary>
    private async Task<(IReadOnlyList<JavaObjectInfo> Items, bool Cut)> ListJavaAsync(ISession session, ObjectQuery query, CancellationToken ct)
    {
        try
        {
            var items = await _meta!.ListJavaObjectsAsync(session, query with { Limit = JavaListLimit + 1 }, ct);
            return items.Count > JavaListLimit ? ([.. items.Take(JavaListLimit)], true) : (items, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ([], false);
        }
    }

    /// <summary>트리에 보이는 객체 이름: Java 객체는 긴 이름(<c>com/example/Util</c>), 나머지는 사전 이름.</summary>
    internal string DisplayName(DbObjectRef r) => _javaNames.TryGetValue(r, out var longName) ? longName : r.Name;

    private void AddPage(SchemaNode category)
    {
        var invalid = category.Owner is { } owner && _invalidByOwner.TryGetValue(owner, out var set) ? set : null;
        var next = category.Items.Skip(category.Shown).Take(PageSize).ToList();
        foreach (var r in next)
        {
            var status = _filter.InvalidOnly || (invalid?.Contains(SchemaSql.FavoriteKey(r)) ?? false) ? "INVALID" : null;
            category.Children.Add(MakeObject(r, category, status));
        }

        category.Shown += next.Count;
        if (category.Shown < category.Items.Count)
        {
            category.Children.Add(new SchemaNode(this, SchemaNodeKind.More, $"더 보기… ({category.Items.Count - category.Shown}개 남음)") { Parent = category });
        }
    }

    private SchemaNode MakeObject(DbObjectRef r, SchemaNode? parent, string? status = null, string? detail = null)
    {
        var n = new SchemaNode(this, SchemaNodeKind.Item, DisplayName(r))
        {
            Ref = r,
            Owner = r.Owner,
            Parent = parent,
            Status = status,
            Detail = detail,
            IsFavorite = _favorites.Contains(SchemaSql.FavoriteKey(r)),
        };
        n.ResetChildren();
        return n;
    }

    /// <summary>「더 보기」 노드를 눌렀을 때: 다음 쪽을 올린다.</summary>
    public void ShowMore(SchemaNode more)
    {
        ArgumentNullException.ThrowIfNull(more);
        if (more.Kind != SchemaNodeKind.More || more.Parent is not { } cat)
        {
            return;
        }

        cat.Children.Remove(more);
        AddPage(cat);
    }

    /// <summary>필터를 모두(빠른 필터·저장 필터) 풀고 바로 적용한다(입력 지연 타이머는 쓰지 않는다 — 다시 읽기가 겹치지 않게).</summary>
    private async Task ClearFilterAsync(CancellationToken ct)
    {
        _suppressFilterSchedule = true;
        try
        {
            FilterText = string.Empty;
            Types.Reset();
            InvalidOnly = false;
            FavoritesOnly = false;
        }
        finally
        {
            _suppressFilterSchedule = false;
        }

        _debounce?.Cancel();
        if (ActiveSavedFilter is not null)
        {
            await SelectSavedFilterAsync(null, ct); // 숨겼던 스키마를 되살리고 다시 거른다
        }
        else
        {
            await ApplyFilterAsync(ct);
        }
    }
}
