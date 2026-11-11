using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>
/// PKG1(D-067): 스키마 트리의 「패키지」·「타입」 범주는 객체마다 한 노드이고, 명세·본문 상태(정상·오류·없음)를 작은 표로 보인다.
/// 「패키지 본문」·「타입 본문」 범주는 없다. 예전 저장 필터·유형 선택·즐겨찾기의 본문 유형은 명세 범주로 옮긴다.
/// </summary>
public sealed class SchemaBrowserPackageNodeTests
{
    private static readonly DbObjectRef PkSpec = new("SCOTT", "PK_OK", "PACKAGE");
    private static readonly DbObjectRef PkBody = new("SCOTT", "PK_OK", "PACKAGE BODY");
    private static readonly DbObjectRef BadSpec = new("SCOTT", "PK_BAD", "PACKAGE");
    private static readonly DbObjectRef BadBody = new("SCOTT", "PK_BAD", "PACKAGE BODY");
    private static readonly DbObjectRef SpecOnly = new("SCOTT", "PK_NOBODY", "PACKAGE");
    private static readonly DbObjectRef BodyOnly = new("SCOTT", "PK_ORPHAN", "PACKAGE BODY");
    private static readonly DbObjectRef TypeSpec = new("SCOTT", "T_POINT", "TYPE");
    private static readonly DbObjectRef TypeBody = new("SCOTT", "T_POINT", "TYPE BODY");

