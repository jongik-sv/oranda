using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.Core.Tests.Schema.TableEdit;

// 소유: te-l1(T16a). 새 테이블 작업 저장의 편집 모델 JSON 왕복(설계 18.7, TE-43).
public sealed class TableDraftJsonTests
{
    /// <summary>모든 줄 종류(열·제약 4종·인덱스 4종)와 2차 칸(신원·숨김·도메인·식 인덱스·GTT·규칙 이름)을 채운 새 테이블.</summary>
    private static TableDraft Full() => new("SAMPLE", "ORDER_ITEMS")
    {
        IsNewTable = true,
        Comment = "주문 줄",
        Kind = TableKind.Temporary,
        OnCommitPreserveRows = true,
        Tablespace = "USERS",
        RenameRuleNames = true,
        Storage = new TableStorage
        {
            PctFree = 20, InitTrans = 4, Compression = TableCompression.Advanced, Logging = false, Parallel = 8,
            InitialExtent = "64K", NextExtent = "1M",
        },
        RebuildIndexes = ["IX_ORDER_ITEMS_01", "PK_ORDER_ITEMS"],
        Columns =
        [
            new ColumnDraft("n1", "ITEM_ID")
            {
                DataType = "NUMBER", Precision = 10, Scale = 0, IsNullable = false,
                Identity = new IdentityInfo(IdentityGeneration.ByDefaultOnNull) { StartWith = 100, IncrementBy = 5 },
            },
            new ColumnDraft("n2", "ORDER_ID") { DataType = "NUMBER", Precision = 10, Scale = 0, IsNullable = false, Comment = "주문" },
            new ColumnDraft("n3", "NAME") { DataType = "VARCHAR2", Length = 100, CharSemantics = CharSemantics.Characters, DefaultValue = "'-'" },
            new ColumnDraft("n8", "DOC")
            {
                DataType = "CLOB",
                Lob = new LobStorage { Kind = LobStorageKind.SecureFile, Tablespace = "LOBS", Compression = LobCompression.Medium, Deduplicate = true, InRow = false },
            },
            new ColumnDraft("n4", "CODE") { DataType = "CHAR", Length = 2, CharSemantics = CharSemantics.Bytes, IsInvisible = true, Domain = "SAMPLE.CODE_D" },
            new ColumnDraft("n5", "TOTAL") { VirtualExpression = "\"QTY\"*2" },
            new ColumnDraft("n6", "OLD") { DataType = "DATE", IsDropped = true },
            new ColumnDraft("n7", "UNKNOWN_ID") { DataType = "NUMBER", IsIdentityUnknown = true, OriginalPosition = 7 },
        ],
        Constraints =
        [
            new ConstraintDraft("k1", "PK_ORDER_ITEMS", ConstraintKind.PrimaryKey) { ColumnKeys = ["n1"], IsAutoName = true },
            new ConstraintDraft("k2", "UK_ORDER_ITEMS_01", ConstraintKind.Unique) { ColumnKeys = ["n2", "n3"], IsEnabled = false },
            new ConstraintDraft("k3", "FK_ORDER_ITEMS_01", ConstraintKind.ForeignKey)
            {
                ColumnKeys = ["n2"],
                ReferencedTable = new DbObjectRef("SAMPLE", "ORDERS", "TABLE"),
                ReferencedConstraint = "PK_ORDERS",
                ReferencedColumns = ["ORDER_ID"],
                DeleteRule = DeleteRule.Cascade,
            },
            new ConstraintDraft("k4", "CK_ORDER_ITEMS_01", ConstraintKind.Check) { ColumnKeys = ["n4"], Condition = "code in ('A','B')", IsDropped = true },
        ],
        Indexes =
        [
            new IndexDraft("i1", "IX_ORDER_ITEMS_01") { Columns = [new IndexColumnDraft("n2"), new IndexColumnDraft("n3", true)], Tablespace = "IDX", IsAutoName = true },
            new IndexDraft("i2", "IX_ORDER_ITEMS_02") { Kind = IndexKind.FunctionBased, Columns = [new IndexColumnDraft(null) { Expression = "UPPER(\"NAME\")" }] },
            new IndexDraft("i3", "IX_ORDER_ITEMS_03") { Kind = IndexKind.Bitmap, Columns = [new IndexColumnDraft("n4")] },
            new IndexDraft("i4", "PK_ORDER_ITEMS") { IsUnique = true, ConstraintName = "PK_ORDER_ITEMS", Kind = IndexKind.Other, IsDropped = true },
        ],
    };

