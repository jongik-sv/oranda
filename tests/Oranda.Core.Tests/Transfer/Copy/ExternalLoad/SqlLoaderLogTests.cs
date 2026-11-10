using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Tests.Transfer.Copy.ExternalLoad;

// 소유: XL1. SQL*Loader 로그 읽기(19c 형식 표본, 23ai 도 같은 줄을 쓴다).
public class SqlLoaderLogTests
{
    private const string Header = """
        SQL*Loader: Release 19.0.0.0.0 - Production on Tue Nov 10 21:14:03 2026
        Version 19.3.0.0.0

        Copyright (c) 1982, 2019, Oracle and/or its affiliates.  All rights reserved.

        Control File:   C:\Temp\oranda-load\r1\1-EMP\load.ctl
        Character Set AL32UTF8 specified for all input.

        Data File:      C:\Temp\oranda-load\r1\1-EMP\data.dat
          File processing option string: "str X'1E0A'"
          Bad File:     C:\Temp\oranda-load\r1\1-EMP\load.bad
          Discard File: C:\Temp\oranda-load\r1\1-EMP\load.dsc
         (Allow all discards)

        Number to load: ALL
        Number to skip: 0
        Errors allowed: 50
        Continuation:    none specified
        Path used:      Conventional

        Table "SCOTT"."EMP", loaded from every logical record.
        Insert option in effect for this table: APPEND
        TRAILING NULLCOLS option in effect

           Column Name                  Position   Len  Term Encl Datatype
        ------------------------------ ---------- ----- ---- ---- ---------------------
        "ID"                                FIRST   400     ,  O(") CHARACTER
        "NAME"                               NEXT  4000     ,  O(") CHARACTER
        "HIRED"                              NEXT     *     ,  O(") DATE YYYY-MM-DD HH24:MI:SS

        """;

    private const string Rejections = """
        Record 3: Rejected - Error on table "SCOTT"."EMP", column "SAL".
        ORA-01722: invalid number

        Record 7: Rejected - Error on table "SCOTT"."EMP".
        ORA-00001: unique constraint (SCOTT.PK_EMP) violated

        Record 9: Rejected - Error on table "SCOTT"."EMP", column "NAME".
        ORA-12899: value too large for column "SCOTT"."EMP"."NAME" (actual: 30, maximum: 20)

        Record 12: Rejected - Error on table "SCOTT"."EMP", column "NOTE".
        Field in data file exceeds maximum length

        """;

    private const string Summary = """
        Table "SCOTT"."EMP":
          96 Rows successfully loaded.
          4 Rows not loaded due to data errors.
          0 Rows not loaded because all WHEN clauses were failed.
          0 Rows not loaded because all fields were null.


        Space allocated for bind array:                 255936 bytes(64 rows)
        Read   buffer bytes: 1048576

        Total logical records skipped:          0
        Total logical records read:           100
        Total logical records rejected:         4
        Total logical records discarded:        0

        Run began on Tue Nov 10 21:14:03 2026
        Run ended on Tue Nov 10 21:14:04 2026

        Elapsed time was:     00:00:00.52
        CPU time was:         00:00:00.08
        """;

    [Fact]
    public void Clean_load_reads_counts()
    {
        var log = SqlLoaderLog.Parse(Header + Summary.Replace("96 Rows", "100 Rows", StringComparison.Ordinal).Replace("4 Rows not loaded due", "0 Rows not loaded due", StringComparison.Ordinal)
            .Replace("rejected:         4", "rejected:         0", StringComparison.Ordinal));

        Assert.Equal(100, log.Loaded);
        Assert.Equal(0, log.Rejected);
        Assert.Equal(100, log.Read);
        Assert.Empty(log.Rejects);
        Assert.Empty(log.Fatal);
        Assert.False(log.ErrorLimitHit);
        Assert.True(log.Clean);
    }

