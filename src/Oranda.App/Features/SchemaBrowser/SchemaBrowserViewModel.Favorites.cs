using Oranda.Core.Metadata;

namespace Oranda.App.Features.SchemaBrowser;

// SchemaBrowserViewModel 의 일부: 접속별 즐겨찾기(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SchemaBrowserViewModel
{
    private void LoadFavorites(SchemaNode node)
    {
        node.Children.Clear();
        var refs = _favorites.OrderBy(k => k, StringComparer.Ordinal).Select(TryParseKey).OfType<DbObjectRef>().ToList();

        // 패키지·타입은 명세와 본문 즐겨찾기를 한 줄로(PKG1, 명세가 대표).
        var pairs = SchemaPairs.Pair(refs.Where(r => SchemaPairs.IsPairType(r.ObjectType))).ToDictionary(p => p.Ref);
        foreach (var r in refs.Where(r => !SchemaPairs.IsPairType(r.ObjectType) || pairs.ContainsKey(r)))
        {
            node.Children.Add(MakeObject(r, node, pair: pairs.GetValueOrDefault(r)));
        }

        if (node.Children.Count == 0)
        {
            node.Children.Add(new SchemaNode(null, SchemaNodeKind.Message, "즐겨찾기가 없다"));
        }
    }

    internal static DbObjectRef? TryParseKey(string key)
    {
        var bar = key.LastIndexOf('|');
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        if (bar < 0 || dot < 0 || dot > bar)
        {
            return null;
        }

        return new DbObjectRef(key[..dot], key[(dot + 1)..bar], key[(bar + 1)..]);
    }

    /// <summary>즐겨찾기를 켜고 끈다(접속별).</summary>
    public async Task ToggleFavoriteAsync(SchemaNode? node, CancellationToken ct = default)
    {
        node ??= SelectedNode;
        var session = CurrentSession;
        if (node?.Ref is not { } r || session is null)
        {
            return;
        }

        // 패키지·타입 노드는 명세·본문 두 열쇠를 함께 본다(예전에 본문을 즐겨찾기했으면 그것도 끈다).
        var key = SchemaSql.FavoriteKey(r);
        var keys = node.FavoriteKeys.ToHashSet(StringComparer.Ordinal);
        var on = !keys.Any(_favorites.Contains);
        if (on)
        {
            await _favoriteStore.SetAsync(session.Profile.Id, key, true, ct);
        }
        else
        {
            foreach (var k in keys.Where(_favorites.Contains))
            {
                await _favoriteStore.SetAsync(session.Profile.Id, k, false, ct);
            }
        }

        _favorites = await _favoriteStore.LoadAsync(session.Profile.Id, ct);
        foreach (var n in Flatten(Roots).Where(n => n.Ref is not null && n.FavoriteKeys.Any(keys.Contains)))
        {
            n.IsFavorite = n.FavoriteKeys.Any(_favorites.Contains);
        }

        if (_favoritesNode is { } fav)
        {
            var wasExpanded = fav.IsExpanded;
            fav.ResetChildren();
            if (wasExpanded)
            {
                await ExpandAsync(fav, ct);
            }
        }

        if (_filter.FavoritesOnly)
        {
            await ApplyFilterAsync(ct);
        }
    }

    private static IEnumerable<SchemaNode> Flatten(IEnumerable<SchemaNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children))
            {
                yield return c;
            }
        }
    }
}