    [Fact]
    public void Round_trip_keeps_every_row_kind_and_phase2_field()
    {
        var draft = Full();
        var json = TableDraftJson.Serialize(draft);
        var back = TableDraftJson.TryDeserialize(json);

        Assert.NotNull(back);
        Assert.Equal(json, TableDraftJson.Serialize(back)); // 다시 써도 같은 글 = 모든 칸이 돌아왔다
        Assert.True(back.IsNewTable);
        Assert.Equal(TableKind.Temporary, back.Kind);
        Assert.True(back.OnCommitPreserveRows);
        Assert.True(back.RenameRuleNames);

        var id = back.Column("n1")!;
        Assert.Equal(IdentityGeneration.ByDefaultOnNull, id.Identity!.Generation);
        Assert.Equal(100, id.Identity.StartWith);
        Assert.Equal(5, id.Identity.IncrementBy);
        Assert.False(id.IsNullable);
        Assert.True(back.Column("n3")!.IsNullable);
        Assert.Equal(CharSemantics.Characters, back.Column("n3")!.CharSemantics);
        Assert.True(back.Column("n4")!.IsInvisible);
        Assert.Equal("SAMPLE.CODE_D", back.Column("n4")!.Domain);
        Assert.True(back.Column("n5")!.IsVirtual);
        Assert.True(back.Column("n6")!.IsDropped);
        Assert.True(back.Column("n7")!.IsIdentityColumn);
        Assert.Equal(7, back.Column("n7")!.OriginalPosition);

        var fk = back.Constraint("k3")!;
        Assert.Equal(new DbObjectRef("SAMPLE", "ORDERS", "TABLE"), fk.ReferencedTable);
        Assert.Equal(["ORDER_ID"], fk.ReferencedColumns);
        Assert.Equal(DeleteRule.Cascade, fk.DeleteRule);
        Assert.False(back.Constraint("k2")!.IsEnabled);
        Assert.True(back.Constraint("k1")!.IsAutoName);

        Assert.Equal("UPPER(\"NAME\")", back.Index("i2")!.Columns[0].Expression);
        Assert.Null(back.Index("i2")!.Columns[0].ColumnKey);
        Assert.True(back.Index("i1")!.Columns[1].Descending);
        Assert.Equal(IndexKind.Bitmap, back.Index("i3")!.Kind);
        Assert.Equal("PK_ORDER_ITEMS", back.Index("i4")!.ConstraintName);

        // 저장 페이지(설계 18.2): 테이블 저장 속성·LOB 저장 절·남은 REBUILD.
        Assert.Equal(draft.Storage, back.Storage); // 목록 없는 record = 값 비교
        Assert.Equal(TableCompression.Advanced, back.Storage.Compression);
        Assert.False(back.Storage.Logging);
        Assert.Equal(draft.Column("n8")!.Lob, back.Column("n8")!.Lob);
        Assert.Null(back.Column("n3")!.Lob);
        Assert.Equal(["IX_ORDER_ITEMS_01", "PK_ORDER_ITEMS"], back.RebuildIndexes);
    }

    [Fact]
    public void Round_trip_keeps_empty_values()
    {
        // 막 연 새 테이블 탭: 이름 비어 있음, 줄 없음, 빈 주석.
        var draft = new TableDraft("SAMPLE", string.Empty)
        {
            IsNewTable = true,
            Comment = string.Empty,
            Columns = [new ColumnDraft("n1", string.Empty) { DataType = string.Empty, DefaultValue = string.Empty }],
        };

        var back = TableDraftJson.TryDeserialize(TableDraftJson.Serialize(draft));

        Assert.NotNull(back);
        Assert.Equal(string.Empty, back.Name);
        Assert.Equal(string.Empty, back.Comment);
        Assert.Empty(back.Constraints);
        Assert.Empty(back.Indexes);
        var c = Assert.Single(back.Columns);
        Assert.Equal(string.Empty, c.Name);
        Assert.Equal(string.Empty, c.DataType);
        Assert.Equal(string.Empty, c.DefaultValue);
        Assert.Null(c.Length);
        Assert.Null(c.Identity);
        Assert.True(c.IsNullable); // 기본값이 참인 칸
        Assert.Equal(new TableStorage(), back.Storage); // 모든 칸 null = 「바꾸지 않음」
        Assert.Empty(back.RebuildIndexes);
    }

