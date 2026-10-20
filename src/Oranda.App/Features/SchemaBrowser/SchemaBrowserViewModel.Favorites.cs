using Oranda.Core.Metadata;

namespace Oranda.App.Features.SchemaBrowser;

// SchemaBrowserViewModel 의 일부: 접속별 즐겨찾기(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SchemaBrowserViewModel
{
    private void LoadFavorites(SchemaNode node)
    {
        node.Children.Clear();
        foreach (var key in _favorites.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (TryParseKey(key) is { } r)
            {
                node.Children.Add(MakeObject(r, node));
            }
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

        var key = SchemaSql.FavoriteKey(r);
        var on = !_favorites.Contains(key);
        await _favoriteStore.SetAsync(session.Profile.Id, key, on, ct);
        _favorites = await _favoriteStore.LoadAsync(session.Profile.Id, ct);
        foreach (var n in Flatten(Roots).Where(n => n.Ref is { } rr && SchemaSql.FavoriteKey(rr) == key))
        {
            n.IsFavorite = on;
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
