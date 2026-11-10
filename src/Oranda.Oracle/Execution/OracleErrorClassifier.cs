using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.Oracle.Execution;

/// <summary>
/// Oracle 오류 번호(ORA-nnnnn) → <see cref="DbErrorKind"/> (D-046, O1). 기능 코드는 번호 대신 <see cref="DbError.Kind"/> 를 본다.
/// 번호 표는 이 파일이 정본이다 — 번호를 더하거나 옮길 때는 OraNumberBranchTests·OracleErrorClassifierTests 도 함께 본다.
/// </summary>
[OrandaService(typeof(IDbErrorClassifier), Dbms = Dbms.Oracle)]
internal sealed class OracleErrorClassifier : IDbErrorClassifier
{
    public Dbms Dbms => Dbms.Oracle;

    public DbErrorKind Classify(int number) => ClassifyNumber(number);

    /// <summary>드라이버 예외의 분류. Oracle 어셈블리 안에서 ORA 숫자를 직접 비교하던 자리가 쓴다.</summary>
    internal static DbErrorKind KindOf(OracleException ex) => ClassifyNumber(ex.Number);

    /// <summary>「볼 수 없음」(객체 없음·권한 부족) — <see cref="DbErrorKinds.IsNoAccess(DbErrorKind)"/>.</summary>
    internal static bool IsNoAccess(OracleException ex) => KindOf(ex).IsNoAccess();

    /// <summary>
    /// 연결이 끊김으로 분류하는 번호는 복사 실행기가 쓰던 집합(28·3113·3114·3135·12537·12570·12571·12583)이다.
    /// <see cref="Sessions.OracleSession.IsBrokenError"/> 의 집합(세션을 Broken 으로 돌리는 규칙)은 이와 다르며 이번엔 바꾸지 않는다.
    /// </summary>
    internal static DbErrorKind ClassifyNumber(int number) => number switch
    {
        1013 => DbErrorKind.Cancelled,
        60 => DbErrorKind.Deadlock,
        28 or 3113 or 3114 or 3135 or 12537 or 12570 or 12571 or 12583 => DbErrorKind.Disconnected,
        1 or 1400 or 2290 or 2291 or 2292 => DbErrorKind.ConstraintViolation,
        1555 or 8180 => DbErrorKind.SnapshotTooOld,
        942 or 4043 => DbErrorKind.ObjectNotFound,
        1031 or 1039 => DbErrorKind.InsufficientPrivileges,
        54 or 30006 => DbErrorKind.ResourceBusy,
        1456 or 16000 => DbErrorKind.ReadOnly,
        1017 => DbErrorKind.InvalidCredentials,
        28001 => DbErrorKind.PasswordExpired,
        28000 => DbErrorKind.AccountLocked,
        24344 => DbErrorKind.CompiledWithErrors,
        _ => DbErrorKind.Other,
    };

    public DbError? FromException(Exception exception)
    {
        var ex = Find(exception);
        return ex is null ? null : ToDbError(ex);
    }

    /// <summary>코드·번호·Dbms·Kind 를 채운 <see cref="DbError"/>. 문장 위치는 호출자(실행 서비스)가 채운다.</summary>
    internal static DbError ToDbError(OracleException ex) =>
        new($"ORA-{ex.Number:D5}", ex.Message) { Number = ex.Number, Dbms = Dbms.Oracle, Kind = ClassifyNumber(ex.Number) };

    private static OracleException? Find(Exception? exception)
    {
        switch (exception)
        {
            case null:
                return null;
            case OracleException oe:
                return oe;
            case AggregateException agg:
                foreach (var inner in agg.InnerExceptions)
                {
                    if (Find(inner) is { } found)
                    {
                        return found;
                    }
                }

                return Find(agg.InnerException);
            default:
                return Find(exception.InnerException);
        }
    }
}