    [Fact]
    public void Serialize_writes_enums_as_names_and_skips_computed_properties()
    {
        var json = TableDraftJson.Serialize(Full());

        Assert.Contains("\"kind\":\"Temporary\"", json, StringComparison.Ordinal);
        Assert.Contains("\"generation\":\"ByDefaultOnNull\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isVirtual", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isIdentityColumn", json, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"kind\":\"NoSuchKind\"}")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"kind\":7}")] // 숫자 열거형 = 정의 안 된 값이 들어올 수 있음
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"kind\":1}")]
    [InlineData("{\"name\":\"T\"}")] // 소유자 빠짐
    [InlineData("{\"owner\":null,\"name\":\"T\"}")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"columns\":null}")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"columns\":[{\"name\":\"A\"}]}")] // 줄 키 빠짐
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"columns\":[null]}")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"constraints\":[{\"rowKey\":\"k1\",\"name\":\"PK\",\"kind\":\"PrimaryKey\",\"columnKeys\":[null]}]}")]
    [InlineData("null")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"constraints\":[{\"rowKey\":\"k1\",\"name\":\"UK\",\"columnKeys\":[]}]}")] // 제약 종류 빠짐(PK 로 읽히면 안 됨)
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"columns\":[{\"rowKey\":\"n1\",\"name\":\"A\",\"identity\":{\"startWith\":1}}]}")] // 신원 방식 빠짐
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"constraints\":[{\"rowKey\":\"k1\",\"name\":\"FK\",\"kind\":\"ForeignKey\",\"referencedTable\":{\"owner\":\"SAMPLE\",\"name\":\"ORDERS\"}}]}")]
    public void TryDeserialize_returns_null_for_broken_text(string? json)
        => Assert.Null(TableDraftJson.TryDeserialize(json));

    [Fact]
    public void TryDeserialize_ignores_unknown_properties()
    {
        // 다음 버전이 칸을 더해도 옛 앱이 읽을 수 있다.
        var back = TableDraftJson.TryDeserialize("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"futureField\":1,\"columns\":[{\"rowKey\":\"n1\",\"name\":\"A\",\"x\":true}]}");

        Assert.NotNull(back);
        Assert.Equal("A", Assert.Single(back.Columns).Name);
    }

    [Fact]
    public void TryDeserialize_turns_missing_lists_into_empty_lists()
    {
        // 소스 생성 직렬화기는 빠진 init 목록에 null 을 넣는다 — 읽은 뒤 빈 목록으로 맞춘다.
        var back = TableDraftJson.TryDeserialize(
            "{\"owner\":\"SAMPLE\",\"name\":\"T\",\"constraints\":[{\"rowKey\":\"k1\",\"name\":\"CK\",\"kind\":\"Check\"}],\"indexes\":[{\"rowKey\":\"i1\",\"name\":\"IX\"}]}");

        Assert.NotNull(back);
        Assert.Empty(back.Columns);
        Assert.Empty(back.Constraints[0].ColumnKeys);
        Assert.Empty(back.Constraints[0].ReferencedColumns);
        Assert.Empty(back.Indexes[0].Columns);
        Assert.NotNull(back.Storage); // 저장 칸이 없던 옛 저장분
        Assert.Equal(new TableStorage(), back.Storage);
        Assert.Empty(back.RebuildIndexes);
    }

    [Theory]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"storage\":{\"compression\":2}}")] // 숫자 열거형
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"storage\":{\"compression\":\"Hcc\"}}")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"columns\":[{\"rowKey\":\"n1\",\"name\":\"A\",\"lob\":{\"kind\":0}}]}")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"columns\":[{\"rowKey\":\"n1\",\"name\":\"A\",\"lob\":{\"compression\":\"Ultra\"}}]}")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"storage\":null}")]
    [InlineData("{\"owner\":\"SAMPLE\",\"name\":\"T\",\"rebuildIndexes\":[null]}")]
    public void TryDeserialize_rejects_broken_storage(string json)
        => Assert.Null(TableDraftJson.TryDeserialize(json));

    [Fact]
    public void Every_enum_in_the_draft_model_has_a_name_only_converter()
    {
        // TableDraft 에 열거형 칸을 더하면 Converters 목록에도 더해야 숫자 값을 거부한다 — 빠뜨리면 여기서 걸린다.
        var enums = new HashSet<Type>();
        CollectEnums(typeof(TableDraft), [], enums);
        var converted = TableDraftJsonContext.Default.Options.Converters
            .Select(c => c.GetType())
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(TableDraftEnumConverter<>))
            .Select(t => t.GetGenericArguments()[0])
            .ToHashSet();

        Assert.Contains(typeof(TableCompression), enums); // 훑기가 중첩 형식(TableStorage·LobStorage)까지 간다
        Assert.Contains(typeof(LobCompression), enums);
        Assert.Empty(enums.Except(converted).Select(t => t.Name));
    }

    /// <summary>쓰기 가능한 공개 속성을 따라 Oranda 형식 안의 열거형을 모은다(목록 원소·Nullable 포함).</summary>
    private static void CollectEnums(Type type, HashSet<Type> seen, HashSet<Type> enums)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
        {
            enums.Add(type);
            return;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            CollectEnums(type.GetGenericArguments()[0], seen, enums);
            return;
        }

        // IReadOnlyList 말고 다른 모음(배열·List·사전 …)이 생기면 그 원소를 훑지 못하니 여기서 알린다(훑기를 넓힌다).
        Assert.False(
            type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type),
            $"모르는 모음 형식 {type} — CollectEnums 가 원소를 훑게 고친다");

        if (type.Namespace?.StartsWith("Oranda.", StringComparison.Ordinal) != true || !seen.Add(type))
        {
            return;
        }

        foreach (var p in type.GetProperties().Where(p => p.CanWrite))
        {
            CollectEnums(p.PropertyType, seen, enums);
        }
    }
}
