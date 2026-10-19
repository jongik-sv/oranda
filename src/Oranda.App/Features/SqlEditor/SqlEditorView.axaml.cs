using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 편집기 문서 View. AvaloniaEdit 위에 구문 강조·접기·괄호 짝·북마크·다중 커서·찾기 패널을 얹는다.
/// 논리는 <see cref="SqlEditorViewModel"/> 에 있고, 이 클래스는 컨트롤이 있어야 하는 일(클립보드·접기·화면 이동)만 한다.
/// </summary>
public partial class SqlEditorView : UserControl, IEditorSurface
{
    /// <summary>입력이 이만큼 멈추면 접기·짝을 다시 계산한다. 타이머는 이 간격의 1/4 로 돌며 키마다 다시 맞추지 않는다(타이머 재설정이 키당 ≈0.8 ms 였다).</summary>
    private static readonly TimeSpan StructureDelay = TimeSpan.FromMilliseconds(400);

    private readonly BracketHighlightRenderer _brackets = new();
    private readonly DispatcherTimer _structureTimer;
    private SqlEditorViewModel? _vm;
    private SqlSyntaxColorizer? _colorizer;
    private FoldingManager? _folding;
    private MultiCaretController? _carets;
    private BookmarkRenderer? _bookmarkRenderer;
    private IReadOnlyList<StructurePair> _pairs = [];
    private long _pairsVersion = -1;
    private long _lastChangeTimestamp;

    public SqlEditorView()
    {
        InitializeComponent();
        _structureTimer = new DispatcherTimer { Interval = StructureDelay / 4 };
        _structureTimer.Tick += (_, _) =>
        {
            if (Stopwatch.GetElapsedTime(_lastChangeTimestamp) < StructureDelay)
            {
                return; // 아직 입력 중
            }

            _structureTimer.Stop();
            RefreshStructureNow();
        };

        Editor.Options.IndentationSize = 4;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.TextArea.Caret.PositionChanged += (_, _) => OnCaretOrSelectionChanged();
        Editor.TextArea.SelectionChanged += (_, _) => OnCaretOrSelectionChanged();
        Editor.ActualThemeVariantChanged += (_, _) => Editor.TextArea.TextView.Redraw();

        NextButton.Click += (_, _) => _vm?.FindNext();
        PrevButton.Click += (_, _) => _vm?.FindPrevious();
        ReplaceButton.Click += (_, _) => _vm?.ReplaceCurrent();
        ReplaceAllButton.Click += (_, _) => _vm?.ReplaceAll();
        CloseFindButton.Click += (_, _) => _vm?.CloseFind();
        FindBox.KeyDown += OnFindKeyDown;
        ReplaceBox.KeyDown += OnFindKeyDown;
    }

    /// <summary>안쪽 편집기 컨트롤(시험·계측·확장용).</summary>
    public AvaloniaEdit.TextEditor TextEditorControl => Editor;

    public SqlSyntaxColorizer? Colorizer => _colorizer;

    public FoldingManager? Folding => _folding;

    public BracketHighlightRenderer Brackets => _brackets;

    public int CursorCount => _carets?.Count ?? 1;

