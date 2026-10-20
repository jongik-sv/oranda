using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Language;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Diagnostics;

/// <summary>밑줄이 어디서 왔는지. 각 출처는 자기 결과만 바꾸고 지운다.</summary>
public enum DiagnosticSource
{
    /// <summary>L2 문법 분석.</summary>
    Analysis,

    /// <summary>서버 컴파일 오류(user_errors).</summary>
    Server,

    /// <summary>SQL 점검(lint, TSK-13-04).</summary>
    Lint,
}

/// <summary>밑줄 하나. 문서가 바뀌면 AvaloniaEdit 가 위치를 따라 옮긴다.</summary>
public sealed class DiagnosticSegment : TextSegment
{
    public DiagnosticSegment(Diagnostic diagnostic, bool fromServer)
        : this(diagnostic, fromServer ? DiagnosticSource.Server : DiagnosticSource.Analysis)
    {
    }

    public DiagnosticSegment(Diagnostic diagnostic, DiagnosticSource source)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        Diagnostic = diagnostic;
        Source = source;
        StartOffset = diagnostic.Range.Start;
        Length = diagnostic.Range.Length;
    }

    public Diagnostic Diagnostic { get; }

    /// <summary>서버 컴파일 오류(user_errors)에서 왔는지. L2 재분석은 이것을 지우지 않는다.</summary>
    public bool FromServer => Source == DiagnosticSource.Server;

    public DiagnosticSource Source { get; }
}

/// <summary>
/// SQL 편집기 1개의 문법 오류 표시(TSK-04-03, EDIT-03). 입력이 <see cref="Debounce"/> 만큼 멈추면 문서를 <see cref="ILanguageService.AnalyzeAsync"/> 로
/// 백그라운드 분석하고(언어 서비스가 바뀐 문장만 다시 파싱), 결과를 물결 밑줄과 마우스 올림 설명으로 보인다.
/// 다시 입력하면 진행 중 분석을 취소한다. 서버 컴파일 오류는 <see cref="SetServerDiagnostics"/> 로 같은 밑줄에 얹는다.
/// UI 스레드에서만 다룬다(파싱은 UI 스레드에서 하지 않는다). 소유: TSK-04-03.
/// </summary>
public sealed class EditorDiagnostics : IBackgroundRenderer
{
    /// <summary>
    /// 입력이 이만큼 멈춘 뒤 분석한다(TRD 4.5). 값은 이 한 곳에서만 정한다(측정 시나리오도 이것을 읽는다).
    /// PRD 7.2 「입력 멈춘 뒤 300 ms」 는 마지막 키 기준이라 디바운스가 한도의 일부만 쓰도록 100 ms 로 둔다(D-020, TSK-04-09).
    /// </summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    private static readonly ConditionalWeakTable<SqlEditorView, EditorDiagnostics> s_attached = new();

