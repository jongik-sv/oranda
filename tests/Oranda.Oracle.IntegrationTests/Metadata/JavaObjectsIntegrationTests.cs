using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Metadata;

// 소유: TSK-14-22. Java 저장 객체 목록·상세·소스 DDL 을 실제 Oracle 로 본다.
// 상주 컨테이너를 쓴다: ORANDA_IT_HOST, ORANDA_IT_PORT(기본 1521), ORANDA_IT_SERVICE, ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD.
// 26ai = 1521/FREEPDB1, 11g = ORANDA_IT_PORT=1522 · ORANDA_IT_SERVICE=XE. 환경 변수가 없으면 아무것도 하지 않고 끝난다(「돌렸다」 로 세지 않는다).
// 시험 계정은 실행마다 고유 이름으로 만들고 끝나면 지운다. 서버에 JVM 이 없으면(Free·XE 이미지, CREATE JAVA 가 ORA-29538 등)
// Java 객체를 만들 수 없으므로 「JVM 없음」 경로(목록이 비고 오류가 없다)만 확인하고 나머지는 건너뛴 까닭을 출력한다.
[Trait("Category", "Integration")]
public sealed class JavaObjectsIntegrationTests : IAsyncLifetime
{
    private const string SourceName = "com/example/Hello";

    private readonly ITestOutputHelper _output;
    private readonly string _owner = "JV_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<ISession> _sessions = [];
    private bool _enabled;

    /// <summary>CREATE JAVA 가 실패한 까닭(JVM 없음 등). null 이면 Java 객체를 만들었다.</summary>
    private string? _noJava;

    public JavaObjectsIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

    private static OracleConnection Admin() =>
        new(new ConnectionProfile("admin", Env("ADMIN_USER")!, DataSource).BuildConnectionString(Env("ADMIN_PASSWORD")!));

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

        await using var conn = Admin();
        await conn.OpenAsync();
        await ExecAsync(conn, $"CREATE USER {_owner} IDENTIFIED BY \"Jv{Guid.NewGuid().ToString("N")[..16]}\"");
        await ExecAsync(conn, $"GRANT CREATE SESSION, CREATE PROCEDURE, UNLIMITED TABLESPACE TO {_owner}");
        try
        {
            await ExecAsync(conn,
                $"CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED {_owner}.\"{SourceName}\" AS\n"
                + "package com.example;\n"
                + "public class Hello implements java.io.Serializable {\n"
                + "    private int count;\n"
                + "    public static String hi(String name, int[] times) { return \"hi \" + name; }\n"
                + "}\n");
        }
        catch (OracleException ex)
        {
            _noJava = ex.Message.Split('\n')[0].Trim(); // 예: ORA-29538: Java not installed
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

        await using var conn = Admin();
        await conn.OpenAsync();
        await ExecAsync(conn, $"DROP USER {_owner} CASCADE");
    }

    [Fact]
    public async Task Java_objects_are_listed_with_long_names_or_the_list_is_empty_without_a_jvm()
    {
        if (!_enabled)
        {
            _output.WriteLine("건너뜀: ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다");
            return;
        }

        var provider = NewProvider();
        var s = await OpenAsync();
        var list = await provider.ListJavaObjectsAsync(s, new ObjectQuery { Owner = _owner, Limit = 100 }, default);

        if (_noJava is not null)
        {
            _output.WriteLine("서버에 Java(JVM)가 없다 — 목록·상세 시험 건너뜀: " + _noJava);
            Assert.False(await provider.IsJavaAvailableAsync(s, default));
            Assert.Empty(list);
            Assert.Empty(await provider.ListJavaObjectsAsync(s, new ObjectQuery { Owner = "SYS", Limit = 10 }, default));
            return;
        }

        Assert.True(await provider.IsJavaAvailableAsync(s, default));
        Assert.Contains(list, j => j.Ref.ObjectType == "JAVA SOURCE" && j.LongName == SourceName && j.Status == "VALID");
        Assert.Contains(list, j => j.Ref.ObjectType == "JAVA CLASS" && j.LongName == SourceName);

        var onlyClasses = await provider.ListJavaObjectsAsync(s, new ObjectQuery { Owner = _owner, ObjectTypes = ["JAVA CLASS"], Limit = 100 }, default);
        Assert.All(onlyClasses, j => Assert.Equal("JAVA CLASS", j.Ref.ObjectType));
        Assert.Single(await provider.ListJavaObjectsAsync(s, new ObjectQuery { Owner = "SYS", Limit = 1 }, default)); // 행 수 제한
    }

