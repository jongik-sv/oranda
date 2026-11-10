using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy.CrossDbms;

public class CrossTypeMapTests
{
    private static ColumnInfo Col(string type, int? length = null, int? precision = null, int? scale = null, bool identity = false) =>
        new("C", 1, type) { Length = length, Precision = precision, Scale = scale, IsIdentity = identity };

    [Theory]
    [InlineData("VARCHAR2")]
    [InlineData("TIMESTAMP(9)")]
    [InlineData("LONG")]
    [InlineData("XMLTYPE")]
    public void Same_dbms_is_always_exact_and_unchanged(string type)
    {
        foreach (var dbms in new[] { Dbms.Oracle, Dbms.Tibero })
        {
            var column = Col(type, 10);
            var m = CrossTypeMap.Map(dbms, dbms, column);
            Assert.Equal(CrossMapKind.Exact, m.Kind);
            Assert.Same(column, m.Target);
        }
    }

    [Theory]
    [InlineData("NUMBER", "NUMBER")]
    [InlineData("FLOAT", "FLOAT")]
    [InlineData("VARCHAR2", "VARCHAR2")]
    [InlineData("CHAR", "CHAR")]
    [InlineData("NVARCHAR2", "NVARCHAR2")]
    [InlineData("NCHAR", "NCHAR")]
    [InlineData("CLOB", "CLOB")]
    [InlineData("NCLOB", "NCLOB")]
    [InlineData("BLOB", "BLOB")]
    [InlineData("DATE", "DATE")]
    [InlineData("TIMESTAMP(6)", "TIMESTAMP")]
    [InlineData("INTERVAL YEAR(2) TO MONTH", "INTERVAL YEAR TO MONTH")]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", "INTERVAL DAY TO SECOND")]
    public void Oracle_to_tibero_exact(string type, string bind)
    {
        var m = CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, Col(type, 10));
        Assert.Equal(CrossMapKind.Exact, m.Kind);
        Assert.Equal(bind, m.BindType);
        Assert.Equal(type, m.Target!.DataType);
    }

    [Theory]
    [InlineData("BINARY_FLOAT")]
    [InlineData("BINARY_DOUBLE")]
    public void Oracle_binary_float_binds_as_number_but_keeps_ddl_type(string type)
    {
        var m = CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, Col(type));
        Assert.Equal(CrossMapKind.Converted, m.Kind);
        Assert.Equal(CrossNote.BinaryAsNumber, m.Note);
        Assert.Equal("NUMBER", m.BindType);
        Assert.Equal(type, m.Target!.DataType);
    }

    [Fact]
    public void Oracle_extended_varchar_is_exact()
    {
        var m = CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, Col("VARCHAR2", 32767));
        Assert.Equal(CrossMapKind.Exact, m.Kind);
        Assert.Equal(32767, m.Target!.Length);
    }

    [Fact]
    public void Oracle_raw_is_exact_up_to_2000_then_blob()
    {
        Assert.Equal(CrossMapKind.Exact, CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, Col("RAW", 2000)).Kind);

        var big = CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, Col("RAW", 2001));
        Assert.Equal(CrossMapKind.Converted, big.Kind);
        Assert.Equal(CrossNote.RawToBlob, big.Note);
        Assert.Equal("BLOB", big.BindType);
        Assert.Equal("BLOB", big.Target!.DataType);
        Assert.Null(big.Target.Length);
    }

    [Fact]
    public void Oracle_time_zone_converts_region_to_offset()
    {
        var m = CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, Col("TIMESTAMP(6) WITH TIME ZONE"));
        Assert.Equal(CrossMapKind.Converted, m.Kind);
        Assert.Equal(CrossNote.TimeZoneRegionToOffset, m.Note);
        Assert.Equal("TIMESTAMP WITH TIME ZONE", m.BindType);
    }

    [Theory]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", CrossDropReason.LocalTimeZone)]
    [InlineData("LONG", CrossDropReason.LongSource)]
    [InlineData("LONG RAW", CrossDropReason.LongSource)]
    [InlineData("ROWID", CrossDropReason.RowId)]
    [InlineData("UROWID", CrossDropReason.RowId)]
    [InlineData("BFILE", CrossDropReason.Unreadable)]
    [InlineData("XMLTYPE", CrossDropReason.Unreadable)]
    [InlineData("JSON", CrossDropReason.Unreadable)]
    [InlineData("VECTOR", CrossDropReason.Unreadable)]
    [InlineData("BOOLEAN", CrossDropReason.Unreadable)]
    [InlineData("SDO_GEOMETRY", CrossDropReason.Unreadable)]
    [InlineData("MY_OBJECT_TYPE", CrossDropReason.Unreadable)]
    public void Oracle_to_tibero_dropped(string type, CrossDropReason reason)
    {
        var m = CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, Col(type));
        Assert.Equal(CrossMapKind.Dropped, m.Kind);
        Assert.True(m.IsDropped);
        Assert.Equal(reason, m.DropReason);
        Assert.Null(m.Target);
        Assert.Null(m.BindType);
    }

    [Theory]
    [InlineData("NUMBER", 0)]
    [InlineData("FLOAT", 0)]
    [InlineData("BINARY_FLOAT", 0)]
    [InlineData("BINARY_DOUBLE", 0)]
    [InlineData("CHAR", 10)]
    [InlineData("VARCHAR2", 4000)]
    [InlineData("VARCHAR", 100)]
    [InlineData("NVARCHAR2", 2000)]
    [InlineData("NCHAR", 1000)]
    [InlineData("CLOB", 0)]
    [InlineData("NCLOB", 0)]
    [InlineData("BLOB", 0)]
    [InlineData("RAW", 2000)]
    [InlineData("DATE", 0)]
    [InlineData("TIMESTAMP(6)", 0)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", 0)]
    [InlineData("INTERVAL YEAR(2) TO MONTH", 0)]
    [InlineData("INTERVAL DAY(9) TO SECOND(6)", 0)]
    public void Tibero_to_oracle_exact(string type, int length)
    {
        var m = CrossTypeMap.Map(Dbms.Tibero, Dbms.Oracle, Col(type, length == 0 ? null : length));
        Assert.Equal(CrossMapKind.Exact, m.Kind);
        Assert.Equal(CrossNote.None, m.Note);
        Assert.NotNull(m.BindType);
    }

    [Fact]
    public void Tibero_varchar_dictionary_name_normalizes_to_varchar2()
    {
        var m = CrossTypeMap.Map(Dbms.Tibero, Dbms.Oracle, Col("VARCHAR", 100));
        Assert.Equal("VARCHAR2", m.BindType);
        Assert.Equal("NVARCHAR2", CrossTypeMap.Map(Dbms.Tibero, Dbms.Oracle, Col("NVARCHAR", 100)).BindType);
    }

    [Theory]
    [InlineData("VARCHAR2", 4001, "CLOB", CrossNote.LongStringToClob)]
    [InlineData("VARCHAR", 65532, "CLOB", CrossNote.LongStringToClob)]
    [InlineData("NVARCHAR2", 2001, "NCLOB", CrossNote.LongNationalStringToNClob)]
    [InlineData("NCHAR", 1001, "NCLOB", CrossNote.LongNationalStringToNClob)]
    public void Tibero_long_strings_become_lobs(string type, int length, string lob, CrossNote note)
    {
        var m = CrossTypeMap.Map(Dbms.Tibero, Dbms.Oracle, Col(type, length));
        Assert.Equal(CrossMapKind.Converted, m.Kind);
        Assert.Equal(note, m.Note);
        Assert.Equal(lob, m.BindType);
        Assert.Equal(lob, m.Target!.DataType);
        Assert.Null(m.Target.Length);
    }

    [Theory]
    [InlineData("LONG", "CLOB", CrossNote.LongToClob)]
    [InlineData("LONG RAW", "BLOB", CrossNote.LongRawToBlob)]
    public void Tibero_long_becomes_lob(string type, string lob, CrossNote note)
    {
        var m = CrossTypeMap.Map(Dbms.Tibero, Dbms.Oracle, Col(type));
        Assert.Equal(CrossMapKind.Converted, m.Kind);
        Assert.Equal(note, m.Note);
        Assert.Equal(lob, m.BindType);
        Assert.Equal(lob, m.Target!.DataType);
    }

    [Theory]
    [InlineData("TIMESTAMP WITH LOCAL TIME ZONE", CrossDropReason.LocalTimeZone)]
    [InlineData("ROWID", CrossDropReason.RowId)]
    [InlineData("BFILE", CrossDropReason.Unreadable)]
    [InlineData("XMLTYPE", CrossDropReason.Unreadable)]
    [InlineData("TIME", CrossDropReason.Unreadable)]
    [InlineData("GEOMETRY", CrossDropReason.Unreadable)]
    public void Tibero_to_oracle_dropped(string type, CrossDropReason reason)
    {
        var m = CrossTypeMap.Map(Dbms.Tibero, Dbms.Oracle, Col(type));
        Assert.True(m.IsDropped);
        Assert.Equal(reason, m.DropReason);
    }

    [Fact]
    public void Identity_column_becomes_plain_for_tibero_target()
    {
        var m = CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, Col("NUMBER", precision: 10, scale: 0, identity: true));
        Assert.Equal(CrossMapKind.Converted, m.Kind);
        Assert.Equal(CrossNote.IdentityToPlain, m.Note);
        Assert.False(m.Target!.IsIdentity);
        Assert.Equal("NUMBER", m.BindType);
    }

    [Fact]
    public void Identity_does_not_hide_a_type_conversion_note()
    {
        var m = CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, Col("BINARY_DOUBLE", identity: true));
        Assert.Equal(CrossNote.BinaryAsNumber, m.Note);
        Assert.False(m.Target!.IsIdentity);
    }

    [Fact]
    public void Target_drops_domain_and_keeps_name_nullability_default()
    {
        var c = Col("NUMBER", precision: 5) with { Name = "ID", Position = 3, IsNullable = false, DefaultValue = "0", Domain = "D1" };
        var t = CrossTypeMap.Map(Dbms.Oracle, Dbms.Tibero, c).Target!;
        Assert.Null(t.Domain);
        Assert.Equal("ID", t.Name);
        Assert.Equal(3, t.Position);
        Assert.False(t.IsNullable);
        Assert.Equal("0", t.DefaultValue);
        Assert.Equal(5, t.Precision);
    }

    [Theory]
    [InlineData("varchar2(10 char)", "VARCHAR2")]
    [InlineData("  timestamp ( 6 )   with  time zone ", "TIMESTAMP WITH TIME ZONE")]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", "INTERVAL DAY TO SECOND")]
    [InlineData("NVARCHAR", "NVARCHAR2")]
    public void Normalize_strips_precision_and_spaces(string input, string expected) =>
        Assert.Equal(expected, CrossTypeMap.Normalize(input));
}
