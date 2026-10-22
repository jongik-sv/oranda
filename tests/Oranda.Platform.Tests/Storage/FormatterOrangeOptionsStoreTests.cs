using Oranda.Core.Formatting;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-13-38. 새 서식 옵션(keywordAlign·lineBreaks.groupOrderBy)의 저장·옛 파일 읽기.
public sealed class FormatterOrangeOptionsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-fpo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FormatterProfileStore NewStore() => new(new AppDataPaths(_root), []);

    [Fact]
    public async Task New_options_round_trip_as_named_json_fields()
    {
        var store = NewStore();
        var options = new FormatterOptions { KeywordAlign = KeywordAlign.Right, LineBreaks = new LineBreakOptions { GroupOrderBy = ByListLayout.MultiLine } };
        await store.SaveAsync(new FormatterProfile { Id = "sql-format", Name = "SQL 포맷(설정 창)", Options = options }, default);

        var loaded = (await store.ListAsync(default)).Single(p => p.Id == "sql-format");
        Assert.Equal(options, loaded.Options);
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "formatter", "sql-format.json"));
        Assert.Contains("\"keywordAlign\": \"Right\"", json, StringComparison.Ordinal);
        Assert.Contains("\"groupOrderBy\": \"MultiLine\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_older_file_without_the_new_fields_reads_the_current_defaults()
    {
        Directory.CreateDirectory(Path.Combine(_root, "formatter"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "formatter", "team.json"),
            """{ "schemaVersion": 1, "id": "team", "name": "팀", "options": { "commaPosition": "Leading", "lineBreaks": { "maxLineWidth": 100 } } }""");

        var o = (await NewStore().ListAsync(default)).Single(p => p.Id == "team").Options;
        Assert.Equal(CommaPosition.Leading, o.CommaPosition);
        Assert.Equal(KeywordAlign.Left, o.KeywordAlign);
        Assert.Equal(ByListLayout.SingleLine, o.LineBreaks.GroupOrderBy);
        Assert.Equal(100, o.LineBreaks.MaxLineWidth);
    }
}
