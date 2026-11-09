namespace Oranda.App.Tests.Integration;

/// <summary>
/// Oracle 통합 시험의 「건너뜀」을 시험 결과에 보이게 한다(xunit 2.5.3 에는 실행 중 Skip 이 없어, 환경이 안 갖춰지면 시험 메서드에 Skip 사유를 정적으로 붙인다).
/// 예전에는 시험 본문이 <c>if (!db.Enabled) return;</c> 으로 빠져나가 아무것도 안 하고 「통과」로 찍혔다. 본문의 그 줄은 그대로 둔다(관문을 거치지 않는 실행 대비).
/// <list type="bullet">
/// <item><see cref="ItDbFactAttribute"/>·<see cref="ItDbTheoryAttribute"/> = <see cref="ItDb"/> 를 쓰는 시험: <c>ORANDA_IT_ADMIN_PASSWORD</c> 가 있을 때만.</item>
/// <item><see cref="SafetyDbFactAttribute"/>·<see cref="SafetyDbTheoryAttribute"/> = <c>SafetyAppDbFixture</c> 를 쓰는 시험: <c>ORANDA_IT_HOST</c>·<c>SERVICE</c>·<c>ADMIN_USER</c>·<c>ADMIN_PASSWORD</c> 가 모두 있을 때만.
/// <c>ORANDA_IT_REQUIRE=1</c> 이면 건너뛰지 않는다(픽스처가 던져 실패한다).</item>
/// </list>
/// 사유 문장에는 없는 변수의 이름만 싣고 값은 싣지 않는다.
/// </summary>
internal static class ItGate
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    /// <summary><see cref="ItDb"/> 용: 건너뛸 사유(없으면 null).</summary>
    public static string? ItDbReason() =>
        string.IsNullOrEmpty(Env("ADMIN_PASSWORD")) ? "ORANDA_IT_ADMIN_PASSWORD 가 없어 건너뜀." : null;

    /// <summary>SafetyAppDbFixture 용(HOST·SERVICE·ADMIN_USER·ADMIN_PASSWORD): 건너뛸 사유(없으면 null). REQUIRE=1 이면 null(픽스처가 실패시킨다).</summary>
    public static string? SafetyDbReason()
    {
        if (Env("REQUIRE") == "1")
        {
            return null;
        }

        string[] required = ["HOST", "SERVICE", "ADMIN_USER", "ADMIN_PASSWORD"];
        var missing = required.Where(n => Env(n) is null).Select(n => "ORANDA_IT_" + n).ToArray();
        return missing.Length == 0 ? null : "DB 접속 환경 변수가 없어 건너뜀: " + string.Join(", ", missing);
    }
}

/// <summary><see cref="ItDb"/> 를 쓰는 통합 시험(<see cref="ItGate"/>).</summary>
public sealed class ItDbFactAttribute : FactAttribute
{
    public ItDbFactAttribute() => Skip = ItGate.ItDbReason();
}

/// <summary><see cref="ItDb"/> 를 쓰는 통합 이론 시험(<see cref="ItGate"/>).</summary>
public sealed class ItDbTheoryAttribute : TheoryAttribute
{
    public ItDbTheoryAttribute() => Skip = ItGate.ItDbReason();
}

/// <summary>SafetyAppDbFixture 를 쓰는 통합 시험(<see cref="ItGate"/>).</summary>
public sealed class SafetyDbFactAttribute : FactAttribute
{
    public SafetyDbFactAttribute() => Skip = ItGate.SafetyDbReason();
}

/// <summary>SafetyAppDbFixture 를 쓰는 통합 이론 시험(<see cref="ItGate"/>).</summary>
public sealed class SafetyDbTheoryAttribute : TheoryAttribute
{
    public SafetyDbTheoryAttribute() => Skip = ItGate.SafetyDbReason();
}
