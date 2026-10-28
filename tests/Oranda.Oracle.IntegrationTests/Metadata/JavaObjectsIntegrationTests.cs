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
        Assert.Contains(detail.Sections.Single(x => x.Title == "필드").Rows, r => (string?)r[1] == "count");
        Assert.Contains(detail.Sections.Single(x => x.Title == "구현 인터페이스").Rows, r => (string?)r[1] == "java.io.Serializable");
        Assert.Equal(source, detail.SourceObject);

        var sourceDetail = await provider.GetObjectDetailAsync(s, source, default);
        Assert.Contains(sourceDetail.Sections.Single(x => x.Title == "Java 클래스").Rows, r => (string?)r[0] == "com.example.Hello");
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
