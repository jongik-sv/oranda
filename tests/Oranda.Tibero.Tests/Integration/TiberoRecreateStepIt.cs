using System.Runtime.ExceptionServices;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Tibero 「테이블 다시 만들기」(T5 TB4)를 실행기(<c>CopyRunner.RecreateTablesAsync</c>·<c>RestoreAsync</c>·<c>NoteRecreated</c>)와 같은 순서로 실제 서버에서 끝까지 잰다.
/// 이 시험이 통과해야 <see cref="TiberoCopyPrecheckDialect.RecreateBlockedReason"/> 이 null 인 것이 맞다.
/// 쓰기 시험(<c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만, ORT_ 이름만). 단계는 시험용 생성자(세션 질의 도우미 + T3 트리거 DDL 조립)로 만든다 — Tibero 대량 실행 서비스(TB1)가 아직 없다.
/// <list type="bullet">
/// <item>시험 1: 받아 두기(트리거 켜짐·꺼짐, PUBLIC 권한, 이름 있는·서버 이름 참조 FK) → 휴지통 켜기 → 참조 FK 떼기 → DROP(휴지통 이름 하나 생김)
/// → <see cref="CreateTableDdl"/> 로 다시 만들기(휴지통에 간 옛 표의 PK·인덱스 이름과 겹치지 않는가) → 행 넣기 → 트리거·권한·참조 FK 되살리기(상태·VALID·발동)
/// → 실행기 안내 문장 그대로 BIN 이름으로 <c>FLASHBACK TABLE … TO BEFORE DROP RENAME TO "표_OLD"</c>(옛 행이 돌아오는가).</item>
/// <item>시험 2: <c>VARCHAR2(4000 BYTE)</c> 열과 서버 이름 제약(PK·CHECK)이 CHAR 길이로 쓰는 다시 만들기 DDL 로 돌아오고 4000바이트 값이 다시 들어가는가.
/// 최대 바이트 길이(65532 BYTE) 열은 결과만 출력에 남긴다(단언하지 않음).</item>
/// </list>
/// 접속 값(계정·DB·호스트)은 단언 메시지·출력에 넣지 않는다: 참·거짓 단언만 쓰고, 예외는 형식 이름·오류 번호만 남긴다.
/// 휴지통은 이 시험의 ORT_ 표 항목만 BIN 이름으로 <c>PURGE TABLE</c> 한다(<c>PURGE RECYCLEBIN</c> 금지).
/// </summary>
[Trait("Category", "Integration")]
[Collection("TiberoPublicGrant")] // #117 TBR-12033
public class TiberoRecreateStepIt(ITestOutputHelper output)
{
    private static readonly TiberoCopyDialect Dialect = TiberoCopyDialect.Instance;

    private static readonly string Parent = TiberoItSessions.ObjectName("RCS_P");
    private static readonly string OldParent = Parent + "_OLD"; // 실행기 안내 문장의 RENAME TO 이름(ORT_ 로 시작)
    private static readonly string Child = TiberoItSessions.ObjectName("RCS_C");
    private static readonly string ServerChild = TiberoItSessions.ObjectName("RCS_C2");
    private static readonly string Pk = TiberoItSessions.ObjectName("RCS_PK");
    private static readonly string Index = TiberoItSessions.ObjectName("RCS_IX");
    private static readonly string Fk = TiberoItSessions.ObjectName("RCS_FK");
    private static readonly string TriggerOn = TiberoItSessions.ObjectName("RCS_TRG_ON");
    private static readonly string TriggerOff = TiberoItSessions.ObjectName("RCS_TRG_OFF");
    private static readonly string ByteTable = TiberoItSessions.ObjectName("RCS_B");
    private static readonly string ByteMaxTable = TiberoItSessions.ObjectName("RCS_BMAX");
    private static readonly string BadTrigger = TiberoItSessions.ObjectName("RCS_TRG_BAD");

    [TiberoWriteFact]
    public Task Recreate_captures_drops_into_the_bin_recreates_restores_and_flashes_back_the_old_table() =>
        WriteAsync([Child, ServerChild, Parent, OldParent], MainAsync);

    [TiberoWriteFact]
    public Task Byte_length_varchar2_and_server_named_constraints_round_trip_through_the_recreate_ddl() =>
        WriteAsync([ByteTable, ByteMaxTable], ByteAsync);