    private static readonly IPen ErrorPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xE5, 0x1A, 0x1A)), 1);
    private static readonly IPen WarningPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xD9, 0x8C, 0x00)), 1);

    private readonly SqlEditorView _view;
    private readonly Func<ILanguageService?> _language;
    private readonly DispatcherTimer _timer;
    private SqlEditorViewModel? _vm;
    private TextSegmentCollection<DiagnosticSegment>? _segments;
    private CancellationTokenSource? _running;
    private long _lastChange;
    private bool _toolTipShown;

    private EditorDiagnostics(SqlEditorView view, Func<ILanguageService?> language)
    {
        _view = view;
        _language = language;
        _timer = new DispatcherTimer { Interval = Debounce };
        _timer.Tick += OnTick;
        var textView = view.TextEditorControl.TextArea.TextView;
        textView.BackgroundRenderers.Add(this);
        textView.PointerMoved += OnPointerMoved;
        textView.PointerExited += (_, _) => HideToolTip();
        view.DataContextChanged += (_, _) => Bind(view.DataContext as SqlEditorViewModel);
        Bind(view.DataContext as SqlEditorViewModel);
    }

    /// <summary>언어 서비스를 얻는 방법. 시작 작업(<see cref="EditorDiagnosticsStartup"/>)이 컨테이너로 채운다. 백그라운드 스레드에서 부른다.</summary>
    public static Func<ILanguageService?> LanguageServiceProvider { get; set; } = () => null;

    /// <summary>SQL 점검 서비스를 얻는 방법(TSK-13-04). null 을 돌려주면 점검하지 않는다. 백그라운드 스레드에서 부른다.</summary>
    public static Func<ILintService?> LintServiceProvider { get; set; } = () => null;

    /// <summary>점검에 쓸 세션(표 정의 캐시의 키). 없으면 null: 메타데이터 규칙만 건너뛴다. UI 스레드에서 부른다.</summary>
    public static Func<ISession?> LintSessionProvider { get; set; } = () => null;

    /// <summary>설정 「SQL 점검(lint) 켜기」. UI 스레드에서 부른다.</summary>
    public static Func<bool> LintEnabledProvider { get; set; } = () => true;

    /// <summary>마지막으로 반영한 L2 분석 횟수(시험·진단용).</summary>
    public int AppliedAnalyses { get; private set; }

    /// <summary>지금 표시 중인 진단(문서 오프셋, 편집을 따라 옮겨진 위치).</summary>
    public IReadOnlyList<Diagnostic> Current
        => _segments is null
            ? []
            : _segments.Select(s => s.Diagnostic with { Range = new Core.Text.TextRange(s.StartOffset, s.Length) }).ToList();

    /// <summary>진행 중이거나 기다리는 분석이 있는지.</summary>
    public bool IsPending => _timer.IsEnabled || _running is not null;

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>편집기 View 에 붙인다(이미 붙었으면 그것을 돌려준다).</summary>
    public static EditorDiagnostics Attach(SqlEditorView view, Func<ILanguageService?>? language = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (s_attached.TryGetValue(view, out var existing))
        {
            return existing;
        }

        var created = new EditorDiagnostics(view, language ?? (() => LanguageServiceProvider()));
        s_attached.Add(view, created);
        return created;
    }

    /// <summary>View 에 붙은 표시기. 없으면 null.</summary>
    public static EditorDiagnostics? Of(SqlEditorView view)
        => view is not null && s_attached.TryGetValue(view, out var d) ? d : null;

    /// <summary>
    /// 서버 컴파일 오류(user_errors → <c>PlSqlAnalyzer.FromServerErrors</c> 등으로 문서 범위로 바꾼 것)를 밑줄로 보인다.
    /// 다시 부르면 바꾼다. 빈 목록이면 지운다. 활성 탭의 표시기는 <c>EditorDiagnostics.Of(vm.Surface as SqlEditorView)</c>.
    /// </summary>
    public void SetServerDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Replace(DiagnosticSource.Server, diagnostics);
    }

    /// <summary>디바운스를 건너뛰고 지금 분석한다(시험·명령용).</summary>
    public Task AnalyzeNowAsync()
    {
        _timer.Stop();
        return RunAnalysisAsync();
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(textView);
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_segments is null || _segments.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            return;
        }

        int viewStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        int viewEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var seg in _segments.FindOverlappingSegments(viewStart, viewEnd - viewStart))
        {
            var pen = seg.Diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => ErrorPen,
                DiagnosticSeverity.Warning => WarningPen,
                _ => null, // 「분석 불가」(Info)는 밑줄을 긋지 않는다 — 오류로 단정하지 않는다.
            };
            if (pen is null)
            {
                continue;
            }

            var target = seg.Length > 0 ? (ISegment)seg : new TextSegment { StartOffset = seg.StartOffset, Length = 0 };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, target, extendToFullWidthAtLineEnd: false))
            {
                var r = rect.Width < 4 ? rect.WithWidth(4) : rect;
                drawingContext.DrawGeometry(null, pen, Squiggle(r));
            }
        }
    }

    /// <summary>물결선(높이 3 DIP, 반주기 3 DIP).</summary>
    internal static StreamGeometry Squiggle(Rect r)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double y = r.Bottom - 1;
            ctx.BeginFigure(new Point(r.Left, y), false);
            bool up = true;
            for (double x = r.Left + 3; x <= r.Right + 0.01; x += 3)
            {
                ctx.LineTo(new Point(x, up ? y - 3 : y));
                up = !up;
            }

            ctx.EndFigure(false);
        }

        return geometry;
    }

    private void Bind(SqlEditorViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.TextChanged -= OnTextChanged;
        }

        Cancel();
        _timer.Stop();
        _segments = null;
        _vm = vm;
        if (vm is null)
        {
            Redraw();
            return;
        }

        _segments = new TextSegmentCollection<DiagnosticSegment>(vm.Document);
        vm.TextChanged += OnTextChanged;
        Schedule();
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        Cancel();
        Schedule();
    }

    private void Schedule()
    {
        _lastChange = Stopwatch.GetTimestamp();
        if (!_timer.IsEnabled)
        {
            _timer.Interval = Debounce;
            _timer.Start();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var remaining = Debounce - Stopwatch.GetElapsedTime(_lastChange);
        if (remaining > TimeSpan.Zero)
        {
            // 아직 입력 중: 마지막 키에서 Debounce 가 지나는 때에 다시 깬다. 키마다 타이머를 다시 걸지 않고,
            // 예전처럼 Debounce/4 마다 확인하지도 않는다(확인 주기만큼 늦게 시작하던 0~75 ms 를 없앤다, TSK-04-09).
            _timer.Interval = remaining;
            return;
        }

        _timer.Stop();
        _ = RunAnalysisAsync();
    }

    private async Task RunAnalysisAsync()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        string text = vm.Text;
        long version = vm.Version;
        try
        {
            bool lintOn = false;
            ISession? lintSession = null;
            try
            {
                lintOn = LintEnabledProvider();
                lintSession = lintOn ? LintSessionProvider() : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lintOn = false; // 점검 설정·세션을 못 읽어도 문법 분석은 그대로 한다
            }

            // 언어 서비스를 찾는 일(지연 어셈블리 로드)도 파싱도 UI 스레드 밖에서 한다.
            var language = await Task.Run(_language, cts.Token).ConfigureAwait(true);
            if (language is null)
            {
                return;
            }

            var result = await language.AnalyzeAsync(text, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || !ReferenceEquals(vm, _vm) || vm.Version != version)
            {
                return;
            }

            Replace(DiagnosticSource.Analysis, result);
            AppliedAnalyses++;

            // 점검(lint)은 문법 분석과 별도 단계다: 문법 오류 밑줄을 먼저 보이고, 그 뒤 같은 취소 토큰으로 돌린다.
            IReadOnlyList<Diagnostic> lint = [];
            if (lintOn)
            {
                try
                {
                    lint = await Task.Run(() => LintServiceProvider()?.Lint(text, lintSession, cts.Token) ?? [], cts.Token).ConfigureAwait(true);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    lint = []; // 점검이 실패해도 이전 점검 밑줄은 지운다(옮겨진 위치에 남지 않게)
                }
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(vm, _vm) || vm.Version != version)
            {
                return;
            }

            Replace(DiagnosticSource.Lint, lint);
        }
        catch (OperationCanceledException)
        {
            // 새 입력이 들어와 취소됐다.
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

    private void Replace(DiagnosticSource source, IReadOnlyList<Diagnostic> diagnostics)
    {
        if (_segments is null || _vm is null)
        {
            return;
        }

        foreach (var old in _segments.Where(s => s.Source == source).ToList())
        {
            _segments.Remove(old);
        }

        int length = _vm.Document.TextLength;
        foreach (var d in diagnostics)
        {
            if (d.Range.Start >= 0 && d.Range.Start + d.Range.Length <= length)
            {
                _segments.Add(new DiagnosticSegment(d, source));
            }
        }

        Redraw();
    }

    private void Cancel()
    {
        if (_running is { } running)
        {
            _running = null;
            running.Cancel();
        }
    }

    private void Redraw() => _view.TextEditorControl.TextArea.TextView.InvalidateLayer(Layer);

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var textView = _view.TextEditorControl.TextArea.TextView;
        if (_segments is null || _segments.Count == 0 || _vm is null)
        {
            HideToolTip();
            return;
        }

        var pos = textView.GetPositionFloor(e.GetPosition(textView) + textView.ScrollOffset);
        if (pos is not { } p)
        {
            HideToolTip();
            return;
        }

        int offset = _vm.Document.GetOffset(p.Location);
        var hit = _segments.FindSegmentsContaining(offset).FirstOrDefault()
                  ?? (offset > 0 ? _segments.FindSegmentsContaining(offset - 1).FirstOrDefault() : null);
        if (hit is null)
        {
            HideToolTip();
            return;
        }

        ToolTip.SetTip(textView, hit.Diagnostic.Message);
        ToolTip.SetIsOpen(textView, true);
        _toolTipShown = true;
    }

    private void HideToolTip()
    {
        if (!_toolTipShown)
        {
            return;
        }

        var textView = _view.TextEditorControl.TextArea.TextView;
        ToolTip.SetIsOpen(textView, false);
        ToolTip.SetTip(textView, null);
        _toolTipShown = false;
    }
}
