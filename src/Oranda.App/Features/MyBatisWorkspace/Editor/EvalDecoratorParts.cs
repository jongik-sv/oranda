using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.Diagnostics;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Text;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Editor;

/// <summary>if·when 머리 줄 하나의 여백 표시(문장 원문 기준 범위).</summary>
/// <param name="Line">머리 줄 번호(1부터).</param>
/// <param name="Range">블록 범위(덮어쓰기 열쇠).</param>
/// <param name="IsOn">이번 값으로 켜졌다.</param>
/// <param name="IsOverridden">덮어쓰기로 정했다.</param>
/// <param name="Unevaluable">test 식을 계산하지 못했다(덮어쓰기로 고른다).</param>
/// <param name="Tip">툴팁(식·상태·이유).</param>
internal sealed record EvalHeadMark(int Line, TextRange Range, bool IsOn, bool IsOverridden, bool Unevaluable, string Tip);

/// <summary>
/// 편집기 하나에 붙은 계산 꾸밈 묶음: 흐림 변환기·물결 그리기·여백. 계산 상태가 바뀌면 다시 그린다. Dispose 하면 모두 뗀다.
/// 범위는 계산한 원문 기준이라 디바운스 사이(글을 고친 직후)에는 조금 어긋날 수 있다. 문서 길이를 넘는 범위는 버린다.
/// </summary>
internal sealed class EvalDecoration : IDisposable
{
    private readonly MyBatisStatementEval _eval;
    private readonly TextEditor _editor;
    private readonly IDisposable _track;
    private readonly DimTransformer _dim;
    private readonly IssueRenderer _issues;
    private readonly EvalMargin _margin;
    private bool _disposed;

    public EvalDecoration(MyBatisStatementEval eval, SqlEditorViewModel tab, TextEditor editor)
    {
        _eval = eval;
        _editor = editor;
        _track = eval.Track(tab);
        _dim = new DimTransformer();
        _issues = new IssueRenderer(editor);
        _margin = new EvalMargin(eval, editor);
        editor.TextArea.TextView.LineTransformers.Add(_dim);
        editor.TextArea.TextView.BackgroundRenderers.Add(_issues);
        editor.TextArea.LeftMargins.Add(_margin);
        eval.Changed += OnChanged;
        Apply();
    }

    /// <summary>흐리게 칠하는 범위(시험용).</summary>
    internal IReadOnlyList<TextRange> DimRanges => _dim.Ranges;

    /// <summary>물결 밑줄 범위(시험용).</summary>
    internal IReadOnlyList<TextRange> IssueRanges => _issues.Ranges;

    /// <summary>여백 점(시험용).</summary>
    internal IReadOnlyList<EvalHeadMark> Heads => _margin.Heads;

