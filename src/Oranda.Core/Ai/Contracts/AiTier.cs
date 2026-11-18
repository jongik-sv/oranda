namespace Oranda.Core.Ai.Contracts;

/// <summary>
/// 요청 등급(설계 §1.2). 명령이 등급을 정하고, 라우터가 등급에 맞는 대상(주 모델·가벼운 일 모델)으로 보낸다.
/// 가벼운 일 모델(<c>AiSettings.Light</c>)이 비어 있으면 두 등급 모두 주 모델로 간다.
/// </summary>
public enum AiTier
{
    /// <summary>주 모델. 도구 루프(튜닝·벤치마크)와 일반 대화.</summary>
    Main,

    /// <summary>가벼운 일 모델. 도구를 쓰지 않는 한 번짜리 요청.</summary>
    Light,
}

/// <summary>명령별 등급(설계 §1.4). 명령이 요청을 만들 때 <see cref="AiRequest.Tier"/> 에 이 값을 넣는다. 새 명령은 여기에 줄을 더한다.</summary>
public static class AiCommandTiers
{
    /// <summary>이 문장 튜닝: 벤치마크·통계를 여러 번 오가는 도구 루프.</summary>
    public const AiTier Tune = AiTier.Main;

    /// <summary>실행 계획 해설: A-Rows·자식 커서를 엮어 읽어야 한다.</summary>
    public const AiTier ExplainPlan = AiTier.Main;

    /// <summary>SQL 설명: 문장과 객체 정의만 있으면 된다.</summary>
    public const AiTier ExplainSql = AiTier.Light;

    /// <summary>오류 고치기(SQL): 오류문·위치·문장. 입력이 크면 라우터가 주 모델로 올린다.</summary>
    public const AiTier FixError = AiTier.Light;

    /// <summary>일반 대화: 무엇을 물을지 모른다.</summary>
    public const AiTier General = AiTier.Main;

    /// <summary>락·세션 상황 풀이(§3.2): 앱이 사슬을 모아 한 번에 보낸다.</summary>
    public const AiTier LockExplain = AiTier.Light;

    /// <summary>PL/SQL 오류 고치기(§3.3): 오류 목록과 오류 주변 소스. 크면 라우터가 주 모델로 올린다.</summary>
    public const AiTier PlsqlFixError = AiTier.Light;

    /// <summary>변경 일지 기간 요약(§4.2).</summary>
    public const AiTier JournalSummary = AiTier.Light;

    /// <summary>플래시백 두 버전 차이 요약(§4.2).</summary>
    public const AiTier FlashbackDiffSummary = AiTier.Light;

    /// <summary>원복 스크립트 점검(§4.2): 잘못 짚으면 비용이 크다.</summary>
    public const AiTier RollbackCheck = AiTier.Main;
}