    public int FirstVisibleLine
        => Editor.TextArea.TextView.GetDocumentLineByVisualTop(Editor.TextArea.TextView.ScrollOffset.Y)?.LineNumber ?? 1;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        if (DataContext is SqlEditorViewModel vm)
        {
            Attach(vm);
        }
    }

    private void Attach(SqlEditorViewModel vm)
    {
        _vm = vm;
        Editor.Document = vm.Document;
        Editor.IsReadOnly = vm.IsReadOnly;
        _colorizer = new SqlSyntaxColorizer(Editor);
        _folding = FoldingManager.Install(Editor.TextArea);
        _carets = new MultiCaretController(Editor);
        _bookmarkRenderer = new BookmarkRenderer(vm);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_bookmarkRenderer);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_brackets);

        vm.Surface = this;
        vm.TextChanged += OnVmTextChanged;
        vm.BookmarksChanged += OnBookmarksChanged;
        vm.PropertyChanged += OnVmPropertyChanged;

        int caret = Math.Clamp(vm.CaretOffset, 0, vm.Document.TextLength);
        if (vm.SelectionLength > 0)
        {
            Editor.Select(vm.SelectionStart, vm.SelectionLength);
        }
        else
        {
            Editor.CaretOffset = caret;
        }

        RefreshStructureNow();
        int scrollLine = vm.ScrollLine;
        Dispatcher.UIThread.Post(() =>
        {
            if (scrollLine > 1 && scrollLine <= Editor.Document.LineCount)
            {
                Editor.ScrollToLine(scrollLine);
            }
        });
    }

    private void Detach()
    {
        _structureTimer.Stop();
        if (_vm is not null)
        {
            _vm.TextChanged -= OnVmTextChanged;
            _vm.BookmarksChanged -= OnBookmarksChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            if (ReferenceEquals(_vm.Surface, this))
            {
                _vm.Surface = null;
            }
        }

        _colorizer?.Detach();
        _carets?.Detach();
        if (_folding is not null)
        {
            FoldingManager.Uninstall(_folding);
        }

        if (_bookmarkRenderer is not null)
        {
            Editor.TextArea.TextView.BackgroundRenderers.Remove(_bookmarkRenderer);
        }

        Editor.TextArea.TextView.BackgroundRenderers.Remove(_brackets);
        _brackets.Set(null);
        _colorizer = null;
        _carets = null;
        _folding = null;
        _bookmarkRenderer = null;
        _vm = null;
        _pairs = [];
        _pairsVersion = -1;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlEditorViewModel.IsReadOnly) && _vm is not null)
        {
            Editor.IsReadOnly = _vm.IsReadOnly;
        }
    }

    private void OnBookmarksChanged(object? sender, EventArgs e)
        => Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

    private void OnVmTextChanged(object? sender, EventArgs e)
    {
        // 접기·괄호 짝은 입력이 멈춘 뒤 한 번만 다시 계산한다(키 입력 경로에 싣지 않는다).
        _brackets.Set(null);
        _lastChangeTimestamp = Stopwatch.GetTimestamp();
        if (!_structureTimer.IsEnabled)
        {
            _structureTimer.Start();
        }
    }

    /// <summary>접기 구간과 괄호 짝을 지금 문서로 다시 계산한다(타이머가 부르며, 시험은 직접 부른다).</summary>
    public void RefreshStructureNow()
    {
        if (_vm is null || _folding is null)
        {
            return;
        }

        var text = _vm.Text;
        _pairs = SqlStructure.FindPairs(text);
        _pairsVersion = _vm.Version;
        var folds = SqlStructure.ComputeFolds(text, _pairs)
            .Where(f => f.End <= text.Length && f.End > f.Start)
            .Select(f => new NewFolding(f.Start, f.End) { Name = f.Name });
        _folding.UpdateFoldings(folds, -1);
        UpdateBrackets();
    }

    private void OnCaretOrSelectionChanged()
    {
        if (_vm is null)
        {
            return;
        }

        var area = Editor.TextArea;
        _vm.UpdateCaret(area.Caret.Offset, Editor.SelectionStart, Editor.SelectionLength);
        UpdateBrackets();
    }

    private void UpdateBrackets()
    {
        if (_vm is null)
        {
            return;
        }

        var match = _pairsVersion == _vm.Version ? SqlStructure.FindMatch(_pairs, Editor.TextArea.Caret.Offset) : null;
        _brackets.Set(match);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    // ---- 찾기 패널 ------------------------------------------------------------------------

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (e.KeyModifiers == KeyModifiers.Shift)
            {
                _vm.FindPrevious();
            }
            else
            {
                _vm.FindNext();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.CloseFind();
            e.Handled = true;
        }
    }

    // ---- IEditorSurface -------------------------------------------------------------------

    public void Cut() => Editor.Cut();

    public void Copy() => Editor.Copy();

    public void Paste() => Editor.Paste();

    public void SelectAll() => Editor.SelectAll();

    public void SelectRange(int start, int length)
    {
        Editor.Select(start, length);
        Editor.TextArea.Caret.BringCaretToView();
    }

    public void ScrollToLine(int line)
    {
        if (line >= 1 && line <= Editor.Document.LineCount)
        {
            Editor.ScrollToLine(line);
        }
    }

    public void AddCursor(int direction) => _carets?.AddCaretVertical(direction);

    public void ShowFind(bool replace)
    {
        var box = FindBox;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    public void FocusEditor() => Editor.TextArea.Focus();

    public void Fold(FoldAction action)
    {
        if (_folding is null)
        {
            return;
        }

        switch (action)
        {
            case FoldAction.FoldAll:
                foreach (var f in _folding.AllFoldings)
                {
                    f.IsFolded = true;
                }

                break;
            case FoldAction.UnfoldAll:
                foreach (var f in _folding.AllFoldings)
                {
                    f.IsFolded = false;
                }

                break;
            case FoldAction.Fold:
                {
                    // 커서를 품은 접히지 않은 구간 중 가장 안쪽.
                    var target = _folding.GetFoldingsContaining(Editor.TextArea.Caret.Offset)
                        .Where(f => !f.IsFolded)
                        .OrderByDescending(f => f.StartOffset)
                        .FirstOrDefault();
                    if (target is not null)
                    {
                        target.IsFolded = true;
                    }

                    break;
                }

            default:
                {
                    var target = _folding.GetFoldingsContaining(Editor.TextArea.Caret.Offset)
                        .Where(f => f.IsFolded)
                        .OrderByDescending(f => f.StartOffset)
                        .FirstOrDefault();
                    if (target is not null)
                    {
                        target.IsFolded = false;
                    }

                    break;
                }
        }
    }
}