    /// <summary>여백 점 하나를 누른 것과 같다(시험용): 그 블록을 지금과 반대로 덮어쓴다.</summary>
    internal void ToggleHead(EvalHeadMark head) => _margin.Toggle(head);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _eval.Changed -= OnChanged;
        _track.Dispose();
        _editor.TextArea.TextView.LineTransformers.Remove(_dim);
        _editor.TextArea.TextView.BackgroundRenderers.Remove(_issues);
        _editor.TextArea.LeftMargins.Remove(_margin);
    }

    private void OnChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        var document = _editor.Document;
        var length = document?.TextLength ?? 0;
        var dims = new List<TextRange>();
        var issues = new List<TextRange>();
        var heads = new List<EvalHeadMark>();
        if (_eval.Result is { } result && document is not null)
        {
            var unevaluable = result.Issues.Where(i => i.Kind == MyBatisEvalIssueKind.Unevaluable && i.Fragment is null).ToList();
            foreach (var block in result.Blocks)
            {
                if (block.Fragment is not null || block.Range.End > length || block.Range.Length <= 0)
                {
                    continue; // 조각 안 블록은 include 태그 자리라 따로 그리지 않는다
                }

                if (!block.IsOn)
                {
                    dims.Add(block.Range);
                }

                if (block.Tag is "if" or "when")
                {
                    var issue = unevaluable.FirstOrDefault(i => i.Range.Start >= block.Range.Start && i.Range.End <= HeadEnd(document, block.Range));
                    heads.Add(new EvalHeadMark(
                        document.GetLineByOffset(block.Range.Start).LineNumber,
                        block.Range,
                        block.IsOn,
                        block.IsOverridden,
                        issue is not null,
                        Tip(block, issue)));
                }
            }

            foreach (var issue in result.Issues)
            {
                if (issue.Range.End <= length && issue.Range.Length > 0)
                {
                    issues.Add(issue.Range);
                }
            }

            // 대소문자만 다른 두 파라미터가 바인드 창 한 행이 되는 곳(계산 상태가 모음).
            foreach (var (range, _) in _eval.Warnings)
            {
                if (range.End <= length && range.Length > 0)
                {
                    issues.Add(range);
                }
            }
        }

        _dim.Ranges = dims;
        _issues.Ranges = issues;
        _margin.Heads = heads;
        _editor.TextArea.TextView.Redraw();
        _margin.InvalidateVisual();
    }

    // 머리 줄 끝(여는 태그가 든 줄의 끝): test 식 문제는 이 안에 있다.
    private static int HeadEnd(TextDocument document, TextRange block) => document.GetLineByOffset(block.Start).EndOffset;

    private static string Tip(MyBatisBlock block, MyBatisEvalIssue? issue)
    {
        var state = block.IsOn ? "켬" : "끔";
        var how = block.IsOverridden ? "고른 값" : issue is not null ? "계산 못 함" : "이번 값";
        var head = block.Test is { Length: > 0 } test ? $"{block.Tag} test=\"{test}\"" : block.Tag;
        var reason = issue is not null ? Environment.NewLine + issue.Reason : string.Empty;
        return $"{head}{Environment.NewLine}{how}: {state}{reason}{Environment.NewLine}누르면 켬·끔을 바꿉니다(우클릭: 계산대로).";
    }

    /// <summary>테마 리소스 키로 붓을 찾는다(창에 붙기 전이면 앱 리소스). 못 찾으면 null.</summary>
    internal static IBrush? FindBrush(Control control, string key)
    {
        var variant = control.ActualThemeVariant;
        if (control.TryFindResource(key, variant, out var found) && found is IBrush brush)
        {
            return brush;
        }

        return Application.Current is { } app && app.TryGetResource(key, variant ?? ThemeVariant.Default, out found) && found is IBrush appBrush ? appBrush : null;
    }

    /// <summary>끈 블록의 글자를 흐리게(지금 글자색의 불투명도를 낮춤) 한다. 강조(Order 0) 뒤에 붙으므로 그 색을 그대로 흐린다.</summary>
    private sealed class DimTransformer : DocumentColorizingTransformer
    {
        private const double DimOpacity = 0.4;

        public List<TextRange> Ranges { get; set; } = [];

        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (var r in Ranges)
            {
                int start = Math.Max(r.Start, line.Offset);
                int end = Math.Min(r.End, line.EndOffset);
                if (start >= end)
                {
                    continue;
                }

                ChangeLinePart(start, end, element =>
                {
                    var brush = element.TextRunProperties.ForegroundBrush;
                    if (brush is ISolidColorBrush solid && solid.Opacity > DimOpacity)
                    {
                        element.TextRunProperties.SetForegroundBrush(new ImmutableSolidColorBrush(solid.Color, DimOpacity));
                    }
                });
            }
        }
    }

    /// <summary>문제 범위에 물결 밑줄(경고 색, 기존 진단 물결과 같은 모양).</summary>
    private sealed class IssueRenderer(TextEditor editor) : IBackgroundRenderer
    {
        public List<TextRange> Ranges { get; set; } = [];

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Ranges.Count == 0 || !textView.VisualLinesValid || FindBrush(editor, "OrandaWarningForegroundBrush") is not { } brush)
            {
                return;
            }

            var pen = new ImmutablePen(brush.ToImmutable(), 1);
            foreach (var r in Ranges)
            {
                var segment = new TextSegment { StartOffset = r.Start, Length = r.Length };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, extendToFullWidthAtLineEnd: false))
                {
                    var box = rect.Width < 4 ? rect.WithWidth(4) : rect;
                    drawingContext.DrawGeometry(null, pen, EditorDiagnostics.Squiggle(box));
                }
            }
        }
    }

    /// <summary>if·when 머리 줄 여백: 켬 = 채운 점, 끔 = 빈 점, 계산 못 함 = 경고 색 테두리. 누르면 덮어쓰기.</summary>
    private sealed class EvalMargin : AbstractMargin
    {
        private const double MarginWidth = 14;
        private readonly MyBatisStatementEval _eval;
        private readonly TextEditor _editor;
        private EvalHeadMark? _menuTarget;

        public EvalMargin(MyBatisStatementEval eval, TextEditor editor)
        {
            _eval = eval;
            _editor = editor;
            Cursor = new Cursor(StandardCursorType.Hand);
            ContextMenu = BuildMenu();
        }

        public List<EvalHeadMark> Heads { get; set; } = [];

        public void Toggle(EvalHeadMark head) => _eval.SetOverride(head.Range, !head.IsOn);

        protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

        public override void Render(DrawingContext context)
        {
            var view = TextView;
            if (view is null || !view.VisualLinesValid || Heads.Count == 0)
            {
                return;
            }

            var on = FindBrush(_editor, "OrandaSuccessForegroundBrush");
            var off = FindBrush(_editor, "OrandaButtonDisabledForegroundBrush");
            var warn = FindBrush(_editor, "OrandaWarningForegroundBrush");
            foreach (var line in view.VisualLines)
            {
                var number = line.FirstDocumentLine.LineNumber;
                var head = Heads.FirstOrDefault(h => h.Line == number);
                if (head is null)
                {
                    continue;
                }

                var y = line.VisualTop - view.ScrollOffset.Y + (line.Height / 2);
                var center = new Point(MarginWidth / 2, y);
                if (head.IsOn)
                {
                    context.DrawEllipse(on, head.Unevaluable && warn is not null ? new ImmutablePen(warn.ToImmutable(), 1.5) : null, center, 4, 4);
                }
                else
                {
                    var ring = (head.Unevaluable ? warn : off) ?? off;
                    context.DrawEllipse(null, ring is null ? null : new ImmutablePen(ring.ToImmutable(), 1.5), center, 3, 3);
                }
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var head = HeadAt(e.GetPosition(this).Y);
            ToolTip.SetTip(this, head?.Tip);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var head = HeadAt(e.GetPosition(this).Y);
            _menuTarget = head;
            if (head is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                Toggle(head);
                e.Handled = true;
            }
        }

        private EvalHeadMark? HeadAt(double y)
        {
            var view = TextView;
            if (view is null || !view.VisualLinesValid)
            {
                return null;
            }

            var line = view.GetVisualLineFromVisualTop(y + view.ScrollOffset.Y);
            return line is null ? null : Heads.FirstOrDefault(h => h.Line == line.FirstDocumentLine.LineNumber);
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("켬으로 고르기", true));
            menu.Items.Add(Item("끔으로 고르기", false));
            menu.Items.Add(Item("계산대로", null));
            menu.Opening += (_, e) => e.Cancel = _menuTarget is null;
            return menu;
        }

        private MenuItem Item(string header, bool? value)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                if (_menuTarget is { } head)
                {
                    _eval.SetOverride(head.Range, value);
                }
            };
            return item;
        }
    }
}
