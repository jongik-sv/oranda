using System.Reflection;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Oracle.Schema;

namespace Oranda.Oracle.Tests.Schema;

// 계획 Task 3: Oracle 검사 읽기의 서버 없는 부분(형식 표기·판정 규칙). DB 질의는 Oracle IT(TableEditIntegrationTests).
public class OracleTableEditChecksTests
{
    [Fact]
    public void Is_registered_as_the_oracle_table_edit_checks()
    {
        var attribute = typeof(OracleTableEditChecks).GetCustomAttribute<OrandaServiceAttribute>();

        Assert.Equal(typeof(ITableEditChecks), attribute!.ServiceType);
        Assert.Equal(Dbms.Oracle, attribute.ForDbms);
    }

    [Theory]
    [InlineData(null, null, 38, OracleTableEditChecks.FloatScale)] // NUMBER
    [InlineData(null, 0, 38, 0)] // NUMBER(*,0)·INTEGER
    [InlineData(10, null, 10, 0)] // NUMBER(10)
    [InlineData(10, 2, 10, 2)]
    public void Number_shape_matches_the_dictionary_forms(int? precision, int? scale, int expectedPrecision, int expectedScale)
    {
        Assert.Equal((expectedPrecision, expectedScale), OracleTableEditChecks.NumberShape(precision, scale));
    }

    [Theory]
    [InlineData(10, 10, 2, 8, 2, true)] // 정밀도 줄임
    [InlineData(10, 10, 2, 10, 1, true)] // 소수 줄임
    [InlineData(10, 10, 2, 10, 4, true)] // 정수 자리 8 → 6 (실측 ORA-01440)
    [InlineData(5, 5, 0, 5, 1, true)] // (5) → (5,1)
    [InlineData(10, 10, 2, 12, 4, false)] // 정수 자리 그대로
    [InlineData(10, 10, 2, 11, 2, false)]
    [InlineData(null, 38, 0, 38, 2, false)] // (*,0) → (*,2) 는 된다
    [InlineData(null, 38, OracleTableEditChecks.FloatScale, 5, 2, true)] // NUMBER → NUMBER(5,2)
    public void Number_decrease_follows_the_server_rule(int? oldExplicit, int oldP, int oldS, int newP, int newS, bool expected)
    {
        Assert.Equal(expected, OracleTableEditChecks.Decreases(oldExplicit, oldP, oldS, newP, newS));
    }

    [Theory]
    [InlineData("VARCHAR2", CharSemantics.Characters, "LENGTH")]
    [InlineData("VARCHAR2", CharSemantics.Bytes, "LENGTHB")]
    [InlineData("CHAR", null, "LENGTHB")]
    [InlineData("NVARCHAR2", null, "LENGTH")]
    [InlineData("RAW", null, "UTL_RAW.LENGTH")]
    [InlineData("NUMBER", null, null)]
    [InlineData("DATE", null, null)]
    public void Length_function_follows_the_length_semantics(string type, CharSemantics? semantics, string? expected)
    {
        Assert.Equal(expected, OracleTableEditChecks.LengthFunction(type, semantics));
    }

    [Theory]
    [InlineData("VARCHAR2", 20, CharSemantics.Characters, null, null, "VARCHAR2(20 CHAR)")]
    [InlineData("VARCHAR2", null, null, null, null, "VARCHAR2(4000)")]
    [InlineData("NVARCHAR2", 10, CharSemantics.Characters, null, null, "NVARCHAR2(10)")]
    [InlineData("NUMBER", null, null, null, 0, "NUMBER(*,0)")]
    [InlineData("NUMBER", null, null, 10, 2, "NUMBER(10,2)")]
    [InlineData("timestamp(6)", null, null, null, null, "TIMESTAMP(6)")]
    [InlineData("DATE", null, null, null, null, "DATE")]
    public void Cast_type_writes_a_valid_oracle_type(string type, int? length, CharSemantics? semantics, int? precision, int? scale, string expected)
    {
        var c = new ColumnDraft("n1", "A") { DataType = type, Length = length, CharSemantics = semantics, Precision = precision, Scale = scale };

        Assert.Equal(expected, OracleTableEditChecks.CastType(c));
    }

    [Fact]
    public void Empty_row_source_skips_virtual_dropped_and_untyped_columns()
    {
        ColumnDraft[] columns =
        [
            new("n1", "QTY") { DataType = "NUMBER", Precision = 5 },
            new("n2", "TOTAL") { VirtualExpression = "QTY * 2" },
            new("n3", "OLD_COL") { DataType = "DATE", IsDropped = true },
            new("n4", "NOTE") { DataType = "" },
        ];

        Assert.Equal("CAST(NULL AS NUMBER(5)) AS \"QTY\"", OracleTableEditChecks.EmptyRowSource(columns));
        Assert.Equal("NULL AS \"DUMMY\"", OracleTableEditChecks.EmptyRowSource([]));
    }
}
