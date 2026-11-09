using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Tests;

/// <summary>
/// Tibero 통합 시험의 「건너뜀」을 시험 결과에 보이게 하는 관문(xunit 2.5.3 에는 실행 중 Skip 이 없어서, 환경이 안 갖춰지면
/// 시험 메서드에 <c>Skip</c> 사유를 정적으로 붙인다 — <c>EndToEndFact</c> 와 같은 방식).
/// 예전에는 시험 본문이 <c>if (…) return;</c> 으로 빠져나가 아무것도 안 하고 「통과」로 찍혔다.
/// <list type="bullet">
/// <item><see cref="TiberoItFactAttribute"/> = 서버(<c>ORANDA_IT_TIBERO_*</c> 5개)와 드라이버 위치(<c>ORANDA_TIBERO_DRIVER</c>)가 있을 때만.</item>
/// <item><see cref="TiberoWriteFactAttribute"/> = 위에 더해 <c>ORANDA_IT_TIBERO_WRITE=1</c> (쓰기·DDL).</item>
/// <item><see cref="TiberoKillFactAttribute"/> = 쓰기에 더해 <c>ORANDA_IT_TIBERO_KILL=1</c> + <c>KILL_USER</c> + <c>KILL_PASSWORD</c> (권한 계정 세션 Kill).</item>
/// </list>
/// <c>ORANDA_IT_REQUIRE=1</c> 이면 서버·드라이버가 없을 때 건너뛰지 않고 본문이 던져 실패하게 둔다(<see cref="TiberoItEnv.Skip"/>).
/// 사유 문장에는 없는 변수의 이름만 싣고 값은 싣지 않는다. 본문의 <c>if (it is null) return;</c> 은 그대로 둔다(관문을 거치지 않는 실행 대비).
/// </summary>
internal static class TiberoItGate
{
    private const string Prefix = "ORANDA_IT_TIBERO_";

    /// <summary>건너뛸 사유(없으면 null = 돈다).</summary>
    public static string? Reason(bool write, bool kill, bool driverOnly = false)
    {
        var env = new TiberoItEnv();
        if (!env.RequireMode)
        {
            if (!driverOnly && !env.Enabled)
            {
                return "서버 접속 환경 변수가 없어 건너뜀: " + string.Join(", ", env.Missing);
            }

            if (TiberoDriverHost.Shared.SelectPath(null) is null)
            {
                return "ORANDA_TIBERO_DRIVER(드라이버 위치)가 없어 건너뜀.";
            }
        }

        if (write && Environment.GetEnvironmentVariable(Prefix + "WRITE") != "1")
        {
            return "쓰기 시험은 ORANDA_IT_TIBERO_WRITE=1 일 때만 돈다.";
        }

        if (kill && !KillConfigured())
        {
            return "권한 계정 Kill 시험은 ORANDA_IT_TIBERO_KILL=1 과 KILL_USER·KILL_PASSWORD 가 모두 있을 때만 돈다.";
        }

        return null;
    }

    /// <summary>Kill 시험 환경(<c>KILL=1</c>, <c>KILL_USER</c>, <c>KILL_PASSWORD</c>)이 모두 있다.</summary>
    public static bool KillConfigured() =>
        Environment.GetEnvironmentVariable(Prefix + "KILL") == "1"
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Prefix + "KILL_USER"))
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Prefix + "KILL_PASSWORD"));
}

/// <summary>서버·드라이버가 있을 때만 도는 읽기 통합 시험(<see cref="TiberoItGate"/>).</summary>
public sealed class TiberoItFactAttribute : FactAttribute
{
    public TiberoItFactAttribute() => Skip = TiberoItGate.Reason(write: false, kill: false);
}

/// <summary>서버·드라이버·<c>ORANDA_IT_TIBERO_WRITE=1</c> 이 있을 때만 도는 쓰기 통합 시험(<see cref="TiberoItGate"/>).</summary>
public sealed class TiberoWriteFactAttribute : FactAttribute
{
    public TiberoWriteFactAttribute() => Skip = TiberoItGate.Reason(write: true, kill: false);
}

/// <summary>쓰기 조건에 더해 Kill 권한 계정 환경이 있을 때만 도는 통합 시험(<see cref="TiberoItGate"/>).</summary>
public sealed class TiberoKillFactAttribute : FactAttribute
{
    public TiberoKillFactAttribute() => Skip = TiberoItGate.Reason(write: true, kill: true);
}

/// <summary>서버·드라이버가 있을 때만 도는 읽기 통합 이론 시험(<see cref="TiberoItGate"/>).</summary>
public sealed class TiberoItTheoryAttribute : TheoryAttribute
{
    public TiberoItTheoryAttribute() => Skip = TiberoItGate.Reason(write: false, kill: false);
}

/// <summary>드라이버 위치(<c>ORANDA_TIBERO_DRIVER</c>)만 있으면 도는 시험(서버 불필요, <see cref="TiberoItGate"/>).</summary>
public sealed class TiberoDriverFactAttribute : FactAttribute
{
    public TiberoDriverFactAttribute() => Skip = TiberoItGate.Reason(write: false, kill: false, driverOnly: true);
}

/// <summary>.NET Framework 판 드라이버 위치(<c>ORANDA_IT_TIBERO_FRAMEWORK_DRIVER</c>)가 있을 때만 도는 시험.</summary>
public sealed class TiberoFrameworkDriverFactAttribute : FactAttribute
{
    public TiberoFrameworkDriverFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORANDA_IT_TIBERO_FRAMEWORK_DRIVER")))
        {
            Skip = "ORANDA_IT_TIBERO_FRAMEWORK_DRIVER(.NET Framework 판 드라이버 위치)가 없어 건너뜀.";
        }
    }
}
