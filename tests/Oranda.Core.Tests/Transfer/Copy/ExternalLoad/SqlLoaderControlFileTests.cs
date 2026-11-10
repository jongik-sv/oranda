using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Tests.Transfer.Copy.ExternalLoad;

// 소유: XL1. SQL*Loader 제어 파일 글.
public class SqlLoaderControlFileTests
{
    private static readonly LoaderColumn[] AllTypes =
    [
        new("ID", "NUMBER(10,2)"),
        new("NAME", "VARCHAR2(20)"),
        new("BIG", "VARCHAR2(4000 CHAR)"),
        new("D", "DATE"),
        new("TS", "TIMESTAMP(6)"),
        new("TZ", "TIMESTAMP(6) WITH TIME ZONE"),
        new("YM", "INTERVAL YEAR(2) TO MONTH"),
        new("DS", "INTERVAL DAY(2) TO SECOND(6)"),
        new("R", "RAW(16)"),
        new("BF", "BINARY_FLOAT"),
        new("BD", "BINARY_DOUBLE"),
        new("DOC", "CLOB"),
        new("PIC", "BLOB"),
    ];

    private static LoaderControlSpec Spec(bool direct, IReadOnlyList<LoaderColumn>? columns = null) => new(
        "SCOTT", "Emp", columns ?? AllTypes, @"C:\t\oranda-load\r1\1-EMP\data.dat", @"C:\t\oranda-load\r1\1-EMP\load.bad",
        @"C:\t\oranda-load\r1\1-EMP\load.dsc", 50, direct);

    private static string[] Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void Header_names_options_file_format_and_table()
    {
        var lines = Lines(SqlLoaderControlFile.Build(Spec(direct: true)));

        Assert.Equal("OPTIONS (DIRECT=TRUE, ERRORS=50, COLUMNARRAYROWS=10000, STREAMSIZE=4194304, READSIZE=20971520, MULTITHREADING=TRUE)", lines[0]);
        Assert.Equal("LOAD DATA", lines[1]);
        Assert.Equal("CHARACTERSET AL32UTF8", lines[2]);
        Assert.Equal(@"INFILE 'C:\t\oranda-load\r1\1-EMP\data.dat' ""str X'1E0A'""", lines[3]);
        Assert.Equal(@"BADFILE 'C:\t\oranda-load\r1\1-EMP\load.bad'", lines[4]);
        Assert.Equal(@"DISCARDFILE 'C:\t\oranda-load\r1\1-EMP\load.dsc'", lines[5]);
        Assert.Equal("APPEND", lines[6]);
        Assert.Equal("INTO TABLE \"SCOTT\".\"Emp\"", lines[7]);
        Assert.Equal("REENABLE DISABLED_CONSTRAINTS", lines[8]);
        Assert.Equal("FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '\"'", lines[9]);
        Assert.Equal("TRAILING NULLCOLS", lines[10]);
        Assert.Equal("(", lines[11]);
    }

    [Fact]
    public void Conventional_path_has_no_reenable_clause()
    {
        var text = SqlLoaderControlFile.Build(Spec(direct: false) with { Rows = 5000 });

        Assert.StartsWith("OPTIONS (DIRECT=FALSE, ERRORS=50, ROWS=5000, BINDSIZE=20971520, READSIZE=20971520)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("REENABLE", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_type_gets_its_field_clause()
    {
        var lines = Lines(SqlLoaderControlFile.Build(Spec(direct: true)));
        var fields = lines.SkipWhile(l => l != "(").Skip(1).TakeWhile(l => l != ")").ToArray();

        string[] expected =
        [
            "  \"ID\" CHAR(400),",
            "  \"NAME\" CHAR(80),", // VARCHAR2(20) × 4
            "  \"BIG\" CHAR(16000),",
            "  \"D\" DATE \"YYYY-MM-DD HH24:MI:SS\",",
            "  \"TS\" TIMESTAMP \"YYYY-MM-DD HH24:MI:SS.FF9\",",
            "  \"TZ\" TIMESTAMP WITH TIME ZONE \"YYYY-MM-DD HH24:MI:SS.FF9 TZR\",",
            "  \"YM\" CHAR(100) \"TO_YMINTERVAL(:\\\"YM\\\")\",",
            "  \"DS\" CHAR(100) \"TO_DSINTERVAL(:\\\"DS\\\")\",",
            "  \"R\" CHAR(32) \"HEXTORAW(:\\\"R\\\")\",",
            "  \"BF\" CHAR(100) \"TO_BINARY_FLOAT(:\\\"BF\\\")\",",
            "  \"BD\" CHAR(100) \"TO_BINARY_DOUBLE(:\\\"BD\\\")\",",
            "  \"F$LOB12\" FILLER CHAR(512),",
            "  \"DOC\" LOBFILE(\"F$LOB12\") TERMINATED BY EOF NULLIF \"F$LOB12\"=BLANKS,",
            "  \"F$LOB13\" FILLER CHAR(512),",
            "  \"PIC\" LOBFILE(\"F$LOB13\") TERMINATED BY EOF NULLIF \"F$LOB13\"=BLANKS",
        ];
        Assert.Equal(expected, fields);
        Assert.Equal(")", lines.Last(l => l.Length > 0));
    }

    [Fact]
    public void Filler_name_avoids_a_real_column_with_the_same_name()
    {
        var text = SqlLoaderControlFile.Build(Spec(direct: false, [new("F$LOB2", "NUMBER"), new("DOC", "NCLOB")]));

        Assert.Contains("\"F$LOB2$\" FILLER CHAR(512)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_in_paths_or_names_and_unsupported_types_are_refused()
    {
        Assert.Throws<ArgumentException>(() => SqlLoaderControlFile.Build(Spec(true) with { DataFile = @"C:\o'brien\data.dat" }));
        Assert.Throws<ArgumentException>(() => SqlLoaderControlFile.Build(Spec(true) with { Table = "A\"B" }));
        Assert.Throws<ArgumentException>(() => SqlLoaderControlFile.Build(Spec(true, [new("L", "LONG")])));
    }
}
