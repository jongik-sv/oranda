using System.Text.RegularExpressions;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Oranda.App.Features.Diagnostics;
using Oranda.App.Features.MyBatisWorkspace.Expanded;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;
using Oranda.Core.Language;
using Oranda.Core.Text;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Diagnostics;

/// <summary>
/// MyBatis 쿼리 탭의 문법 오류 밑줄(Order 150). 원문은 XML 이라 일반 L2 분석·점검을 끄고(<see cref="EditorDiagnostics.SqlAnalysisOff"/>),
/// 계산한 펼친 SQL(<see cref="MyBatisStatementEval"/>, 값·원문 고침 뒤 300 ms 디바운스)을 L2 로 분석해 그 오류를 원문 줄로 옮겨 밑줄을 긋는다
/// (<see cref="MyBatisLineMap"/>, 줄 단위). 옮긴 줄이 태그·빈 줄이면 가장 가까운 기준점의 SQL 줄로 간다.
/// 값이 빈 자리(null 인 <c>${…}</c>, 계산 문제 — null collection foreach 등)가 있으면 펼친 SQL 이 덜 만들어진 것이라 오류를 단정하지 않고
/// 밑줄 없는 안내(Info)로 낮춘다. 원문이 바뀌면 옛 결과가 엉뚱한 줄에 남지 않게 바로 지우고, 다음 계산 결과로 다시 긋는다.
/// </summary>
[OrandaService(typeof(IMyBatisEditorDecorator))]
public sealed partial class MyBatisSqlDiagnostics : IMyBatisEditorDecorator
{
    /// <summary>언어 서비스를 아직 못 얻었을 때(시작 직후) 다시 해 보는 간격.</summary>
    internal static readonly TimeSpan LanguageRetry = TimeSpan.FromMilliseconds(500);

    private readonly Func<MyBatisEvalHub?> _hub;
    private readonly Func<ILanguageService?> _language;

    /// <summary>셸(DI)이 쓰는 생성자. 서비스는 처음 붙일 때 꺼낸다.</summary>
    public MyBatisSqlDiagnostics(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _hub = () => services.GetService(typeof(MyBatisEvalHub)) as MyBatisEvalHub;
        _language = () => services.GetService(typeof(ILanguageService)) as ILanguageService;
    }

