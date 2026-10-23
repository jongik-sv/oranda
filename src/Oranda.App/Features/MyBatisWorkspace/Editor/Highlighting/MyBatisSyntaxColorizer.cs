using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Editor.Highlighting;

/// <summary>
/// MyBatis 문장 원문(xml 조각 + SQL)에 강조를 입힌다(MB-04). <see cref="SqlSyntaxColorizer"/> 를 대신 쓰는 변환기다.
/// 줄 시작 상태 배열을 들고, 문서가 바뀌면 바뀐 줄부터 줄 끝 상태가 예전과 같아질 때까지만 다시 분류한다. 상태가 아래 줄로 번졌으면 그 줄들을 다시 그린다.
/// 줄 안 구간은 그릴 때 다시 분류한다(분류는 한 줄 단위로 가볍고, 그릴 줄은 화면에 보이는 줄뿐이다).
/// 색은 SQL 구간이 <see cref="SqlSyntaxColorizer"/> 의 팔레트, xml 구간이 테마 키(아이콘 톤 브러시)다. 테마가 바뀌면 다시 찾는다.
/// </summary>
public sealed class MyBatisSyntaxColorizer : DocumentColorizingTransformer, IDisposable
{
    private readonly TextEditor _editor;
    private readonly TextDocument _doc;
    private readonly List<MyBatisLineState> _starts = [];
    private readonly List<MyBatisSpan> _spans = [];
    private IBrush?[]? _brushes;
    private int _changeFirstLine;
    private int _changeOldLastLine;
    private bool _disposed;

    public MyBatisSyntaxColorizer(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _doc = editor.Document;
        ClassifyAllLines();
        _doc.Changing += OnChanging;
        _doc.Changed += OnChanged;
        editor.ActualThemeVariantChanged += OnThemeChanged;
        editor.TextArea.TextView.LineTransformers.Add(this);
    }

    /// <summary>줄마다 시작 상태. 마지막 항목은 글 끝 상태라 항목 수 = 줄 수 + 1 이다(시험·계측용).</summary>
    public IReadOnlyList<MyBatisLineState> LineStartStates => _starts;

    /// <summary>마지막 편집에서 다시 분류한 줄 수(시험·계측용).</summary>
    public int LastReclassifiedLines { get; private set; }