    [Fact]
    public async Task Java_source_ddl_and_class_detail_come_from_the_dictionary()
    {
        if (!_enabled || _noJava is not null)
        {
            _output.WriteLine("건너뜀: " + (_enabled ? "서버에 Java(JVM)가 없다 — " + _noJava : "환경 변수 없음"));
            return;
        }

        var provider = NewProvider();
        var s = await OpenAsync();
        var list = await provider.ListJavaObjectsAsync(s, new ObjectQuery { Owner = _owner, Limit = 100 }, default);
        var source = list.Single(j => j.Ref.ObjectType == "JAVA SOURCE").Ref;
        var cls = list.Single(j => j.Ref.ObjectType == "JAVA CLASS" && j.LongName == SourceName).Ref;

        var ddl = await provider.GetDdlAsync(s, source, new DdlOptions(), default);
        _output.WriteLine(ddl);
        Assert.StartsWith($"CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"{_owner}\".\"{SourceName}\" AS", ddl, StringComparison.Ordinal);
        Assert.Contains("public class Hello", ddl, StringComparison.Ordinal);
        Assert.EndsWith("\n/", ddl, StringComparison.Ordinal);

        var detail = await provider.GetObjectDetailAsync(s, cls, default);
        foreach (var sec in detail.Sections)
        {
            _output.WriteLine($"{sec.Title}: {string.Join(" | ", sec.Rows.Select(r => string.Join(", ", r)))}");
        }

        Assert.Contains(detail.Sections.Single(x => x.Title == "메서드").Rows, r => (r[2] as string)?.StartsWith("hi(java.lang.String, int[])", StringComparison.Ordinal) == true);
        // 실제 사전: 원시형이면 클래스 열이 '-' — 형식은 base_type 으로(「-」 나 「-[]」 가 아니다).
        var methods = detail.Sections.Single(x => x.Title == "메서드").Rows;
        Assert.Contains(methods, r => (string?)r[1] == "<init>" && (string?)r[3] == "void");
        Assert.Contains(methods, r => (string?)r[1] == "hi" && (string?)r[3] == "java.lang.String");
        Assert.Contains(detail.Sections.Single(x => x.Title == "필드").Rows, r => (string?)r[1] == "count" && (string?)r[2] == "int");
        var info = detail.Sections.Single(x => x.Title == "Java 클래스").Rows;
        Assert.Contains(info, r => (string?)r[0] == "상위 클래스" && (string?)r[1] == "java.lang.Object");
        Assert.Contains(info, r => (string?)r[0] == "바깥 클래스" && r[1] is null);
        Assert.Contains(detail.Sections.Single(x => x.Title == "구현 인터페이스").Rows, r => (string?)r[1] == "java.io.Serializable");
        Assert.Equal(source, detail.SourceObject);

        var sourceDetail = await provider.GetObjectDetailAsync(s, source, default);
        Assert.Contains(sourceDetail.Sections.Single(x => x.Title == "Java 클래스").Rows, r => (string?)r[0] == "com.example.Hello");
    }

