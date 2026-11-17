using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Schema;

// 계획 Task 3(T3b): 테이블 편집기 Oracle 메타데이터 보강(TE-29)·검사 읽기(ITableEditChecks)·방언을 실제 Oracle 에서 본다.
// 상주 컨테이너: ORANDA_IT_HOST, ORANDA_IT_PORT(기본 1521), ORANDA_IT_SERVICE, ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD.
// 26ai = 1521/FREEPDB1, 11g = ORANDA_IT_PORT=1522 · ORANDA_IT_SERVICE=XE 로 한 번 더 돌린다. 환경 변수가 없으면 건너뛰고(REQUIRE=1 이면 실패),
// 시험 계정은 실행마다 고유 이름(TE_xxxxxxxx)으로 만들고 끝나면 지운다. 실측 근거 = docs/design/oracle-table-edit-probe.md.
// 편집 모델 → 문장: 실측 표마다 「안 고치면 0문장」(설계 8.4)과 8.2 문장을 실제로 실행한 뒤 다시 읽어 0문장.
[Trait("Category", "Integration")]
public sealed class TableEditIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _owner = "TE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _password = "Te" + Guid.NewGuid().ToString("N")[..16] + "x1";
    private readonly List<ISession> _sessions = [];
    private readonly OracleTableEditChecks _checks = new();
    private bool _enabled;
    private int _major;
    private string _serverVersion = string.Empty;

    public TableEditIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

    private DbObjectRef Orders => new(_owner, "ORDERS", "TABLE");

    private DbObjectRef Items => new(_owner, "ORDER_ITEMS", "TABLE");

    public async Task InitializeAsync()
    {
        _enabled = Env("HOST") is not null && Env("SERVICE") is not null && Env("ADMIN_USER") is not null && Env("ADMIN_PASSWORD") is not null;
        if (!_enabled)
        {
            if (Env("REQUIRE") == "1")
            {
                throw new InvalidOperationException("ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            }

            return;
        }

        await using (var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!))
        {
            await admin.OpenAsync();
            _serverVersion = admin.ServerVersion;
            _major = int.Parse(_serverVersion.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
            await ExecAsync(admin, $"CREATE USER {_owner} IDENTIFIED BY \"{_password}\" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users");
            await ExecAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE TO {_owner}");
        }

        try
        {
            await using var owner = Connect(_owner, _password);
            await owner.OpenAsync();
            foreach (var sql in Setup())
            {
                await ExecAsync(owner, sql);
            }
        }
        catch
        {
            await DropOwnerAsync(); // InitializeAsync 가 실패하면 xUnit 은 DisposeAsync 를 부르지 않는다 — 계정이 남지 않게 여기서 지운다
            throw;
        }
    }

    private async Task DropOwnerAsync()
    {
        await using var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!);
        await admin.OpenAsync();
        await ExecAsync(admin, $"DROP USER {_owner} CASCADE");
    }

    /// <summary>실측 표: CHAR 기준·NUMBER(*,0)·기본값 줄바꿈·가상 열·FK 삭제 규칙·DESC·함수 기반 인덱스·GTT·IOT·한글 데이터.</summary>
    private IEnumerable<string> Setup()
    {
        yield return """
            CREATE TABLE orders (
              id NUMBER CONSTRAINT pk_orders PRIMARY KEY,
              order_no VARCHAR2(10 CHAR) CONSTRAINT uk_orders_01 UNIQUE,
              amount NUMBER(10,2),
              qty NUMBER(*,0),
              note VARCHAR2(20 BYTE) DEFAULT 'x'
              ,
              code CHAR(3),
              nname NVARCHAR2(10),
              total AS (amount * 2)
            )
            """;
        yield return """
            CREATE TABLE order_items (
              id NUMBER PRIMARY KEY,
              order_id NUMBER,
              order_ref NUMBER,
              order_code VARCHAR2(10 CHAR),
              qty NUMBER(5),
              name VARCHAR2(30 CHAR),
              CONSTRAINT fk_items_01 FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE SET NULL,
              CONSTRAINT fk_items_02 FOREIGN KEY (order_ref) REFERENCES orders (id) ON DELETE CASCADE,
              CONSTRAINT fk_items_03 FOREIGN KEY (order_code) REFERENCES orders (order_no),
              CONSTRAINT ck_items_01 CHECK (qty > 0)
            )
            """;
        yield return "CREATE INDEX ix_items_01 ON order_items (order_id DESC, qty)";
        yield return "CREATE INDEX ix_items_02 ON order_items (UPPER(name))";
        yield return "CREATE GLOBAL TEMPORARY TABLE work_tmp (id NUMBER) ON COMMIT PRESERVE ROWS";
        yield return "CREATE TABLE lookup_iot (id NUMBER, v VARCHAR2(10), CONSTRAINT pk_lookup_iot PRIMARY KEY (id)) ORGANIZATION INDEX";
        yield return "CREATE VIEW v_orders AS SELECT id, amount FROM orders";
        yield return "CREATE VIEW v_slow AS SELECT a.object_id FROM all_objects a, all_objects b, all_objects c";
        yield return "INSERT INTO orders (id, order_no, amount, qty, code, nname) VALUES (1, '가나다라', 12345678.25, 7, 'ab', N'가')";
        yield return "INSERT INTO orders (id, order_no, amount, qty) VALUES (2, 'A2', 1.5, NULL)";
        yield return "INSERT INTO orders (id, order_no, amount, qty) VALUES (3, 'A3', NULL, NULL)";
        yield return "INSERT INTO order_items (id, order_id, order_ref, order_code, qty, name) VALUES (10, 1, 1, 'A2', 1, 'x')";
        yield return "INSERT INTO order_items (id, order_id, order_ref, order_code, qty, name) VALUES (11, 1, 2, NULL, 1, 'x')";
        yield return "INSERT INTO order_items (id, order_id, order_ref, order_code, qty, name) VALUES (12, 2, NULL, NULL, 2, NULL)";
        yield return "INSERT INTO order_items (id, order_id, order_ref, order_code, qty, name) VALUES (13, NULL, NULL, NULL, 3, NULL)";
        yield return "INSERT INTO order_items (id, order_id, order_ref, order_code, qty, name) VALUES (14, NULL, NULL, NULL, 4, 'x')";
        yield return "COMMIT";
        if (_major >= 12)
        {
            yield return "CREATE TABLE seq_rows (id NUMBER GENERATED BY DEFAULT ON NULL AS IDENTITY (START WITH 100 INCREMENT BY 5), v NUMBER)";
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        if (!_enabled)
        {
            return;
        }

        await DropOwnerAsync();
    }

    [Fact]
    public async Task Metadata_reads_char_semantics_delete_rules_and_referenced_columns()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var orders = await NewProvider().GetTableAsync(s, Orders, default);
        var items = await NewProvider().GetTableAsync(s, Items, default);

        Assert.Equal(CharSemantics.Characters, Col(orders, "ORDER_NO").CharSemantics);
        Assert.Equal(CharSemantics.Bytes, Col(orders, "NOTE").CharSemantics);
        Assert.Equal(CharSemantics.Bytes, Col(orders, "CODE").CharSemantics);
        Assert.Null(Col(orders, "NNAME").CharSemantics); // N 계열은 기준을 쓰지 않는다
        Assert.Null(Col(orders, "AMOUNT").CharSemantics);
        Assert.Equal((null, 0), (Col(orders, "QTY").Precision, Col(orders, "QTY").Scale)); // NUMBER(*,0)
        Assert.Equal("'x'", Col(orders, "NOTE").DefaultValue); // 줄바꿈·공백은 다듬는다
        Assert.True(Col(orders, "TOTAL").IsVirtual);
        Assert.Equal("\"AMOUNT\"*2", Col(orders, "TOTAL").DefaultValue); // 서버 표기

        var fk1 = items.Constraints.Single(c => c.Name == "FK_ITEMS_01");
        Assert.Equal(DeleteRule.SetNull, fk1.DeleteRule);
        Assert.Equal(["ID"], fk1.ReferencedColumns);
        Assert.Equal(DeleteRule.Cascade, items.Constraints.Single(c => c.Name == "FK_ITEMS_02").DeleteRule);
        var fk3 = items.Constraints.Single(c => c.Name == "FK_ITEMS_03");
        Assert.Equal(DeleteRule.NoAction, fk3.DeleteRule);
        Assert.Equal(["ORDER_NO"], fk3.ReferencedColumns);
        Assert.Equal("UK_ORDERS_01", fk3.ReferencedConstraint);
    }

    [Fact]
    public async Task Metadata_reads_descending_expressions_tablespace_and_constraint_indexes()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var items = await NewProvider().GetTableAsync(s, Items, default);
        var orders = await NewProvider().GetTableAsync(s, Orders, default);

        var desc = items.Indexes.Single(i => i.Name == "IX_ITEMS_01");
        Assert.Equal(["ORDER_ID", "QTY"], desc.Columns); // 숨은 열 SYS_NC… 대신 원래 이름
        Assert.Equal([true, false], desc.Descending);
        Assert.Empty(desc.Expressions);
        Assert.Equal("USERS", desc.Tablespace);

        var fn = items.Indexes.Single(i => i.Name == "IX_ITEMS_02");
        Assert.Equal(["UPPER(\"NAME\")"], fn.Expressions);
        Assert.StartsWith("SYS_NC", fn.Columns[0], StringComparison.Ordinal);

        Assert.Equal("PK_ORDERS", orders.Indexes.Single(i => i.Name == "PK_ORDERS").ConstraintName);
        Assert.Equal("UK_ORDERS_01", orders.Indexes.Single(i => i.Name == "UK_ORDERS_01").ConstraintName);
        var sysPk = items.Constraints.Single(c => c.Kind == ConstraintKind.PrimaryKey).Name;
        Assert.Equal(sysPk, items.Indexes.Single(i => i.Name == sysPk).ConstraintName);
        Assert.Null(desc.ConstraintName);
    }

    [Fact]
    public async Task Metadata_reads_table_kind_and_tablespace()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var provider = NewProvider();

        var orders = await provider.GetTableAsync(s, Orders, default);
        Assert.Equal((TableKind.Heap, "USERS", false), (orders.Kind, orders.Tablespace, orders.IsPartitioned));
        Assert.Equal(TableKind.Temporary, (await provider.GetTableAsync(s, new DbObjectRef(_owner, "WORK_TMP", "TABLE"), default)).Kind);
        Assert.Equal(TableKind.Iot, (await provider.GetTableAsync(s, new DbObjectRef(_owner, "LOOKUP_IOT", "TABLE"), default)).Kind);
        Assert.Equal(TableKind.Heap, (await provider.GetTableAsync(s, new DbObjectRef(_owner, "V_ORDERS", "VIEW"), default)).Kind);
    }

    [Fact]
    public async Task Metadata_reads_identity_options_from_12c()
    {
        if (Skip())
        {
            return;
        }

        if (_major < 12)
        {
            _output.WriteLine("건너뜀: 11g 에는 신원 열이 없다");
            return;
        }

        var s = await OpenAsync();
        var id = Col(await NewProvider().GetTableAsync(s, new DbObjectRef(_owner, "SEQ_ROWS", "TABLE"), default), "ID");

        Assert.True(id.IsIdentity);
        Assert.Equal(new IdentityInfo(IdentityGeneration.ByDefaultOnNull) { StartWith = 100, IncrementBy = 5 }, id.Identity);
    }

    [Fact]
    public async Task Untouched_real_tables_produce_no_statements()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var dialect = new OracleTableEditDialect();
        var names = new List<string> { "ORDERS", "ORDER_ITEMS", "WORK_TMP", "LOOKUP_IOT" };
        if (_major >= 12)
        {
            names.Add("SEQ_ROWS");
        }

        foreach (var name in names)
        {
            var table = await NewProvider().GetTableAsync(s, new DbObjectRef(_owner, name, "TABLE"), default);
            var draft = TableDraftFactory.FromTable(table, dialect);

            var script = TableEditScriptBuilder.Build(draft, draft, dialect);
            Assert.True(script.IsEmpty, name + ": " + script.PreviewText());
        }
    }

    [Fact]
    public async Task Edit_statements_run_and_the_reread_table_has_no_differences()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var dialect = new OracleTableEditDialect();
        var orders = TableDraftFactory.FromTable(await NewProvider().GetTableAsync(s, Orders, default), dialect);
        string Key(TableDraft d, string column) => d.Columns.Single(c => c.Name == column).RowKey;

        var editedOrders = orders with
        {
            Comment = "주문",
            Columns =
            [
                .. orders.Columns.Select(c => c.Name switch
                {
                    "ORDER_NO" => c with { Length = 20 }, // 글자 기준을 지켜야 한다(실측 4a)
                    "NOTE" => c with { DefaultValue = null },
                    "NNAME" => c with { Name = "NICK" },
                    "AMOUNT" => c with { Comment = "금액" },
                    "TOTAL" => c with { VirtualExpression = "amount * 3" }, // 실측 1: MODIFY (c AS (expr))
                    "CODE" => c with { IsDropped = true },
                    _ => c,
                }),
                new ColumnDraft("n-status", "STATUS") { DataType = "VARCHAR2", Length = 1, DefaultValue = "'N'", IsNullable = false },
                new ColumnDraft("n-total2", "TOTAL2") { VirtualExpression = "amount + 1" },
            ],
            Constraints =
            [
                .. orders.Constraints,
                new ConstraintDraft("n-ck", "CK_ORDERS_01", ConstraintKind.Check) { Condition = "amount >= 0" },
                new ConstraintDraft("n-uk", "UK_ORDERS_02", ConstraintKind.Unique) { ColumnKeys = ["n-status", Key(orders, "ID")] },
            ],
            Indexes =
            [
                .. orders.Indexes,
                new IndexDraft("n-ix", "IX_ORDERS_01") { Columns = [new(Key(orders, "AMOUNT"), Descending: true), new(Key(orders, "QTY"))] },
            ],
        };

        var items = TableDraftFactory.FromTable(await NewProvider().GetTableAsync(s, Items, default), dialect);
        var editedItems = items with
        {
            Constraints =
            [
                .. items.Constraints.Select(c => c.Name switch
                {
                    "FK_ITEMS_02" => c with { IsDropped = true },
                    "CK_ITEMS_01" => c with { IsEnabled = false },
                    _ => c,
                }),
                new ConstraintDraft("n-fk", "FK_ITEMS_04", ConstraintKind.ForeignKey)
                {
                    ColumnKeys = [Key(items, "ORDER_REF")],
                    ReferencedTable = Orders,
                    ReferencedColumns = ["ID"],
                    DeleteRule = DeleteRule.Cascade,
                },
            ],
            Indexes =
            [
                .. items.Indexes.Select(i => i.Name switch
                {
                    "IX_ITEMS_01" => i with { Name = "IX_ITEMS_11" },
                    "IX_ITEMS_02" => i with { IsDropped = true },
                    _ => i,
                }),
            ],
        };

        await using (var owner = Connect(_owner, _password))
        {
            await owner.OpenAsync();
            foreach (var (original, edited) in new[] { (orders, editedOrders), (items, editedItems) })
            {
                var script = TableEditScriptBuilder.Build(original, edited, dialect);
                Assert.False(script.IsEmpty);
                _output.WriteLine(script.PreviewText());
                foreach (var statement in script.SqlStatements)
                {
                    try
                    {
                        await ExecAsync(owner, statement.Text);
                    }
                    catch (OracleException ex)
                    {
                        Assert.Fail($"{statement.Text}{Environment.NewLine}{ex.Message}");
                    }
                }
            }
        }

        var rereadOrders = await NewProvider().GetTableAsync(s, Orders, default);
        var rereadItems = await NewProvider().GetTableAsync(s, Items, default);
        foreach (var table in new[] { rereadOrders, rereadItems })
        {
            var draft = TableDraftFactory.FromTable(table, dialect);
            var again = TableEditScriptBuilder.Build(draft, draft, dialect);
            Assert.True(again.IsEmpty, table.Table.Name + ": " + again.PreviewText());
        }

        Assert.Equal((20, CharSemantics.Characters), (Col(rereadOrders, "ORDER_NO").Length, Col(rereadOrders, "ORDER_NO").CharSemantics));
        Assert.True(Col(rereadOrders, "NOTE").DefaultValue is null or "NULL"); // 실측 5: DEFAULT NULL 은 글자 NULL 로 남는다
        Assert.Equal("\"AMOUNT\"*3", Col(rereadOrders, "TOTAL").DefaultValue);
        Assert.DoesNotContain(rereadOrders.Columns, c => c.Name is "CODE" or "NNAME");
        Assert.Contains(rereadOrders.Columns, c => c.Name == "NICK");
        Assert.False(Col(rereadOrders, "STATUS").IsNullable);
        Assert.Equal("주문", rereadOrders.Comment);
        Assert.Equal([true, false], rereadOrders.Indexes.Single(i => i.Name == "IX_ORDERS_01").Descending);
        Assert.DoesNotContain(rereadItems.Constraints, c => c.Name == "FK_ITEMS_02");
        Assert.False(rereadItems.Constraints.Single(c => c.Name == "CK_ITEMS_01").IsEnabled);
        Assert.Equal(DeleteRule.Cascade, rereadItems.Constraints.Single(c => c.Name == "FK_ITEMS_04").DeleteRule);
        Assert.Contains(rereadItems.Indexes, i => i.Name == "IX_ITEMS_11");
        Assert.DoesNotContain(rereadItems.Indexes, i => i.Name == "IX_ITEMS_02");
    }

    [Fact]
    public async Task Row_counts_and_statistics()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();

        Assert.Equal(3, await _checks.CountRowsAsync(s, Orders, default));
        Assert.Null(await _checks.NumRowsAsync(s, Orders, default)); // 통계 없음
        await using (var owner = Connect(_owner, _password))
        {
            await owner.OpenAsync();
            await ExecAsync(owner, "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'ORDERS'); END;");
        }

        Assert.Equal(3, await _checks.NumRowsAsync(s, Orders, default));
    }

    [Fact]
    public async Task Length_overflow_counts_characters_or_bytes_by_the_new_semantics()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();

        // '가나다라' = 4글자 12바이트. 실측 ORA-01441 과 같은 판정
        Assert.Equal(1, await _checks.OverflowCountAsync(s, Orders, "ORDER_NO", Text("VARCHAR2", 3, CharSemantics.Characters), default));
        Assert.Equal(0, await _checks.OverflowCountAsync(s, Orders, "ORDER_NO", Text("VARCHAR2", 4, CharSemantics.Characters), default));
        Assert.Equal(1, await _checks.OverflowCountAsync(s, Orders, "ORDER_NO", Text("VARCHAR2", 10, CharSemantics.Bytes), default));
        Assert.Equal(1, await _checks.OverflowCountAsync(s, Orders, "CODE", Text("CHAR", 2, CharSemantics.Bytes), default)); // CHAR 는 빈칸까지
    }

    [Fact]
    public async Task Number_decrease_counts_every_value_and_increase_counts_integer_overflow()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();

        // 실측 ORA-01440: 정밀도·소수를 줄이면 들어갈 값이어도 데이터가 있으면 안 된다 → NULL 아닌 행 모두
        Assert.Equal(2, await _checks.OverflowCountAsync(s, Orders, "AMOUNT", Number(12, 1), default));
        Assert.Equal(2, await _checks.OverflowCountAsync(s, Orders, "AMOUNT", Number(9, 2), default));
        Assert.Equal(0, await _checks.OverflowCountAsync(s, Orders, "AMOUNT", Number(12, 2), default));
        Assert.Equal(0, await _checks.OverflowCountAsync(s, Orders, "QTY", Number(null, null), default)); // NUMBER(*,0) → NUMBER 은 넓히기
        Assert.Equal(1, await _checks.OverflowCountAsync(s, Orders, "QTY", Number(1, null), default)); // 38 → 1 자리 줄이기, 값 7 하나
        Assert.Equal(2, await _checks.OverflowCountAsync(s, Orders, "AMOUNT", Number(10, 4), default)); // 실측 12i: 정수 자리 8 → 6 도 ORA-01440
        Assert.Equal(0, await _checks.OverflowCountAsync(s, Orders, "QTY", Number(null, 2), default)); // 정밀도 안 쓴 (*,0) → (*,2) 는 된다
        Assert.Equal(0, await _checks.OverflowCountAsync(s, Orders, "ORDER_NO", Number(10, 0), default)); // 형식 바꾸기는 이 검사 몫이 아니다(ORA-01722 없이 0)
        Assert.Equal(0, await _checks.OverflowCountAsync(s, Orders, "ORDER_NO", new ColumnDraft("k", "ORDER_NO") { DataType = "DATE" }, default));
    }

    [Fact]
    public async Task Null_nonnull_duplicate_orphan_and_check_counts()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();

        Assert.Equal(2, await _checks.NullCountAsync(s, Orders, "QTY", default));
        Assert.Equal(1, await _checks.NonNullCountAsync(s, Orders, "QTY", default));
        Assert.Equal(2, await _checks.DuplicateKeyCountAsync(s, Items, ["ORDER_ID", "QTY"], default)); // (1,1) 두 줄
        Assert.Equal(0, await _checks.DuplicateKeyCountAsync(s, Items, ["ID"], default));
        Assert.Equal(2, await _checks.DuplicateKeyCountAsync(s, Items, ["NAME", "ORDER_CODE"], default)); // ('x',NULL) 두 줄은 겹침(실측 ORA-02299), (NULL,NULL) 은 뺌
        Assert.Equal(0, await _checks.OrphanCountAsync(s, Items, ["ORDER_ID"], Orders, ["ID"], default)); // NULL 키는 세지 않는다
        Assert.Equal(5, await _checks.OrphanCountAsync(s, Items, ["ID"], Orders, ["ID"], default)); // 10~14 는 orders 에 없다
        Assert.Equal(3, await _checks.CheckViolationCountAsync(s, Items, "qty < 2", default)); // 2·3·4
        Assert.Equal(0, await _checks.CheckViolationCountAsync(s, Orders, "qty > 0", default)); // NULL 은 UNKNOWN → 어기지 않음
        Assert.Equal(3, await _checks.CheckViolationCountAsync(s, Items, "qty < 2 -- 끝 주석", default)); // 서버는 CHECK 원문을 주석째 둔다
    }

    [Fact]
    public async Task Expression_preview_returns_rows_or_the_server_error()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();

        var ok = await _checks.PreviewExpressionAsync(s, Orders, "amount * 3", ["AMOUNT"], 5, default);
        Assert.Null(ok.Error);
        Assert.Equal(["EXPRESSION_VALUE", "AMOUNT"], ok.Columns);
        Assert.Equal(3, ok.Rows.Count);
        Assert.Contains(ok.Rows, r => r[1] is null && r[0] is null);

        var tiny = await _checks.PreviewExpressionAsync(s, Orders, "1/3", [], 1, default);
        Assert.Single(tiny.Rows); // 38자리 NUMBER 도 글로 읽는다
        Assert.Contains("3333333333333333333333333333", tiny.Rows[0][0], StringComparison.Ordinal);

        var commented = await _checks.PreviewExpressionAsync(s, Orders, "amount + 1 -- 끝 주석", ["AMOUNT"], 5, default);
        Assert.Null(commented.Error);
        Assert.All(commented.Rows, r => Assert.Equal(2, r.Count)); // 주석이 뒤 열을 먹지 않는다

        var bad = await _checks.PreviewExpressionAsync(s, Orders, "no_such_col + 1", [], 5, default);
        Assert.Contains("ORA-00904", bad.Error, StringComparison.Ordinal);
        Assert.Empty(bad.Rows);
    }

    [Fact]
    public async Task Expression_validation_uses_an_empty_row_of_the_new_columns()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        ColumnDraft[] columns = [new("n1", "QTY") { DataType = "NUMBER", Precision = 5 }, new("n2", "NAME") { DataType = "VARCHAR2", Length = 10 }];

        Assert.Null(await _checks.ValidateExpressionAsync(s, _owner, columns, "qty * 2 || UPPER(name)", default));
        Assert.Contains("ORA-00904", await _checks.ValidateExpressionAsync(s, _owner, columns, "price * 2", default), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Name_checks_cover_each_namespace_and_dependents_list_the_view()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();

        Assert.True(await _checks.NameExistsAsync(s, _owner, "V_ORDERS", TableEditNameKind.Table, null, default)); // 뷰도 같은 이름 공간
        Assert.False(await _checks.NameExistsAsync(s, _owner, "ORDERS_NEW", TableEditNameKind.Table, null, default));
        Assert.True(await _checks.NameExistsAsync(s, _owner, "AMOUNT", TableEditNameKind.Column, Orders, default));
        Assert.False(await _checks.NameExistsAsync(s, _owner, "AMOUNT", TableEditNameKind.Column, Items, default));
        Assert.True(await _checks.NameExistsAsync(s, _owner, "FK_ITEMS_01", TableEditNameKind.Constraint, null, default));
        Assert.True(await _checks.NameExistsAsync(s, _owner, "IX_ITEMS_01", TableEditNameKind.Index, null, default));
        Assert.False(await _checks.NameExistsAsync(s, _owner, "IX_ITEMS_09", TableEditNameKind.Index, null, default));

        var dependents = await _checks.DependentsAsync(s, Orders, default);
        Assert.Contains(new DbObjectRef(_owner, "V_ORDERS", "VIEW"), dependents);
    }

    [Fact]
    public async Task Cancelled_check_ends_with_operation_cancelled_and_the_session_stays_usable()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _checks.CountRowsAsync(s, Orders, cancelled.Token));
        Assert.Equal(3, await _checks.CountRowsAsync(s, Orders, default));
    }

    [Fact]
    public async Task Cancelling_a_running_check_cancels_the_server_command_and_the_session_stays_usable()
    {
        if (Skip())
        {
            return;
        }

        if (_major < 12)
        {
            // 개발 PC 의 podman 중계는 OOB 브레이크를 버려 11g 에서 실행 중 취소가 끝나지 않는다(Oracle11gFixture 주석, ORANDA_IT_11G_DISABLE_OOB).
            _output.WriteLine("건너뜀: 11g 실행 중 취소는 podman 중계에서 확인할 수 없다");
            return;
        }

        var s = await OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;
        var count = _checks.CountRowsAsync(s, new DbObjectRef(_owner, "V_SLOW", "VIEW"), cts.Token);
        var finished = await Task.WhenAny(count, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.Same(count, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => count);
        _output.WriteLine($"취소까지 {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
        Assert.Equal(3, await _checks.CountRowsAsync(s, Orders, default));
    }

    [Fact]
    public void Dialect_identifier_limit_matches_the_server()
    {
        if (Skip())
        {
            return;
        }

        var server = new ServerInfo(_serverVersion, null, null, null, null, null, false, 1, new ServerCapabilities(false, false, false, false, false));
        Assert.Equal(_major >= 19 ? 128 : 30, new OracleTableEditDialect().MaxIdentifierBytes(server)); // 실측 11: 26ai 38자 됨, 11g 31자 ORA-00972
    }

    private bool Skip()
    {
        if (_enabled)
        {
            return false;
        }

        _output.WriteLine("건너뜀: ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다");
        return true;
    }

    private static ColumnInfo Col(TableInfo t, string name) => t.Columns.Single(c => c.Name == name);

    private static ColumnDraft Text(string type, int length, CharSemantics semantics) =>
        new("k", "C") { DataType = type, Length = length, CharSemantics = semantics };

    private static ColumnDraft Number(int? precision, int? scale) => new("k", "N") { DataType = "NUMBER", Precision = precision, Scale = scale };

    private static OracleMetadataProvider NewProvider() => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<ISession> OpenAsync()
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(_password);
        var session = await manager.OpenAsync(new ConnectionProfile("t-table-edit", _owner, DataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private static OracleConnection Connect(string user, string password) =>
        new(new ConnectionProfile("t-table-edit", user, DataSource).BuildConnectionString(password));

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
