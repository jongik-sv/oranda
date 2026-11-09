namespace Oranda.Tibero.Tests;

/// <summary>
/// Tibero 통합 시험의 접속 정보(docs/design/tibero.md 4장). 공개 Tibero 컨테이너가 없어 환경 변수가 있을 때만 서버 시험을 돈다.
/// 환경 변수: ORANDA_IT_TIBERO_HOST, ORANDA_IT_TIBERO_PORT, ORANDA_IT_TIBERO_DB_NAME, ORANDA_IT_TIBERO_USER, ORANDA_IT_TIBERO_PASSWORD(모두 필수).
/// 하나라도 없으면 <see cref="Enabled"/> 가 거짓이고 시험은 아무것도 하지 않는다(<c>ItDb</c> 와 같은 규칙).
/// <c>ORANDA_IT_REQUIRE=1</c> 이면 없을 때 <see cref="Skip"/> 가 던져 시험이 실패한다. 값은 코드·로그·메시지에 싣지 않는다(없는 변수의 이름만 알린다).
/// </summary>
public sealed class TiberoItEnv
{
    private const string Prefix = "ORANDA_IT_TIBERO_";

    private static readonly string[] Required = ["HOST", "PORT", "DB_NAME", "USER", "PASSWORD"];

    private readonly Dictionary<string, string?> _values;

    public TiberoItEnv()
        : this(name => Environment.GetEnvironmentVariable(name))
    {
    }

    /// <summary>환경 읽기를 바꿔 끼운다(시험용).</summary>
    public TiberoItEnv(Func<string, string?> read)
    {
        _values = Required.ToDictionary(k => k, k => read(Prefix + k), StringComparer.Ordinal);
        Missing = [.. Required.Where(k => string.IsNullOrEmpty(_values[k])).Select(k => Prefix + k)];
        RequireMode = read("ORANDA_IT_REQUIRE") == "1";
    }

    /// <summary>필수 환경 변수가 모두 있다.</summary>
    public bool Enabled => Missing.Count == 0;

    /// <summary>비어 있는 필수 환경 변수 이름(값은 싣지 않는다).</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary><c>ORANDA_IT_REQUIRE=1</c>.</summary>
    public bool RequireMode { get; }

    public string Host => _values["HOST"] ?? string.Empty;

    public string Port => _values["PORT"] ?? string.Empty;

    public string DbName => _values["DB_NAME"] ?? string.Empty;

    public string User => _values["USER"] ?? string.Empty;

    public string Password => _values["PASSWORD"] ?? string.Empty;

    /// <summary>
    /// 서버가 없으면 true(시험은 아무것도 하지 않고 끝낸다). 서버가 없고 <c>ORANDA_IT_REQUIRE=1</c> 이면 던진다.
    /// 사용: <c>if (env.Skip()) return;</c>
    /// </summary>
    public bool Skip()
    {
        if (Enabled)
        {
            return false;
        }

        if (RequireMode)
        {
            throw new InvalidOperationException("Tibero 시험 환경 변수가 없다: " + string.Join(", ", Missing));
        }

        return true;
    }
}