    /// <summary>
    /// TSK-14-23: 편집기 문장 분리기가 Java 코드(; { } 문자열·주석)를 한 문장으로 묶은 글을 그대로 보내면 서버가 받아 컴파일하고,
    /// 오류는 ALL_ERRORS(JAVA SOURCE)로 읽히며, 고친 뒤 <c>ALTER JAVA SOURCE … COMPILE</c>·<c>ALTER JAVA CLASS … RESOLVE</c> 가 통한다.
    /// JVM 이 없는 서버(Free·XE 이미지)에서는 건너뛴 까닭만 출력한다.
    /// </summary>
    [Fact]
    public async Task Split_java_source_compiles_reports_errors_and_recompiles()
    {
        if (!_enabled || _noJava is not null)
        {
            _output.WriteLine("건너뜀: " + (_enabled ? "서버에 Java(JVM)가 없다 — " + _noJava : "환경 변수 없음"));
            return;
        }

        const string name = "com/example/Broken";
        string Script(string body) =>
            $"CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED {_owner}.\"{name}\" AS\n"
            + "package com.example;\n"
            + "public class Broken {\n"
            + "    // ; } in a comment\n"
            + "    /* ; { */\n"
            + "    public static String hi() { String s = \"a;b}\"; return " + body + "; }\n"
            + "}\n/\nselect 1 from dual;\n";

        var statements = Oranda.Language.Lexing.StatementSplitter.Split(Script("missing"), Oranda.Core.Language.SplitOptions.Default);
        Assert.Equal(2, statements.Count);

        await using var conn = Admin();
        await conn.OpenAsync();
        // 실제 동작(JVM 서버 확인): 컴파일 오류가 있어도 ODP.NET 은 예외 없이 끝난다(SQL*Plus 는 「Java created with compilation errors」 경고).
        // 그래서 편집기·상세는 성공으로 끝난 CREATE·ALTER 뒤에도 ALL_ERRORS 를 읽는다(SqlCompileErrors.ShouldCapture 의 성공 경로).
        await ExecAsync(conn, statements[0].Text);

        var provider = NewProvider();
        var s = await OpenAsync();
        var source = new DbObjectRef(_owner, name, "JAVA SOURCE");
        var cls = source with { ObjectType = "JAVA CLASS" };
        var errors = await provider.GetCompileErrorsAsync(s, source, default);
        foreach (var e in errors)
        {
            _output.WriteLine($"{e.Line}:{e.Column} {(e.IsWarning ? "W" : "E")} {e.Message}");
        }

        // ALL_ERRORS 의 Java 행은 줄·열 0 + javac 출력 한 줄씩 — 묶어서 오류 하나, 줄은 「이름:5:」 에서(AS 다음 줄이 1줄), 열은 ^ 자리.
        var error = Assert.Single(errors);
        Assert.Equal(5, error.Line);
        Assert.Equal("    public static String hi() { String s = \"a;b}\"; return ".Length + 1, error.Column);
        Assert.StartsWith("cannot find symbol", error.Message, StringComparison.Ordinal);
        Assert.Contains("variable missing", error.Message, StringComparison.Ordinal);
        Assert.False(error.IsWarning);

        // 클래스 쪽은 「다시 컴파일해야 한다」 한 줄, 둘 다 INVALID 로 일괄 컴파일 대상에 든다(소스·클래스).
        Assert.Contains("ORA-29535", Assert.Single(await provider.GetCompileErrorsAsync(s, cls, default)).Message, StringComparison.Ordinal);
        var invalid = await provider.ListJavaObjectsAsync(s, new ObjectQuery { Owner = _owner, ObjectTypes = ["JAVA SOURCE", "JAVA CLASS"], InvalidOnly = true }, default);
        Assert.Contains(invalid, j => j.Ref == source && j.Status == "INVALID");
        Assert.Contains(invalid, j => j.Ref == cls && j.Status == "INVALID");
        Assert.DoesNotContain(invalid, j => j.LongName == SourceName); // VALID 는 빠진다

        // 깨진 채로 ALTER … COMPILE·RESOLVE 도 예외 없이 끝나고 오류는 그대로 남는다(상세의 「다시 컴파일」 은 오류 목록으로 알린다).
        await ExecAsync(conn, Oranda.Oracle.Schema.SchemaStatements.Compile(source)!.Text);
        await ExecAsync(conn, Oranda.Oracle.Schema.SchemaStatements.Compile(cls)!.Text);
        Assert.Equal(5, Assert.Single(await provider.GetCompileErrorsAsync(s, source, default)).Line);

        // 상세의 「컴파일 오류」 구역도 같은 묶음으로 보인다.
        var detail = await provider.GetObjectDetailAsync(s, source, default);
        var row = Assert.Single(detail.Sections.Single(x => x.Title == "컴파일 오류").Rows);
        Assert.Equal(5, row[0]);

        var fixedText = Oranda.Language.Lexing.StatementSplitter.Split(Script("s"), Oranda.Core.Language.SplitOptions.Default)[0].Text;
        await ExecAsync(conn, fixedText);
        await ExecAsync(conn, Oranda.Oracle.Schema.SchemaStatements.Compile(source)!.Text);
        await ExecAsync(conn, Oranda.Oracle.Schema.SchemaStatements.Compile(cls)!.Text);
        Assert.Empty(await provider.GetCompileErrorsAsync(s, source, default));
        Assert.Empty(await provider.GetCompileErrorsAsync(s, cls, default));
    }

