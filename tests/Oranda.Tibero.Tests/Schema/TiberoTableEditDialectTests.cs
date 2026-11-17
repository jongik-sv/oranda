using System.Text.Json;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Tibero.Schema;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>Tibero 테이블 편집 방언(실측 docs/design/tibero-table-edit-probe.md 표를 입력으로).</summary>
public class TiberoTableEditDialectTests
{
    private static readonly TiberoTableEditDialect Dialect = new();

    private static ColumnDraft Col(string type, int? length = null, CharSemantics? semantics = null, int? precision = null, int? scale = null) =>
        new("k", "C") { DataType = type, Length = length, CharSemantics = semantics, Precision = precision, Scale = scale };

    [Fact]
    public void Basic_facts_follow_the_probe()
    {
        Assert.Equal(Dbms.Tibero, Dialect.Dbms);
        Assert.False(Dialect.SupportsVirtualColumnModify); // 1장 TBR-8081
        Assert.True(Dialect.PurgesOnDrop); // 18장 USE_RECYCLEBIN=NO
        Assert.DoesNotContain("BFILE", Dialect.TypeNames); // 8장 7001
        Assert.DoesNotContain("LONG", Dialect.TypeNames);
        Assert.Contains("NVARCHAR2", Dialect.TypeNames);
        Assert.Contains("TIMESTAMP WITH TIME ZONE", Dialect.TypeNames);
    }

    /// <summary>파티션(실측 11·19): 해시 수 바꾸기·일반 → 파티션은 막고, 범위·목록 정비는 됨. 나누기·합치기 뒤 전역 인덱스 다시 만들기.</summary>
    [Fact]
    public void Partition_operations_follow_the_probe()
    {
        ITableEditDialect d = Dialect;
        Assert.False(d.KeepsGlobalIndexesOnSplitMerge);
        Assert.False(d.SupportsOnlinePartitionConversion(Server(7)));
        Assert.Equal(TiberoTableEditDialect.HashPartitionChangeKey, d.UnsupportedPartitionReasonKey(PartitionOperation.Add, PartitionMethod.Hash, null));
        Assert.Equal(TiberoTableEditDialect.HashPartitionChangeKey, d.UnsupportedPartitionReasonKey(PartitionOperation.Coalesce, PartitionMethod.Hash, null));
        Assert.Equal(TiberoTableEditDialect.ReasonPrefix + "partitionConversion", d.UnsupportedPartitionReasonKey(PartitionOperation.Convert, PartitionMethod.Range, null));
        foreach (var op in new[] { PartitionOperation.Add, PartitionOperation.Drop, PartitionOperation.Rename, PartitionOperation.Split, PartitionOperation.Merge, PartitionOperation.Truncate, PartitionOperation.SetInterval })
        {
            Assert.Null(d.UnsupportedPartitionReasonKey(op, PartitionMethod.Range, null));
            Assert.Null(d.UnsupportedPartitionReasonKey(op, PartitionMethod.List, null));
        }
    }

    [Fact]
    public void Identifier_limit_is_128_bytes_from_tibero_6_and_the_small_one_when_unknown()
    {
        Assert.Equal(128, Dialect.MaxIdentifierBytes(Server(6)));
        Assert.Equal(128, Dialect.MaxIdentifierBytes(Server(7)));
        Assert.Equal(30, Dialect.MaxIdentifierBytes(Server(5)));
        Assert.Equal(30, Dialect.MaxIdentifierBytes(null));
    }

    [Theory]
    [InlineData(TableEditFeature.VirtualColumnModify, "virtualColumnModify")]
    [InlineData(TableEditFeature.IdentityColumn, "identityColumn")]
    [InlineData(TableEditFeature.InvisibleColumn, "invisibleColumn")]
    [InlineData(TableEditFeature.DomainColumn, "domainColumn")]
    [InlineData(TableEditFeature.PartitionConversion, "partitionConversion")]
    public void Unsupported_features_carry_a_reason_key(TableEditFeature feature, string suffix)
    {
        Assert.Equal(TiberoTableEditDialect.ReasonPrefix + suffix, Dialect.UnsupportedReasonKey(feature, Server(6)));
        Assert.Equal(TiberoTableEditDialect.ReasonPrefix + suffix, Dialect.UnsupportedReasonKey(feature, null));
    }