    [Fact]
    public void Rejected_records_carry_the_record_number_column_and_error()
    {
        var log = SqlLoaderLog.Parse((Header + Rejections + Summary).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(96, log.Loaded);
        Assert.Equal(4, log.Rejected);
        Assert.False(log.Clean);
        Assert.Equal([3L, 7L, 9L, 12L], log.Rejects.Select(r => r.Record).ToArray());
        Assert.Equal("열 SAL: ORA-01722: invalid number", log.Rejects[0].Error);
        Assert.Equal("ORA-00001: unique constraint (SCOTT.PK_EMP) violated", log.Rejects[1].Error);
        Assert.StartsWith("열 NAME: ORA-12899", log.Rejects[2].Error, StringComparison.Ordinal);
        Assert.Equal("열 NOTE: Field in data file exceeds maximum length", log.Rejects[3].Error);
        Assert.Empty(log.Fatal);
    }

    [Fact]
    public void Single_row_wording_and_discards_are_counted()
    {
        var log = SqlLoaderLog.Parse("""
            Table "SCOTT"."EMP":
              1 Row successfully loaded.
              0 Rows not loaded due to data errors.
              2 Rows not loaded because all WHEN clauses were failed.
              1 Row not loaded because all fields were null.
            """);

        Assert.Equal(1, log.Loaded);
        Assert.Equal(2, log.Discarded);
        Assert.Equal(1, log.AllNull);
    }

    [Fact]
    public void Error_limit_is_detected()
    {
        var log = SqlLoaderLog.Parse(Header + Rejections + """
            MAXIMUM ERROR COUNT EXCEEDED - Above statistics reflect partial run.

            """ + Summary);

        Assert.True(log.ErrorLimitHit);
    }

    [Fact]
    public void Login_failure_is_fatal_with_its_ora_line()
    {
        var log = SqlLoaderLog.Parse("""
            SQL*Loader: Release 19.0.0.0.0 - Production on Tue Nov 10 21:14:03 2026
            Version 19.3.0.0.0

            Copyright (c) 1982, 2019, Oracle and/or its affiliates.  All rights reserved.

            SQL*Loader-128: unable to begin a session
            ORA-01017: invalid username/password; logon denied
            """);

        Assert.Null(log.Loaded);
        Assert.Equal(["SQL*Loader-128: unable to begin a session", "ORA-01017: invalid username/password; logon denied"], log.Fatal.ToArray());
        Assert.False(log.Clean);
    }

    [Fact]
    public void Error_lines_after_a_blank_line_or_tns_lines_still_count_as_the_cause()
    {
        var log = SqlLoaderLog.Parse("""
            SQL*Loader-128: unable to begin a session

            ORA-12514: Cannot connect to database. Service FREEPDB1 is not registered with the listener
            TNS-12564: TNS:connection refused
            """.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(["SQL*Loader-128: unable to begin a session", "ORA-12514: Cannot connect to database. Service FREEPDB1 is not registered with the listener", "TNS-12564: TNS:connection refused"],
                     log.Fatal.ToArray());
        Assert.Empty(log.Rejects);
    }

    [Fact]
    public void Run_summary_keeps_only_the_speed_lines()
    {
        var summary = CopyRunner.ToolSummary(Header + Summary.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal("Path used: Conventional · Space allocated for bind array: 255936 bytes(64 rows) · Read buffer bytes: 1048576 · Elapsed time was: 00:00:00.52 · CPU time was: 00:00:00.08",
                     summary);
        Assert.Equal(string.Empty, CopyRunner.ToolSummary(null));
    }

    [Fact]
    public void File_errors_are_fatal()
    {
        var log = SqlLoaderLog.Parse("""
            SQL*Loader-500: Unable to open file (C:\Temp\x\data.dat)
            SQL*Loader-553: file not found
            SQL*Loader-509: System error: The system cannot find the file specified.
            SQL*Loader-2026: the load was aborted because SQL Loader cannot continue.
            """);

        Assert.Equal(4, log.Fatal.Count);
        Assert.Empty(log.UnusableIndexes);
    }

    [Fact]
    public void Direct_path_unusable_indexes_are_reported()
    {
        var log = SqlLoaderLog.Parse(Header.Replace("Path used:      Conventional", "Path used:      Direct", StringComparison.Ordinal) + """
            The following index(es) on table "SCOTT"."EMP" were processed:
            index SCOTT.PK_EMP was made unusable due to:
            ORA-01452: cannot CREATE UNIQUE INDEX; duplicate keys found
            index SCOTT.EMP_NAME_IX loaded successfully with 100 keys

            """ + Summary.Replace("96 Rows", "100 Rows", StringComparison.Ordinal).Replace("4 Rows not loaded due", "0 Rows not loaded due", StringComparison.Ordinal));

        Assert.Equal(
            ["index SCOTT.PK_EMP was made unusable due to: ORA-01452: cannot CREATE UNIQUE INDEX; duplicate keys found"],
            log.UnusableIndexes.ToArray());
        Assert.Empty(log.Fatal);
        Assert.False(log.Clean);
    }

    [Fact]
    public void Index_unusable_tool_messages_are_reported_as_unusable_and_fatal()
    {
        var log = SqlLoaderLog.Parse("SQL*Loader-2026: the load was aborted because SQL Loader cannot continue.\nSQL*Loader-937: Index SCOTT.PK_EMP is in an unusable state\n");

        Assert.Single(log.UnusableIndexes);
        Assert.Equal(2, log.Fatal.Count);
    }

    [Fact]
    public void Reject_list_is_capped_but_counts_are_not()
    {
        var blocks = string.Concat(Enumerable.Range(1, SqlLoaderLog.MaxRejects + 5)
            .Select(i => $"Record {i}: Rejected - Error on table \"S\".\"T\".\nORA-01400: cannot insert NULL into (\"S\".\"T\".\"ID\")\n\n"));
        var log = SqlLoaderLog.Parse(blocks + $"  0 Rows successfully loaded.\n  {SqlLoaderLog.MaxRejects + 5} Rows not loaded due to data errors.\n");

        Assert.Equal(SqlLoaderLog.MaxRejects, log.Rejects.Count);
        Assert.Equal(SqlLoaderLog.MaxRejects + 5, log.Rejected);
    }

    [Theory]
    [InlineData(0, SqlLoaderExit.Success)]
    [InlineData(1, SqlLoaderExit.Failure)]
    [InlineData(2, SqlLoaderExit.Warning)]
    [InlineData(3, SqlLoaderExit.Fatal)]
    [InlineData(-1, SqlLoaderExit.Unknown)]
    public void Exit_codes_are_classified(int code, SqlLoaderExit expected)
    {
        Assert.Equal(expected, SqlLoaderLog.ClassifyExit(code));
    }

    [Fact]
    public void Empty_text_is_empty_result()
    {
        Assert.Same(LoaderLogResult.Empty, SqlLoaderLog.Parse(null));
        Assert.Same(LoaderLogResult.Empty, SqlLoaderLog.Parse(string.Empty));
    }
}
