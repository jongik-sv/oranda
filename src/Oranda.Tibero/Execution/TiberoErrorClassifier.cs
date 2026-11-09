using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Execution;

/// <summary>
/// Tibero 오류 번호(<c>TBR-nnnnn</c>, 드라이버 메시지 머리말은 <c>ODP-nnnnn</c>) → <see cref="DbErrorKind"/> (D-046, docs/design/tibero.md 2장).
/// 서버 번호표 출처: Tibero 7.2.6 오류 참조 안내서 원문 + 6.2.15 실측(docs/design/tibero-driver-session.md 6장). 드라이버 번호(9xxxx)는
/// TDP.NET 1.1.35 의 <c>tbcomm.err.TbError</c> 상수 이름에서 가져왔다. 문서·실측에서 찾지 못한 분류(<see cref="DbErrorKind.SnapshotTooOld"/>·
/// <see cref="DbErrorKind.ReadOnly"/>·<see cref="DbErrorKind.AccountLocked"/>·<see cref="DbErrorKind.PasswordExpired"/>·
/// <see cref="DbErrorKind.CompiledWithErrors"/>)는 번호를 추측해 넣지 않고 <see cref="DbErrorKind.Other"/> 로 둔다.
/// <para>
/// 드라이버를 컴파일 때 참조하지 않으므로(D-046-1) 드라이버 예외는 어댑터(<see cref="TiberoDriver.TryReadError"/>)가 알아본다(실측 1장):
/// 서버 오류(<c>Number</c>), 드라이버 쪽 오류(접속 실패·끊긴 연결, <c>ErrorCode</c>, <c>DbException</c> 아님).
/// 이 읽기는 <see cref="TryReadDriverError"/> 한 곳에만 있다.
/// </para>
/// </summary>
[OrandaService(typeof(IDbErrorClassifier), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoErrorClassifier : IDbErrorClassifier
{
    private static readonly Dictionary<int, DbErrorKind> Kinds = new()
    {
        [12040] = DbErrorKind.Cancelled, // ERROR_STMT_CANCEL (실측: Cancel()·CommandTimeout 모두)
        [12032] = DbErrorKind.Deadlock, // ERROR_DEADLOCK
        [12001] = DbErrorKind.Disconnected, // ERROR_SESS_KILLED
        [12002] = DbErrorKind.Disconnected, // ERROR_SESS_NOT_FOUND
        [12094] = DbErrorKind.Disconnected, // ERROR_TMC_SESSION_CLOSE
        [2069] = DbErrorKind.Disconnected, // ERROR_CLI_IO_READ
        [90400] = DbErrorKind.Disconnected, // 드라이버 COMM_ERROR
        [90402] = DbErrorKind.Disconnected, // 드라이버 COMM_PROTOCOL_ERROR
        [90403] = DbErrorKind.Disconnected, // 드라이버 COMM_CONNECTION_BROKEN
        [90405] = DbErrorKind.Disconnected, // 드라이버 COMM_IO_ERROR_READ
        [90406] = DbErrorKind.Disconnected, // 드라이버 COMM_IO_ERROR_WRITE
        [90407] = DbErrorKind.Disconnected, // 드라이버 COMM_IO_ERROR_FLUSH
        [90408] = DbErrorKind.Disconnected, // 드라이버 COMM_IO_ERROR_ENCODING
        [90409] = DbErrorKind.Disconnected, // 드라이버 COMM_IO_ERROR_CLOSE
        [90410] = DbErrorKind.Disconnected, // 드라이버 COMM_IO_CORRUPTED_MSG
        [90603] = DbErrorKind.Disconnected, // 드라이버 MU_ACTION_ON_CLOSED_CONNECTION (실측: 닫힌 연결에 명령)
        [90643] = DbErrorKind.Disconnected, // 드라이버 MU_CONNECTION_CLOSED
        [590719] = DbErrorKind.Disconnected, // 드라이버 INTERNAL_REQUESTED_DISCONNECT
        [590720] = DbErrorKind.Disconnected, // 드라이버 INTERNAL_COMM_ERROR_INTERNAL_SOCKET
        [590729] = DbErrorKind.Disconnected, // 드라이버 INTERNAL_COMM_IO_ERROR_READ_PACKET
        [590730] = DbErrorKind.Disconnected, // 드라이버 INTERNAL_COMM_IO_ERROR_READ_STREAM_BUFFER
        [10007] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_UNIQUE
        [10008] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_PK_NOT_EXIST
        [10009] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_FK_EXIST
        [10005] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_NOT_NULL
        [10006] = DbErrorKind.ConstraintViolation, // ERROR_EXEC_CONSTRAINT_CHECK
        [4005] = DbErrorKind.ConstraintViolation, // ERROR_TD_REFCON_VIOLATION_FK_EXIST
        [4006] = DbErrorKind.ConstraintViolation, // ERROR_TD_REFCON_VIOLATION_PK_NOT_FOUND
        [4008] = DbErrorKind.ConstraintViolation, // ERROR_TD_MI_UNIQ_VIOLATION
        [8033] = DbErrorKind.ObjectNotFound, // ERROR_DML_OBJ_NOT_EXIST (실측: 권한 없는 객체도 이 번호)
        [8053] = DbErrorKind.InsufficientPrivileges, // ERROR_DML_NOT_ENOUGH_PRIVILEGE
        [17004] = DbErrorKind.InsufficientPrivileges, // ALTER SYSTEM 권한 없음 (실측: 일반 계정의 ALTER SYSTEM KILL SESSION, tibero-monitor-catalog.md app.kill_session)
        [12033] = DbErrorKind.ResourceBusy, // ERROR_LOCKFAIL_IN_NOWAIT
        [12034] = DbErrorKind.ResourceBusy, // ERROR_LOCKFAIL_IN_WAIT_TIMEOUT
        [12022] = DbErrorKind.InvalidCredentials, // ERROR_CLIMSG_AUTH_FAILED
    };

    public Dbms Dbms => Dbms.Tibero;

    /// <summary>번호의 부호는 무시한다(드라이버는 <c>-10007</c> 로 준다).</summary>
    public DbErrorKind Classify(int number) => ClassifyNumber(number);

    /// <inheritdoc cref="Classify"/>
    internal static DbErrorKind ClassifyNumber(int number) =>
        number == int.MinValue ? DbErrorKind.Other : Kinds.GetValueOrDefault(Math.Abs(number), DbErrorKind.Other);

    /// <summary>
    /// 이 예외 자체가 드라이버 예외(서버 오류 <c>TbException</c>·드라이버 쪽 <c>TbOdpException</c>)인가. 드라이버 쪽 오류는 <c>DbException</c> 이 아니라서
    /// 복사(<c>CopyErrors</c>)가 이것으로 서버 오류 범위에 넣는다(안쪽 예외는 보지 않는다).
    /// </summary>
    public bool IsOwnDriverException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return TryReadDriverError(exception, out _, out _);
    }

    public DbError? FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Find(exception) is (var number, var message) ? Create(number, message) : null;
    }

    /// <summary>
    /// <see cref="FromException"/> + 문장 위치(<see cref="DbError.Offset"/>·<see cref="DbError.Line"/>·<see cref="DbError.Column"/>).
    /// <paramref name="text"/> 는 서버에 보낸 문장 원문이다. 드라이버 예외가 아니면 null. 실행 서비스(T3~T5)가 쓴다.
    /// </summary>
    internal static DbError? ToDbError(Exception exception, string? text)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Find(exception) is not (var number, var message))
        {
            return null;
        }

        var error = Create(number, message);
        var (offset, line, column) = Locate(message, text);
        return offset is null ? error : error with { Offset = offset, Line = line, Column = column };
    }

    /// <summary>예외 사슬(Inner·Aggregate) 어딘가에 드라이버 예외가 있다.</summary>
    internal static bool IsDriverException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Find(exception) is not null;
    }

    /// <summary>
    /// 드라이버 번호·메시지 → <see cref="DbError"/>(코드·번호·Dbms·Kind). 번호가 0 이면 메시지의 첫 <c>TBR-</c>/<c>ODP-</c> 번호를 쓴다(부호 없이 양수).
    /// 메시지 줄 끝 공백(드라이버가 붙여 보냄)은 지운다.
    /// </summary>
    internal static DbError Create(int number, string? message)
    {
        var text = TrimLineEnds(message);
        if (number == 0 && TryParseCode(text, out var parsed))
        {
            number = parsed;
        }

        var abs = number == int.MinValue ? 0 : Math.Abs(number);
        var code = abs == 0 ? "TBR" : "TBR-" + abs.ToString(CultureInfo.InvariantCulture);
        return new DbError(code, text)
        {
            Number = number,
            Dbms = Dbms.Tibero,
            Kind = ClassifyNumber(number),
        };
    }

    /// <summary>
    /// 메시지 안의 위치(실측 6장) → 문장 원문 기준 (0부터 센 오프셋, 1부터 센 줄·열). 못 찾거나 원문 밖이면 모두 null.
    /// <list type="bullet">
    /// <item><c>at line L, column C of null:</c> + 원문 줄 + <c>^</c> 줄: 열은 첫 <c>^</c> 위치(보고된 C 는 이름 오류에서 1 크다). <c>^</c> 줄이 없으면 C.
    /// <c>of</c> 뒤가 <c>null</c> 이 아니면(저장 객체 안의 위치) 문장 원문 위치가 아니므로 쓰지 않는다.</item>
    /// <item><c>Unhandled exception at  line L.</c> (PSM 실행 오류, 객체 이름 자리가 비었을 때만): 그 줄 첫 글자.</item>
    /// </list>
    /// </summary>
    internal static (int? Offset, int? Line, int? Column) Locate(string? message, string? text)
    {
        if (string.IsNullOrEmpty(message) || text is null)
        {
            return (null, null, null);
        }

        int line;
        int column;
        var at = AtLinePattern().Match(message);
        if (at.Success)
        {
            if (!TryInt(at.Groups["line"].Value, out line) || !TryInt(at.Groups["col"].Value, out column))
            {
                return (null, null, null);
            }

            if (CaretColumn(message, at.Index + at.Length) is { } caret)
            {
                column = caret;
            }
        }
        else
        {
            var unhandled = UnhandledPattern().Match(message);
            if (!unhandled.Success || !TryInt(unhandled.Groups["line"].Value, out line))
            {
                return (null, null, null);
            }

            column = 1;
        }

        // 열은 그 줄 안(끝 바로 뒤 한 칸까지)이어야 한다 — 넘으면 다음 줄을 가리키게 되므로 버린다.
        if (line < 1 || column < 1 || LineStart(text, line) is not { } start || column - 1 > LineLength(text, start))
        {
            return (null, null, null);
        }

        return (start + column - 1, line, column);
    }

    /// <summary>글 안의 <c>TBR-nnnnn</c>·<c>ODP-nnnnn</c>(TDP.NET 메시지 머리말, 실측) 에서 번호를 뽑는다(앞쪽 첫 번째). 없으면 false.</summary>
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

    [GeneratedRegex(@"\bat line (?<line>\d{1,9}), column (?<col>\d{1,9}) of null:", RegexOptions.CultureInvariant)]
    private static partial Regex AtLinePattern();

    [GeneratedRegex(@"\bUnhandled exception at[ \t]+line (?<line>\d{1,9})\b", RegexOptions.CultureInvariant)]
    private static partial Regex UnhandledPattern();

    /// <summary><paramref name="from"/> 뒤 세 줄 안의 <c>^</c> 줄(공백 + <c>^</c> 하나 이상)에서 첫 <c>^</c> 의 열(1부터). 없으면 null.</summary>
    private static int? CaretColumn(string message, int from)
    {
        var lines = message[from..].Split('\n');
        // lines[0] = 「of null:」 뒤 같은 줄의 나머지, [1] = 원문 줄, [2] = ^ 줄.
        for (var i = 1; i < lines.Length && i <= 3; i++)
        {
            var current = lines[i].TrimEnd('\r', ' ', '\t');
            var caret = current.IndexOf('^', StringComparison.Ordinal);
            if (caret >= 0 && current.AsSpan(0, caret).IsWhiteSpace() && current.AsSpan(caret).TrimStart('^').IsEmpty)
            {
                return caret + 1;
            }
        }

        return null;
    }

    /// <summary>1부터 센 <paramref name="line"/> 번째 줄의 시작 오프셋. 원문 줄 수보다 크면 null.</summary>
    private static int? LineStart(string text, int line)
    {
        var start = 0;
        for (var i = 1; i < line; i++)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                return null;
            }

            start = newline + 1;
        }

        return start;
    }

    /// <summary><paramref name="start"/> 에서 시작하는 줄의 길이(줄 바꿈 <c>\n</c>·<c>\r\n</c> 제외).</summary>
    private static int LineLength(string text, int start)
    {
        var newline = text.IndexOf('\n', start);
        var end = newline < 0 ? text.Length : newline;
        if (end > start && text[end - 1] == '\r')
        {
            end--;
        }

        return end - start;
    }

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static string TrimLineEnds(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var lines = message.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd(' ', '\t', '\r');
        }

        return string.Join('\n', lines).TrimEnd('\n');
    }

    /// <summary>
    /// 이 예외 자체(안쪽은 보지 않음)가 드라이버 예외면 true 와 번호(드라이버 값 그대로, 부호 포함)·메시지.
    /// 드라이버 형식은 어댑터만 안다(docs/design/tibero-driver-adapter.md 2.7) — 묶인 어댑터 어느 것이든 알아보면 드라이버 예외다.
    /// </summary>
    private static bool TryReadDriverError(Exception exception, out int number, out string message)
    {
        if (TiberoDriver.TryReadAnyError(exception, out var error))
        {
            number = error.Number;
            message = error.Message;
            return true;
        }

        number = 0;
        message = string.Empty;
        return false;
    }

    /// <summary>예외 사슬(Inner·Aggregate)에서 첫 드라이버 예외의 번호·메시지.</summary>
    private static (int Number, string Message)? Find(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        var guard = 0;
        while (pending.Count > 0 && guard++ < 64)
        {
            var current = pending.Pop();
            if (TryReadDriverError(current, out var number, out var message))
            {
                return (number, message);
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
}
