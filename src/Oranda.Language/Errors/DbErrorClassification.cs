using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.Language.Errors;

/// <summary>
/// 예외·오류 글 → <see cref="DbErrorKind"/> (D-046, A4). 앱·언어 서비스 코드가 <c>ORA-nnnnn</c> 문자열이나 번호를 직접 비교하던 자리를 이것으로 바꾼다.
/// 판정은 DBMS 공급자의 <see cref="IDbErrorClassifier"/> 가 한다: 드라이버 예외면 <see cref="IDbErrorClassifier.FromException"/>,
/// 아니면 메시지 안의 <c>ORA-nnnnn</c>·<c>TBR-nnnnn</c> 번호를 <see cref="IDbErrorClassifier.Classify"/> 로 옮긴다
/// (드라이버 예외가 <see cref="InvalidOperationException"/> 등으로 감싸져 글만 남은 경우).
/// <para>
/// 이 표는 정적이다: 호출 지점(정적 판정 함수)이 세션을 받지 않기 때문이다. <see cref="Selector"/> 를 앱 조립이 연결한다(tb-core C7).
/// <b>연결되기 전(null)</b>에는 지금까지의 Oracle 번호 비교와 똑같은 한정된 표(<see cref="LegacyOracle"/>)로 판정해 Oracle 동작이 바뀌지 않는다.
/// 연결된 뒤에는 이 표를 쓰지 않는다.
/// </para>
/// </summary>
public static partial class DbErrorClassification
{
    private static volatile IDbmsServiceSelector? _selector;

    /// <summary>DBMS 별 구현 고르기. 앱 조립이 설정한다. null 이면 Oracle 한정 안전판(<see cref="LegacyOracle"/>)으로 판정한다.</summary>
    public static IDbmsServiceSelector? Selector
    {
        get => _selector;
        set => _selector = value;
    }

    /// <summary>예외 사슬(Inner·Aggregate) 전체에서 나온 분류. 분류할 수 없으면(<see cref="DbErrorKind.Other"/>) 넣지 않는다.</summary>
    public static IReadOnlySet<DbErrorKind> KindsOf(Exception? exception) =>
        exception is null ? new HashSet<DbErrorKind>() : Collect(exception, Chain(exception).Select(e => e.Message ?? string.Empty).ToList());

    /// <summary>
    /// 예외 <b>바깥 메시지만</b>의 분류(사슬의 안쪽 예외는 보지 않는다). 드라이버 예외 자체의 분류는 포함한다.
    /// 예전에 <c>ex.Message.Contains("ORA-…")</c> 로 바깥 글만 보던 자리가 쓴다.
    /// </summary>
    public static IReadOnlySet<DbErrorKind> TopKindsOf(Exception? exception) =>
        exception is null ? new HashSet<DbErrorKind>() : Collect(exception, [exception.Message ?? string.Empty]);

    /// <summary>오류 글(서버 메시지·실패 사유 문자열)에 들어 있는 오류 번호의 분류. 예외 대신 글만 남은 자리가 쓴다.</summary>
    public static IReadOnlySet<DbErrorKind> KindsOf(string? message) =>
        string.IsNullOrEmpty(message) ? new HashSet<DbErrorKind>() : Collect(null, [message]);

    /// <summary>
    /// 바깥 메시지에서 <b>맨 앞</b> 오류 번호의 분류(예: 「ORA-00604 … ORA-01031」은 604 의 분류). 번호가 없으면 <see cref="DbErrorKind.Other"/>.
    /// 드라이버 예외면 드라이버가 준 번호의 분류가 먼저다.
    /// </summary>
    public static DbErrorKind LeadingKindOf(Exception? exception)
    {
        if (exception is null)
        {
            return DbErrorKind.Other;
        }

        if (_selector is { } selector)
        {
            foreach (var dbms in selector.AvailableDbms)
            {
                if (selector.Find<IDbErrorClassifier>(dbms)?.FromException(exception) is { } error)
                {
                    return error.Kind;
                }
            }
        }

        return Hits([exception.Message ?? string.Empty]).FirstOrDefault().Kind;
    }

    /// <summary>
    /// 바깥 메시지에서 분류가 <paramref name="kinds"/> 중 하나인 <b>첫</b> 오류 코드(<c>ORA-01456</c>). 없으면 null.
    /// 힌트·상태 줄에 「어느 코드 때문인지」를 보일 때 쓴다.
    /// </summary>
    public static string? CodeOfKind(Exception? exception, params DbErrorKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (exception is null)
        {
            return null;
        }

        foreach (var (code, kind) in Hits([exception.Message ?? string.Empty]))
        {
            if (kinds.Contains(kind))
            {
                return code;
            }
        }

        return null;
    }

    private static HashSet<DbErrorKind> Collect(Exception? exception, IReadOnlyList<string> texts)
    {
        var kinds = new HashSet<DbErrorKind>();
        if (_selector is { } selector && exception is not null)
        {
            foreach (var dbms in selector.AvailableDbms)
            {
                if (selector.Find<IDbErrorClassifier>(dbms)?.FromException(exception) is { } error)
                {
                    AddKind(kinds, error.Kind);
                }
            }
        }

        foreach (var (_, kind) in Hits(texts))
        {
            AddKind(kinds, kind);
        }

        return kinds;
    }

