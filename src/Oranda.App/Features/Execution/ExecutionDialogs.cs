using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Execution;

/// <summary>미커밋 상태로 끝낼 때의 선택(SAFE: 종료 시 확인).</summary>
public enum UncommittedChoice
{
    Commit,
    Rollback,
    Cancel,
}

/// <summary>실행 확인 대화상자에 필요한 것. 접속은 머리 띠(접속 이름·사용자·서비스)에 크게 보인다.</summary>
public sealed record ConfirmRequest(SqlStatement Statement, PolicyDecision Decision, ConnectionProfile? Profile);

/// <summary>
/// 실행 중 사용자에게 묻는 대화상자 모음. 화면 구현은 <see cref="AvaloniaExecutionDialogs"/>, 시험은 가짜를 넣는다.
/// 어느 스레드에서 불러도 된다(구현이 UI 스레드로 옮긴다).
/// </summary>
public interface IExecutionDialogs
{
    /// <summary>운영·WHERE 없는 DML·위험 명령 확인. true = 진행.</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct);

    /// <summary>치환 변수 값 입력. <paramref name="last"/> 는 마지막으로 넣은 값. null = 취소.</summary>
    Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(
        IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct);

    Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct);

    Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct);

    /// <summary>전용 세션 탭(D-017)의 미커밋 확인. <paramref name="subject"/> = 탭 이름. 기본은 탭 이름 없이 묻는다.</summary>
    Task<UncommittedChoice> AskUncommittedAsync(string subject, CancellationToken ct) => AskUncommittedAsync(ct);

    /// <summary>DML 미리보기(그리드 반영 전, GRID-05). true = 반영.</summary>
    Task<bool> PreviewDmlAsync(string text, CancellationToken ct);
}

/// <summary>바인드 값 한 개의 입력: 이름·형식·방향·글자(EXEC-03). 「바인드 변수」 목록 줄이 실행 값으로 바꿀 때 쓴다.</summary>
public sealed class BindInput
{
    public BindInput(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public BindDataType Type { get; set; } = BindDataType.Varchar2;

    public BindDirection Direction { get; set; } = BindDirection.In;

    public string Text { get; set; } = string.Empty;

    public bool IsNull { get; set; }

    /// <summary>값을 형식에 맞게 바꾼다. 못 바꾸면 <paramref name="error"/> 에 이유를 담고 null.</summary>
    public BindValue? ToBindValue(out string? error)
    {
        error = null;
        if (Direction == BindDirection.Out || Type == BindDataType.RefCursor || IsNull || (Text.Length == 0 && Type is not (BindDataType.Varchar2 or BindDataType.NVarchar2 or BindDataType.FixedChar or BindDataType.Clob)))
        {
            return new BindValue(Type, null, Direction) { Size = Direction == BindDirection.In ? null : 4000 };
        }

        try
        {
            object? value = Type switch
            {
                BindDataType.Number => decimal.Parse(Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                BindDataType.BinaryDouble => double.Parse(Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                BindDataType.Date or BindDataType.Timestamp => DateTime.Parse(Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None),
                BindDataType.TimestampTz => DateTimeOffset.Parse(Text.Trim(), CultureInfo.InvariantCulture),
                BindDataType.Raw => ParseHex(Text),
                BindDataType.Boolean => ParseBool(Text),
                _ => Text,
            };
            return new BindValue(Type, value, Direction) { Size = Direction == BindDirection.In ? null : 4000 };
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            error = $":{Name} 값 '{Text}' 을(를) {Type} 로 읽을 수 없습니다.";
            return null;
        }
    }

    private static byte[] ParseHex(string text)
    {
        var hex = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        return Convert.FromHexString(hex);
    }

    private static bool ParseBool(string text) => text.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" or "Y" or "T" => true,
        "FALSE" or "0" or "N" or "F" => false,
        _ => throw new FormatException("불리언 값이 아닙니다."),
    };
}

/// <summary>실행 확인 대화상자의 판단 부분(화면 없이 시험한다).</summary>
public sealed class ConfirmDialogModel(ConfirmRequest request)
{
    public ConfirmRequest Request { get; } = request;

    public bool IsProduction => Request.Profile?.Environment == ConnectionEnvironment.Production;

    /// <summary>다시 입력해야 하는 이름. 없으면 null.</summary>
    public string? RetypeText => string.IsNullOrWhiteSpace(Request.Decision.RetypeText) ? null : Request.Decision.RetypeText;

    public string Message => Request.Decision.Message ?? "이 문장을 실행합니다.";

    /// <summary>머리 띠 첫 줄: 접속 이름.</summary>
    public string HeaderTitle => Request.Profile?.Name ?? "접속 없음";

    /// <summary>머리 띠 둘째 줄: 사용자@서비스.</summary>
    public string HeaderDetail => Request.Profile is { } p ? $"{p.User}@{p.DataSource}" : string.Empty;

    /// <summary>환경 칩 글.</summary>
    public string EnvironmentText => Request.Profile?.Environment switch
    {
        ConnectionEnvironment.Production => "운영",
        ConnectionEnvironment.Test => "테스트",
        ConnectionEnvironment.Development => "개발",
        _ => string.Empty,
    };

    /// <summary>실행 단추를 켤 수 있는지. 이름 재입력이 필요하면 같은 이름(대소문자 무시)을 쳐야 한다.</summary>
    public bool CanAccept(string typed) =>
        RetypeText is not { } expected || string.Equals(typed?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
}
