using Oranda.Core.Tuning;

namespace Oranda.Core.Tests.Tuning;

// 소유: TSK-06-02(TN-08). 10046 트레이스 → TKPROF 식 요약.
public class TraceParserTests
{
    private static TraceSummary Parse(string text) => TraceParser.Parse(text.Split('\n'));

    // 19c 형식(긴 커서 번호, plh·tim, 12.2+ CLOSE type).
    private const string Trace19 = """
        Trace file /opt/oracle/diag/rdbms/free/FREE/trace/FREE_ora_1234.trc
        *** 2026-10-24T10:00:00.000000+09:00
        =====================
        PARSING IN CURSOR #140234567890 len=33 dep=0 uid=104 oct=3 lid=104 tim=100 hv=1111 ad='7f' sqlid='a1b2c3d4e5f6g'
        select count(*)
          from emp where 1=1
        END OF STMT
        PARSE #140234567890:c=100,e=150,p=0,cr=0,cu=0,mis=1,r=0,dep=0,og=1,plh=0,tim=101
        EXEC #140234567890:c=10,e=20,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,plh=2083865914,tim=102
        WAIT #140234567890: nam='SQL*Net message to client' ela= 3 driver id=1413697536 #bytes=1 p3=0 obj#=-1 tim=103
        WAIT #140234567890: nam='db file sequential read' ela= 250 file#=7 block#=130 blocks=1 obj#=73 tim=104
        FETCH #140234567890:c=200,e=400,p=2,cr=7,cu=1,mis=0,r=1,dep=0,og=1,plh=2083865914,tim=105
        STAT #140234567890 id=1 cnt=1 pid=0 pos=1 obj=0 op='SORT AGGREGATE (cr=7 pr=2 pw=0 str=1 time=390 us)'
        FETCH #140234567890:c=0,e=2,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=0,plh=2083865914,tim=106
        WAIT #140234567890: nam='SQL*Net message from client' ela= 5000 driver id=1413697536 #bytes=1 p3=0 obj#=-1 tim=107
        CLOSE #140234567890:c=0,e=4,dep=0,type=1,tim=108
        XCTEND rec=0, rlbk=0, rd_only=1, tim=109
        """;

    [Fact]
    public void Nineteen_c_trace_sums_calls_and_waits_per_statement()
    {
        var summary = Parse(Trace19);

        var st = Assert.Single(summary.Statements);
        Assert.Equal("select count(*)\n  from emp where 1=1", st.SqlText);
        Assert.Equal("a1b2c3d4e5f6g", st.SqlId);
        Assert.Equal(0, st.Depth);
        Assert.Equal(1, st.ParseCount);
        Assert.Equal(1, st.ExecuteCount);
        Assert.Equal(2, st.FetchCount);
        Assert.Equal(310, st.CpuMicros); // CLOSE 의 c·e 는 넣지 않는다(TKPROF 와 같다).
        Assert.Equal(572, st.ElapsedMicros);
        Assert.Equal(2, st.Disk);
        Assert.Equal(7, st.Query);
        Assert.Equal(1, st.Current);
        Assert.Equal(1, st.Rows);

        Assert.Equal(["SQL*Net message from client", "db file sequential read", "SQL*Net message to client"], summary.Waits.Select(w => w.Event));
        var read = summary.Waits.Single(w => w.Event == "db file sequential read");
        Assert.Equal(1, read.Count);
        Assert.Equal(250, read.ElapsedMicros);
    }

    // 11g 형식(짧은 커서 번호, plh 없음, CLOSE 에 type 만, BINDS 줄).
    private const string Trace11 = """
        *** 2026-10-24 10:00:00.000
        PARSING IN CURSOR #3 len=30 dep=0 uid=84 oct=3 lid=84 tim=1 hv=2222 ad='3a' sqlid='zz11g000000aa'
        select * from dept where d = :b
        END OF STMT
        PARSE #3:c=1000,e=1200,p=1,cr=5,cu=0,mis=1,r=0,dep=0,og=1,tim=2
        BINDS #3:
         Bind#0
          oacdty=02 mxl=22(22) mxlc=00 mal=00 scl=00 pre=00
          value=10
        EXEC #3:c=0,e=50,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,tim=3
        WAIT #3: nam='SQL*Net message to client' ela= 1 driver id=1650815232 #bytes=1 p3=0 obj#=-1 tim=4
        FETCH #3:c=0,e=60,p=0,cr=3,cu=0,mis=0,r=4,dep=0,og=1,tim=5
        STAT #3 id=1 cnt=4 pid=0 pos=1 obj=74 op='TABLE ACCESS FULL DEPT (cr=3 pr=0 pw=0 time=0 us cost=3 size=80 card=4)'
        CLOSE #3:c=0,e=0,dep=0,type=0,tim=6
        """;