    /// <summary>글 안의 오류 번호를 나온 순서대로 (코드 글, 분류)로. 연결 전(Selector 없음)에는 Oracle 한정 표를 쓴다.</summary>
    private static List<(string Code, DbErrorKind Kind)> Hits(IReadOnlyList<string> texts)
    {
        var hits = new List<(string Code, DbErrorKind Kind)>();
        var selector = _selector;
        foreach (var text in texts)
        {
            var found = new List<(int Index, string Code, DbErrorKind Kind)>();
            if (selector is null)
            {
                AddMatches(found, text, OraCode(), LegacyOracle);
            }
            else
            {
                foreach (var dbms in selector.AvailableDbms)
                {
                    if (selector.Find<IDbErrorClassifier>(dbms) is { } classifier)
                    {
                        AddMatches(found, text, CodePattern(dbms), classifier.Classify);
                    }
                }
            }

            hits.AddRange(found.OrderBy(f => f.Index).Select(f => (f.Code, f.Kind)));
        }

        return hits;
    }

    private static void AddMatches(List<(int Index, string Code, DbErrorKind Kind)> found, string text, Regex pattern, Func<int, DbErrorKind> classify)
    {
        foreach (Match m in pattern.Matches(text))
        {
            if (int.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                found.Add((m.Index, m.Value, classify(number)));
            }
        }
    }

    /// <summary>예외 사슬에 <paramref name="kinds"/> 중 하나가 있는가.</summary>
    public static bool Has(Exception? exception, params DbErrorKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var found = KindsOf(exception);
        return kinds.Any(found.Contains);
    }

    /// <summary>예외 바깥 메시지에 <paramref name="kinds"/> 중 하나가 있는가(<see cref="TopKindsOf(Exception)"/>).</summary>
    public static bool HasTop(Exception? exception, params DbErrorKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var found = TopKindsOf(exception);
        return kinds.Any(found.Contains);
    }

    /// <summary>오류 글에 <paramref name="kinds"/> 중 하나가 있는가(<see cref="KindsOf(string)"/>).</summary>
    public static bool HasInMessage(string? message, params DbErrorKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var found = KindsOf(message);
        return kinds.Any(found.Contains);
    }

    /// <summary>「볼 수 없음」(객체 없음·권한 부족): 사전·성능 뷰를 못 읽을 때 기능을 끄고 안내하는 판단. <see cref="DbErrorKinds.IsNoAccess(DbErrorKind)"/> 와 같다.</summary>
    public static bool IsNoAccess(Exception? exception) => Has(exception, DbErrorKind.ObjectNotFound, DbErrorKind.InsufficientPrivileges);

    /// <summary>연결이 끊김 또는 세션이 죽음.</summary>
    public static bool IsDisconnected(Exception? exception) => Has(exception, DbErrorKind.Disconnected);

    /// <summary>접속 환경 탓의 거절: 읽기 전용 접속·대기 DB 에서 쓰기 시도, 또는 권한 부족. 문장 탓이 아니다.</summary>
    public static bool IsEnvironmentRefusal(Exception? exception) => Has(exception, DbErrorKind.ReadOnly, DbErrorKind.InsufficientPrivileges);

    /// <summary>읽기 전용 접속·DB 에서 쓰기를 시도했다(바깥 메시지만 본다).</summary>
    public static bool IsReadOnly(Exception? exception) => HasTop(exception, DbErrorKind.ReadOnly);

    /// <summary>「볼 수 없음」(<see cref="IsNoAccess(Exception)"/>)을 <b>바깥 메시지만</b> 보고 판단한다.</summary>
    public static bool IsNoAccessTop(Exception? exception) => HasTop(exception, DbErrorKind.ObjectNotFound, DbErrorKind.InsufficientPrivileges);

    /// <summary>
    /// 예외 메시지(첫 번째 글)의 DBMS 오류 코드(<c>ORA-00942</c>·<c>TBR-8033</c>). 없으면 null.
    /// 서버 원문에는 값이 들어갈 수 있어 LLM·상태 줄에는 코드만 쓰는 자리가 쓴다.
    /// </summary>
    public static string? CodeOf(Exception? exception) => CodeOf(exception?.Message);

    /// <summary><see cref="CodeOf(Exception)"/> 의 글 버전.</summary>
    public static string? CodeOf(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var m = AnyCode().Match(message);
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// 연결되기 전의 Oracle 한정 표. 지금까지 App·Language 가 비교하던 번호만 담는다
    /// (942 볼 수 없음, 1031 권한, 3113·3114·3135·28 끊김, 1456·16000 읽기 전용, 24344 오류와 함께 컴파일됨). OracleErrorClassifier 의 부분집합이다.
    /// </summary>
    internal static DbErrorKind LegacyOracle(int number) => number switch
    {
        942 => DbErrorKind.ObjectNotFound,
        1031 => DbErrorKind.InsufficientPrivileges,
        3113 or 3114 or 3135 or 28 => DbErrorKind.Disconnected,
        1456 or 16000 => DbErrorKind.ReadOnly,
        24344 => DbErrorKind.CompiledWithErrors,
        _ => DbErrorKind.Other,
    };

    private static void AddKind(HashSet<DbErrorKind> kinds, DbErrorKind kind)
    {
        if (kind != DbErrorKind.Other)
        {
            kinds.Add(kind);
        }
    }

    private static List<Exception> Chain(Exception exception)
    {
        var list = new List<Exception>();
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0 && list.Count < 64)
        {
            var current = pending.Pop();
            list.Add(current);
            if (current is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } next)
            {
                pending.Push(next);
            }
        }

        return list;
    }

    private static Regex CodePattern(Dbms dbms) => dbms == Dbms.Tibero ? TiberoCode() : OraCode();

    [GeneratedRegex(@"(?<![A-Za-z0-9])ORA-(\d{5})", RegexOptions.CultureInvariant)]
    private static partial Regex OraCode();

    [GeneratedRegex(@"(?<![A-Za-z0-9])TBR-(\d{1,9})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex TiberoCode();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:ORA-\d{5}|TBR-\d{1,9}(?!\d))", RegexOptions.CultureInvariant)]
    private static partial Regex AnyCode();
}
