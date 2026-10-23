using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Composition;
using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>
/// 문장 편집기의 조합 검사 꾸밈(Order 200, design.md 9장 <see cref="IMyBatisEditorDecorator"/>, 시안 Combos 아트보드).
/// <list type="bullet">
/// <item>마지막 조합 검사의 원인 줄(오류 조합의 <see cref="ComboCheckResult.SourceLine"/>)을 옅은 빨강으로 칠한다. 검사 뒤 문장을 고치면 지운다.</item>
/// <item>SET·WHERE 바로 뒤에 if 가 이어지면 머리 줄 오른쪽에 빠른 고침 단추(「빠른 고침 · 줄 3–9: &lt;set&gt; 으로 고치기」)를 둔다.
/// 누르면 「자동 고침」 명령으로 조합 검사 창을 열어 미리보기를 보인다(적용은 미리보기 뒤).</item>
/// </list>
/// </summary>
[OrandaService(typeof(IMyBatisEditorDecorator))]
public sealed class AutoFixDecorator : IMyBatisEditorDecorator
{
    internal const string ApplyAutoFixCommand = "oranda.mybatis.applyAutoFix";

    private readonly IServiceProvider? _services;

    public AutoFixDecorator()
    {
    }

    public AutoFixDecorator(IServiceProvider services)
    {
        _services = services;
    }

    public int Order => 200;

    public IDisposable Attach(MyBatisEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Attachment(context, _services);
    }

    /// <summary>편집기 하나에 붙은 꾸밈. Dispose 하면 칠과 단추를 뗀다.</summary>
    internal sealed class Attachment : IDisposable
    {
        private readonly MyBatisEditorContext _context;
        private readonly IServiceProvider? _services;
        private readonly ErrorLineRenderer _renderer;
        private readonly Button _button;
        private Panel? _slot;

        public Attachment(MyBatisEditorContext context, IServiceProvider? services)
        {
            _context = context;
            _services = services;
            _renderer = new ErrorLineRenderer(this);
            _button = new Button { Classes = { "toolbar" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            _button.Click += OnFixClicked;
            context.Editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
            PlaceButton();
            if (_slot is null)
            {
                context.Editor.AttachedToVisualTree += OnEditorAttached;
            }

            context.Tab.TextChanged += OnTextChanged;
            ComboCheckResults.Changed += OnResultsChanged;
            Refresh();
        }

        /// <summary>빠른 고침 단추(시험용).</summary>
        internal Button FixButton => _button;

        /// <summary>지금 칠하는 줄(시험용).</summary>
        internal IReadOnlyList<int> ErrorLines { get; private set; } = [];

        public void Dispose()
        {
            ComboCheckResults.Changed -= OnResultsChanged;
            _context.Editor.AttachedToVisualTree -= OnEditorAttached;
            _context.Tab.TextChanged -= OnTextChanged;
            _context.Editor.TextArea.TextView.BackgroundRenderers.Remove(_renderer);
            _slot?.Children.Remove(_button);
            _button.Click -= OnFixClicked;
        }

        private void OnTextChanged(object? sender, EventArgs e) => Refresh();

        // 결과는 UI 스레드에서 바뀌지만, 다른 스레드에서 오면 넘긴다(편집기 문서는 UI 스레드 전용).
        private void OnResultsChanged(object? sender, EventArgs e)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Refresh();
            }
            else
            {
                Dispatcher.UIThread.Post(Refresh);
            }
        }

        // 꾸밈은 문장 편집기 View 가 화면에 붙을 때 붙지만 안쪽 편집기는 아직 템플릿 전일 수 있다. 논리 트리로 먼저 찾고, 없으면 붙은 뒤 다시 찾는다.
        private void PlaceButton()
        {
            _slot = (_context.Editor.FindLogicalAncestorOfType<MyBatisStatementEditorView>() ?? _context.Editor.FindAncestorOfType<MyBatisStatementEditorView>())?.HeaderExtraSlot;
            _slot?.Children.Add(_button);
        }

        private void OnEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (_slot is null)
            {
                PlaceButton();
            }

            if (_slot is not null)
            {
                _context.Editor.AttachedToVisualTree -= OnEditorAttached;
            }
        }

        private void Refresh()
        {
            var id = _context.Session.StatementIdOf(_context.Tab);
            var lines = id is null ? [] : ComboCheckResults.ErrorLines(_context.Session, id);
            if (!lines.SequenceEqual(ErrorLines))
            {
                ErrorLines = lines;
                _context.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            }

            var fixes = _context.Tab.IsReadOnly ? [] : MyBatisAutoFix.Find(_context.Tab.Text);
            var fix = fixes.Count > 0 ? fixes[0] : null;
            _button.IsVisible = fix is not null;
            if (fix is not null)
            {
                var item = new ComboFixItem(fix);
                _button.Content = $"{item.Heading}: {item.ActionText}";
                ToolTip.SetTip(_button, item.Description + " " + ComboText.FixPreviewTip);
            }
        }

        private async void OnFixClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_services?.GetService(typeof(ICommandRegistry)) is not ICommandRegistry registry
                || _services.GetService(typeof(ContextKeyService)) is not ContextKeyService keys)
            {
                return;
            }

            // 자동 고침 명령은 고른 문장을 대상으로 한다. 이 편집기의 문장을 먼저 고른다.
            _context.Session.SelectStatement(_context.Session.StatementIdOf(_context.Tab));
            try
            {
                await registry.ExecuteAsync(ApplyAutoFixCommand, keys).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // async void 처리기라 예외가 앱을 멈추지 않게 기록만 한다.
                System.Diagnostics.Trace.TraceError("자동 고침 단추 처리 중 예외: {0}", ex);
            }
        }

        // 원인 줄 칠하기. 붓은 테마 자원(OrandaDiffRemovedBrush)이라 어두운 테마에서도 맞는다.
        private sealed class ErrorLineRenderer(Attachment owner) : IBackgroundRenderer
        {
            public KnownLayer Layer => KnownLayer.Background;

            public void Draw(TextView textView, DrawingContext drawingContext)
            {
                if (owner.ErrorLines.Count == 0
                    || !textView.TryFindResource("OrandaDiffRemovedBrush", textView.ActualThemeVariant, out var found) || found is not IBrush fill)
                {
                    return;
                }

                var document = owner._context.Tab.Document;
                foreach (int lineNo in owner.ErrorLines)
                {
                    if (lineNo < 1 || lineNo > document.LineCount)
                    {
                        continue;
                    }

                    var line = document.GetLineByNumber(lineNo);
                    var segment = new TextSegment { StartOffset = line.Offset, Length = line.Length };
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    {
                        drawingContext.FillRectangle(fill, new Rect(0, rect.Y, textView.Bounds.Width, rect.Height));
                    }
                }
            }
        }
    }
}