    [Fact]
    public void Eleven_g_trace_with_binds_block_is_read_the_same_way()
    {
        var st = Assert.Single(Parse(Trace11).Statements);

        Assert.Equal("select * from dept where d = :b", st.SqlText);
        Assert.Equal("zz11g000000aa", st.SqlId);
        Assert.Equal((1, 1, 1), (st.ParseCount, st.ExecuteCount, st.FetchCount));
        Assert.Equal(1000, st.CpuMicros);
        Assert.Equal(1310, st.ElapsedMicros);
        Assert.Equal(1, st.Disk);
        Assert.Equal(8, st.Query);
        Assert.Equal(4, st.Rows);
    }

    [Fact]
    public void Same_sql_on_reused_and_different_cursor_numbers_is_merged_and_order_kept()
    {
        var text = """
            PARSING IN CURSOR #1 len=8 dep=0 uid=1 oct=3 lid=1 tim=1 hv=10 ad='a' sqlid='aaaaaaaaaaaaa'
            select 1
            END OF STMT
            EXEC #1:c=1,e=1,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,tim=1
            CLOSE #1:c=0,e=0,dep=0,type=0,tim=2
            PARSING IN CURSOR #1 len=8 dep=0 uid=1 oct=3 lid=1 tim=3 hv=20 ad='b' sqlid='bbbbbbbbbbbbb'
            select 2
            END OF STMT
            EXEC #1:c=2,e=2,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,tim=4
            PARSING IN CURSOR #7 len=8 dep=0 uid=1 oct=3 lid=1 tim=5 hv=10 ad='a' sqlid='aaaaaaaaaaaaa'
            select 1
            END OF STMT
            EXEC #7:c=4,e=4,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,tim=6
            EXEC #1:c=8,e=8,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,tim=7
            """;

        var s = Parse(text).Statements;

        Assert.Equal(["aaaaaaaaaaaaa", "bbbbbbbbbbbbb"], s.Select(x => x.SqlId));
        Assert.Equal(2, s[0].ExecuteCount);
        Assert.Equal(5, s[0].CpuMicros);
        Assert.Equal(2, s[1].ExecuteCount); // 커서 #1 은 두 번째 PARSING 뒤로 select 2 다.
        Assert.Equal(10, s[1].CpuMicros);
    }

    [Fact]
    public void Recursive_statements_keep_their_depth_and_are_separate()
    {
        var text = """
            PARSING IN CURSOR #10 len=21 dep=1 uid=0 oct=3 lid=0 tim=1 hv=30 ad='c' sqlid='rrrrrrrrrrrrr'
            select obj# from obj$
            END OF STMT
            EXEC #10:c=5,e=6,p=0,cr=2,cu=0,mis=0,r=0,dep=1,og=4,tim=2
            FETCH #10:c=1,e=1,p=0,cr=3,cu=0,mis=0,r=1,dep=1,og=4,tim=3
            PARSING IN CURSOR #11 len=8 dep=0 uid=1 oct=3 lid=1 tim=4 hv=40 ad='d' sqlid='ttttttttttttt'
            select 3
            END OF STMT
            EXEC #11:c=20,e=30,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,tim=5
            """;

        var s = Parse(text).Statements;

        Assert.Equal(2, s.Count);
        Assert.Equal(1, s[0].Depth);
        Assert.Equal(5, s[0].Query);
        Assert.Equal(0, s[1].Depth);
        Assert.Equal(20, s[1].CpuMicros);
    }

