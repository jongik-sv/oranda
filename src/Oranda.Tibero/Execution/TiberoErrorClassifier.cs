using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.Tibero.Execution;

/// <summary>
/// Tibero 오류 번호(<c>TBR-nnnnn</c>) → <see cref="DbErrorKind"/> (D-046, docs/design/tibero.md 2장).
/// 번호표 출처: Tibero 7.2.6 오류 참조 안내서(error-reference-guide) 원문. 시험 서버는 6.2.15 라서 6 에서 같은 번호인지는
/// T0 실측으로 확인한다. 문서에서 찾지 못한 분류(<see cref="DbErrorKind.SnapshotTooOld"/>·<see cref="DbErrorKind.ReadOnly"/>·
/// <see cref="DbErrorKind.AccountLocked"/>·<see cref="DbErrorKind.PasswordExpired"/>·<see cref="DbErrorKind.CompiledWithErrors"/>)는
/// 번호를 추측해 넣지 않고 <see cref="DbErrorKind.Other"/> 로 둔다.
/// <para>
/// 드라이버(TDP.NET)를 아직 참조하지 않으므로 <see cref="FromException"/> 은 예외 형식 이름(<c>TiberoException</c>)과
/// 공개 속성 <c>Number</c> 로 찾는다. 드라이버를 참조하는 단계(T2)에서 형식 있는 코드로 바꾼다.
/// </para>
/// </summary>
[OrandaService(typeof(IDbErrorClassifier), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoErrorClassifier : IDbErrorClassifier
{
    private const string DriverExceptionName = "TiberoException";

    private static readonly Dictionary<int, DbErrorKind> Kinds = new()
    {
        [12040] = DbErrorKind.Cancelled, // ERROR_STMT_CANCEL
        [12032] = DbErrorKind.Deadlock, // ERROR_DEADLOCK
        [12001] = DbErrorKind.Disconnected, // ERROR_SESS_KILLED
        [12002] = DbErrorKind.Disconnected, // ERROR_SESS_NOT_FOUND
        [12094] = DbErrorKind.Disconnected, // ERROR_TMC_SESSION_CLOSE
        [2069] = DbErrorKind.Disconnected, // ERROR_CLI_IO_READ
        [10007] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_UNIQUE
        [10008] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_PK_NOT_EXIST
        [10009] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_FK_EXIST
        [10005] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_NOT_NULL
        [10006] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_CHECK
        [4005] = DbErrorKind.ConstraintViolation, // ERROR_TD_REFCON_VIOLATION_FK_EXIST
        [4006] = DbErrorKind.ConstraintViolation, // ERROR_TD_REFCON_VIOLATION_PK_NOT_FOUND
        [4008] = DbErrorKind.ConstraintViolation, // ERROR_TD_MI_UNIQ_VIOLATION
        [8033] = DbErrorKind.ObjectNotFound, // ERROR_DML_OBJ_NOT_EXIST
        [8053] = DbErrorKind.InsufficientPrivileges, // ERROR_DML_NOT_ENOUGH_PRIVILEGE
        [12033] = DbErrorKind.ResourceBusy, // ERROR_LOCKFAIL_IN_NOWAIT
        [12034] = DbErrorKind.ResourceBusy, // ERROR_LOCKFAIL_IN_WAIT_TIMEOUT
        [12022] = DbErrorKind.InvalidCredentials, // ERROR_CLIMSG_AUTH_FAILED
    };

    public Dbms Dbms => Dbms.Tibero;

    /// <summary>번호의 부호는 무시한다(드라이버가 <c>-10007</c> 로 줄 수 있다).</summary>
    public DbErrorKind Classify(int number)
    {
        if (number == int.MinValue)
        {
            return DbErrorKind.Other;
        }

        return Kinds.GetValueOrDefault(Math.Abs(number), DbErrorKind.Other);
    }

    public DbError? FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (Find(exception) is not { } driver)
        {
            return null;
        }

        var number = ReadNumber(driver);
        var message = driver.Message;
        if (number == 0 && TryParseCode(message, out var parsed))
        {
            number = parsed; // 메시지에서 뽑은 번호는 부호 없이 양수(드라이버 값이 아니므로)
        }

        var abs = number == int.MinValue ? 0 : Math.Abs(number);
        var code = abs == 0 ? "TBR" : "TBR-" + abs.ToString(CultureInfo.InvariantCulture);
        return new DbError(code, message)
        {
            Number = number,
            Dbms = Dbms.Tibero,
            Kind = Classify(number),
        };
    }

    /// <summary>글 안의 <c>TBR-nnnnn</c>·<c>ODP-nnnnn</c>(TDP.NET 메시지 머리말, 실측 2026-11-09) 에서 번호를 뽑는다(앞쪽 첫 번째). 없으면 false.</summary>
    internal static bool TryParseCode(string? text, out int number)
    {
        number = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var match = CodePattern().Match(text);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:TBR|ODP)-(\d{1,9})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    /// <summary>예외 사슬(Inner·Aggregate)에서 드라이버 예외를 찾는다.</summary>
    private static Exception? Find(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        var guard = 0;
        while (pending.Count > 0 && guard++ < 64)
        {
            var current = pending.Pop();
            if (IsDriverException(current))
            {
                return current;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Reverse())
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } next)
            {
                pending.Push(next);
            }
        }

        return null;
    }

    private static bool IsDriverException(Exception exception)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Name == DriverExceptionName)
            {
                return true;
            }
        }

        return false;
    }

    private static int ReadNumber(Exception exception)
    {
        try
        {
            var property = exception.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance);
            return property?.PropertyType == typeof(int) && property.GetValue(exception) is int value ? value : 0;
        }
        catch (Exception ex) when (ex is AmbiguousMatchException or TargetInvocationException)
        {
            return 0; // 번호를 못 읽어도 원래 오류를 가리지 않는다. 메시지의 TBR-nnnnn 으로 넘어간다.
        }
    }
}
