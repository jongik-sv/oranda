using System.Globalization;
using Oranda.App.Features.Localization;
using Oranda.App.Shell.Features;
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
        _commentsByOwner.Clear(); // 새로 고침: 주석도 서버에서 다시 읽는다
        _favoritesNode = null;
        SelectedNode = null;
        // 이전 접속(또는 짓기 전)의 목록 기준 시각을 남기지 않는다: 접속을 바꾸는 동안·짓기에 실패한 뒤에도 다른 접속의 시각이 보이지 않게.
        ListBaseTimeText = string.Empty;
        OnPropertyChanged(nameof(HasSession));
        var session = CurrentSession;
        if (session is null || _meta is null)
        {
            StatusText = "접속하면 객체 트리가 나타난다";
            return;
        }

        // 이 DBMS 에 스키마 트리·메타데이터 구현이 없으면(Tibero 등) 읽지 않고 이유만 패널 아래 안내줄에 보인다.
        if (NotImplementedReason(session) is { } notImplemented)
        {
            StatusText = notImplemented;
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

            if (gen == _buildGeneration)
            {
                await ShowListBaseTimeAsync(session, ct);
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

    /// <summary>
    /// 새로 고침(F5·툴바 단추, <c>oranda.schema.refresh</c>). 유형 범주 노드(함수 등)면 그 유형 목록을 서버에서 다시 받아 캐시를 바꾼 뒤 다시 그린다
    /// (사용자가 기다린다, 세션이 바쁘면 차례를 기다린다). 그 밖의 노드면 트리를 처음부터 다시 만든다(<see cref="LoadAsync"/>: 캐시로 그리고 뒤에서 전체 목록을 맞춘다).
    /// </summary>
    /// <param name="node">대상 노드. null 이면 고른 노드.</param>
    public async Task RefreshAsync(SchemaNode? node, CancellationToken ct = default)
    {
        node ??= SelectedNode;
        if (node is { Kind: SchemaNodeKind.Category, CategoryType: { } type, Owner: { } owner })
        {
            await RefreshCategoryAsync(owner, type, ct);
            return;
        }

        await LoadAsync(ct);
    }

    /// <summary>범주 하나(소유자·유형)를 서버에서 다시 받는다. 끝나면 펼쳐 둔 범주와 고른 노드를 그대로 두고 다시 그린다.</summary>
    private async Task RefreshCategoryAsync(string owner, string type, CancellationToken ct)
    {
        ResolveServices();
        var session = CurrentSession;
        if (session is null || _meta is null)
        {
            return;
        }

        string status;
        BeginLoad();
        _ownListRefreshes++;
        try
        {
            // Java 저장 객체는 캐시에 없고 스키마를 펼칠 때마다 서버에 묻는다: 다시 그리기만 하면 된다.
            var change = IsJavaType(type) ? ObjectListChange.None : await _meta.RefreshObjectTypeAsync(session, owner, type, ct);
            status = UiText.Format("Shell.schemaBrowser.typeRefreshed", "{0} 목록을 서버에서 다시 받았다: 추가 {1} · 바뀜 {2} · 지움 {3}",
                owner + " " + type, change.Added, change.Changed, change.Removed);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            StatusText = UiText.Format("Shell.schemaBrowser.typeRefreshFailed", "목록을 서버에서 다시 받지 못했다: {0}", ex.Message);
            return;
        }
        finally
        {
            _ownListRefreshes--;
            EndLoad();
        }

        // 다시 그리면 범주 노드가 새로 만들어진다: 고른 범주를 다시 골라 F5 를 이어 누를 수 있게. 다시 고르는 범주는 받는 동안 바뀐 선택을 따른다
        // (긴 F5 동안 다른 범주를 골랐으면 그 범주, 고른 것이 없으면 F5 한 범주). 범주가 아닌 노드를 골랐으면 다시 그리기가 그 객체를 다시 고른다.
        var (pickOwner, pickType) = SelectedNode switch
        {
            null => (owner, type),
            { Kind: SchemaNodeKind.Category, Owner: { } o, CategoryType: { } t } => (o, t),
            _ => ((string?)null, (string?)null),
        };
        await ApplyFilterAsync(ct);
        if (pickOwner is not null && (SelectedNode is null || SelectedNode.Kind == SchemaNodeKind.Category))
        {
            var again = Roots.FirstOrDefault(r => r.Kind == SchemaNodeKind.Schema && string.Equals(r.Owner, pickOwner, StringComparison.Ordinal))?
                .Children.FirstOrDefault(c => c.Kind == SchemaNodeKind.Category && string.Equals(c.CategoryType, pickType, StringComparison.Ordinal));
            if (again is not null)
            {
                SelectedNode = again;
            }
        }

        StatusText = status;
    }

    private static bool IsJavaType(string type) => type is "JAVA SOURCE" or "JAVA CLASS" or "JAVA RESOURCE";

    /// <summary>
    /// 「메타데이터 캐시 초기화」(<c>oranda.schema.resetMetadataCache</c>): 이 접속의 객체 목록 캐시를 비우고 서버의 전체 목록을 다시 받은 뒤 트리를 다시 만든다.
    /// 전체 목록 읽기는 계정·서버에 따라 분 단위다(그동안 진행 막대).
    /// </summary>
    public async Task ResetMetadataCacheAsync(CancellationToken ct = default)
    {
        ResolveServices();
        var session = CurrentSession;
        if (session is null || _meta is null || NotImplementedReason(session) is not null)
        {
            return;
        }

        StatusText = UiText.Get("Shell.schemaBrowser.cacheResetting", "메타데이터 캐시를 비우고 객체 목록을 서버에서 다시 받는 중…");
        ObjectListChange change;
        BeginLoad();
        _ownListRefreshes++;
        try
        {
            change = await _meta.ResetObjectListCacheAsync(session, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            StatusText = UiText.Format("Shell.schemaBrowser.cacheResetFailed", "메타데이터 캐시를 초기화하지 못했다: {0}", ex.Message);
            return;
        }
        finally
        {
            _ownListRefreshes--;
            EndLoad();
        }

        await LoadAsync(ct);
        StatusText = UiText.Format("Shell.schemaBrowser.cacheReset", "메타데이터 캐시를 초기화했다: 객체 {0}개를 서버에서 다시 받았다", change.Added + change.Changed);
    }

    /// <summary>캐시의 목록 기준 시각을 읽어 상태 줄에 보인다. 읽지 못하면 비운다(트리는 그대로).</summary>
    private async Task ShowListBaseTimeAsync(ISession session, CancellationToken ct)
    {
        DateTimeOffset? at;
        try
        {
            at = await _meta!.GetObjectListRefreshedAtAsync(session, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            at = null;
        }

        if (ReferenceEquals(CurrentSession, session))
        {
            ListBaseTimeText = ListBaseTimeLabel(at);
        }
    }

    /// <summary>「목록 기준 시각: yyyy-MM-dd HH:mm」(로컬 시각). 모르면 빈 글.</summary>
    internal static string ListBaseTimeLabel(DateTimeOffset? refreshedAt)
        => refreshedAt is { } at
            ? UiText.Format("Shell.schemaBrowser.listBaseTime", "목록 기준 시각: {0}", at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            : string.Empty;

    /// <summary>세션의 DBMS 에 <see cref="DbmsServiceArea.Metadata"/> 구현이 없을 때의 이유 문구(현재 언어). 있으면 null.</summary>
    private string? NotImplementedReason(ISession session)
        => DbmsServiceKeys.Reason(_services, session.Profile.Dbms, DbmsServiceArea.Metadata, key => UiText.Current?.TryLookup(key));

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
            ObjectTypes = SchemaPairs.WithBodies(f.Types), // 패키지를 고르면 본문도(상태 표)
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

        // 테이블 명: 「테이블 명」 체크가 켜져 있을 때만 읽는다(끄면 서버를 부르지 않는다). 켜면 그때 다시 거르며 읽는다.
        if (ShowTableComments && node.Owner is { } commentOwner && !(_reuseInvalid && _commentsByOwner.ContainsKey(commentOwner)))
        {
            _commentsByOwner[commentOwner] = await ListCommentsAsync(session, commentOwner, ct);
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
        var (groups, pairs) = await Task.Run(
            () =>
            {
                IEnumerable<DbObjectRef> src = objects;
                if (matcher is not null)
                {
                    src = src.Where(matcher.Shows); // 저장 필터: 숨긴 유형·유형별 이름 패턴(본문은 명세 유형 규칙을 따른다)
                }

                // 패키지·타입은 명세와 본문을 한 줄로 묶는다(PKG1). 즐겨찾기만 보기는 두 쪽 중 하나라도 즐겨찾기면 보인다.
                var list = src.ToList();
                var paired = SchemaPairs.Pair(list.Where(o => SchemaPairs.IsPairType(o.ObjectType)));
                IEnumerable<DbObjectRef> rest = list.Where(o => !SchemaPairs.IsPairType(o.ObjectType));
                if (f.FavoritesOnly)
                {
                    rest = rest.Where(o => favorites.Contains(SchemaSql.FavoriteKey(o)));
                    paired = [.. paired.Where(p => new[] { p.Spec, p.Body }.OfType<DbObjectRef>().Any(o => favorites.Contains(SchemaSql.FavoriteKey(o))))];
                }

                var byRef = paired.ToDictionary(p => p.Ref);
                var grouped = rest.Concat(paired.Select(p => p.Ref)).GroupBy(o => SchemaPairs.CategoryOf(o.ObjectType), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<DbObjectRef>)[.. g.OrderBy(o => javaNames.GetValueOrDefault(o, o.Name), StringComparer.Ordinal)], StringComparer.Ordinal);
                return (grouped, byRef);
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
                Pairs = SchemaPairs.IsPairType(type) ? items.Where(pairs.ContainsKey).ToDictionary(i => i, i => pairs[i]) : null,
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

    /// <summary>소유자의 테이블·뷰 주석. 읽지 못하면(권한·끊김) 빈 사전: 주석 없이 트리는 그대로 보인다.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ListCommentsAsync(ISession session, string owner, CancellationToken ct)
    {
        try
        {
            return await _meta!.ListTableCommentsAsync(session, owner, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>트리 줄에 붙일 주석 글: 줄바꿈·연속 공백은 공백 하나로, 길면 <see cref="CommentMaxLength"/> 자에서 자르고 「…」.</summary>
    internal static string? CommentLabel(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        var text = string.Join(' ', comment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > CommentMaxLength ? text[..CommentMaxLength] + "…" : text;
    }

    /// <summary>트리 줄에 보이는 주석 최대 글자 수.</summary>
    internal const int CommentMaxLength = 60;

    /// <summary>표·뷰 노드의 주석(그 소유자를 읽었을 때만). 다른 유형은 null.</summary>
    private string? CommentOf(DbObjectRef r) =>
        SchemaNode.IsTableLike(r.ObjectType) && _commentsByOwner.TryGetValue(r.Owner, out var map) && map.TryGetValue(r.Name, out var c)
            ? CommentLabel(c)
            : null;

    /// <summary>트리에 보이는 객체 이름: Java 객체는 긴 이름(<c>com/example/Util</c>), 나머지는 사전 이름.</summary>
    internal string DisplayName(DbObjectRef r) => _javaNames.TryGetValue(r, out var longName) ? longName : r.Name;

    private void AddPage(SchemaNode category)
    {
        var invalid = category.Owner is { } owner && _invalidByOwner.TryGetValue(owner, out var set) ? set : null;
        var next = category.Items.Skip(category.Shown).Take(PageSize).ToList();
        bool IsInvalid(DbObjectRef r) => _filter.InvalidOnly || (invalid?.Contains(SchemaSql.FavoriteKey(r)) ?? false);
        foreach (var r in next)
        {
            var pair = category.Pairs?.GetValueOrDefault(r);
            var status = (pair is null ? IsInvalid(r) : new[] { pair.Spec, pair.Body }.OfType<DbObjectRef>().Any(IsInvalid)) ? "INVALID" : null;
            category.Children.Add(MakeObject(r, category, status, pair: pair, isInvalid: IsInvalid));
        }

        category.Shown += next.Count;
        if (category.Shown < category.Items.Count)
        {
            category.Children.Add(new SchemaNode(this, SchemaNodeKind.More, $"더 보기… ({category.Items.Count - category.Shown}개 남음)") { Parent = category });
        }
    }

    /// <param name="pair">패키지·타입 노드가 묶은 명세·본문(상태 표). 다른 유형은 null.</param>
    /// <param name="isInvalid">쪽마다 INVALID 인지(상태 표). 없으면 표를 보이지 않는다(즐겨찾기 줄 — 상태를 읽지 않았다).</param>
    private SchemaNode MakeObject(DbObjectRef r, SchemaNode? parent, string? status = null, string? detail = null, SchemaPair? pair = null,
        Func<DbObjectRef, bool>? isInvalid = null)
    {
        var badges = pair is not null && isInvalid is not null;
        var n = new SchemaNode(this, SchemaNodeKind.Item, DisplayName(r))
        {
            Ref = r,
            Owner = r.Owner,
            Parent = parent,
            Status = status,
            Detail = detail,
            Comment = CommentOf(r),
            Pair = pair,
            SpecState = badges ? SchemaPairs.StateOf(pair!.Spec, isInvalid!, _filter.InvalidOnly) : SchemaPartState.None,
            BodyState = badges ? SchemaPairs.StateOf(pair!.Body, isInvalid!, _filter.InvalidOnly) : SchemaPartState.None,
        };
        n.IsFavorite = n.FavoriteKeys.Any(_favorites.Contains);
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