    /// <summary>구간 종류에 지금 테마에서 입힐 브러시(시험·계측용). 못 찾은 종류는 null.</summary>
    internal IBrush? BrushOf(MyBatisSpanKind kind) => (_brushes ??= ResolveBrushes())[(int)kind];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _doc.Changing -= OnChanging;
        _doc.Changed -= OnChanged;
        _editor.ActualThemeVariantChanged -= OnThemeChanged;
        _editor.TextArea.TextView.LineTransformers.Remove(this);
    }

    private void ClassifyAllLines()
    {
        _starts.Clear();
        var state = MyBatisLineState.Start;
        _starts.Add(state);
        for (int n = 1; n <= _doc.LineCount; n++)
        {
            state = ClassifyLineAt(n - 1, state);
            _starts.Add(state);
        }
    }

    /// <summary>0 기반 줄 <paramref name="index"/> 를 분류해 <see cref="_spans"/> 에 담고 줄 끝 상태를 돌려준다.</summary>
    private MyBatisLineState ClassifyLineAt(int index, MyBatisLineState start)
    {
        var line = _doc.GetLineByNumber(index + 1);
        _spans.Clear();
        return MyBatisXmlClassifier.ClassifyLine(_doc.GetText(line.Offset, line.Length), start, _spans);
    }

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        _changeFirstLine = _doc.GetLineByOffset(e.Offset).LineNumber;
        _changeOldLastLine = _doc.GetLineByOffset(Math.Min(e.Offset + e.RemovalLength, _doc.TextLength)).LineNumber;
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        int newLast = _doc.GetLineByOffset(Math.Min(e.Offset + e.InsertionLength, _doc.TextLength)).LineNumber;
        int oldCount = _changeOldLastLine - _changeFirstLine + 1;
        int newCount = newLast - _changeFirstLine + 1;
        int first0 = _changeFirstLine - 1;

        // 바뀐 줄 구간의 시작 상태(첫 줄 것은 그대로, 둘째 줄부터 마지막 줄 끝까지)를 새 줄 수만큼 갈아 끼운다.
        // 구간 바로 뒤 항목(바뀌지 않은 첫 줄의 시작 상태)은 옛 값 그대로 남아, 아래에서 상태가 같아졌는지 맞대는 기준이 된다.
        _starts.RemoveRange(first0 + 1, oldCount - 1);
        _starts.InsertRange(first0 + 1, new MyBatisLineState[newCount - 1]);

        int index = first0;
        int count = 0;
        while (index < _doc.LineCount)
        {
            var end = ClassifyLineAt(index, _starts[index]);
            count++;

            // 바뀐 구간의 마지막 줄부터는 줄 끝 상태가 옛 값(남겨 둔 항목)과 같아지면 아래 줄은 그대로라 멈춘다.
            if (index >= newLast - 1 && _starts[index + 1] == end)
            {
                break;
            }

            _starts[index + 1] = end;
            index++;
        }

        LastReclassifiedLines = count;

        // 편집한 줄 밖까지 상태가 번졌으면(예: 여는 xml 주석) 그 줄들도 다시 그린다. 편집한 줄은 TextView 가 알아서 다시 그린다.
        int last = first0 + count; // 다시 분류한 마지막 줄(1 기반)
        if (last > newLast)
        {
            var a = _doc.GetLineByNumber(newLast);
            var b = _doc.GetLineByNumber(Math.Min(last, _doc.LineCount));
            _editor.TextArea.TextView.Redraw(a.Offset, b.EndOffset - a.Offset);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _brushes = null;
        _editor.TextArea.TextView.Redraw();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int index = line.LineNumber - 1;
        if (index >= _starts.Count - 1)
        {
            return;
        }

        var brushes = _brushes ??= ResolveBrushes();
        ClassifyLineAt(index, _starts[index]);
        foreach (var span in _spans)
        {
            if (brushes[(int)span.Kind] is not { } brush)
            {
                continue;
            }

            int start = line.Offset + span.Start;
            int end = Math.Min(start + span.Length, line.EndOffset);
            if (end > start)
            {
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    /// <summary>구간 종류 → 브러시(종류 값이 색인). SQL 구간은 SQL 편집기 팔레트, xml 구간은 테마 키. 못 찾은 종류는 null(색 없음).</summary>
    private IBrush?[] ResolveBrushes()
    {
        // 창에 붙기 전의 편집기는 변형이 null 일 수 있어 앱 변형으로 대신한다.
        ThemeVariant? variant = _editor.ActualThemeVariant ?? Application.Current?.ActualThemeVariant;
        var palette = IsDark(variant) ? SqlSyntaxColorizer.Dark : SqlSyntaxColorizer.Light;
        var purple = Find("OrandaIconPurpleBrush", variant);
        var neutral = Find("OrandaIconNeutralBrush", variant);
        var teal = Find("OrandaIconTealBrush", variant);
        var orange = Find("OrandaIconOrangeBrush", variant);

        var map = new IBrush?[Enum.GetValues<MyBatisSpanKind>().Length];
        map[(int)MyBatisSpanKind.XmlTag] = purple;
        map[(int)MyBatisSpanKind.XmlAttrName] = neutral;
        map[(int)MyBatisSpanKind.XmlAttrValue] = palette.String;
        map[(int)MyBatisSpanKind.XmlComment] = palette.Comment;
        map[(int)MyBatisSpanKind.XmlCData] = neutral;
        map[(int)MyBatisSpanKind.XmlEntity] = teal;
        map[(int)MyBatisSpanKind.Param] = teal;
        map[(int)MyBatisSpanKind.Substitution] = orange;
        map[(int)MyBatisSpanKind.SqlKeyword] = palette.Keyword;
        map[(int)MyBatisSpanKind.SqlString] = palette.String;
        map[(int)MyBatisSpanKind.SqlComment] = palette.Comment;
        map[(int)MyBatisSpanKind.SqlNumber] = palette.Number;
        map[(int)MyBatisSpanKind.SqlBind] = palette.Bind;
        map[(int)MyBatisSpanKind.SqlHint] = palette.Hint;
        map[(int)MyBatisSpanKind.SqlFunction] = palette.Function;
        map[(int)MyBatisSpanKind.SqlQuoted] = palette.Quoted;
        return map;
    }

    /// <summary>어두운 테마인가. 고대비 변형은 어두운 테마를 바탕으로 한다(ThemeService.HighContrastVariant). 창에 붙기 전에는 변형이 null 일 수 있다.</summary>
    internal static bool IsDark(ThemeVariant? variant) =>
        variant is not null && (variant == ThemeVariant.Dark || variant.InheritVariant == ThemeVariant.Dark);

    private IBrush? Find(string key, ThemeVariant? variant)
    {
        // 창에 붙기 전의 편집기는 앱 리소스까지 닿지 못할 수 있어 앱에서 한 번 더 찾는다. 못 찾으면 색을 입히지 않는다.
        if (_editor.TryFindResource(key, variant, out var found) && found is IBrush brush)
        {
            return brush;
        }

        return Application.Current is { } app && app.TryGetResource(key, variant, out found) && found is IBrush appBrush ? appBrush : null;
    }
}