    private async Task MainAsync(TiberoSession session, string owner)
    {
        var ct = CancellationToken.None;
        string[] setup =
        [
            $"CREATE TABLE {Parent} (ID NUMBER NOT NULL, NAME VARCHAR2(30), NOTE VARCHAR2(30), NOTE2 VARCHAR2(30), CONSTRAINT {Pk} PRIMARY KEY (ID))",
            $"CREATE INDEX {Index} ON {Parent} (NAME)",
            $"COMMENT ON TABLE {Parent} IS 'rcs parent'",
            $"COMMENT ON COLUMN {Parent}.NAME IS 'rcs name'",
            $"CREATE TRIGGER {TriggerOn} BEFORE INSERT ON {Parent} FOR EACH ROW\nBEGIN\n  :NEW.NOTE := 'trg';\nEND;",
            $"CREATE TRIGGER {TriggerOff} BEFORE INSERT ON {Parent} FOR EACH ROW\nBEGIN\n  :NEW.NOTE2 := 'off';\nEND;",
            $"ALTER TRIGGER {TriggerOff} DISABLE",
            $"GRANT SELECT ON {Parent} TO PUBLIC",
            $"INSERT INTO {Parent} (ID, NAME) VALUES (1, 'a')",
            $"INSERT INTO {Parent} (ID, NAME) VALUES (2, 'b')",
            $"INSERT INTO {Parent} (ID, NAME) VALUES (3, 'c')",
            $"CREATE TABLE {Child} (ID NUMBER PRIMARY KEY, PID NUMBER, CONSTRAINT {Fk} FOREIGN KEY (PID) REFERENCES {Parent} (ID) ON DELETE CASCADE)",
            $"CREATE TABLE {ServerChild} (ID NUMBER PRIMARY KEY, PID NUMBER, FOREIGN KEY (PID) REFERENCES {Parent} (ID))",
            $"INSERT INTO {Child} (ID, PID) VALUES (1, 1)",
            $"INSERT INTO {Child} (ID, PID) VALUES (2, 2)",
            $"INSERT INTO {ServerChild} (ID, PID) VALUES (1, 3)",
            "COMMIT",
        ];
        for (var i = 0; i < setup.Length; i++)
        {
            await ExecQuietAsync(session, "setup statement " + i, setup[i]);
        }

        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {Parent} WHERE NOTE = 'trg' AND NOTE2 IS NULL") == 3,
            "setup: the enabled trigger did not fire or the disabled one did");

        // PUBLIC 에 WITH GRANT OPTION 은 Oracle 에서는 거부된다. Tibero 가 받으면 받아 두기가 GRANTABLE 을 읽는지도 본다.
        var optionGrant = await TryExecAsync(session, $"GRANT INSERT ON {Parent} TO PUBLIC WITH GRANT OPTION");
        output.WriteLine("GRANT ... TO PUBLIC WITH GRANT OPTION: " + (optionGrant ?? "accepted"));

        var serverFks = await RowsAsync(session, $"SELECT constraint_name FROM user_constraints WHERE table_name = '{ServerChild}' AND constraint_type = 'R'");
        Assert.True(serverFks.Count == 1, "setup: the unnamed foreign key was not found exactly once");
        var serverFk = serverFks[0];
        output.WriteLine($"server-generated FK name prefix: {Prefix(serverFk, owner)}; Core CreateTableDdl.IsServerName = {CreateTableDdl.IsServerName(serverFk)}");
        Assert.True(TiberoRecreateStep.IsServerName(serverFk, owner), "the server-generated foreign key name is not recognized by TiberoRecreateStep.IsServerName");

