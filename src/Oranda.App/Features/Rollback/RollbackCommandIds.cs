using System;
using System.Collections.Generic;

namespace Oranda.App.Features.Rollback;

/// <summary>
/// 되돌리기 지점 화면의 명령 id(되돌리기 지점 설계 13.4, RB-00 뒤 동결). 변경 일지(RB-42)는 이 id 로 명령을 부르기만 하고
/// 원복 스크립트 탭(RB-43)과 화면 3(RB-32)을 직접 참조하지 않는다.
/// </summary>
public static class RollbackCommandIds
{
    /// <summary>원복 스크립트 탭 열기. 인자: <see cref="RollbackScriptArgs"/>. 구현 RB-43.</summary>
    public const string OpenScript = "oranda.rollback.openScript";

    /// <summary>활성 원복 스크립트 탭의 원복 실행(커밋 안 함). 구현 RB-43.</summary>
    public const string RunScript = "oranda.rollback.runScript";

    /// <summary>이 실행 전과 비교(화면 3). 인자: <see cref="PointCompareArgs"/>. 구현 RB-32.</summary>
    public const string ComparePoint = "oranda.rollback.compare";
}

/// <summary>원복 스크립트 탭을 여는 인자.</summary>
public sealed record RollbackScriptArgs(long GroupId);

/// <summary>화면 3 을 여는 인자. GroupId 가 있으면 그 묶음의 기준·커밋 시점과 표, 없으면 Table·Keys(그리드)와 시점 목록.</summary>
public sealed record PointCompareArgs(Guid ProfileId, long? GroupId, Oranda.Core.Metadata.DbObjectRef? Table,
                                      IReadOnlyList<string>? KeyColumns, IReadOnlyList<object?[]>? Keys, int? DefaultMinutesAgo);
