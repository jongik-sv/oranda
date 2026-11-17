using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 테이블 편집기 「저장」 페이지(T13c, 설계 18.2): Tibero 메타데이터 공급자가 실제 서버에서 저장 속성(<c>meta.table_kind</c> 끝 열)과
/// LOB 저장 절(<c>meta.table_lobs</c>)을 <see cref="TableInfo.Storage"/>·<see cref="TableInfo.Lobs"/> 에 채운다.
/// <list type="bullet">
/// <item>환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>(<see cref="TiberoWriteFactAttribute"/>).</item>
/// <item>공유 계정이라 <c>ORT_TE_</c> 객체만 만들고 끝에 지운다.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTableStorageIt(ITestOutputHelper output)
{
    private static string T(string suffix) => TiberoItSessions.ObjectName("TE_" + suffix);

    [TiberoWriteFact]
    public async Task Metadata_provider_reads_storage_attributes_and_lob_storage()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var owner = session.Server.SessionUser!.ToUpperInvariant();
        var p = new TableEditProbe(session, output, owner, it.Env.DbName);
        p.Hide(await p.ScalarAsync("SELECT DEFAULT_TABLESPACE FROM USER_USERS"), "<ts>");

        var t = T("SG");
        var plain = T("SN");
        await p.DropTableAsync(t);
        await p.DropTableAsync(plain);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, MEMO CLOB, PIC BLOB) PCTFREE 20 INITRANS 4 STORAGE (INITIAL 256K) NOLOGGING COMPRESS PARALLEL 4 " +
                              "LOB (MEMO) STORE AS (DISABLE STORAGE IN ROW COMPRESS DEDUPLICATE)");
            await p.MustAsync($"CREATE TABLE {plain} (ID NUMBER)");
            var defaultParallel = T("SP");
            await p.DropTableAsync(defaultParallel);
            await p.MustAsync($"CREATE TABLE {defaultParallel} (ID NUMBER) PARALLEL");

            var provider = TableEditProbe.NewProvider();
            var info = await provider.GetTableAsync(session, new DbObjectRef(p.Owner, t, "TABLE"), CancellationToken.None);
            var storage = Assert.IsType<TableStorage>(info.Storage);
            p.Log("storage", storage.ToString());
            Assert.Equal(20, storage.PctFree);
            Assert.Equal(4, storage.InitTrans);
            Assert.Equal(TableCompression.Basic, storage.Compression);
            Assert.False(storage.Logging);
            Assert.Equal(4, storage.Parallel);
            Assert.Null(storage.InitialExtent); // 익스텐트는 읽지 않는다(TE-39, 사전 값은 INITIAL 256K 로 만들어도 131072)

            Assert.Equal(["MEMO", "PIC"], info.Lobs.Select(l => l.Column));
            var lobs = info.Lobs.ToDictionary(l => l.Column, l => l.Storage);
            p.Log("lobs", string.Join(" | ", info.Lobs));
            Assert.Null(lobs["MEMO"].Kind); // Tibero 는 SECUREFILE 열이 없다
            Assert.False(lobs["MEMO"].InRow);
            Assert.Equal((LobCompression.Medium, true), (lobs["MEMO"].Compression, lobs["MEMO"].Deduplicate)); // COMPRESS 는 MEDIUM 으로 읽힌다
            Assert.True(lobs["PIC"].InRow);
            Assert.Equal((LobCompression.None, false), (lobs["PIC"].Compression, lobs["PIC"].Deduplicate)); // NOCOMPRESS·NO
            Assert.False(string.IsNullOrEmpty(lobs["MEMO"].Tablespace), "lob tablespace is empty");

            var none = await provider.GetTableAsync(session, new DbObjectRef(p.Owner, plain, "TABLE"), CancellationToken.None);
            Assert.NotNull(none.Storage);
            Assert.Equal(TableCompression.None, none.Storage!.Compression);
            Assert.True(none.Storage.Logging);
            Assert.Equal(1, none.Storage.Parallel); // DEGREE 0 = NOPARALLEL
            Assert.Empty(none.Lobs);

            var server = await provider.GetTableAsync(session, new DbObjectRef(p.Owner, defaultParallel, "TABLE"), CancellationToken.None);
            Assert.Equal(0, server.Storage!.Parallel); // DEGREE -1 = PARALLEL(서버 기본)
        }
        finally
        {
            await p.DropTableAsync(t);
            await p.DropTableAsync(plain);
            await p.DropTableAsync(T("SP"));
        }
    }

    /// <summary>새 테이블 저장 절(빌더가 만든 CREATE)이 Tibero 에서 실행되고, 다시 읽은 값이 입력과 같다(익스텐트는 읽지 않음).</summary>
    [TiberoWriteFact]
    public async Task New_table_storage_clause_runs_and_reads_back()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var p = new TableEditProbe(session, output, session.Server.SessionUser!.ToUpperInvariant(), it.Env.DbName);
        var ts = await p.ScalarAsync("SELECT DEFAULT_TABLESPACE FROM USER_USERS");
        p.Hide(ts, "<ts>");
        var t = T("SC");
        await p.DropTableAsync(t);
        try
        {
            var dialect = new TiberoTableEditDialect();
            var draft = (TableDraftFactory.Empty(p.Owner) with
                {
                    Name = t,
                    Tablespace = ts,
                    Storage = new TableStorage { PctFree = 20, InitTrans = 4, InitialExtent = "64K", NextExtent = "64K", Logging = false, Compression = TableCompression.Basic, Parallel = 4 },
                    Columns =
                    [
                        new ColumnDraft("n1", "ID") { DataType = "NUMBER", IsNullable = false },
                        new ColumnDraft("n2", "MEMO") { DataType = "CLOB", Lob = new LobStorage { Tablespace = ts, Compression = LobCompression.Medium, Deduplicate = true, InRow = false } },
                    ],
                });
            var script = TableEditScriptBuilder.Build(TableDraftFactory.Empty(p.Owner), draft, dialect, session.Server);
            foreach (var statement in script.SqlStatements)
            {
                var outcome = await p.RunAsync("create", statement.Text);
                Assert.True(outcome.Ok, statement.Text + " → " + outcome);
            }

            var read = await TableEditProbe.NewProvider().GetTableAsync(session, new DbObjectRef(p.Owner, t, "TABLE"), CancellationToken.None);
            Assert.Equal(new TableStorage { PctFree = 20, InitTrans = 4, Compression = TableCompression.Basic, Logging = false, Parallel = 4 }, read.Storage);
            Assert.Equal((LobCompression.Medium, true, false), (read.Lobs[0].Storage.Compression, read.Lobs[0].Storage.Deduplicate, read.Lobs[0].Storage.InRow));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>
    /// 저장 편집 문장을 실제로 실행하고 다시 읽으면 0문장: PCTFREE·INITRANS·NOCOMPRESS·LOGGING·PARALLEL 은 MOVE 앞 ALTER(Tibero MOVE 는 이 값을 반영하지 않는다,
    /// <see cref="TableEditDefaults.MoveCarriesAttributes"/>) + LOB 행 안 저장(MOVE, 지금 압축·중복 제거 값을 이어 씀 — SECUREFILE 낱말은 쓰지 않음)
    /// + MOVE 뒤 인덱스 REBUILD(Tibero 는 UPDATE INDEXES 없음).
    /// </summary>
    [TiberoWriteFact]
    public async Task Storage_edits_run_and_the_reread_table_has_no_differences()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var p = new TableEditProbe(session, output, session.Server.SessionUser!.ToUpperInvariant(), it.Env.DbName);
        p.Hide(await p.ScalarAsync("SELECT DEFAULT_TABLESPACE FROM USER_USERS"), "<ts>");
        var t = T("SE");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER CONSTRAINT {T("SE_PK")} PRIMARY KEY, MEMO CLOB) PCTFREE 20 NOLOGGING COMPRESS PARALLEL 4 " +
                              "LOB (MEMO) STORE AS (DISABLE STORAGE IN ROW COMPRESS DEDUPLICATE)");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, 'x')");
            await p.MustAsync("COMMIT");

            var dialect = new TiberoTableEditDialect();
            var provider = TableEditProbe.NewProvider();
            var table = new DbObjectRef(p.Owner, t, "TABLE");
            var original = TableDraftFactory.FromTable(await provider.GetTableAsync(session, table, CancellationToken.None), dialect);
            var second = TableDraftFactory.FromTable(await TableEditProbe.NewProvider().GetTableAsync(session, table, CancellationToken.None), dialect);
            Assert.True(TableEditScriptBuilder.Build(original, second, dialect, session.Server).IsEmpty);

            var edited = original with
            {
                Storage = original.Storage with { PctFree = 30, InitTrans = 4, Logging = true, Parallel = 1, Compression = TableCompression.None },
                Columns = [.. original.Columns.Select(c => c.Name == "MEMO" ? c with { Lob = c.Lob! with { InRow = true } } : c)],
            };
            var script = TableEditScriptBuilder.Build(original, edited, dialect, session.Server);
            foreach (var statement in script.SqlStatements)
            {
                var outcome = await p.RunAsync("edit", statement.Text);
                Assert.True(outcome.Ok, statement.Text + " → " + outcome);
            }

            var reread = await TableEditProbe.NewProvider().GetTableAsync(session, table, CancellationToken.None);
            p.Log("reread", $"{reread.Storage} | {string.Join(" | ", reread.Lobs)}");
            Assert.Equal(new TableStorage { PctFree = 30, InitTrans = 4, Compression = TableCompression.None, Logging = true, Parallel = 1 }, reread.Storage);
            Assert.Equal(new LobStorage { Tablespace = reread.Lobs[0].Storage.Tablespace, Compression = LobCompression.Medium, Deduplicate = true, InRow = true }, Assert.Single(reread.Lobs).Storage);
            var again = TableEditScriptBuilder.Build(
                TableDraftFactory.FromTable(reread, dialect), edited, dialect, session.Server);
            Assert.True(again.IsEmpty, again.PreviewText());
            Assert.Equal("0", await p.ScalarAsync($"SELECT COUNT(*) FROM USER_INDEXES WHERE TABLE_NAME = '{t}' AND STATUS NOT IN ('VALID', 'N/A')"));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }
}