        // 실행기와 같이 지우기 전에 원본 구조(TableInfo)를 읽는다(이 시험은 원본 = 대상).
        var tableInfo = await QuietAsync("GetTableAsync(parent)", () => new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync)
            .GetTableAsync(session, new DbObjectRef(owner, Parent, "TABLE"), ct));
        var step = new TiberoRecreateStep(RunAsync, TiberoRecreateStep.MetadataTriggerDdlAsync);

        // 1. 받아 두기.
        var kept = await QuietAsync("CaptureAsync", () => step.CaptureAsync(session, owner, Parent, ct));
        Assert.True(kept.Triggers.Count == 3 && kept.TriggerObjects.Count == 3,
            "CaptureAsync: expected 3 trigger statements (create off, disable off, create on), got " + kept.Triggers.Count + "/" + kept.TriggerObjects.Count);
        Assert.True(IsCreate(kept.Triggers[0], TriggerOff) && IsCreate(kept.Triggers[2], TriggerOn),
            "CaptureAsync: trigger statements 1 and 3 are not CREATE OR REPLACE TRIGGER … END; for the right triggers");
        Assert.True(kept.Triggers[1] == $"ALTER TRIGGER {CopyCommonStatements.Qualified(owner, TriggerOff)} DISABLE",
            "CaptureAsync: the disabled trigger is not followed by its ALTER TRIGGER … DISABLE");
        Assert.True(kept.TriggerObjects.Select(o => o.Name).SequenceEqual([TriggerOff, TriggerOff, TriggerOn], StringComparer.Ordinal)
                    && kept.TriggerObjects.All(o => o.Kind == DisabledKind.Trigger && o.Table == Parent && TiberoItSessions.Same(owner, o.Owner)),
            "CaptureAsync: TriggerObjects are not aligned with the trigger statements");

        var parentQualified = CopyCommonStatements.Qualified(owner, Parent);
        Assert.True(kept.Grants.Contains($"GRANT SELECT ON {parentQualified} TO PUBLIC", StringComparer.Ordinal),
            "CaptureAsync: the PUBLIC SELECT grant was not captured as GRANT SELECT … TO PUBLIC");
        if (optionGrant is null)
        {
            Assert.True(kept.Grants.Contains($"GRANT INSERT ON {parentQualified} TO PUBLIC WITH GRANT OPTION", StringComparer.Ordinal),
                "CaptureAsync: the grant with grant option was not captured WITH GRANT OPTION");
        }

        Assert.True(kept.ReferencingConstraints.Count == 2 && kept.ReferencingForeignKeys.Count == 2,
            "CaptureAsync: expected 2 referencing foreign keys, got " + kept.ReferencingConstraints.Count);
        var named = kept.ReferencingConstraints.ToList().FindIndex(o => o.Table == Child && o.Name == Fk);
        var unnamed = kept.ReferencingConstraints.ToList().FindIndex(o => o.Table == ServerChild && o.Name == serverFk);
        Assert.True(named >= 0 && unnamed >= 0, "CaptureAsync: the referencing foreign keys are not the named one and the server-named one");
        Assert.True(kept.ReferencingForeignKeys[named].Contains($"CONSTRAINT \"{Fk}\"", StringComparison.Ordinal)
                    && kept.ReferencingForeignKeys[named].EndsWith(" ON DELETE CASCADE", StringComparison.Ordinal),
            "CaptureAsync: the named referencing foreign key DDL lost its name or ON DELETE CASCADE (delete_rule)");

        var jobParents = new HashSet<string>([Parent], StringComparer.Ordinal);
        var source = await QuietAsync("SourceForeignKeysAsync(child)", () => step.SourceForeignKeysAsync(session, owner, Child, jobParents, owner, ct));
        Assert.True(source.Count == 1 && source[0].Name == Fk && source[0].Parent == Parent && source[0].Ddl.Contains(" ON DELETE CASCADE", StringComparison.Ordinal),
            "SourceForeignKeysAsync: the named child foreign key is wrong");
        var sourceServer = await QuietAsync("SourceForeignKeysAsync(server child)", () => step.SourceForeignKeysAsync(session, owner, ServerChild, jobParents, owner, ct));
        Assert.True(sourceServer.Count == 1 && sourceServer[0].Name is null && !sourceServer[0].Ddl.Contains("CONSTRAINT", StringComparison.Ordinal),
            "SourceForeignKeysAsync: a server-named foreign key must come back without a name");
        var outside = await QuietAsync("SourceForeignKeysAsync(other parents)",
            () => step.SourceForeignKeysAsync(session, owner, Child, new HashSet<string>(["ORT_NOT_IN_JOB"], StringComparer.Ordinal), owner, ct));
        Assert.True(outside.Count == 0, "SourceForeignKeysAsync: a foreign key to a parent outside the job was returned");

        // 2. 휴지통 켜기 → 참조 FK 떼기 → DROP(CASCADE·PURGE 없음) → 이번 DROP 의 BIN 이름 하나.
        await ExecQuietAsync(session, "RecycleBinOn", Dialect.RecycleBinOn().Text);
        var before = await QuietAsync("RecycleBinAsync(before)", () => step.RecycleBinAsync(session, Parent, ct));
        foreach (var fk in kept.ReferencingConstraints)
        {
            await ExecQuietAsync(session, "DropConstraint(referencing)", Dialect.DropConstraint(fk).Text);
        }

        await ExecQuietAsync(session, "DropTable", Dialect.DropTable(owner, Parent).Text);
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{Parent}'") == 0, "DropTable: the table is still there");
        var after = await QuietAsync("RecycleBinAsync(after)", () => step.RecycleBinAsync(session, Parent, ct));
        var added = after.Except(before, StringComparer.Ordinal).ToList();
        Assert.True(added.Count == 1, "RecycleBinAsync: expected exactly one new recycle bin name for the dropped table, got " + added.Count);
        var binName = added[0];
        output.WriteLine($"bin name: starts with '<account>_' = {binName.StartsWith(owner + "_", StringComparison.Ordinal)}, BIN$ shape = {binName.StartsWith("BIN$", StringComparison.Ordinal)}, length = {binName.Length}");

        // 3. 다시 만들기(휴지통에 간 옛 표의 PK·인덱스 이름과 겹치지 않는가) → 행 넣기 → 되살리기.
        var create = CreateTableDdl.Build(tableInfo, owner, jobParents);
        for (var i = 0; i < create.Count; i++)
        {
            await RunCreateAsync(session, owner, $"CreateTableDdl statement {i + 1}/{create.Count}", create[i]);
        }

        string[] reload = ["(1, 'a')", "(2, 'b')", "(3, 'c')"];
        for (var i = 0; i < reload.Length; i++)
        {
            await ExecQuietAsync(session, "reload row " + (i + 1), $"INSERT INTO {Parent} (ID, NAME) VALUES {reload[i]}");
        }

        await ExecQuietAsync(session, "commit reload", "COMMIT");

        for (var i = 0; i < kept.Triggers.Count; i++)
        {
            await ExecQuietAsync(session, $"restore trigger statement {i + 1}/{kept.Triggers.Count}", kept.Triggers[i]);
        }

        for (var i = 0; i < kept.Grants.Count; i++)
        {
            await ExecQuietAsync(session, $"restore grant {i + 1}/{kept.Grants.Count}", kept.Grants[i]);
        }

        await ExecQuietAsync(session, "restore named referencing FK", kept.ReferencingForeignKeys[named]);
        await ExecQuietAsync(session, "restore server-named referencing FK (explicit server name)", kept.ReferencingForeignKeys[unnamed]);

        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {Parent}") == 3, "recreate: the reloaded table does not have 3 rows");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{Parent}' AND constraint_name = '{Pk}' AND constraint_type = 'P' AND status = 'ENABLED'") == 1,
            "recreate: the named primary key is missing (name clash with the recycled table?)");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_indexes WHERE table_name = '{Parent}' AND index_name = '{Index}'") == 1,
            "recreate: the named index is missing");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_tab_comments WHERE table_name = '{Parent}' AND comments = 'rcs parent'") == 1, "recreate: table comment is missing");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_col_comments WHERE table_name = '{Parent}' AND column_name = 'NAME' AND comments = 'rcs name'") == 1,
            "recreate: column comment is missing");

        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_triggers WHERE table_name = '{Parent}' AND trigger_name = '{TriggerOn}' AND status = 'ENABLED'") == 1,
            "restore: the enabled trigger is missing or not ENABLED on the recreated table");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_triggers WHERE table_name = '{Parent}' AND trigger_name = '{TriggerOff}' AND status = 'DISABLED'") == 1,
            "restore: the disabled trigger is missing or not DISABLED on the recreated table");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name IN ('{TriggerOn}', '{TriggerOff}') AND object_type = 'TRIGGER' AND status = 'VALID'") == 2,
            "restore: the restored triggers are not both VALID");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM all_tab_privs WHERE owner = USER AND table_name = '{Parent}' AND grantee = 'PUBLIC' AND privilege = 'SELECT'") == 1,
            "restore: the PUBLIC SELECT grant is missing");
        if (optionGrant is null)
        {
            Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM all_tab_privs WHERE owner = USER AND table_name = '{Parent}' AND grantee = 'PUBLIC' AND privilege = 'INSERT' AND grantable IN ('YES', 'Y')") == 1,
                "restore: the PUBLIC INSERT grant with grant option is missing");
        }

        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{Child}' AND constraint_name = '{Fk}' AND constraint_type = 'R' AND status = 'ENABLED' AND delete_rule = 'CASCADE' AND r_constraint_name = '{Pk}'") == 1,
            "restore: the named referencing FK is missing, not ENABLED, lost ON DELETE CASCADE or does not reference the new primary key");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{ServerChild}' AND constraint_name = '{serverFk}' AND constraint_type = 'R' AND status = 'ENABLED'") == 1,
            "restore: the server-named referencing FK is missing or not ENABLED");

        await ExecQuietAsync(session, "insert after restore", $"INSERT INTO {Parent} (ID, NAME) VALUES (10, 'z')");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {Parent} WHERE ID = 10 AND NOTE = 'trg' AND NOTE2 IS NULL") == 1,
            "restore: the restored enabled trigger did not fire or the restored disabled trigger fired");
        await ExecQuietAsync(session, "rollback test row", "ROLLBACK");

        // 4. 실행기 안내 문장(NoteRecreated) 그대로: 휴지통 이름(소유자로 한정)으로 옛 표를 다른 이름으로 되살린다.
        var flashback = $"FLASHBACK TABLE {CopyCommonStatements.Qualified(owner, binName)} TO BEFORE DROP RENAME TO {SqlIdentifier.QuoteAlways(Parent + "_OLD")}";
        await ExecQuietAsync(session, "FLASHBACK TABLE <bin name> TO BEFORE DROP RENAME TO", flashback);
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{OldParent}'") == 1, "flashback: the old table is not back under the _OLD name");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {OldParent} WHERE NOTE = 'trg'") == 3, "flashback: the old table does not have its 3 old rows");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {Parent}") == 3, "flashback: the recreated table changed");
        var left = await QuietAsync("RecycleBinAsync(after flashback)", () => step.RecycleBinAsync(session, Parent, ct));
        Assert.True(!left.Contains(binName), "flashback: the bin name is still in the recycle bin");
    }

    private async Task ByteAsync(TiberoSession session, string owner)
    {
        var ct = CancellationToken.None;
        var provider = new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync);
        await ExecQuietAsync(session, "create byte table", $"CREATE TABLE {ByteTable} (ID NUMBER PRIMARY KEY, S VARCHAR2(4000 BYTE), CHECK (ID > 0))");
        await ExecQuietAsync(session, "insert 4000-byte value", $"INSERT INTO {ByteTable} (ID, S) VALUES (1, RPAD('x', 4000, 'x'))");
        await ExecQuietAsync(session, "commit", "COMMIT");

        // 문자셋: #76 에서 nls_database_parameters 는 (null). 후보 뷰를 차례로 찍는다(어느 것이 되는지 실측).
        foreach (var (what, sql) in new[]
                 {
                     ("nls_database_parameters", "SELECT value FROM nls_database_parameters WHERE parameter = 'NLS_CHARACTERSET'"),
                     ("v$nls_parameters", "SELECT value FROM v$nls_parameters WHERE parameter = 'NLS_CHARACTERSET'"),
                     ("database_properties", "SELECT property_value FROM database_properties WHERE property_name = 'NLS_CHARACTERSET'"),
                     ("lengthb of a 3-byte char", "SELECT TO_CHAR(LENGTHB(UNISTR('\\AC00'))) FROM DUAL"),
                 })
        {
            output.WriteLine($"database character set via {what}: " + await TryScalarAsync(session, sql));
        }
        output.WriteLine("source S (char_used/data_length/char_length): " + await TryScalarAsync(session, ColumnShapeSql(ByteTable)));

        var info = await QuietAsync("GetTableAsync(byte table)", () => provider.GetTableAsync(session, new DbObjectRef(owner, ByteTable, "TABLE"), ct));
        var length = info.Columns.FirstOrDefault(c => c.Name == "S")?.Length;
        output.WriteLine("meta.columns length for S: " + (length?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"));
        var keyNames = info.Constraints.Where(c => c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Check).Select(c => c.Name).ToList();
        Assert.True(info.Constraints.Any(c => c.Kind == ConstraintKind.PrimaryKey) && info.Constraints.Any(c => c.Kind == ConstraintKind.Check),
            "the unnamed primary key and check were not both read (" + keyNames.Count + " PK/CHECK constraints)");
        Assert.True(keyNames.All(n => TiberoRecreateStep.IsServerName(n, owner)), "a server-generated PK/CHECK name is not recognized by TiberoRecreateStep.IsServerName");
        output.WriteLine("server-generated PK/CHECK name prefixes: " + string.Join(", ", keyNames.Select(n => Prefix(n, owner)))
                         + "; Core CreateTableDdl.IsServerName = " + string.Join(", ", keyNames.Select(CreateTableDdl.IsServerName)));

        await ExecQuietAsync(session, "RecycleBinOn", Dialect.RecycleBinOn().Text);
        await ExecQuietAsync(session, "DropTable(byte table)", Dialect.DropTable(owner, ByteTable).Text);
        var create = CreateTableDdl.Build(info, owner, new HashSet<string>([ByteTable], StringComparer.Ordinal));
        Assert.True(create[0].Contains($"\"S\" VARCHAR2({length?.ToString(System.Globalization.CultureInfo.InvariantCulture)} CHAR)", StringComparison.Ordinal),
            "CreateTableDdl: S is not written with a CHAR length");
        for (var i = 0; i < create.Count; i++)
        {
            await RunCreateAsync(session, owner, $"CreateTableDdl statement {i + 1}/{create.Count} (byte table)", create[i]);
        }

        output.WriteLine("recreated S (char_used/data_length/char_length): " + await TryScalarAsync(session, ColumnShapeSql(ByteTable)));
        await ExecQuietAsync(session, "insert 4000-byte value into the recreated table", $"INSERT INTO {ByteTable} (ID, S) VALUES (1, RPAD('x', 4000, 'x'))");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {ByteTable} WHERE LENGTH(S) = 4000") == 1, "the 4000-byte value did not round-trip into the recreated table");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{ByteTable}' AND constraint_type = 'P' AND status = 'ENABLED'") == 1,
            "the recreated byte table has no enabled primary key");
        await ExecQuietAsync(session, "commit byte table", "COMMIT");

        // 리뷰 TB4 #1 실측(단언 없음): 다시 만든 표에 없는 열을 가리키는 트리거를 되살리면 서버가 무엇을 주는가 — 오류 번호와 남는 상태.
        // 실행기는 컴파일 오류(Oracle ORA-24344)면 그 트리거를 끈다. Tibero 번호를 TiberoErrorClassifier 에 넣을지 이것으로 정한다.
        var badTrigger = await TryExecAsync(session,
            $"CREATE OR REPLACE TRIGGER {BadTrigger} BEFORE INSERT ON {ByteTable} FOR EACH ROW\nBEGIN\n  :NEW.NO_SUCH_COLUMN := 1;\nEND;");
        output.WriteLine("trigger referencing a missing column: " + (badTrigger ?? "accepted")
                         + "; status = " + await TryScalarAsync(session, $"SELECT status FROM user_objects WHERE object_name = '{BadTrigger}' AND object_type = 'TRIGGER'")
                         + "; trigger status = " + await TryScalarAsync(session, $"SELECT status FROM user_triggers WHERE trigger_name = '{BadTrigger}'"));
        await TryExecAsync(session, $"DROP TRIGGER {BadTrigger}");

        // 최대 바이트 길이: 결과만 남긴다(CHAR 로 다시 쓰면 멀티바이트 문자셋에서 바이트 한도를 넘을 수 있다).
        var maxSource = await TryExecAsync(session, $"CREATE TABLE {ByteMaxTable} (S VARCHAR2(65532 BYTE))");
        if (maxSource is not null)
        {
            output.WriteLine("VARCHAR2(65532 BYTE) source table: create failed " + maxSource);
            return;
        }

        var maxInfo = await QuietAsync("GetTableAsync(max byte table)", () => provider.GetTableAsync(session, new DbObjectRef(owner, ByteMaxTable, "TABLE"), ct));
        var maxLength = maxInfo.Columns.FirstOrDefault(c => c.Name == "S")?.Length;
        await ExecQuietAsync(session, "DropTable(max byte table)", Dialect.DropTable(owner, ByteMaxTable).Text);
        var maxCreate = CreateTableDdl.Build(maxInfo, owner, new HashSet<string>([ByteMaxTable], StringComparer.Ordinal));
        var maxResult = await TryExecAsync(session, maxCreate[0]);
        output.WriteLine($"VARCHAR2(65532 BYTE): meta.columns length = {maxLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}, "
                         + $"recreated as VARCHAR2(<length> CHAR): {maxResult ?? "accepted"}");
        if (maxResult is null)
        {
            output.WriteLine("recreated max S (char_used/data_length/char_length): " + await TryScalarAsync(session, ColumnShapeSql(ByteMaxTable)));
        }
    }

    private static string ColumnShapeSql(string table) =>
        $"SELECT char_used || '/' || data_length || '/' || char_length FROM user_tab_cols WHERE table_name = '{table}' AND column_name = 'S'";

    private static bool IsCreate(string text, string trigger) =>
        text.StartsWith("CREATE OR REPLACE TRIGGER ", StringComparison.Ordinal)
        && text.Contains("\"" + trigger + "\"", StringComparison.Ordinal)
        && text.EndsWith("END;", StringComparison.Ordinal)
        && !text.Contains("ALTER TRIGGER", StringComparison.Ordinal);

    /// <summary>
    /// 서버 이름의 숫자 앞 부분(예: <c>&lt;OWNER&gt;_CON</c>). 서버 규칙만 남긴다: 소유자(접속 계정) 이름은 <c>&lt;OWNER&gt;</c> 로 가린다(출력에 계정 값 금지).
    /// </summary>
    private static string Prefix(string name, string owner)
    {
        var digit = name.AsSpan().IndexOfAnyInRange('0', '9');
        var head = digit < 0 ? name : name[..digit];
        return head.Replace(owner, "<OWNER>", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>시험용 생성자의 행 실행기: 세션 질의 도우미 + 이름 바인드.</summary>
    private static async Task<List<object?[]>> RunAsync(ISession session, string sql, (string Name, string Value)[] binds, CancellationToken ct)
    {
        var table = await TiberoSessionQuery.RunAsync(session, sql, c =>
        {
            foreach (var (name, value) in binds)
            {
                TiberoSessionQuery.Bind(c, name, value);
            }
        }, ct: ct);
        return [.. table.Rows];
    }

    /// <summary>쓰기 시험 틀: 남은 제 객체를 지우고 본문을 돌린 뒤 다시 지운다. 본문 실패는 정리 뒤 그대로 다시 던진다.</summary>
    private static async Task WriteAsync(string[] tables, Func<TiberoSession, string, Task> body)
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        await CleanupAsync(session, tables);

        Exception? failure = null;
        try
        {
            await body(session, it.Env.User.ToUpperInvariant());
        }
#pragma warning disable CA1031 // 본문 실패를 잡아 두고 정리한 뒤 그대로 다시 던진다(정리가 본문 실패를 가리지 않게).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        await CleanupAsync(session, tables);
        await IgnoreAsync(session, "ALTER SESSION SET USE_RECYCLEBIN = N");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        var names = string.Join(", ", tables.Select(t => "'" + t + "'"));
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name IN ({names})") == 0, "ORT_ cleanup failed: a test table is left");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_recyclebin WHERE original_name IN ({names})") == 0, "ORT_ cleanup failed: a test table is left in the recycle bin");
    }

    /// <summary>미커밋 변경을 되돌리고 표를 지운 뒤(CASCADE CONSTRAINTS PURGE) 이 시험 표의 휴지통 항목만 비운다. 오류는 무시한다(없는 객체 등).</summary>
    private static async Task CleanupAsync(TiberoSession session, string[] tables)
    {
        await IgnoreAsync(session, "ROLLBACK");
        foreach (var table in tables)
        {
            await IgnoreAsync(session, $"DROP TABLE \"{table}\" CASCADE CONSTRAINTS PURGE");
        }

        await PurgeOwnBinItemsAsync(session, tables);
    }

    /// <summary>휴지통에서 이 시험이 지운 ORT_ 표의 항목만 BIN 이름으로 <c>PURGE TABLE</c> 한다(<c>PURGE RECYCLEBIN</c> 금지, TM0b 규칙). 오류는 무시한다.</summary>
    private static async Task PurgeOwnBinItemsAsync(TiberoSession session, IEnumerable<string> tables)
    {
        foreach (var table in tables)
        {
            TiberoSessionQuery.Table items;
            try
            {
                items = await TiberoSessionQuery.RunAsync(session, "SELECT object_name FROM user_recyclebin WHERE original_name = :t",
                    c => TiberoSessionQuery.Bind(c, "t", table));
            }
#pragma warning disable CA1031 // 정리 단계: 오류는 무시한다.
            catch (Exception)
#pragma warning restore CA1031
            {
                continue;
            }

            foreach (var row in items.Rows)
            {
                if (TiberoSessionQuery.Str(row[0]) is { Length: > 0 } binName)
                {
                    await IgnoreAsync(session, "PURGE TABLE " + SqlIdentifier.QuoteAlways(binName));
                }
            }
        }
    }

    private static async Task<List<string>> RowsAsync(TiberoSession session, string sql)
    {
        var result = await QuietAsync("query", () => TiberoSessionQuery.RunAsync(session, sql));
        return [.. result.Rows.Select(r => TiberoSessionQuery.Str(r[0]) ?? string.Empty)];
    }

    /// <summary>값 하나를 글자로(실패면 형식 이름·오류 번호). 출력에만 쓴다 — 계정 값이 나오는 질의에는 쓰지 않는다.</summary>
    private static async Task<string> TryScalarAsync(TiberoSession session, string sql)
    {
        try
        {
            return TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? "(null)";
        }
#pragma warning disable CA1031 // 출력용: 실패를 글로 바꾼다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return "unreadable " + Describe(ex);
        }
    }

    private static Task<long> CountAsync(TiberoSession session, string sql) =>
        QuietAsync("count query", async () => TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0);

    /// <summary>문장을 보내고 실패하면 (형식 이름·오류 번호) 글을, 성공하면 null 을 돌려준다.</summary>
    private static async Task<string?> TryExecAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
            return null;
        }
