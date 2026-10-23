using Microsoft.Data.Sqlite;
using Oranda.Core.MyBatis;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-02-03. 시나리오 저장소(재시작·순서·id 따라감·옮김·비밀 값·한글·읽기 전용).
public sealed class MyBatisScenarioStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-mbscn-" + Guid.NewGuid().ToString("N"));
    private readonly string _mapper;

    public MyBatisScenarioStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _mapper = Path.Combine(_dir, "EmpMapper.xml");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private string DbFile => Path.Combine(_dir, "oranda.db");

    private SqliteMyBatisScenarioStore Store() => SqliteMyBatisScenarioStore.ForFile(DbFile);

    private MyBatisScenario New(string name, string? path = null, string id = "selectEmp", string? ns = "com.acme.EmpMapper",
        Dictionary<string, MyBatisScenarioValue>? values = null)
        => new()
        {
            MapperPath = path ?? _mapper,
            Namespace = ns,
            StatementId = id,
            Name = name,
            Values = values ?? new Dictionary<string, MyBatisScenarioValue> { ["empno"] = new(MyBatisScenarioValueKind.Number, "7369") },
        };

    private static async Task<string[]> NamesAsync(SqliteMyBatisScenarioStore store, string path, string id = "selectEmp", string? ns = "com.acme.EmpMapper")
        => (await store.ListAsync(path, ns, id, default)).Select(s => s.Name).ToArray();

    [Fact]
    public async Task Scenarios_survive_restart_with_all_value_kinds()
    {
        var values = new Dictionary<string, MyBatisScenarioValue>
        {
            ["name"] = new(MyBatisScenarioValueKind.Text, "SMITH"),
            ["empno"] = new(MyBatisScenarioValueKind.Number, "7369"),
            ["active"] = new(MyBatisScenarioValueKind.Boolean, "true"),
            ["ids"] = new(MyBatisScenarioValueKind.List, null, ["1", "2", "3"], MyBatisScenarioValueKind.Number),
            ["none"] = MyBatisScenarioValue.Null,
        };
        using (var first = Store())
        {
            var saved = await first.SaveAsync(New("기본", values: values), default);
            Assert.True(saved.Id > 0);
        }

        SqliteConnection.ClearAllPools();
        using var second = Store();
        var row = Assert.Single(await second.ListAsync(_mapper, "com.acme.EmpMapper", "selectEmp", default));
        Assert.Equal("기본", row.Name);
        Assert.Equal(values.Count, row.Values.Count);
        Assert.Equal(values["name"], row.Values["name"]);
        Assert.Equal(MyBatisScenarioValueKind.Number, row.Values["empno"].Kind);
        Assert.Equal("true", row.Values["active"].Text);
        Assert.Equal(["1", "2", "3"], row.Values["ids"].Items);
        Assert.Equal(MyBatisScenarioValueKind.Number, row.Values["ids"].ItemKind);
        Assert.Equal(MyBatisScenarioValueKind.Null, row.Values["none"].Kind);
        Assert.NotEqual(default, row.CreatedAt);
    }

    [Fact]
    public async Task New_scenarios_go_to_the_end_and_other_statements_stay_separate()
    {
        using var store = Store();
        await store.SaveAsync(New("a"), default);
        await store.SaveAsync(New("b"), default);
        await store.SaveAsync(New("other", id: "selectDept"), default);
        await store.SaveAsync(New("c"), default);

        Assert.Equal(["a", "b", "c"], await NamesAsync(store, _mapper));
        Assert.Equal(["other"], await NamesAsync(store, _mapper, "selectDept"));
        var orders = (await store.ListAsync(_mapper, null, "selectEmp", default)).Select(s => s.SortOrder).ToArray();
        Assert.Equal([0, 1, 2], orders);
    }

    [Fact]
    public async Task Save_with_existing_id_overwrites_in_place()
    {
        using var store = Store();
        var a = await store.SaveAsync(New("a"), default);
        await store.SaveAsync(New("b"), default);
        var values = new Dictionary<string, MyBatisScenarioValue> { ["empno"] = new(MyBatisScenarioValueKind.Number, "1") };
        var changed = await store.SaveAsync(a with { Name = "a2", Values = values }, default);

        Assert.Equal(a.Id, changed.Id);
        Assert.Equal(a.SortOrder, changed.SortOrder);
        Assert.Equal(a.CreatedAt, changed.CreatedAt);
        Assert.Equal(["a2", "b"], await NamesAsync(store, _mapper));
        Assert.Equal("1", (await store.ListAsync(_mapper, null, "selectEmp", default))[0].Values["empno"].Text);
    }

    [Fact]
    public async Task Rename_changes_name_only()
    {
        using var store = Store();
        var a = await store.SaveAsync(New("a"), default);
        await store.RenameAsync(a.Id, "새 이름", default);

        var row = Assert.Single(await store.ListAsync(_mapper, null, "selectEmp", default));
        Assert.Equal("새 이름", row.Name);
        Assert.Equal(a.Values["empno"], row.Values["empno"]);
        Assert.True(row.UpdatedAt >= a.UpdatedAt);
    }

    [Fact]
    public async Task Duplicate_places_copy_right_after_original()
    {
        using var store = Store();
        var a = await store.SaveAsync(New("a"), default);
        await store.SaveAsync(New("b"), default);
        await store.SaveAsync(New("c"), default);
        var copy = await store.DuplicateAsync(a.Id, "a 복사", default);

        Assert.NotEqual(a.Id, copy.Id);
        Assert.Equal("a 복사", copy.Name);
        Assert.Equal(a.Values["empno"], copy.Values["empno"]);
        Assert.Equal(["a", "a 복사", "b", "c"], await NamesAsync(store, _mapper));
    }

    [Fact]
    public async Task Delete_removes_one_and_keeps_order()
    {
        using var store = Store();
        await store.SaveAsync(New("a"), default);
        var b = await store.SaveAsync(New("b"), default);
        await store.SaveAsync(New("c"), default);
        await store.DeleteAsync(b.Id, default);
        await store.DeleteAsync(b.Id, default); // 없는 id 는 조용히 지나간다.

        Assert.Equal(["a", "c"], await NamesAsync(store, _mapper));
    }

    [Fact]
    public async Task Reorder_applies_new_order()
    {
        using var store = Store();
        var a = await store.SaveAsync(New("a"), default);
        var b = await store.SaveAsync(New("b"), default);
        var c = await store.SaveAsync(New("c"), default);
        await store.ReorderAsync([c.Id, a.Id, b.Id], default);

        Assert.Equal(["c", "a", "b"], await NamesAsync(store, _mapper));
    }

    [Fact]
    public async Task MoveStatement_follows_renamed_id_and_leaves_other_mappers()
    {
        var otherMapper = Path.Combine(_dir, "DeptMapper.xml");
        using var store = Store();
        await store.SaveAsync(New("a"), default);
        await store.SaveAsync(New("b"), default);
        await store.SaveAsync(New("same id elsewhere", path: otherMapper, ns: "com.acme.DeptMapper"), default);

        var moved = await store.MoveStatementAsync(_mapper, "selectEmp", "findEmp", default);

        Assert.Equal(2, moved);
        Assert.Equal(["a", "b"], await NamesAsync(store, _mapper, "findEmp"));
        Assert.Empty(await NamesAsync(store, _mapper, "selectEmp", ns: null));
        Assert.Equal(["same id elsewhere"], await NamesAsync(store, otherMapper, "selectEmp", "com.acme.DeptMapper"));
    }

    [Fact]
    public async Task MoveStatement_appends_after_existing_target_scenarios()
    {
        using var store = Store();
        await store.SaveAsync(New("x1", id: "findEmp"), default);
        await store.SaveAsync(New("a"), default);
        await store.SaveAsync(New("b"), default);
        await store.MoveStatementAsync(_mapper, "selectEmp", "findEmp", default);

        Assert.Equal(["x1", "a", "b"], await NamesAsync(store, _mapper, "findEmp"));
    }

    [Fact]
    public async Task List_falls_back_to_namespace_after_mapper_moved()
    {
        using var store = Store();
        await store.SaveAsync(New("a"), default);
        var movedPath = Path.Combine(_dir, "sub", "EmpMapper.xml");

        Assert.Empty(await NamesAsync(store, movedPath, ns: "com.acme.Other"));
        Assert.Empty(await NamesAsync(store, movedPath, ns: null));
        Assert.Equal(["a"], await NamesAsync(store, movedPath));
    }

    [Fact]
    public async Task Fallback_hit_reanchors_so_move_statement_follows_after_mapper_moved()
    {
        using var store = Store();
        await store.SaveAsync(New("a"), default);
        var movedPath = Path.Combine(_dir, "sub", "EmpMapper.xml");
        await NamesAsync(store, movedPath);

        Assert.Equal(1, await store.MoveStatementAsync(movedPath, "selectEmp", "findEmp", default));
        Assert.Equal(["a"], await NamesAsync(store, movedPath, "findEmp"));
    }

    [Fact]
    public async Task Reorder_with_partial_or_foreign_ids_keeps_order_distinct()
    {
        using var store = Store();
        var a = await store.SaveAsync(New("a"), default);
        var b = await store.SaveAsync(New("b"), default);
        var c = await store.SaveAsync(New("c"), default);
        var foreign = await store.SaveAsync(New("f", id: "selectDept"), default);
        await store.ReorderAsync([c.Id, foreign.Id, a.Id], default);

        var rows = await store.ListAsync(_mapper, null, "selectEmp", default);
        Assert.Equal(["c", "a", "b"], rows.Select(r => r.Name).ToArray());
        Assert.Equal(3, rows.Select(r => r.SortOrder).Distinct().Count());
        Assert.Equal(0, (await store.ListAsync(_mapper, null, "selectDept", default))[0].SortOrder);
        _ = b;
    }

    [Fact]
    public async Task List_prefers_path_match_over_namespace()
    {
        var otherMapper = Path.Combine(_dir, "Copy.xml");
        using var store = Store();
        await store.SaveAsync(New("original"), default);
        await store.SaveAsync(New("copy", path: otherMapper), default);

        Assert.Equal(["copy"], await NamesAsync(store, otherMapper));
    }

    [Fact]
    public async Task Path_is_normalized_and_stored_absolute()
    {
        using var store = Store();
        var saved = await store.SaveAsync(New("a", path: Path.Combine(_dir, "x", "..", "EmpMapper.xml")), default);

        Assert.Equal(SqliteMyBatisScenarioStore.NormalizePath(_mapper), saved.MapperPath);
        Assert.Equal(["a"], await NamesAsync(store, Path.Combine(_dir, ".", "EmpMapper.xml"), ns: null));
    }

    [Fact]
    public async Task Path_case_is_ignored_on_windows_only()
    {
        using var store = Store();
        await store.SaveAsync(New("a"), default);
        var upper = Path.Combine(_dir, "EMPMAPPER.XML");

        var found = await NamesAsync(store, upper, ns: null);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(["a"], found);
        }
        else
        {
            Assert.Empty(found);
        }
    }

    [Theory]
    [InlineData("password")]
    [InlineData("DB_PWD")]
    [InlineData("apiSecret")]
    [InlineData("authToken")]
    public async Task Secret_named_values_are_dropped(string secretName)
    {
        var values = new Dictionary<string, MyBatisScenarioValue>
        {
            [secretName] = new(MyBatisScenarioValueKind.Text, "Hunter2xyz"),
            ["empno"] = new(MyBatisScenarioValueKind.Number, "7369"),
        };
        using (var store = Store())
        {
            var saved = await store.SaveAsync(New("a", values: values), default);
            Assert.Equal(MyBatisScenarioValueKind.Null, saved.Values[secretName].Kind);
            Assert.Null(saved.Values[secretName].Text);
            Assert.Equal("7369", saved.Values["empno"].Text);
        }

        SqliteConnection.ClearAllPools();
        using var again = Store();
        var row = Assert.Single(await again.ListAsync(_mapper, null, "selectEmp", default));
        Assert.Equal(MyBatisScenarioValueKind.Null, row.Values[secretName].Kind);

        SqliteConnection.ClearAllPools();
        await using var c = new SqliteConnection($"Data Source={DbFile};Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT values_json FROM mybatis_scenario";
        Assert.DoesNotContain("Hunter2xyz", (string)(await cmd.ExecuteScalarAsync())!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_list_value_is_dropped_too()
    {
        var values = new Dictionary<string, MyBatisScenarioValue>
        {
            ["tokens"] = new(MyBatisScenarioValueKind.List, null, ["t1", "t2"]),
        };
        using var store = Store();
        var saved = await store.SaveAsync(New("a", values: values), default);

        Assert.Equal(MyBatisScenarioValueKind.Null, saved.Values["tokens"].Kind);
        Assert.Null(saved.Values["tokens"].Items);
    }

    [Fact]
    public async Task Korean_names_and_values_round_trip()
    {
        var values = new Dictionary<string, MyBatisScenarioValue>
        {
            ["사원명"] = new(MyBatisScenarioValueKind.Text, "홍길동"),
        };
        using (var store = Store())
        {
            await store.SaveAsync(New("이름 검색", values: values), default);
        }

        SqliteConnection.ClearAllPools();
        using var again = Store();
        var row = Assert.Single(await again.ListAsync(_mapper, null, "selectEmp", default));
        Assert.Equal("이름 검색", row.Name);
        Assert.Equal("홍길동", row.Values["사원명"].Text);
    }

    [Fact]
    public async Task Concurrent_saves_all_land_with_distinct_order()
    {
        using var store = Store();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveAsync(New("s" + i), default)));

        var rows = await store.ListAsync(_mapper, null, "selectEmp", default);
        Assert.Equal(20, rows.Count);
        Assert.Equal(20, rows.Select(r => r.SortOrder).Distinct().Count());
    }

    [Fact]
    public async Task Changed_is_raised_once_per_write()
    {
        using var store = Store();
        var events = new List<(string Path, string Id)>();
        store.Changed += (_, e) => events.Add((e.MapperPath, e.StatementId));
        var normalized = SqliteMyBatisScenarioStore.NormalizePath(_mapper);

        var a = await store.SaveAsync(New("a"), default);
        var b = await store.SaveAsync(New("b"), default);
        await store.RenameAsync(a.Id, "a2", default);
        var copy = await store.DuplicateAsync(a.Id, "a3", default);
        await store.ReorderAsync([b.Id, a.Id, copy.Id], default);
        await store.DeleteAsync(copy.Id, default);

        Assert.Equal(6, events.Count);
        Assert.All(events, e => Assert.Equal((normalized, "selectEmp"), e));

        events.Clear();
        await store.MoveStatementAsync(_mapper, "selectEmp", "findEmp", default);
        Assert.Equal([(normalized, "selectEmp"), (normalized, "findEmp")], events);
    }

    [Fact]
    public async Task Newer_database_is_read_only()
    {
        using (var seed = Store())
        {
            await seed.SaveAsync(New("kept"), default);
        }

        SqliteConnection.ClearAllPools();
        await using (var c = new SqliteConnection($"Data Source={DbFile};Pooling=False"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO schema_version(version, applied_at, description) VALUES (999, 'x', 'future')";
            await cmd.ExecuteNonQueryAsync();
        }

        using var store = Store();
        var raised = 0;
        store.Changed += (_, _) => raised++;
        var dropped = await store.SaveAsync(New("dropped"), default);

        Assert.Equal(0, dropped.Id);
        Assert.False(store.IsWritable);
        Assert.NotNull(store.Notice);
        Assert.Equal(["kept"], await NamesAsync(store, _mapper));
        Assert.Equal(0, raised);
    }
}