    /// <summary>
    /// 긴 이름(128자 초과): 사전 OBJECT_NAME 은 <c>/해시_끝이름</c> 으로 줄고, 목록은 긴 이름을 보이며, 상세·「소스 열기」·오류 읽기가
    /// 줄인 이름과 긴 이름 사이를 오간다(편집기 머리에는 긴 이름이 있다).
    /// </summary>
    [Fact]
    public async Task Long_java_names_are_shortened_in_the_dictionary_and_resolved_everywhere()
    {
        if (!_enabled || _noJava is not null)
        {
            _output.WriteLine("건너뜀: " + (_enabled ? "서버에 Java(JVM)가 없다 — " + _noJava : "환경 변수 없음"));
            return;
        }

        var pkg = string.Join('.', Enumerable.Range(0, 5).Select(i => new string((char)('a' + i), 28)));
        var longName = pkg.Replace('.', '/') + "/LongNamed";
        Assert.True(longName.Length > 128);

        await using var conn = Admin();
        await conn.OpenAsync();
        string Script(string body) =>
            $"CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED {_owner}.\"{longName}\" AS\n"
            + $"package {pkg};\n"
            + "public class LongNamed {\n"
            + "    public static int f(int x) { return " + body + "; }\n"
            + "}\n";
        await ExecAsync(conn, Script("x"));

        var provider = NewProvider();
        var s = await OpenAsync();
        var list = await provider.ListJavaObjectsAsync(s, new ObjectQuery { Owner = _owner, Limit = 100 }, default);
        foreach (var j in list)
        {
            _output.WriteLine($"{j.Ref.ObjectType} {j.Ref.Name} -> {j.LongName} {j.Status}");
        }

        var source = list.Single(j => j.Ref.ObjectType == "JAVA SOURCE" && j.LongName == longName).Ref;
        var cls = list.Single(j => j.Ref.ObjectType == "JAVA CLASS" && j.LongName == longName).Ref;
        Assert.StartsWith("/", source.Name, StringComparison.Ordinal);

        var detail = await provider.GetObjectDetailAsync(s, cls, default);
        Assert.Contains(detail.Sections.Single(x => x.Title == "이름").Rows, r => (string?)r[1] == longName);
        Assert.Equal(source, detail.SourceObject); // 「소스 열기」 는 줄인 이름의 JAVA SOURCE 로
        Assert.Contains(detail.Sections.Single(x => x.Title == "메서드").Rows, r => (string?)r[2] == "f(int)");
        var sourceDetail = await provider.GetObjectDetailAsync(s, source, default);
        Assert.Contains(sourceDetail.Sections.Single(x => x.Title == "Java 클래스").Rows, r => (string?)r[0] == pkg + ".LongNamed");

        var ddl = await provider.GetDdlAsync(s, source, new DdlOptions(), default);
        Assert.StartsWith($"CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"{_owner}\".\"{longName}\" AS", ddl, StringComparison.Ordinal);

        // 깨진 소스: 편집기는 긴 이름으로 묻고(CREATE 머리의 이름), 상세는 사전의 줄인 이름으로 묻는다. 둘 다 같은 오류.
        await ExecAsync(conn, Script("y"));
        Assert.Equal(3, Assert.Single(await provider.GetCompileErrorsAsync(s, source with { Name = longName }, default)).Line);
        Assert.Equal(3, Assert.Single(await provider.GetCompileErrorsAsync(s, source, default)).Line);

        // 깨진 클래스의 메서드 뷰는 이름이 '-' 인 자리 행만 준다 — 표에 넣지 않는다.
        var broken = await provider.GetObjectDetailAsync(s, cls, default);
        Assert.DoesNotContain(broken.Sections.Single(x => x.Title == "메서드").Rows, r => (string?)r[1] == "-");
    }

    private static OracleMetadataProvider NewProvider() => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<ISession> OpenAsync()
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(Env("ADMIN_PASSWORD")!);
        var session = await manager.OpenAsync(new ConnectionProfile("t-java", Env("ADMIN_USER")!, DataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
