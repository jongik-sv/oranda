using Microsoft.Data.Sqlite;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Platform.Storage;
using Oranda.Platform.TableEdit;
using Oranda.Testing;

namespace Oranda.Platform.Tests.TableEdit;

// 소유: te-l1(T16a). 새 테이블 작업 저장소·마이그레이션 0007(테이블 편집기 설계 18.7, TE-43).
public sealed class TableDraftStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new("oranda-tabledraft-");

    public TableDraftStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private string DbFile => Path.Combine(_dir, "oranda.db");

    private const string Me = "100@20261117090000";

    private const string Other = "200@20261117090000";

    private SqliteTableDraftStore Store() => SqliteTableDraftStore.ForFile(DbFile);

    private static TableDraft Draft(string owner = "SAMPLE", string name = "ORDERS", string column = "ORDER_ID") => new(owner, name)
    {
        IsNewTable = true,
        Comment = "주문",
        Columns = [new ColumnDraft("n1", column) { DataType = "NUMBER", Precision = 10, IsNullable = false }],
        Constraints = [new ConstraintDraft("k1", "PK_" + name, ConstraintKind.PrimaryKey) { ColumnKeys = ["n1"], IsAutoName = true }],
    };

    [Fact]
    public async Task Migration_0007_creates_the_table_and_is_safe_to_reapply()
    {
        using (var store = Store())
        {
            Assert.NotNull(await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(), Me, true, default));
            Assert.True(store.IsWritable);
        }

        SqliteConnection.ClearAllPools();
        await using var connection = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default);
        Assert.True(OrandaDatabase.LatestVersion >= 7);
        Assert.Equal(OrandaDatabase.LatestVersion, await OrandaDatabase.GetVersionAsync(connection, default));

        await Exec(connection, ReadScript("0007_table_draft.sql"));
        Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM table_draft"));
    }

    [Fact]
    public async Task Save_then_get_returns_the_model_and_the_entry()
    {
        using var store = Store();
        var entry = await store.SaveAsync(null, "dev-example", Dbms.Tibero, Draft(), Me, true, default);

        Assert.NotNull(entry);
        var got = await store.GetAsync(entry.Id, default);
        Assert.NotNull(got);
        Assert.Equal(entry, got.Entry);
        Assert.Equal(("dev-example", Dbms.Tibero, "SAMPLE", "ORDERS"), (got.Entry.ConnectionName, got.Entry.Dbms, got.Entry.Owner, got.Entry.Name));
        Assert.Equal(TableDraftJson.Serialize(Draft()), TableDraftJson.Serialize(got.Draft));
    }

    [Fact]
    public async Task Save_with_id_overwrites_the_same_row()
    {
        using var store = Store();
        var first = (await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(name: string.Empty), Me, true, default))!;
        Assert.Equal(string.Empty, first.Name); // 이름을 아직 안 쓴 탭

        var second = (await store.SaveAsync(first.Id, "dev-example", Dbms.Oracle, Draft(name: "ORDER_ITEMS", column: "ITEM_ID"), Me, true, default))!;

        Assert.Equal(first.Id, second.Id);
        var entry = Assert.Single(await store.ListAsync(null, null, default));
        Assert.Equal("ORDER_ITEMS", entry.Name);
        Assert.Equal("ITEM_ID", (await store.GetAsync(first.Id, default))!.Draft.Columns[0].Name);
    }

    [Fact]
    public async Task Save_with_a_deleted_id_inserts_a_new_row_and_ids_are_not_reused()
    {
        using var store = Store();
        var first = (await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(), Me, true, default))!;
        Assert.True(await store.DeleteAsync(first.Id, Me, default)); // 다른 곳에서 지움

        var again = await store.SaveAsync(first.Id, "dev-example", Dbms.Oracle, Draft(), Me, true, default);
        var other = await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(), Me, true, default);

        Assert.NotEqual(first.Id, again!.Id); // 탭 작업은 새 행으로 남고
        Assert.NotEqual(first.Id, other!.Id); // AUTOINCREMENT: 지운 id 를 다시 받지 않음
        Assert.Equal(2, (await store.ListAsync(null, null, default)).Count);
    }

    [Fact]
    public async Task Save_does_not_overwrite_a_row_held_by_another_instance()
    {
        using var store = Store();
        var mine = (await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(name: "MINE"), Me, true, default))!;
        Assert.True(await store.ClaimAsync(mine.Id, Me, Other, default)); // 다른 앱이 가져감

        var saved = (await store.SaveAsync(mine.Id, "dev-example", Dbms.Oracle, Draft(name: "MINE2"), Me, true, default))!;

        Assert.NotEqual(mine.Id, saved.Id);
        Assert.Equal("MINE", (await store.GetAsync(mine.Id, default))!.Draft.Name);
        Assert.Equal(Other, (await store.GetAsync(mine.Id, default))!.Entry.OpenBy);
        Assert.Equal(Me, saved.OpenBy);
    }

    [Fact]
    public async Task Save_without_keepOpen_releases_the_row()
    {
        using var store = Store();
        var saved = (await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(), Me, true, default))!;
        Assert.Equal(Me, saved.OpenBy);

        var kept = (await store.SaveAsync(saved.Id, "dev-example", Dbms.Oracle, Draft(name: "KEPT"), Me, false, default))!; // 닫기 「남겨 두기」

        Assert.Equal(saved.Id, kept.Id);
        Assert.Null(kept.OpenBy);
        Assert.Null(Assert.Single(await store.ListAsync(null, null, default)).OpenBy);
    }

    [Fact]
    public async Task Claim_and_release_change_only_the_holder_when_it_matches()
    {
        using var store = Store();
        var saved = (await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(), Me, true, default))!;

        Assert.False(await store.ClaimAsync(saved.Id, null, Other, default)); // 아직 Me 가 쥠
        await store.ReleaseAsync(saved.Id, Other, default); // 남의 것은 못 놓음
        Assert.Equal(Me, (await store.GetAsync(saved.Id, default))!.Entry.OpenBy);

        await store.ReleaseAsync(saved.Id, Me, default);
        var entry = Assert.Single(await store.ListAsync(null, null, default));
        Assert.Null(entry.OpenBy);
        Assert.Equal(saved.UpdatedAt, entry.UpdatedAt); // 고친 때는 그대로

        Assert.True(await store.ClaimAsync(saved.Id, null, Other, default)); // 놓인 것을 다시 엶
        Assert.False(await store.ClaimAsync(saved.Id, null, Me, default)); // 둘째는 짐
        Assert.False(await store.ClaimAsync(saved.Id + 100, null, Me, default));
        Assert.Equal(Other, (await store.GetAsync(saved.Id, default))!.Entry.OpenBy);
    }

    [Fact]
    public async Task Delete_only_when_the_holder_matches()
    {
        using var store = Store();
        var entry = (await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(), Me, true, default))!;

        Assert.False(await store.DeleteAsync(entry.Id, Other, default));
        Assert.False(await store.DeleteAsync(entry.Id, null, default));
        Assert.NotNull(await store.GetAsync(entry.Id, default));

        Assert.True(await store.DeleteAsync(entry.Id, Me, default));
        Assert.False(await store.DeleteAsync(entry.Id, Me, default)); // 없는 id
        Assert.Null(await store.GetAsync(entry.Id, default));
        Assert.Empty(await store.ListAsync(null, null, default));
    }


    [Fact]
    public async Task Get_returns_null_for_a_broken_model_but_the_entry_stays_listed()
    {
        long id;
        using (var store = Store())
        {
            id = (await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(), Me, true, default))!.Id;
        }

        SqliteConnection.ClearAllPools();
        await using (var c = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default))
        {
            await Exec(c, "UPDATE table_draft SET model_json = '{broken'");
        }

        using var again = Store();
        Assert.Null(await again.GetAsync(id, default));
        Assert.Single(await again.ListAsync(null, null, default)); // 지우기는 사용자가 목록에서 고른다
    }

    [Fact]
    public async Task Newer_database_is_read_only_and_writes_are_skipped()
    {
        using (var first = Store())
        {
            await first.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(), Me, true, default);
        }

        SqliteConnection.ClearAllPools();
        await using (var seed = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default))
        {
            await Exec(seed, "INSERT INTO schema_version VALUES (999, 'x', 'future')");
        }

        SqliteConnection.ClearAllPools();
        using var store = Store();
        Assert.Single(await store.ListAsync(null, null, default)); // 읽기는 된다
        Assert.False(store.IsWritable);
        Assert.Null(await store.SaveAsync(null, "dev-example", Dbms.Oracle, Draft(), Me, true, default));
        Assert.False(await store.DeleteAsync(1, Me, default));
        Assert.Single(await store.ListAsync(null, null, default));
    }

    private static string ReadScript(string name)
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
}