    [Fact]
    public void Truncated_file_keeps_what_was_read()
    {
        // 앞이 잘림(본문 없는 커서 #5), 뒤가 잘림(END OF STMT 없음, 반쯤 쓴 FETCH 줄).
        var text = """
            EXEC #5:c=3,e=4,p=0,cr=1,cu=0,mis=0,r=0,dep=0,og=1,tim=1
            WAIT #5: nam='enq: TX - row lock contention' ela= 900 name|mode=1415053318 usn<<16|slot=1 sequence=2 obj#=7 tim=2
            PARSING IN CURSOR #6 len=40 dep=0 uid=1 oct=3 lid=1 tim=3 hv=50 ad='e' sqlid='ccccccccccccc'
            select a,
            """;

        var summary = Parse(text + "\nFETCH #6:c=1,e=");

        Assert.Equal(2, summary.Statements.Count);
        Assert.Null(summary.Statements[0].SqlId);
        Assert.Contains("#5", summary.Statements[0].SqlText, StringComparison.Ordinal);
        Assert.Equal(1, summary.Statements[0].ExecuteCount);
        Assert.Equal("ccccccccccccc", summary.Statements[1].SqlId);
        Assert.StartsWith("select a,", summary.Statements[1].SqlText, StringComparison.Ordinal);
        Assert.Equal(0, summary.Statements[1].FetchCount); // 본문이 끝나지 않아 FETCH 반 줄은 본문으로 읽혔다.
        Assert.Equal(900, Assert.Single(summary.Waits).ElapsedMicros);
    }

    [Fact]
    public void Call_after_full_length_text_without_end_marker_closes_the_text()
    {
        var text = """
            PARSING IN CURSOR #2 len=8 dep=0 uid=1 oct=3 lid=1 tim=1 hv=60 ad='f' sqlid='ddddddddddddd'
            select 9
            EXEC #2:c=7,e=7,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,tim=2
            """;

        var st = Assert.Single(Parse(text).Statements);

        Assert.Equal("select 9", st.SqlText);
        Assert.Equal(1, st.ExecuteCount);
    }

    [Fact]
    public void Statement_without_sqlid_is_keyed_by_hash_value_and_crlf_is_trimmed()
    {
        var lines = new[]
        {
            "PARSING IN CURSOR #1 len=8 dep=0 uid=1 oct=3 lid=1 tim=1 hv=777 ad='a'\r\n",
            "select 5\r\n",
            "END OF STMT\r\n",
            "EXEC #1:c=1,e=1,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,tim=2\r\n",
            "PARSING IN CURSOR #2 len=8 dep=0 uid=1 oct=3 lid=1 tim=3 hv=777 ad='a'",
            "select 5",
            "END OF STMT",
            "EXEC #2:c=1,e=1,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,tim=4",
        };

        var st = Assert.Single(TraceParser.Parse(lines).Statements);

        Assert.Null(st.SqlId);
        Assert.Equal("select 5", st.SqlText);
        Assert.Equal(2, st.ExecuteCount);
    }

    [Fact]
    public void Empty_and_garbage_input_give_an_empty_summary()
    {
        Assert.Empty(TraceParser.Parse([]).Statements);
        var junk = Parse("hello\nPARSE without cursor\nEXEC #x:c=1\nWAIT #1: nam='x'\n=====");
        Assert.Empty(junk.Statements);
        Assert.Empty(junk.Waits);
        Assert.Throws<ArgumentNullException>(() => TraceParser.Parse(null!));
    }

    [Fact]
    public void Negative_or_bad_numbers_count_as_zero()
    {
        var text = """
            PARSING IN CURSOR #1 len=8 dep=0 uid=1 oct=3 lid=1 tim=1 hv=1 ad='a' sqlid='eeeeeeeeeeeee'
            select 1
            END OF STMT
            FETCH #1:c=abc,e=-5,p=,cr=3,cu=0,mis=0,r=2,dep=0,og=1,tim=2
            """;

        var st = Assert.Single(Parse(text).Statements);

        Assert.Equal(0, st.CpuMicros);
        Assert.Equal(0, st.ElapsedMicros);
        Assert.Equal(3, st.Query);
        Assert.Equal(2, st.Rows);
    }
}
