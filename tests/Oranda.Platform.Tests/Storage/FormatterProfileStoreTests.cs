using Oranda.Core.Formatting;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-05-03. 포매터 프로필 저장·내보내기·가져오기(data-model 2.3).
public sealed class FormatterProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-fp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FormatterProfileStore NewStore(string? dir = null) => new(new AppDataPaths(dir ?? _root), []);

    private static FormatterProfile Sample(string id = "team", string name = "팀 규칙") => new()
    {
        Id = id,
        Name = name,
        Options = new FormatterOptions
        {
            Case = new CaseOptions { Keyword = LetterCase.Lower, Identifier = LetterCase.Upper },
            Indent = new IndentOptions { UseTabs = true, Size = 3 },
            CommaPosition = CommaPosition.Leading,
            LineBreaks = new LineBreakOptions { AndOr = AndOrBreak.After, MaxLineWidth = 90 },
            Align = new AlignOptions { EqualsSign = true },
            StatementTerminator = StatementTerminator.Add,
        },
    };

    [Fact]
    public async Task List_starts_with_three_built_ins_then_user_profiles()
    {
        var store = NewStore();
        await store.SaveAsync(Sample("zeta", "가나다"), default);
        var list = await store.ListAsync(default);

        Assert.Equal(["oranda-default", "orange5", "compact", "zeta"], list.Select(p => p.Id));
        Assert.Equal(FormatterOptions.OrandaDefault, list[0].Options);
    }

    [Fact]
    public async Task Save_then_list_round_trips_every_option()
    {
        var store = NewStore();
        await store.SaveAsync(Sample(), default);
        var loaded = (await store.ListAsync(default)).Single(p => p.Id == "team");

        Assert.Equal(Sample().Options, loaded.Options);
        Assert.Equal("팀 규칙", loaded.Name);
    }

    [Fact]
    public async Task Built_ins_cannot_be_saved_or_deleted()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Sample("compact"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync("orange5", default));
        Assert.Equal(3, (await store.ListAsync(default)).Count);
    }

    [Fact]
    public async Task Bad_id_is_rejected_so_paths_cannot_escape()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Sample("../evil"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("a/b", default));
    }

    [Fact]
    public async Task Duplicate_name_is_rejected_on_save()
    {
        var store = NewStore();
        await store.SaveAsync(Sample("a", "같은 이름"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Sample("b", "같은 이름"), default));
        await store.SaveAsync(Sample("a", "같은 이름"), default); // 자기 자신은 덮어쓴다
    }

    [Fact]
    public async Task Delete_removes_the_file()
    {
        var store = NewStore();
        await store.SaveAsync(Sample(), default);
        await store.DeleteAsync("team", default);

        Assert.DoesNotContain(await store.ListAsync(default), p => p.Id == "team");
        Assert.False(File.Exists(Path.Combine(_root, "formatter", "team.json")));
    }

    [Fact]
    public async Task Out_of_range_values_are_clamped()
    {
        var store = NewStore();
        var bad = Sample() with
        {
            Options = new FormatterOptions
            {
                Indent = new IndentOptions { Size = 99, Subquery = 9, Case = -1, PlsqlBlock = 7 },
                LineBreaks = new LineBreakOptions { MaxLineWidth = 5, InListThreshold = -3 },
                Comments = new CommentOptions { MaxBlankLines = 50 },
            },
        };
        await store.SaveAsync(bad, default);
        var o = (await store.ListAsync(default)).Single(p => p.Id == "team").Options;

        Assert.Equal(16, o.Indent.Size);
        Assert.Equal(4, o.Indent.Subquery);
        Assert.Equal(0, o.Indent.Case);
        Assert.Equal(4, o.Indent.PlsqlBlock);
        Assert.Equal(40, o.LineBreaks.MaxLineWidth);
        Assert.Equal(0, o.LineBreaks.InListThreshold);
        Assert.Equal(5, o.Comments.MaxBlankLines);
    }

    [Fact]
    public async Task Export_then_import_on_another_pc_gives_identical_options()
    {
        var pcA = NewStore(Path.Combine(_root, "a"));
        var pcB = NewStore(Path.Combine(_root, "b"));
        await pcA.SaveAsync(Sample(), default);
        var file = Path.Combine(_root, "share", "team.json");

        await pcA.ExportAsync("team", file, default);
        var imported = await pcB.ImportAsync(file, default);

        Assert.Equal("team", imported.Id);
        Assert.Equal(Sample().Options, imported.Options);
        Assert.Equal(Sample().Options, (await pcB.ListAsync(default)).Single(p => p.Id == "team").Options);
    }

    [Fact]
    public async Task Built_in_profile_can_be_exported()
    {
        var store = NewStore();
        var file = Path.Combine(_root, "orange.json");
        await store.ExportAsync("orange5", file, default);
        var other = NewStore(Path.Combine(_root, "other"));
        var imported = await other.ImportAsync(file, default);

        Assert.Equal(FormatterProfileStore.BuiltIns.Single(p => p.Id == "orange5").Options, imported.Options);
        Assert.Equal("orange5-2", imported.Id); // 기본 제공과 ID 가 겹쳐 새 ID
    }

    [Fact]
    public async Task Import_never_overwrites_and_renames_on_id_or_name_conflict()
    {
        var store = NewStore();
        await store.SaveAsync(Sample(), default);
        var file = Path.Combine(_root, "team.json");
        await store.ExportAsync("team", file, default);

        var first = await store.ImportAsync(file, default);
        var second = await store.ImportAsync(file, default);

        Assert.Equal("team-2", first.Id);
        Assert.Equal("팀 규칙 (2)", first.Name);
        Assert.Equal("team-3", second.Id);
        Assert.Equal("팀 규칙 (3)", second.Name);
        Assert.Equal(3 + 3, (await store.ListAsync(default)).Count);
    }

    [Fact]
    public async Task Import_ignores_unknown_options_and_fills_missing_with_defaults()
    {
        var store = NewStore();
        var file = Path.Combine(_root, "x.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(file, """
            { "schemaVersion": 1, "id": "ext", "name": "외부",
              "options": { "futureThing": 5, "case": { "keyword": "Lower", "bogus": true }, "indent": { "size": 8 } } }
            """);

        var p = await store.ImportAsync(file, default);

        Assert.Equal(LetterCase.Lower, p.Options.Case.Keyword);
        Assert.Equal(LetterCase.Upper, p.Options.Case.Function); // 빠진 값 = 기본값
        Assert.Equal(8, p.Options.Indent.Size);
        Assert.Equal(1, p.Options.Indent.Subquery);
    }

    [Fact]
    public async Task Import_rejects_non_profile_files()
    {
        var store = NewStore();
        var file = Path.Combine(_root, "bad.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(file, "{ not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(file, default));
        await File.WriteAllTextAsync(file, """{ "schemaVersion": 1 }""");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(file, default));
    }

    [Fact]
    public async Task Corrupt_user_file_is_moved_to_backup_and_skipped()
    {
        var store = NewStore();
        Directory.CreateDirectory(Path.Combine(_root, "formatter"));
        await File.WriteAllTextAsync(Path.Combine(_root, "formatter", "broken.json"), "{{{");

        var list = await store.ListAsync(default);

        Assert.Equal(3, list.Count);
        Assert.False(File.Exists(Path.Combine(_root, "formatter", "broken.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "backup"), "broken.json.corrupt-*"));
    }

    [Fact]
    public async Task Future_version_file_is_listed_but_not_overwritten()
    {
        var store = NewStore();
        Directory.CreateDirectory(Path.Combine(_root, "formatter"));
        var path = Path.Combine(_root, "formatter", "fut.json");
        await File.WriteAllTextAsync(path, """{ "schemaVersion": 99, "id": "fut", "name": "미래", "options": { "indent": { "size": 7 } } }""");

        var list = await store.ListAsync(default);
        Assert.Equal(7, list.Single(p => p.Id == "fut").Options.Indent.Size);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Sample("fut", "미래"), default));
        Assert.Contains("\"schemaVersion\": 99", await File.ReadAllTextAsync(path));
    }
}
