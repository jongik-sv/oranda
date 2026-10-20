using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.SchemaBrowser;

// SchemaBrowserViewModel 의 일부: 트리 로딩·필터·더 보기(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SchemaBrowserViewModel
{
    partial void OnFilterTextChanged(string value) => ScheduleFilter();

    partial void OnTypeFilterChanged(string? value) => ScheduleFilter();

    partial void OnInvalidOnlyChanged(bool value) => ScheduleFilter();

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
            _ = ApplyFilterAsync();
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
            await ApplyFilterAsync(ct);
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
        var gen = Interlocked.Increment(ref _generation);
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
            if (gen != _generation)
            {
                return;
            }

            _favorites = favs;
            _filter = BuildFilter();
            _favoritesNode = new SchemaNode(this, SchemaNodeKind.Favorites, "즐겨찾기");
            _favoritesNode.ResetChildren();
            Roots.Add(_favoritesNode);
            // 내 스키마(접속 사용자)를 맨 위에 둔다(SQL Developer·Orange 처럼 자기 객체부터 보인다). 나머지는 이름 순서 그대로.
            var ordered = schemas.OrderBy(n => string.Equals(n, session.Profile.User, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            foreach (var name in ordered)
            {
                var n = new SchemaNode(this, SchemaNodeKind.Schema, name) { Owner = name };
                n.ResetChildren();
                Roots.Add(n);
            }

            StatusText = $"스키마 {schemas.Count}개";
            var own = Roots.FirstOrDefault(r => r.Kind == SchemaNodeKind.Schema
                && string.Equals(r.Owner, session.Profile.User, StringComparison.OrdinalIgnoreCase));
            if (own is not null)
            {
                await ExpandAsync(own, ct);
                own.IsExpandedSilently = true; // 화면에서도 펼친 채로 보인다(이미 읽었으니 다시 읽지 않는다).
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
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    private SchemaFilter BuildFilter() => new(FilterText, TypeFilter, InvalidOnly, FavoritesOnly);

    /// <summary>필터를 적용한다: 이미 불러온 스키마 노드를 비우고 펼쳐 있던 것만 다시 읽는다.</summary>
    public async Task ApplyFilterAsync(CancellationToken ct = default)
    {
        _filter = BuildFilter();
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
        if (node.Loaded || node.Loading || !node.HasChildrenToLoad)
        {
            return;
        }

        var session = CurrentSession;
        if (session is null || _meta is null)
        {
            return;
        }

        node.Loading = true;
        var gen = _generation;
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
        catch (Exception ex)
        {
            node.Children.Clear();
            node.Children.Add(new SchemaNode(null, SchemaNodeKind.Message, "읽지 못했다: " + ex.Message));
        }
        finally
        {
            node.Loading = false;
        }
    }

    private async Task LoadSchemaAsync(SchemaNode node, ISession session, CancellationToken ct)
    {
        var f = _filter;
        var query = new ObjectQuery
        {
            Owner = node.Owner,
            ObjectTypes = f.ObjectType is null ? [] : [f.ObjectType],
            NamePattern = SchemaFilter.ToLikePattern(f.Text),
            InvalidOnly = f.InvalidOnly,
        };
        var gen = _generation;

        // 목록 조회 결과에는 상태가 없으므로 INVALID 객체만 먼저 따로 읽어 트리에서 빨갛게 보인다(Orange 처럼 깨진 객체가 바로 보이게).
        var invalid = f.InvalidOnly
            ? null
            : (await _meta!.ListObjectsAsync(session, query with { InvalidOnly = true, ObjectTypes = [], NamePattern = null }, ct))
                .Select(SchemaSql.FavoriteKey).ToHashSet(StringComparer.Ordinal);
        if (node.Owner is { } owner && invalid is not null)
        {
            _invalidByOwner[owner] = invalid;
        }

        var objects = await _meta!.ListObjectsAsync(session, query, ct);
        var favorites = _favorites;
        var groups = await Task.Run(
            () =>
            {
                IEnumerable<DbObjectRef> src = objects;
                if (f.FavoritesOnly)
                {
                    src = src.Where(o => favorites.Contains(SchemaSql.FavoriteKey(o)));
                }

                return src.GroupBy(o => o.ObjectType, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<DbObjectRef>)[.. g.OrderBy(o => o.Name, StringComparer.Ordinal)], StringComparer.Ordinal);
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

        StatusText = $"{node.Owner}: 객체 {objects.Count}개";
    }

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
        var n = new SchemaNode(this, SchemaNodeKind.Item, r.Name)
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

    /// <summary>필터를 모두 풀고 바로 적용한다(입력 지연 타이머는 쓰지 않는다 — 다시 읽기가 겹치지 않게).</summary>
    private async Task ClearFilterAsync(CancellationToken ct)
    {
        _suppressFilterSchedule = true;
        try
        {
            FilterText = string.Empty;
            TypeFilter = null;
            InvalidOnly = false;
            FavoritesOnly = false;
        }
        finally
        {
            _suppressFilterSchedule = false;
        }

        _debounce?.Cancel();
        await ApplyFilterAsync(ct);
    }
}