    /// <summary>시험용: 계산 상태 묶음과 언어 서비스를 직접 넣는다.</summary>
    internal MyBatisSqlDiagnostics(MyBatisEvalHub hub, Func<ILanguageService?> language)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(language);
        _hub = () => hub;
        _language = language;
    }

    public int Order => 150;

    public IDisposable Attach(MyBatisEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var view = context.Editor.FindLogicalAncestorOfType<SqlEditorView>();
        if (view is null || !ReferenceEquals(view.TextEditorControl, context.Editor))
        {
            return EmptyDisposable.Instance;
        }

        // 안쪽 SqlEditorView 는 MyBatis 쿼리 탭 전용이라(탭마다 새로 만든다) 끈 검사를 되돌리지 않는다.
        var diagnostics = EditorDiagnostics.Attach(view);
        diagnostics.SqlAnalysisOff = true;
        if (_hub() is not { } hub || context.Session.StatementIdOf(context.Tab) is not { Length: > 0 } id)
        {
            return EmptyDisposable.Instance;
        }

        var eval = hub.For(context.Session, id);
        var attachment = new Attachment(diagnostics, eval, context.Tab, _language);
        _ = eval.EnsureLoadedAsync();
        return attachment;
    }

    /// <summary>
    /// 펼친 SQL 진단을 원문 줄 진단으로 옮긴다. 범위는 대응 원문 줄의 첫 글자(앞 공백 뒤)~줄 끝이다.
    /// <paramref name="result"/> 에 값이 빈 자리가 있으면 오류·경고를 Info 로 낮춘다. <paramref name="map"/> 이 null 이면 새로 만든다.
    /// </summary>
    internal static IReadOnlyList<Diagnostic> ToSource(string source, EvalResult result, IReadOnlyList<Diagnostic> found, MyBatisLineMap? map = null)
    {
        if (found.Count == 0)
        {
            return [];
        }

        string expanded = result.Sql;
        map ??= MyBatisLineMap.Build(source, expanded);
        var starts = LineStarts(source);
        var expandedStarts = LineStarts(expanded);
        var off = OffLines(source, starts, result);
        bool holes = HasHoles(result);
        var list = new List<Diagnostic>(found.Count);
        foreach (var d in found)
        {
            int expandedLine = LineOf(expandedStarts, Math.Clamp(d.Range.Start, 0, expanded.Length));
            int line = SqlLine(source, starts, map, expandedLine, off);
            var (start, end) = Trimmed(source, starts, line);
            list.Add(d with
            {
                Range = new TextRange(start, end - start),
                Severity = holes && d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning ? DiagnosticSeverity.Info : d.Severity,
                Message = holes
                    ? MyBatisText.Format("MyBatis.expanded.diagnosticUnsure", "펼친 SQL(값이 빈 자리가 있어 확인하지 못했습니다): {0}", d.Message)
                    : MyBatisText.Format("MyBatis.expanded.diagnostic", "펼친 SQL: {0}", d.Message),
            });
        }

        return list;
    }

    /// <summary>값이 빈 자리가 있는가: null 인 <c>${…}</c> 또는 계산 문제(null collection foreach·계산 못 한 식·조각 없음 등).</summary>
    internal static bool HasHoles(EvalResult result) =>
        result.Issues.Count > 0 || result.Substitutions.Any(s => s.Value is null);

    /// <summary>펼친 줄에 대응하는 원문 줄. 태그·빈 줄·이번 계산에서 꺼진 블록 줄이면 가장 가까운 기준점 쪽 SQL 줄.</summary>
    private static int SqlLine(string source, List<int> starts, MyBatisLineMap map, int expandedLine, HashSet<int> off)
    {
        bool Usable(int l) => l >= 0 && l < starts.Count && !off.Contains(l) && IsSqlLine(source, starts, l);

        int line = Math.Clamp((int)Math.Floor(map.ToSource(expandedLine + 0.5)), 0, starts.Count - 1);
        if (Usable(line))
        {
            return line;
        }

        int best = line;
        double distance = double.MaxValue;
        double target = expandedLine + 0.5;
        foreach (var (s, e) in map.Anchors)
        {
            // 기준점은 줄 경계다: 원문 줄 s 는 펼친 위치 e 의 아래(중심 e+0.5), 줄 s-1 은 위(중심 e-0.5)에 있다.
            foreach (var (candidate, center) in new[] { ((int)s, e + 0.5), ((int)s - 1, e - 0.5) })
            {
                double dist = Math.Abs(center - target);
                if (dist < distance && Usable(candidate))
                {
                    distance = dist;
                    best = candidate;
                }
            }
        }

        return best;
    }

    /// <summary>XML 태그·주석을 지운 뒤 글이 남는 줄인가(한 줄 <c>&lt;if …&gt;and x = 1&lt;/if&gt;</c> 는 SQL 줄).</summary>
    private static bool IsSqlLine(string source, List<int> starts, int line)
    {
        var (start, end) = Trimmed(source, starts, line);
        return end > start && !string.IsNullOrWhiteSpace(Markup().Replace(source[start..end], ""));
    }

    /// <summary>이번 계산에서 꺼진 블록(문장 원문의 if·when·foreach 등)이 덮는 줄. 그 줄은 펼친 SQL 에 없다.</summary>
    private static HashSet<int> OffLines(string source, List<int> starts, EvalResult result)
    {
        var off = new HashSet<int>();
        foreach (var b in result.Blocks)
        {
            if (b.IsOn || b.Fragment is not null || b.Range.End > source.Length)
            {
                continue;
            }

            int first = LineOf(starts, b.Range.Start);
            int last = LineOf(starts, Math.Max(b.Range.Start, b.Range.End - 1));
            for (int l = first; l <= last; l++)
            {
                off.Add(l);
            }
        }

        return off;
    }

    private static (int Start, int End) Trimmed(string source, List<int> starts, int line)
    {
        int start = starts[line];
        int end = line + 1 < starts.Count ? starts[line + 1] : source.Length;
        while (end > start && source[end - 1] is '\n' or '\r')
        {
            end--;
        }

        while (start < end && source[start] is ' ' or '\t')
        {
            start++;
        }

        return (start, Math.Max(start, end));
    }

    /// <summary>줄 시작 위치. 줄 끝은 <c>\r\n</c>·<c>\r</c>·<c>\n</c>(<see cref="MyBatisLineMap"/>·편집기와 같은 규칙).</summary>
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            if (text[i] is '\n' or '\r')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineOf(List<int> starts, int offset)
    {
        int i = starts.BinarySearch(offset);
        return i >= 0 ? i : ~i - 1;
    }

    [GeneratedRegex(@"<!--.*?(-->|$)|<[^>]*>?")]
    private static partial Regex Markup();

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>탭 하나에 붙은 상태: 계산이 끝날 때마다 펼친 SQL 을 분석하고, 원문이 바뀌면 바로 지운다.</summary>
    internal sealed class Attachment : IDisposable
    {
        private readonly EditorDiagnostics _diagnostics;
        private readonly MyBatisStatementEval _eval;
        private readonly SqlEditorViewModel _tab;
        private readonly Func<ILanguageService?> _language;
        private readonly IDisposable _track;
        private CancellationTokenSource? _running;
        private EvalResult? _stale;
        private (string Source, string Sql, MyBatisLineMap Map)? _lastMap;
        private bool _disposed;

        public Attachment(EditorDiagnostics diagnostics, MyBatisStatementEval eval, SqlEditorViewModel tab, Func<ILanguageService?> language)
        {
            _diagnostics = diagnostics;
            _eval = eval;
            _tab = tab;
            _language = language;

            // 원문 고침 뒤 다시 계산하도록 이 꾸밈도 탭을 따라간다(참조 수를 센다 — 계산 꾸밈이 빠져도 다시 계산된다).
            _track = eval.Track(tab);
            eval.Changed += OnEvalChanged;
            tab.TextChanged += OnTextChanged;
            if (eval.Result is not null)
            {
                _ = AnalyzeAsync();
            }
        }

        /// <summary>펼친 SQL 분석 결과를 반영한 횟수(시험용: 분석이 끝났는지 기다린다). 결과가 없어 지우기만 한 것은 세지 않는다.</summary>
        public int Applied { get; private set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Cancel();
            _eval.Changed -= OnEvalChanged;
            _tab.TextChanged -= OnTextChanged;
            _track.Dispose();
            _diagnostics.SetExternalDiagnostics([]);
        }

        private void OnTextChanged(object? sender, EventArgs e)
        {
            // 지금 결과는 고치기 전 원문으로 계산한 것이다: 다음 계산이 새 결과를 낼 때까지 쓰지 않는다.
            _stale = _eval.Result;
            Cancel();
            _diagnostics.SetExternalDiagnostics([]);
        }

        private void OnEvalChanged(object? sender, EventArgs e) => _ = AnalyzeAsync();

        private async Task AnalyzeAsync()
        {
            Cancel();
            if (_disposed)
            {
                return;
            }

            // 결과가 없거나, 고치기 전 결과거나, 이 탭의 원문으로 계산한 것이 아니면(같은 id 의 다른 요소 등) 긋지 않는다.
            if (_eval.Result is not { } result || ReferenceEquals(result, _stale) || _eval.Element?.Text is not { } text || text != _tab.Text)
            {
                if (_eval.Result is null)
                {
                    _diagnostics.SetExternalDiagnostics([]);
                }

                return;
            }

            var cts = new CancellationTokenSource();
            _running = cts;
            string source = _tab.Text;
            long version = _tab.Version;
            try
            {
                var language = await Task.Run(_language, cts.Token).ConfigureAwait(true);
                if (language is null)
                {
                    // 시작 직후라 언어 서비스가 아직 없다: 잠시 뒤 다시 해 본다.
                    _ = RetryAsync(cts.Token);
                    return;
                }

                var found = await language.AnalyzeAsync(result.Sql, cts.Token).ConfigureAwait(true);
                var cached = _lastMap is { } m && m.Source == source && m.Sql == result.Sql ? m.Map : null;
                var map = cached ?? await Task.Run(() => MyBatisLineMap.Build(source, result.Sql), cts.Token).ConfigureAwait(true);
                _lastMap = (source, result.Sql, map);
                var mapped = ToSource(source, result, found, map);
                if (cts.IsCancellationRequested || _disposed || _tab.Version != version)
                {
                    return;
                }

                Apply(mapped);
            }
            catch (OperationCanceledException)
            {
                // 원문이 바뀌었거나 다시 계산됐다.
            }
            catch (Exception) when (!cts.IsCancellationRequested)
            {
                // 분석이 실패하면 옛 밑줄을 남기지 않는다(오류로 단정하지 않는다).
                if (!_disposed)
                {
                    Apply([]);
                }
            }
            finally
            {
                if (ReferenceEquals(_running, cts))
                {
                    _running = null;
                }

                cts.Dispose();
            }
        }

        private async Task RetryAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(LanguageRetry, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_disposed)
            {
                Dispatcher.UIThread.Post(() => _ = AnalyzeAsync());
            }
        }

        private void Apply(IReadOnlyList<Diagnostic> diagnostics)
        {
            _diagnostics.SetExternalDiagnostics(diagnostics);
            Applied++;
        }

        private void Cancel()
        {
            if (_running is { } running)
            {
                _running = null;
                running.Cancel();
            }
        }
    }
}
