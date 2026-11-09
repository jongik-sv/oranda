using System.Data;
using System.Runtime.Loader;
using Oranda.Core.Connection;
using Oranda.Tibero.Connection;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Tests.Driver;

/// <summary>
/// 실제 드라이버(TDP.NET Core 1.1.35) 시험(DV6, docs/design/tibero-driver-adapter.md 5장). 드라이버는 리포에 없다:
/// <c>ORANDA_TIBERO_DRIVER</c> = 실제 dll·폴더·nupkg 위치가 있을 때만 돈다. 서버 실측은 <c>ORANDA_IT_TIBERO_*</c> 도 있을 때만(읽기·임시 LOB·PSM 블록만).
/// <c>ORANDA_IT_TIBERO_FRAMEWORK_DRIVER</c> = .NET Framework 판(2.70) dll 위치가 있으면 그 거절도 본다.
/// <c>ORANDA_IT_REQUIRE=1</c> 이면 드라이버 위치가 없을 때 실패한다.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RealDriverIt
{
    private const string DriverVariable = "ORANDA_TIBERO_DRIVER";

    // 실제 드라이버를 새 로드 컨텍스트에 한 번만 올린다(시험 프로세스 안, 기본 컨텍스트는 건드리지 않음).
    private static readonly Lazy<TiberoDriverHost?> Host = new(CreateHost, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string? DriverPath()
    {
        var path = Environment.GetEnvironmentVariable(DriverVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        Assert.False(Environment.GetEnvironmentVariable("ORANDA_IT_REQUIRE") == "1", $"{DriverVariable} 가 없다(ORANDA_IT_REQUIRE=1).");
        return null;
    }

    private static TiberoDriverHost? CreateHost()
    {
        var path = DriverPath();
        return path is null
            ? null
            : new TiberoDriverHost(new AssemblyLoadContext("tb-real-driver-it"), () => path, Path.Combine(Path.GetTempPath(), "oranda-tbdrv-it"));
    }

    [Fact]
    public void Real_driver_passes_the_check_and_binds_every_adapter_member()
    {
        if (Host.Value is not { } host)
        {
            return;
        }

        var check = new TiberoDriverStatus(null, host).Check(DriverPath());
        Assert.Equal(DriverState.Ok, check.State);
        Assert.Equal("1.1.35", check.Version);

        // 핵심: 2장의 멤버가 모두 실제 드라이버에 묶인다(하나라도 없으면 TiberoDriverBindException 에 이름이 다 나온다).
        var driver = host.Require(null);
        Assert.Equal(new Version(1, 1, 35, 0), driver.Version);
        Assert.Equal(TiberoParamKind.Decimal, driver.KindOfProviderType(107));

        using var connection = driver.CreateConnection("Data Source=((INSTANCE=(HOST=localhost)(PORT=1)(DB_NAME=x)));User Id=u;Password=p;Pooling=false");
        using var command = connection.CreateCommand();
        driver.SetBindByName(command);
        var parameter = command.CreateParameter();
        foreach (var kind in Enum.GetValues<TiberoParamKind>())
        {
            driver.SetParameterKind(parameter, kind);
        }

        Assert.NotNull(driver.CreateTimeStamp(new DateTime(2024, 1, 2, 3, 4, 5), 123_456_789));
        Assert.NotNull(driver.CreateTimeStampTz(new DateTime(2024, 1, 2, 3, 4, 5), "+09:00"));
    }

    [Fact]
    public void Framework_build_of_the_driver_is_refused()
    {
        var path = Environment.GetEnvironmentVariable("ORANDA_IT_TIBERO_FRAMEWORK_DRIVER");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var check = TiberoDriverLoader.Inspect(path);

        Assert.Equal(DriverState.FrameworkBuild, check.State);
        Assert.Equal(TiberoDriverLoader.FrameworkBuildReason, check.Reason);
    }

    [Fact]
    public void Each_clob_read_gives_an_independent_locator_that_outlives_the_reader()
    {
        if (Open() is not { } opened)
        {
            return;
        }

        var (driver, connection) = opened;
        using (connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT TO_CLOB('abcdef') AS C FROM DUAL";
            TiberoLob first;
            TiberoLob second;
            using (var reader = command.ExecuteReader())
            {
                Assert.True(reader.Read());
                first = driver.ReadClob(reader, 0)!;
                second = driver.ReadClob(reader, 0)!;
                Assert.Equal("abcdef", ReadAll(first));

                // 실측 1: 같은 칸을 두 번 읽은 로케이터가 위치를 나누지 않는다(같은 드라이버 객체면 여기서 빈 글이 나온다).
                Assert.Equal("abcdef", ReadAll(second));
            }

            // 실측 2: 리더를 닫은 뒤에도(연결은 열림) 로케이터를 읽을 수 있다.
            using (first)
            using (second)
            {
                second.Position = 0;
                Assert.Equal("abcdef", ReadAll(second));
            }

            connection.Close();
        }
    }

    [Fact]
    public void Number_out_over_28_digits_overflows_as_decimal_and_survives_as_varchar2()
    {
        if (Open() is not { } opened)
        {
            return;
        }

        // 38자리(NUMBER 최대 정밀도 — 39자리면 서버가 반올림, win144 #13). 정수부 30자리 > System.Decimal 최대(약 7.9e28).
        const string Big = "123456789012345678901234567890.12345678";
        var (driver, connection) = opened;
        using (connection)
        {
            // 실측 3(win144 #11): 1.1.35 는 Decimal OUT 을 ExecuteNonQuery 안에서 System.Decimal 로 바꾼다
            // (TbNumber.toDecimal → Decimal.Parse) → 28자리를 넘으면 OverflowException, TiberoDecimal 로 받을 기회가 없다.
            using (var command = BigNumberCommand(driver, connection, Big, TiberoParamKind.Decimal, size: null, out _))
            {
                Assert.Throws<OverflowException>(() => command.ExecuteNonQuery());
            }

            // 그래서 NUMBER OUT 은 Varchar2 OUT 으로 받는다(결정40) — 글자를 잃지 않는다.
            using (var command = BigNumberCommand(driver, connection, Big, TiberoParamKind.Varchar2, size: 100, out var parameter))
            {
                command.ExecuteNonQuery();
                var value = driver.ReadOutValue(parameter);
                Assert.False(value.IsNull);
                Assert.Equal(Big, value.Value);
            }

            connection.Close();
        }
    }

    private static System.Data.Common.DbCommand BigNumberCommand(TiberoDriver driver, System.Data.Common.DbConnection connection,
        string literal, TiberoParamKind kind, int? size, out System.Data.Common.DbParameter parameter)
    {
        var command = connection.CreateCommand();
        driver.SetBindByName(command);
        command.CommandText = $"BEGIN :v := {literal}; END;";
        parameter = command.CreateParameter();
        parameter.ParameterName = "v";
        parameter.Direction = ParameterDirection.Output;
        driver.SetParameterKind(parameter, kind);
        if (size is { } s)
        {
            parameter.Size = s;
        }

        command.Parameters.Add(parameter);
        return command;
    }

    private static (TiberoDriver Driver, System.Data.Common.DbConnection Connection)? Open()
    {
        var env = new TiberoItEnv();
        if (Host.Value is not { } host || env.Skip())
        {
            return null;
        }

        var driver = host.Require(null);
        var profile = new ConnectionProfile("it", env.User, $"{env.Host}:{env.Port}/{env.DbName}", Dbms: Dbms.Tibero);
        var connection = driver.CreateConnection(new TiberoConnectionStringFactory().Build(profile, env.Password));
        connection.Open();
        return (driver, connection);
    }

    private static string ReadAll(TiberoLob lob)
    {
        var buffer = new char[64];
        var text = new System.Text.StringBuilder();
        int read;
        while ((read = lob.Read(buffer, 0, buffer.Length)) > 0)
        {
            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }
}