    [Theory]
    [InlineData(TableEditFeature.VirtualColumn)]
    [InlineData(TableEditFeature.RenameColumn)]
    [InlineData(TableEditFeature.RenameIndex)]
    [InlineData(TableEditFeature.ConstraintEnableDisable)]
    [InlineData(TableEditFeature.ForeignKeyOnDeleteSetNull)]
    [InlineData(TableEditFeature.DescendingIndex)]
    [InlineData(TableEditFeature.RenameTable)]
    [InlineData(TableEditFeature.FunctionBasedIndex)]
    [InlineData(TableEditFeature.BitmapIndex)]
    [InlineData(TableEditFeature.TemporaryTable)]
    [InlineData(TableEditFeature.IndexOrganizedTable)]
    [InlineData(TableEditFeature.StorageAttributes)]
    [InlineData(TableEditFeature.LobStorage)]
    [InlineData(TableEditFeature.Partitioning)]
    [InlineData(TableEditFeature.ConvertingTypeChange)]
    public void Measured_features_are_supported(TableEditFeature feature) => Assert.Null(Dialect.UnsupportedReasonKey(feature, Server(6)));

    /// <summary>이유 키(방언 + 파티션 차이의 문제 키)는 한국어·영어 문자열 파일 모두에 있다.</summary>
    [Fact]
    public void Reason_keys_exist_in_both_string_files()
    {
        var partitionKeys = typeof(PartitionDiff).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.True(partitionKeys.Count >= 7, "partition problem keys were not found");
        foreach (var file in new[] { "strings-ko.json", "strings-en.json" })
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Oranda.App", "Resources", file)));
            var missing = TiberoTableEditDialect.ReasonKeys.Concat(partitionKeys).Where(k => !json.RootElement.TryGetProperty(k, out _)).ToList();
            Assert.True(missing.Count == 0, file + " has no " + string.Join(", ", missing));
        }
    }

    [Theory]
    [InlineData("SYS_C0012", true)]
    [InlineData("BIN$abc==$0", true)]
    [InlineData("SAMPLE_CON777800700", true)] // 실측 7장 서버 이름 제약
    [InlineData("SAMPLE_CON1", true)] // 숫자 길이는 상관없다(데이터 복사 판정과 같게)
    [InlineData("_SYS_IDX1", true)]
    [InlineData("SALES_P202401", false)] // 파티션·LOB 이름 꼴은 제약·인덱스 판정에 넣지 않는다
    [InlineData("PK_ORDERS", false)]
    [InlineData("FK_ORDERS_01", false)]
    [InlineData("_CON1", false)]
    public void Server_names_include_tibero_patterns(string name, bool expected) => Assert.Equal(expected, Dialect.IsServerName(name));

    /// <summary>서버 표기(왼쪽)와 사용자가 쓸 표기(오른쪽)가 같은 정규화 값이 된다 — 거짓 차이 없음(실측 7·8장).</summary>
    public static TheoryData<ColumnDraft, ColumnDraft> SameTypes() => new()
    {
        { Col("VARCHAR", 10, CharSemantics.Bytes), Col("VARCHAR2", 10, CharSemantics.Bytes) },
        { Col("VARCHAR", 10, CharSemantics.Characters), Col("varchar2(10 char)") },
        { Col("NVARCHAR", 10, CharSemantics.Characters), Col("NVARCHAR2", 10) },
        { Col("NCHAR", 5, CharSemantics.Characters), Col("NCHAR(5)") },
        { Col("CHAR", 5, CharSemantics.Bytes), Col("CHAR(5 BYTE)") },
        { Col("NUMBER", precision: 38, scale: 0), Col("NUMBER(*,0)") },
        { Col("NUMBER", precision: 38, scale: 0), Col("INTEGER") },
        { Col("NUMBER", precision: 38, scale: 2), Col("NUMBER(*,2)") },
        { Col("NUMBER", precision: 10, scale: 0), Col("NUMBER", precision: 10) },
        { Col("NUMBER", precision: 10, scale: 2), Col("number(10, 2)") },
        { Col("NUMBER"), Col("FLOAT") },
        { Col("NUMBER"), Col("NUMBER(*)") },
        { Col("TIMESTAMP(6)"), Col("TIMESTAMP") },
        { Col("TIMESTAMP(3)"), Col("TIMESTAMP", precision: 3) },
        { Col("TIMESTAMP(6) WITH TIME ZONE"), Col("TIMESTAMP WITH TIME ZONE") },
        { Col("TIMESTAMP(6) WITH LOCAL TIME ZONE"), Col("timestamp with local time zone") },
        { Col("INTERVAL YEAR(2) TO MONTH"), Col("INTERVAL YEAR TO MONTH") },
        { Col("INTERVAL DAY(2) TO SECOND(6)"), Col("INTERVAL DAY TO SECOND") },
        { Col("RAW", 16), Col("RAW(16)") },
        { Col("DATE"), Col("date") },
    };

    [Theory]
    [MemberData(nameof(SameTypes))]
    public void Server_and_user_spellings_of_the_same_type_normalize_equal(ColumnDraft server, ColumnDraft user) =>
        Assert.Equal(Dialect.NormalizeType(server), Dialect.NormalizeType(user));

    /// <summary>진짜 차이는 남는다.</summary>
    public static TheoryData<ColumnDraft, ColumnDraft> DifferentTypes() => new()
    {
        { Col("VARCHAR", 10, CharSemantics.Bytes), Col("VARCHAR", 10, CharSemantics.Characters) }, // BYTE ↔ CHAR(CHAR_USED 로만 구분)
        { Col("VARCHAR", 10, CharSemantics.Bytes), Col("VARCHAR2", 20, CharSemantics.Bytes) },
        { Col("NUMBER", precision: 10, scale: 0), Col("NUMBER", precision: 10, scale: 2) },
        { Col("NUMBER"), Col("NUMBER", precision: 38, scale: 0) }, // NUMBER 와 NUMBER(*,0) 은 다르다(정수만)
        { Col("TIMESTAMP(6)"), Col("TIMESTAMP(3)") },
        { Col("NUMBER", precision: 3), Col("FLOAT(10)") }, // FLOAT(n) 은 사전에서 바뀌어 같게 볼 수 없다
        { Col("CHAR", 5, CharSemantics.Bytes), Col("VARCHAR2", 5, CharSemantics.Bytes) },
        { Col("TIMESTAMP(6)"), Col("TIMESTAMP(6)", precision: 3) }, // 형식 글은 그대로, 정밀도 칸만 고침
    };

    [Theory]
    [MemberData(nameof(DifferentTypes))]
    public void Real_type_changes_stay_different(ColumnDraft server, ColumnDraft user) =>
        Assert.NotEqual(Dialect.NormalizeType(server), Dialect.NormalizeType(user));

    [Theory]
    [InlineData("'abc'", "'abc'")]
    [InlineData("  'abc'  \r\n", "'abc'")] // 서버가 자르지만, 사용자 칸에 남은 공백도 같게
    [InlineData("NULL", null)] // DEFAULT NULL 뒤 사전 표기
    [InlineData("null", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("sysdate", "sysdate")] // 쓴 대로(대소문자를 바꾸지 않는다)
    [InlineData("'It''s'", "'It''s'")]
    public void Defaults_trim_and_drop_a_written_null(string? text, string? expected) => Assert.Equal(expected, Dialect.NormalizeDefault(text));

    [Theory]
    [InlineData("((\"N2\" * 2) + NVL(\"N3\",0))", "(n2*2) + nvl(n3, 0)")] // 서버가 다시 쓴 가상 식(실측 7장)
    [InlineData("(\"A\" * 2)", "A * 2")]
    [InlineData("UPPER(\"B\")", "upper(b)")]
    [InlineData("\"A\" + 1", "(a + 1)")]
    public void Expressions_ignore_server_quotes_case_spaces_and_outer_parentheses(string server, string user) =>
        Assert.Equal(Dialect.NormalizeExpression(server), Dialect.NormalizeExpression(user));

    [Theory]
    [InlineData("\"A\" * 2", "\"a\" * 2")] // 따옴표 이름의 대소문자는 다른 열
    [InlineData("UPPER(\"B\")", "LOWER(\"B\")")]
    [InlineData("UPPER(S) || 'x'", "UPPER(S) || 'X'")] // 리터럴 안은 그대로
    [InlineData("(A + 1) * (B + 1)", "A + 1 * (B + 1)")] // 바깥 괄호처럼 보여도 짝이 아니면 벗기지 않는다
    [InlineData("X NOT IN (1)", "XNOT IN (1)")] // 낱말 사이 공백은 뜻이 있다
    [InlineData("\"A\"\"B\" + 1", "\"AB\" + 1")] // 따옴표 이름 안의 "" 는 글자
    public void Expressions_keep_real_differences(string a, string b) =>
        Assert.NotEqual(Dialect.NormalizeExpression(a), Dialect.NormalizeExpression(b));

    [Fact]
    public void Conditions_ignore_case_spacing_and_line_breaks_outside_literals()
    {
        Assert.Equal(Dialect.NormalizeCondition("s1 like 'A%'\r\n     OR s1 IS NULL"), Dialect.NormalizeCondition("S1 LIKE 'A%' or S1 is null"));
        Assert.Equal(Dialect.NormalizeCondition(" \"N3\" IS NOT NULL "), Dialect.NormalizeCondition("n3 is not null"));
        Assert.NotEqual(Dialect.NormalizeCondition("s0 in ('a','b')"), Dialect.NormalizeCondition("s0 in ('A','B')"));
        Assert.Null(Dialect.NormalizeCondition("   "));
    }

    [Theory]
    [InlineData("VARCHAR2", 10, CharSemantics.Characters, null, null, "VARCHAR2(10 CHAR)")]
    [InlineData("VARCHAR2", null, null, null, null, "VARCHAR2(4000)")] // 길이를 아직 안 쓴 칸
    [InlineData("NVARCHAR2", 5, CharSemantics.Characters, null, null, "NVARCHAR2(5)")] // N 형식에 기준을 쓰면 구문 오류
    [InlineData("RAW", null, null, null, null, "RAW(2000)")]
    [InlineData("NUMBER", null, null, 10, null, "NUMBER(10,0)")]
    [InlineData("NUMBER", null, null, null, 2, "NUMBER(*,2)")]
    [InlineData("number(5,1)", null, null, null, null, "NUMBER(5,1)")]
    [InlineData("DATE", null, null, null, null, "DATE")]
    public void Validation_row_uses_a_castable_type_text(string type, int? length, CharSemantics? semantics, int? precision, int? scale, string expected) =>
        Assert.Equal(expected, TiberoTableEditChecks.TypeText(Col(type, length, semantics, precision, scale)));

    [Theory]
    [InlineData("VARCHAR2", 5, CharSemantics.Bytes, null, null, "LENGTHB(S) > 5")]
    [InlineData("VARCHAR2", 5, CharSemantics.Characters, null, null, "LENGTH(S) > 5")]
    [InlineData("VARCHAR2", 5, null, null, null, "LENGTHB(S) > 5")]
    [InlineData("NCHAR", 2, null, null, null, "LENGTH(S) > 2")]
    [InlineData("RAW", 16, null, null, null, "LENGTH(RAWTOHEX(S)) / 2 > 16")]
    [InlineData("NUMBER", null, null, 5, 2, "ABS(S) >= POWER(10, 3)")]
    [InlineData("NUMBER", null, null, 5, null, "ABS(S) >= POWER(10, 5)")]
    public void Overflow_condition_follows_the_target_definition(string type, int? length, CharSemantics? semantics, int? precision, int? scale, string expected) =>
        Assert.Equal(expected, TiberoTableEditChecks.OverflowCondition("S", Col(type, length, semantics, precision, scale)));

    [Theory]
    [InlineData("DATE")]
    [InlineData("CLOB")]
    [InlineData("NUMBER")]
    [InlineData("VARCHAR2")]
    public void Overflow_condition_is_none_when_nothing_limits_the_value(string type) =>
        Assert.Null(TiberoTableEditChecks.OverflowCondition("S", Col(type)));

    private static ServerInfo Server(int major) =>
        new(major + ".0", "i", "h", null, "D", null, false, 7, new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), Dbms: Dbms.Tibero);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Oranda.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Oranda.sln not found above " + AppContext.BaseDirectory);
    }
}