#pragma warning disable CA1031 // 실패를 글로 바꿔 돌려준다(접속 값 없는 형식 이름·오류 번호만).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Describe(ex);
        }
    }

    private static async Task IgnoreAsync(TiberoSession session, string sql) => _ = await TryExecAsync(session, sql);

    /// <summary>예외 메시지에는 <c>소유자.이름</c>(접속 계정)이 실릴 수 있다. 형식 이름과 서버 오류 번호만 담아 다시 던진다.</summary>
    private static async Task<T> QuietAsync<T>(string what, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
#pragma warning disable CA1031 // 시험 실패로 바꿔 던진다(메시지에서 접속 계정을 뺀다).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new Xunit.Sdk.XunitException(what + " failed: " + Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var code = TiberoErrorClassifier.ToDbError(ex, null)?.Code;
        return ex.GetType().Name + (code is null ? string.Empty : " " + code);
    }

    private static async Task ExecQuietAsync(TiberoSession session, string what, string sql) =>
        await QuietAsync(what, async () => { await ExecAsync(session, sql); return true; });

    /// <summary>Core 가 만든 다시 만들기 문장을 보낸다. 실패하면 문장 글을 출력에 남긴다(소유자 = 접속 계정 이름은 <c>&lt;OWNER&gt;</c> 로 가림) — 문법 원인을 바로 본다.</summary>
    private async Task RunCreateAsync(TiberoSession session, string owner, string what, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
#pragma warning disable CA1031 // 글을 남기고 시험 실패로 바꿔 던진다(메시지에서 접속 계정을 뺀다).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine(what + " text: " + sql.Replace(owner, "<OWNER>", StringComparison.OrdinalIgnoreCase));
            throw new Xunit.Sdk.XunitException(what + " failed: " + Describe(ex));
        }
    }

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
