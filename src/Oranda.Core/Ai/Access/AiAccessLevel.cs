namespace Oranda.Core.Ai.Access;

/// <summary>
/// 접속마다 정하는 AI 허용 수준(연구서 §5, 설계 §5.2). 값 순서가 포함 관계다: 높은 수준은 낮은 수준이 허용하는 도구를 모두 허용한다.
/// 정수로 저장되지 않지만(JSON 은 이름) 비교 연산에 쓰므로 순서를 바꾸지 않는다. 새 값은 맨 끝에 추가한다.
/// </summary>
public enum AiAccessLevel
{
    /// <summary>AI 가 이 접속에 접근할 수 없다.</summary>
    Off,

    /// <summary>객체 이름·열·제약·인덱스 구성만.</summary>
    Metadata,

    /// <summary>메타데이터 + 실행 계획·통계·히스토그램 개수(끝값 제외).</summary>
    Plan,

    /// <summary>계획 + SELECT 실행(결과 행 값은 주지 않고 행 수·열 형만).</summary>
    Query,

    /// <summary>조회 실행 + 결과 행 값과 히스토그램 끝값(민감 컬럼 가림 뒤).</summary>
    QueryRows,
}
