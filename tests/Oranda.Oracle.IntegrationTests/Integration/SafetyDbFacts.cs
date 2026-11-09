namespace Oranda.Oracle.IntegrationTests.Integration;

/// <summary>
/// <c>SafetyDbFixture</c> 를 쓰는 통합 시험의 「건너뜀」을 시험 결과에 보이게 한다(xunit 2.5.3 에는 실행 중 Skip 이 없어, 환경이 안 갖춰지면 시험 메서드에
/// Skip 사유를 정적으로 붙인다). 예전에는 본문이 <c>if (!_db.Enabled) return;</c> 으로 빠져나가 아무것도 안 하고 「통과」로 찍혔다(본문의 그 줄은 그대로 둔다).
/// <c>ORANDA_IT_HOST</c>·<c>SERVICE</c>·<c>ADMIN_USER</c>·<c>ADMIN_PASSWORD</c> 가 모두 있을 때만 돈다. <c>ORANDA_IT_REQUIRE=1</c> 이면 건너뛰지 않는다(픽스처가 던져 실패한다).
/// 사유 문장에는 없는 변수의 이름만 싣고 값은 싣지 않는다.
/// </summary>
internal static class SafetyDbGate
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    public static string? Reason()
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

/// <summary><c>SafetyDbFixture</c> 를 쓰는 통합 시험(<see cref="SafetyDbGate"/>).</summary>
public sealed class SafetyDbFactAttribute : FactAttribute
{
    public SafetyDbFactAttribute() => Skip = SafetyDbGate.Reason();
}

/// <summary><c>SafetyDbFixture</c> 를 쓰는 통합 이론 시험(<see cref="SafetyDbGate"/>).</summary>
public sealed class SafetyDbTheoryAttribute : TheoryAttribute
{
    public SafetyDbTheoryAttribute() => Skip = SafetyDbGate.Reason();
}
