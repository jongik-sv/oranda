using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.MyBatis;
using Oranda.Language.Lexing;
using Oranda.Language.Parsing;

namespace Oranda.Language.MyBatis;

/// <summary>
/// 매퍼를 연 뒤 그 매퍼 문장 모양으로 ANTLR 예측 캐시(DFA)를 미리 데운다(TSK-03-11). 처음 보는 문장 모양의 첫 구문 분석은
/// 파싱이 아니라 예측 캐시 만들기에 시간을 쓰므로(0.1~7.6 s), 조합 검사 전에 문장마다 「모두 켬」(조합 검사의 첫 조합)과 「모두 끔」을 한 번씩 분석해 둔다.
/// 앱 시작 때는 하지 않는다(D-006). 낮은 우선순위 백그라운드 스레드 하나에서 돌고(UI 스레드를 쓰지 않는다), 새 매퍼를 열면 앞 것은 취소한다.
/// 예측 캐시는 생성된 파서가 정적으로 공유하므로(파서 인스턴스마다 새로 만들지 않는다) 이 스레드에서 데운 것을 조합 검사가 그대로 쓴다.
/// include 는 같은 매퍼 안 조각만 푼다(다른 파일 조각은 모양만 조금 다르다).
/// </summary>
public static class MapperParseWarmUp
{
    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static CancellationTokenSource? s_current;

    /// <summary>마지막으로 시작한 데우기(시험·진단용). 시작한 적이 없으면 끝난 작업.</summary>
    public static Task<int> Last { get; private set; } = Task.FromResult(0);

    /// <summary>마지막 데우기를 돈 스레드의 번호·우선순위·배경 여부(시험용: 부른 스레드가 아니고 우선순위가 낮은지 본다).</summary>
    internal static (int ThreadId, ThreadPriority Priority, bool IsBackground)? LastThread { get; private set; }

    /// <summary>
    /// 데우기를 시작한다. 결과 = 분석한 SQL 문장 수(취소되면 취소된 작업). 앞서 시작한 데우기는 취소한다.
    /// </summary>
    public static Task<int> Start(IMyBatisMapperDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        CancellationToken ct;
        lock (Gate)
        {
            s_current?.Cancel();
            s_current?.Dispose();
            s_current = new CancellationTokenSource();
            ct = s_current.Token;
        }

        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var me = Thread.CurrentThread;
            LastThread = (me.ManagedThreadId, me.Priority, me.IsBackground);
            try
            {
                done.SetResult(Run(document, ct));
            }
            catch (OperationCanceledException)
            {
                done.SetCanceled(ct);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Oranda mapper parse warm-up",
        };
        Last = done.Task;
        thread.Start();
        return done.Task;
    }

    /// <summary>돌고 있는 데우기를 멈춘다(없으면 아무것도 하지 않는다).</summary>
    public static void Cancel()
    {
        lock (Gate)
        {
            s_current?.Cancel();
        }
    }

    /// <summary>
    /// 지금 스레드에서 데운다. 결과 = 분석한 SQL 문장 수. 펼치지 못한 문장은 건너뛴다.
    /// 먼저 모든 문장의 「모두 켬」(조합 검사의 첫 결과)을 돌고, 그다음 「모두 끔」을 돈다: 어느 문장이든 첫 결과가 빨리 데워지게.
    /// </summary>
    internal static int Run(IMyBatisMapperDocument document, CancellationToken ct)
    {
        var evaluator = new MyBatisEvaluator();
        var fragments = new LocalFragments(document);
        var statements = document.Elements.Where(e => e.IsStatement).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int parsed = 0;
        foreach (bool allOn in (bool[])[true, false])
        {
            foreach (var element in statements)
            {
                ct.ThrowIfCancellationRequested();
                if (Shape(evaluator, fragments, element, allOn) is { } sql && seen.Add(sql))
                {
                    parsed += Analyze(sql, ct);
                }
            }
        }

        return parsed;
    }

    private static int Analyze(string sql, CancellationToken ct)
    {
        int parsed = 0;
        foreach (var statement in StatementSplitter.Split(sql, SplitOptions.Default))
        {
            ct.ThrowIfCancellationRequested();
            if (statement.Kind != StatementKind.SqlPlus && !string.IsNullOrWhiteSpace(statement.Text))
            {
                PlSqlAnalyzer.Analyze(statement.Text, statement.Kind, ct);
                parsed++;
            }
        }

        return parsed;
    }

    // 모두 켬(조합 검사의 조합 1번) 또는 모두 끔으로 펼친 SQL. 조건이 없으면 모두 끔은 모두 켬과 같아 null.
    private static string? Shape(MyBatisEvaluator evaluator, LocalFragments fragments, MyBatisElement element, bool allOn)
    {
        try
        {
            var on = evaluator.Evaluate(element, fragments, NoValues, null, new MyBatisEvalOptions(AllOn: true));
            if (allOn)
            {
                return on.Sql;
            }

            return evaluator.Combinations(on).FirstOrDefault(c => c.Overrides.Count > 0 && c.Overrides.Values.All(v => !v)) is { } off
                ? evaluator.Evaluate(element, fragments, NoValues, off.Overrides).Sql
                : null;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or OperationCanceledException))
        {
            return null; // 펼치지 못한 문장은 데우지 않는다(조합 검사가 그 오류를 보인다)
        }
    }

    /// <summary>같은 매퍼 안 sql 조각만 찾는다(문서는 바뀌지 않으므로 어느 스레드에서 불러도 된다).</summary>
    private sealed class LocalFragments(IMyBatisMapperDocument document) : IMyBatisFragmentResolver
    {
        private readonly Dictionary<string, MyBatisElement> _sql = document.Elements
            .Where(e => e.Kind == MyBatisElementKind.Sql && e.Id is not null)
            .GroupBy(e => e.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        public MyBatisFragment? Resolve(string refid, string? currentNamespace)
        {
            var ns = document.MapperNamespace;
            var id = ns is not null && refid.StartsWith(ns + ".", StringComparison.Ordinal) ? refid[(ns.Length + 1)..] : refid;
            return _sql.TryGetValue(id, out var e) ? new MyBatisFragment(id, ns, e.Text) : null;
        }
    }
}
