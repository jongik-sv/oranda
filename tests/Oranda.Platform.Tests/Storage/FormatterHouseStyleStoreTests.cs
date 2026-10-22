using Oranda.Core.Formatting;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-13-43. 기본 제공 「Oranda 기본」 = 사내 서식, 새 옵션(clauseLayout·lineBreaks.orderBy·align.comparison) 저장·옛 파일 읽기.
public sealed class FormatterHouseStyleStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-fhs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FormatterProfileStore NewStore() => new(new AppDataPaths(_root), []);

    [Fact]
    public void Built_in_default_is_the_house_style_and_the_others_keep_the_old_layout()
    {
        var all = FormatterProfileStore.BuiltIns;
        Assert.Equal(FormatterOptions.OrandaDefault, all.Single(p => p.Id == FormatterProfileStore.DefaultId).Options);
        foreach (var id in new[] { FormatterProfileStore.Orange5Id, FormatterProfileStore.CompactId })
        {
            var o = all.Single(p => p.Id == id).Options;
            Assert.Equal(ClauseLayout.Indent, o.ClauseLayout);
            Assert.Equal(OrderByLayout.FollowGroupBy, o.LineBreaks.OrderBy);
            Assert.False(o.Align.Comparison);
        }
    }

    [Fact]
    public async Task New_options_round_trip_as_named_json_fields()
    {
        var store = NewStore();
        await store.SaveAsync(new FormatterProfile { Id = "house-copy", Name = "사내 복사본", Options = FormatterOptions.OrandaDefault }, default);

        var loaded = (await store.ListAsync(default)).Single(p => p.Id == "house-copy");
        Assert.Equal(FormatterOptions.OrandaDefault, loaded.Options);
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "formatter", "house-copy.json"));
        Assert.Contains("\"clauseLayout\": \"Column\"", json, StringComparison.Ordinal);
        Assert.Contains("\"orderBy\": \"SingleLine\"", json, StringComparison.Ordinal);
        Assert.Contains("\"comparison\": true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("effectiveOrderBy", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_user_profile_saved_before_the_new_fields_keeps_the_old_layout()
    {
        Directory.CreateDirectory(Path.Combine(_root, "formatter"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "formatter", "sql-format.json"),
            """{ "schemaVersion": 1, "id": "sql-format", "name": "SQL 포맷(설정 창)", "options": { "commaPosition": "Leading", "lineBreaks": { "groupOrderBy": "MultiLine" } } }""");

        var o = (await NewStore().ListAsync(default)).Single(p => p.Id == "sql-format").Options;
        Assert.Equal(ClauseLayout.Indent, o.ClauseLayout);
        Assert.Equal(OrderByLayout.FollowGroupBy, o.LineBreaks.OrderBy);
        Assert.Equal(ByListLayout.MultiLine, o.LineBreaks.EffectiveOrderBy);
        Assert.False(o.Align.Comparison);
    }
}
