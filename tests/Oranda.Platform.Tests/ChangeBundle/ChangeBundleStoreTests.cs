using Microsoft.Data.Sqlite;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Platform.ChangeBundle;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.Platform.Tests.ChangeBundle;

// 소유: te-bundle(T5). 반영 묶음 저장소·마이그레이션 0006(테이블 편집기 설계 9.1, TE-13·TE-14·TE-35).
public sealed class ChangeBundleStoreTests : IDisposable
{
    internal const string Secret = "Hunter2xyz";

    private readonly TempDirectory _dir = new("oranda-bundle-");

    public ChangeBundleStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private string DbFile => Path.Combine(_dir, "oranda.db");

    private SqliteChangeBundleStore Store() => SqliteChangeBundleStore.ForFile(DbFile, new SecretRedactorStub());

    private static NewChangeBundleItem Item(string text, ChangeBundleItemSource source = ChangeBundleItemSource.SqlEditor)
        => new(text, source, "SAMPLE", "ORDERS");

    [Fact]
    public async Task Migration_0006_creates_tables_and_is_safe_to_reapply()
    {
        using (var store = Store())
        {
            var b = await store.CreateAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], true, default);
            Assert.NotNull(b);
            Assert.True(store.IsWritable);
            await store.AddItemAsync(b.Id, Item("ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100))"), default);
        }

        SqliteConnection.ClearAllPools();
        await using var connection = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default);
        Assert.True(OrandaDatabase.LatestVersion >= 6);
        Assert.Equal(OrandaDatabase.LatestVersion, await OrandaDatabase.GetVersionAsync(connection, default));

        // 이미 적용된 DB 에 스크립트를 다시 실행해도 행이 그대로다.
        await Exec(connection, ReadScript("0006_change_bundle.sql"));
        Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM change_bundle"));
        Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM change_bundle_item"));
    }

    [Fact]
    public async Task Migration_0006_keeps_history_and_accepts_the_new_sources()
    {
        await using var connection = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default);
        foreach (var name in new[] { "0001_init.sql", "0002_change_journal.sql", "0003_mybatis_scenario.sql", "0004_agent.sql", "0005_rollback.sql" })
        {
            await Exec(connection, ReadScript(name));
        }

        await Exec(connection, "INSERT INTO schema_version VALUES (2,'x','x'), (3,'x','x'), (4,'x','x'), (5,'x','x')");
        await Exec(connection, "INSERT INTO history (id, profile_id, profile_name, executed_at, sql_text, statement_kind, source, status) "
            + "VALUES (7, 'p', 'dev', '2026-11-01T00:00:00Z', 'select legacy_marker from dual', 'Query', 'Agent', 'Succeeded')");
        // v5 에서는 데이터 복사 출처가 CHECK 에 막혔다.
        await Assert.ThrowsAsync<SqliteException>(() => Exec(connection, Insert("DataCopy")));

        var result = await OrandaDatabase.MigrateAsync(connection, null, default);
        Assert.True(result.Writable, result.Notice);

        Assert.Equal("Agent", await Scalar(connection, "SELECT source FROM history WHERE id = 7"));
        Assert.Equal(7L, await Scalar(connection, "SELECT rowid FROM history_fts WHERE history_fts MATCH '\"legacy_marker\"'"));
        foreach (var source in new[] { "DataCopy", "TableEditor", "ChangeBundle" })
        {
            await Exec(connection, Insert(source));
        }

        Assert.Equal(4L, await Scalar(connection, "SELECT COUNT(*) FROM history"));
        await Assert.ThrowsAsync<SqliteException>(() => Exec(connection, Insert("Bogus")));

        static string Insert(string source) => "INSERT INTO history (profile_id, profile_name, executed_at, sql_text, statement_kind, source, status) "
            + $"VALUES ('p', 'dev', '2026-11-02T00:00:00Z', 'alter table x add (y number)', 'Ddl', '{source}', 'Succeeded')";
    }

    [Fact]
    public async Task Only_one_bundle_is_current()
    {
        using var store = Store();
        var a = await store.CreateAsync("A", null, ["SAMPLE"], true, default);
        var b = await store.CreateAsync("B", Dbms.Tibero, ["SAMPLE", "SAMPLE_APP", "SAMPLE"], true, default);
        Assert.Equal(b!.Id, (await store.GetCurrentAsync(default))!.Id);
        Assert.Equal(["SAMPLE", "SAMPLE_APP"], b.TargetOwners); // 겹친 이름은 하나로
        Assert.Equal(Dbms.Tibero, b.Dbms);
        Assert.False((await store.GetBundleAsync(a!.Id, default))!.IsCurrent);

        var c = await store.CreateAsync("C", null, [], makeCurrent: false, default);
        Assert.Equal(b.Id, (await store.GetCurrentAsync(default))!.Id);

        await store.SetCurrentAsync(c!.Id, default);
        Assert.Equal(c.Id, (await store.GetCurrentAsync(default))!.Id);
        Assert.Single(await store.ListBundlesAsync(default), x => x.IsCurrent);

        await store.SetCurrentAsync(null, default);
        Assert.Null(await store.GetCurrentAsync(default));
        Assert.Equal(["A", "B", "C"], (await store.ListBundlesAsync(default)).Select(x => x.Name));
    }

    [Fact]
    public async Task Rename_target_update_and_delete_cascade()
    {
        using var store = Store();
        var b = (await store.CreateAsync("A", null, ["SAMPLE"], true, default))!;
        var item = (await store.AddItemAsync(b.Id, Item("DROP INDEX SAMPLE.IX_ORDERS_01"), default))!;
        await store.AddRunAsync(new ChangeBundleRun(item.Id, ConnectionEnvironment.Development, "dev-example", DateTimeOffset.UtcNow, true), default);

        await store.RenameAsync(b.Id, "  릴리스 2 ", default);
        await store.UpdateTargetAsync(b.Id, Dbms.Oracle, ["SAMPLE", "SAMPLE_APP"], default);
        var renamed = (await store.GetBundleAsync(b.Id, default))!;
        Assert.Equal("릴리스 2", renamed.Name);
        Assert.Equal(Dbms.Oracle, renamed.Dbms);
        Assert.Equal(["SAMPLE", "SAMPLE_APP"], renamed.TargetOwners);
        Assert.True(renamed.UpdatedAt >= b.UpdatedAt);

        await store.DeleteAsync(b.Id, default);
        Assert.Null(await store.GetCurrentAsync(default));
        Assert.Empty(await store.ListItemsAsync(b.Id, default));
        Assert.Empty(await store.GetRunMarksAsync(b.Id, default));
        Assert.Null(await store.AddItemAsync(b.Id, Item("DROP TABLE SAMPLE.X"), default)); // 없는 묶음
    }

    [Fact]
    public async Task Items_keep_order_and_reorder_check_and_delete()
    {
        using var store = Store();
        var b = (await store.CreateAsync("A", Dbms.Oracle, ["SAMPLE"], true, default))!;
        var one = (await store.AddItemAsync(b.Id, Item("ALTER TABLE SAMPLE.ORDERS ADD (A NUMBER)", ChangeBundleItemSource.TableEditor), default))!;
        var two = (await store.AddItemAsync(b.Id, Item("ALTER TABLE SAMPLE.ORDERS ADD (B NUMBER)"), default))!;
        var three = (await store.AddItemAsync(b.Id, new NewChangeBundleItem("ALTER TABLE SAMPLE.ORDERS ADD (C NUMBER)", ChangeBundleItemSource.Manual,
            "SAMPLE", "ORDERS", ChangeBundleCautions.OwnerNotQualified, "서버 이름 제약"), default))!;

        Assert.Equal([1, 2, 3], new[] { one.Position, two.Position, three.Position });
        Assert.True(one.Checked);
        Assert.Equal(ChangeBundleItemSource.TableEditor, one.Source);
        Assert.False(one.HasWarning);
        Assert.Equal(ChangeBundleCautions.OwnerNotQualified, three.Cautions);
        Assert.Equal("서버 이름 제약", three.Warning);
        Assert.Equal(StatementHash.Compute(one.Text), one.Hash);

        await store.ReorderAsync(b.Id, [three.Id, 999, one.Id], default); // 모르는 id 는 무시, 빠진 문장은 뒤로
        Assert.Equal([three.Id, one.Id, two.Id], (await store.ListItemsAsync(b.Id, default)).Select(i => i.Id));

        await store.SetCheckedAsync(one.Id, false, default);
        await store.DeleteItemAsync(two.Id, default);
        var items = await store.ListItemsAsync(b.Id, default);
        Assert.Equal([three.Id, one.Id], items.Select(i => i.Id));
        Assert.False(items[1].Checked);

        var four = (await store.AddItemAsync(b.Id, Item("ALTER TABLE SAMPLE.ORDERS ADD (D NUMBER)"), default))!;
        Assert.Equal(3, four.Position); // 끝 = 가장 큰 순서 + 1
    }

    [Fact]
    public async Task Same_statement_is_found_regardless_of_whitespace()
    {
        using var store = Store();
        var b = (await store.CreateAsync("A", Dbms.Oracle, ["SAMPLE"], true, default))!;
        var item = (await store.AddItemAsync(b.Id, Item("ALTER TABLE SAMPLE.ORDERS\n  ADD (NOTE VARCHAR2(100))"), default))!;
        Assert.Equal(item.Id, (await store.FindSameAsync(b.Id, "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100));", default))!.Id);
        Assert.Null(await store.FindSameAsync(b.Id, "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(200))", default));

        var other = (await store.CreateAsync("B", Dbms.Oracle, ["SAMPLE"], false, default))!;
        Assert.Null(await store.FindSameAsync(other.Id, item.Text, default)); // 묶음마다 따로
    }

    [Fact]
    public async Task Editing_the_statement_clears_its_run_marks()
    {
        using var store = Store();
        var b = (await store.CreateAsync("A", Dbms.Oracle, ["SAMPLE"], true, default))!;
        var edited = (await store.AddItemAsync(b.Id, Item("ALTER TABLE SAMPLE.ORDERS ADD (A NUMBER)"), default))!;
        var kept = (await store.AddItemAsync(b.Id, Item("ALTER TABLE SAMPLE.ORDERS ADD (B NUMBER)"), default))!;
        var t = new DateTimeOffset(2026, 11, 17, 1, 2, 3, TimeSpan.Zero);
        await store.AddRunAsync(new ChangeBundleRun(edited.Id, ConnectionEnvironment.Development, "dev-example", t, true), default);
        await store.AddRunAsync(new ChangeBundleRun(kept.Id, ConnectionEnvironment.Test, "test-example", t, true), default);

        await store.UpdateItemTextAsync(edited.Id, "ALTER TABLE SAMPLE.ORDERS ADD (A NUMBER(10))", default);

        var marks = await store.GetRunMarksAsync(b.Id, default);
        Assert.False(marks.ContainsKey(edited.Id));
        Assert.Equal(t, marks[kept.Id].Test);
        var after = (await store.ListItemsAsync(b.Id, default))[0];
        Assert.Equal("ALTER TABLE SAMPLE.ORDERS ADD (A NUMBER(10))", after.Text);
        Assert.Equal(StatementHash.Compute(after.Text), after.Hash);
    }

    [Fact]
    public async Task Run_marks_are_the_last_success_per_environment()
    {
        using var store = Store();
        var b = (await store.CreateAsync("A", Dbms.Oracle, ["SAMPLE"], true, default))!;
        var item = (await store.AddItemAsync(b.Id, Item("ALTER TABLE SAMPLE.ORDERS ADD (A NUMBER)"), default))!;
        var t = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        await store.AddRunAsync(new ChangeBundleRun(item.Id, ConnectionEnvironment.Development, "dev-example", t, true), default);
        await store.AddRunAsync(new ChangeBundleRun(item.Id, ConnectionEnvironment.Development, "dev-example", t.AddHours(1), true), default);
        await store.AddRunAsync(new ChangeBundleRun(item.Id, ConnectionEnvironment.Test, "test-example", t.AddHours(2), false), default); // 실패는 칸에 안 보임
        await store.AddRunAsync(new ChangeBundleRun(item.Id, ConnectionEnvironment.Production, "prod-example", t.AddHours(3), true), default);
        await store.AddRunAsync(new ChangeBundleRun(424242, ConnectionEnvironment.Test, "x", t, true), default); // 없는 문장은 무시

        var marks = (await store.GetRunMarksAsync(b.Id, default))[item.Id];
        Assert.Equal(new ChangeBundleRunMarks(t.AddHours(1), null, t.AddHours(3)), marks);
    }

    [Fact]
    public async Task Secrets_are_redacted_before_storage_and_flagged()
    {
        using var store = Store();
        var b = (await store.CreateAsync("A", Dbms.Oracle, ["SAMPLE"], true, default))!;
        var item = (await store.AddItemAsync(b.Id, Item($"ALTER USER SAMPLE_APP IDENTIFIED BY {Secret}"), default))!;
        Assert.Equal("ALTER USER SAMPLE_APP IDENTIFIED BY ***", item.Text);
        Assert.Equal(ChangeBundleCautions.SecretRedacted, item.Cautions);
        Assert.Equal(item.Id, (await store.FindSameAsync(b.Id, $"ALTER USER SAMPLE_APP IDENTIFIED BY {Secret}", default))!.Id);

        // 가린 *** 를 고쳐도(가리개가 다시 바꿀 것이 없어도) ⚠ 는 남는다 — 실행·저장 전에 다시 적어야 한다.
        await store.UpdateItemTextAsync(item.Id, "ALTER USER SAMPLE_APP IDENTIFIED BY *** ACCOUNT UNLOCK", default);
        Assert.Equal(ChangeBundleCautions.SecretRedacted, (await store.ListItemsAsync(b.Id, default))[0].Cautions);

        var plain = (await store.AddItemAsync(b.Id, Item("ALTER USER SAMPLE_APP ACCOUNT LOCK"), default))!;
        Assert.Equal(ChangeBundleCautions.None, plain.Cautions);
        await store.UpdateItemTextAsync(plain.Id, $"ALTER USER SAMPLE_APP IDENTIFIED BY {Secret}", default);
        var redacted = (await store.ListItemsAsync(b.Id, default))[1];
        Assert.Equal(ChangeBundleCautions.SecretRedacted, redacted.Cautions);
        Assert.Equal("ALTER USER SAMPLE_APP IDENTIFIED BY ***", redacted.Text);

        await using var connection = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default);
        Assert.Equal(0L, await Scalar(connection, $"SELECT COUNT(*) FROM change_bundle_item WHERE statement_text LIKE '%{Secret}%'"));
    }

    [Fact]
    public async Task Newer_database_is_read_only_and_writes_are_skipped()
    {
        using (var first = Store())
        {
            await first.CreateAsync("A", Dbms.Oracle, ["SAMPLE"], true, default);
        }

        SqliteConnection.ClearAllPools();
        await using (var seed = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default))
        {
            await Exec(seed, "INSERT INTO schema_version VALUES (999, 'x', 'future')");
        }

        SqliteConnection.ClearAllPools();
        using var store = Store();
        Assert.Equal("A", (await store.GetCurrentAsync(default))!.Name); // 읽기는 된다
        Assert.False(store.IsWritable);
        Assert.Null(await store.CreateAsync("B", null, [], true, default));
        Assert.Single(await store.ListBundlesAsync(default));
    }

    internal static string ReadScript(string name)
    {
        using var stream = typeof(OrandaDatabase).Assembly.GetManifestResourceStream("Migrations/OrandaDb/" + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task Exec(SqliteConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(SqliteConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    internal sealed class SecretRedactorStub : ISecretRedactor
    {
        public string Redact(string sqlText) => sqlText.Replace(Secret, "***", StringComparison.Ordinal);

        public bool IsSensitiveBindName(string bindName) => false;
    }
}