    private static SchemaBrowserTests.Harness NewHarness()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Objects.AddRange([PkSpec, PkBody, BadSpec, BadBody, SpecOnly, BodyOnly, TypeSpec, TypeBody]);
        h.Meta.Invalid = [BadBody];
        return h;
    }

    private static SchemaNode Category(SchemaBrowserTests.Harness h, string type) =>
        h.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Single(c => c.CategoryType == type);

    private static async Task<List<SchemaNode>> ItemsAsync(SchemaBrowserTests.Harness h, string type)
    {
        var cat = Category(h, type);
        await h.Vm.ExpandAsync(cat);
        return [.. cat.Children.Where(c => c.Kind == SchemaNodeKind.Item)];
    }

    [Fact]
    public void Body_categories_are_gone_from_the_category_list_the_filter_combo_and_the_saved_filter_editor()
    {
        Assert.Contains("PACKAGE", SchemaBrowserViewModel.KnownTypes);
        Assert.Contains("TYPE", SchemaBrowserViewModel.KnownTypes);
        Assert.DoesNotContain("PACKAGE BODY", SchemaBrowserViewModel.KnownTypes);
        Assert.DoesNotContain("TYPE BODY", SchemaBrowserViewModel.KnownTypes);
        Assert.DoesNotContain(SchemaBrowserViewModel.CategoryTitles, c => c.Title is "패키지 본문" or "타입 본문");
        Assert.Equal(SchemaBrowserViewModel.KnownTypes, SchemaBrowserViewModel.CategoryTitles.Select(c => c.Type));

        var draft = FilterDraft.From(new SchemaBrowserFilter { Name = "x" });
        Assert.DoesNotContain(draft.Types, t => t.Type.EndsWith(" BODY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_package_category_lists_one_node_per_package_with_spec_and_body_badges()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        var scott = h.Vm.Roots.Single(r => r.Title == "SCOTT");
        Assert.DoesNotContain(scott.Children, c => c.CategoryType is "PACKAGE BODY" or "TYPE BODY");
        Assert.Equal("패키지 (4)", Category(h, "PACKAGE").Title); // 패키지마다 한 줄(본문만 있는 것 포함)

        var nodes = (await ItemsAsync(h, "PACKAGE")).ToDictionary(n => n.Ref!.Name);
        Assert.Equal(["PK_BAD", "PK_NOBODY", "PK_OK", "PK_ORPHAN"], nodes.Keys.Order(StringComparer.Ordinal));

        Assert.Equal(PkSpec, nodes["PK_OK"].Ref);
        Assert.Equal((SchemaPartState.Valid, SchemaPartState.Valid), (nodes["PK_OK"].SpecState, nodes["PK_OK"].BodyState));
        Assert.Equal(("명세 정상", "본문 정상"), (nodes["PK_OK"].SpecBadge, nodes["PK_OK"].BodyBadge));
        Assert.False(nodes["PK_OK"].IsInvalid);

        Assert.Equal((SchemaPartState.Valid, SchemaPartState.Invalid), (nodes["PK_BAD"].SpecState, nodes["PK_BAD"].BodyState));
        Assert.Equal("본문 오류", nodes["PK_BAD"].BodyBadge);
        Assert.True(nodes["PK_BAD"].BodyBadgeInvalid);
        Assert.True(nodes["PK_BAD"].IsInvalid); // 어느 한 쪽이라도 INVALID 면 이름이 빨갛다

        Assert.Equal(SchemaPartState.Missing, nodes["PK_NOBODY"].BodyState);
        Assert.Equal("본문 없음", nodes["PK_NOBODY"].BodyBadge);

        // 명세 없는 본문(드묾)도 그 이름으로 보인다. 대표 참조는 본문이다(열면 본문 쪽).
        Assert.Equal(BodyOnly, nodes["PK_ORPHAN"].Ref);
        Assert.Equal((SchemaPartState.Missing, SchemaPartState.Valid), (nodes["PK_ORPHAN"].SpecState, nodes["PK_ORPHAN"].BodyState));

        var type = Assert.Single(await ItemsAsync(h, "TYPE"));
        Assert.Equal(TypeSpec, type.Ref);
        Assert.Equal((SchemaPartState.Valid, SchemaPartState.Valid), (type.SpecState, type.BodyState));

        // 패키지가 아닌 노드에는 표가 없다.
        Assert.All(await ItemsAsync(h, "TABLE"), n => Assert.False(n.HasSpecBadge || n.HasBodyBadge));
    }

    [Fact]
    public async Task Choosing_the_package_type_reads_bodies_too_and_old_body_type_choices_map_to_the_package_category()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        h.Vm.Types.Restore(["PACKAGE"]);
        await h.Vm.ApplyFilterAsync();

        Assert.Contains("PACKAGE BODY", h.Meta.LastQuery!.ObjectTypes); // 본문 상태 표를 위해 본문도 읽는다
        Assert.Equal(SchemaPartState.Valid, (await ItemsAsync(h, "PACKAGE")).Single(n => n.Ref!.Name == "PK_OK").BodyState);

        // 예전 접속별 유형 선택에 「PACKAGE BODY」 만 남아 있었다: 트리를 지으면 「패키지」 범주로 읽는다.
        var again = NewHarness();
        await again.Settings.UpdateAsync(s => s with
        {
            SchemaBrowser = s.SchemaBrowser with
            {
                TypeChoices = new Dictionary<string, string> { [again.Session.Profile.Id.ToString()] = SchemaBrowserSettings.JoinTypes(["PACKAGE BODY"]) },
            },
        });
        await again.Vm.SetSessionAsync(again.Session);
        Assert.Equal(["PACKAGE"], again.Vm.Types.SelectedTypes);
        Assert.Equal(["PACKAGE"], again.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Select(c => c.CategoryType));
    }

    [Fact]
    public void Saved_filters_that_name_the_body_types_fold_into_the_spec_categories()
    {
        var old = new SchemaBrowserFilter
        {
            Name = "옛 필터",
            HiddenTypes = ["PACKAGE BODY", "TYPE"],
            NamePatterns = new Dictionary<string, string> { ["PACKAGE BODY"] = "PK_O*", ["TYPE BODY"] = "T_*", ["TYPE"] = "T_P*" },
        };

        var folded = SavedFilterMatcher.FoldBodies(old);

        Assert.Equal(["TYPE"], folded.HiddenTypes); // 본문만 숨겼던 필터는 예전에도 명세를 보였다 — 패키지 노드는 보인다
        Assert.Equal("PK_O*", folded.NamePatterns["PACKAGE"]); // 명세 패턴이 없으면 본문 패턴을 쓴다
        Assert.Equal("T_P*", folded.NamePatterns["TYPE"]); // 명세 패턴이 있으면 그대로
        Assert.DoesNotContain(folded.NamePatterns.Keys, k => k.EndsWith(" BODY", StringComparison.Ordinal));
        Assert.Same(folded, SavedFilterMatcher.FoldBodies(folded)); // 본문 유형이 없으면 그대로

        var m = new SavedFilterMatcher(old, "SCOTT");
        Assert.True(m.Shows(PkSpec)); // PK_O* 가 패키지(명세·본문)에 함께 걸린다
        Assert.True(m.Shows(PkBody));
        Assert.False(m.Shows(BadSpec));
        Assert.False(m.Shows(BadBody));
        Assert.False(m.Shows(TypeBody)); // TYPE 을 숨기면 타입 본문도 숨는다

        // 편집 창도 같은 모양으로 읽는다(「PACKAGE BODY」 같은 낯선 줄이 생기지 않는다).
        var draft = FilterDraft.From(old);
        Assert.DoesNotContain(draft.Types, t => t.Type.EndsWith(" BODY", StringComparison.Ordinal));
        Assert.Equal("PK_O*", draft.Types.Single(t => t.Type == "PACKAGE").Pattern);
        Assert.True(draft.Types.Single(t => t.Type == "PACKAGE").IsShown);
        Assert.False(draft.Types.Single(t => t.Type == "TYPE").IsShown);
    }

    [Fact]
    public async Task A_saved_filter_with_a_body_pattern_keeps_the_tree_working()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        await h.Vm.SaveFiltersAsync([new SchemaBrowserFilter { Name = "본문 패턴", NamePatterns = new Dictionary<string, string> { ["PACKAGE BODY"] = "PK_O*" } }], null);
        var saved = Assert.Single(h.Vm.SavedFilters);
        await h.Vm.SelectSavedFilterAsync(saved.Id);

        var names = (await ItemsAsync(h, "PACKAGE")).Select(n => n.Ref!.Name).ToList();
        Assert.Equal(["PK_OK", "PK_ORPHAN"], names);
        Assert.Equal("패키지 (2)", Category(h, "PACKAGE").Title);
    }

    [Fact]
    public async Task Favorites_see_both_halves_and_favorites_only_keeps_a_package_whose_body_was_starred_before()
    {
        var h = NewHarness();
        await h.Favorites.SetAsync(h.Session.Profile.Id, SchemaSql.FavoriteKey(PkBody), true, default); // 예전에 본문 노드를 즐겨찾기했다
        await h.Vm.SetSessionAsync(h.Session);

        var ok = (await ItemsAsync(h, "PACKAGE")).Single(n => n.Ref!.Name == "PK_OK");
        Assert.True(ok.IsFavorite);

        h.Vm.FavoritesOnly = true;
        await h.Vm.ApplyFilterAsync();
        Assert.Equal(["PK_OK"], (await ItemsAsync(h, "PACKAGE")).Select(n => n.Ref!.Name));

        // 즐겨찾기 묶음에도 한 줄(명세와 본문 열쇠가 둘 다 있어도).
        await h.Favorites.SetAsync(h.Session.Profile.Id, SchemaSql.FavoriteKey(PkSpec), true, default);
        h.Vm.FavoritesOnly = false;
        await h.Vm.LoadAsync();
        var fav = h.Vm.Roots.Single(r => r.Kind == SchemaNodeKind.Favorites);
        await h.Vm.ExpandAsync(fav);
        Assert.Single(fav.Children, c => c.Ref?.Name == "PK_OK");

        // 별을 끄면 두 열쇠를 다 지운다.
        ok = (await ItemsAsync(h, "PACKAGE")).Single(n => n.Ref!.Name == "PK_OK");
        await h.Vm.ToggleFavoriteAsync(ok);
        Assert.Empty(await h.Favorites.LoadAsync(h.Session.Profile.Id, default));
        Assert.False(ok.IsFavorite);
    }

    [Fact]
    public async Task Invalid_only_shows_packages_with_a_broken_half_and_no_missing_badge_for_the_part_it_did_not_read()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        h.Vm.InvalidOnly = true;
        await h.Vm.ApplyFilterAsync();

        var bad = Assert.Single(await ItemsAsync(h, "PACKAGE"));
        Assert.Equal("PK_BAD", bad.Ref!.Name);
        Assert.Equal(BadBody, bad.Ref); // INVALID 만 읽었으니 대표는 본문
        Assert.Equal(SchemaPartState.Invalid, bad.BodyState);
        Assert.Equal(SchemaPartState.None, bad.SpecState); // 명세는 읽지 않았다: 「없음」이라 하지 않는다
    }
}
