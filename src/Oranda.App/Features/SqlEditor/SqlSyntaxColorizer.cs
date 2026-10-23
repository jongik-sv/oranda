using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// L1 렉서(<see cref="IncrementalLexer"/>) 결과로 구문 강조를 입힌다(EDIT-02). 문서가 바뀔 때마다 바뀐 줄부터 다시 렉싱하고,
/// 상태(주석·문자열 열림)가 아래 줄로 번졌으면 그 줄들만 다시 그린다. TSK-01-05 측정: 1만 줄에서 문자 1개 입력 ≈ 0.3 µs.
/// </summary>
public sealed class SqlSyntaxColorizer : DocumentColorizingTransformer
{
    internal sealed record Palette(IBrush Keyword, IBrush String, IBrush Comment, IBrush Number, IBrush Bind, IBrush Hint, IBrush Function, IBrush Quoted);

    internal static readonly Palette Light = new(
        Brush(0x00, 0x33, 0xB3), Brush(0x06, 0x7D, 0x17), Brush(0x8C, 0x8C, 0x8C), Brush(0x17, 0x50, 0xEB),
        Brush(0x87, 0x10, 0x94), Brush(0x9E, 0x88, 0x0D), Brush(0x00, 0x62, 0x7A), Brush(0x1B, 0x6A, 0x6A));

    internal static readonly Palette Dark = new(
        Brush(0x56, 0x9C, 0xD6), Brush(0x6A, 0xAB, 0x73), Brush(0x7A, 0x7E, 0x85), Brush(0x6B, 0x9B, 0xFA),
        Brush(0xC7, 0x8D, 0xE0), Brush(0xD9, 0xC1, 0x4F), Brush(0x56, 0xB6, 0xC2), Brush(0x7F, 0xC7, 0xC7));

    // 줄 원본은 문서 줄의 복사본(ListLineSource)이다. 렉서가 요구하는 ILineSource 를 이 형식이 직접 구현하면
    // 셸이 App 어셈블리의 모든 형식을 훑을 때 Oranda.Language 가 시작 때 로드된다(D-006, 시작 시험이 막는다).
    private readonly ListLineSource _lines;
    private readonly TextEditor _editor;
    private readonly TextDocument _doc;
    private int _changeFirstLine;
    private int _changeOldLastLine;

    public SqlSyntaxColorizer(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _doc = editor.Document;
        _lines = new ListLineSource(Enumerable.Range(1, _doc.LineCount).Select(n => _doc.GetText(_doc.GetLineByNumber(n))));
        Lexer = new IncrementalLexer(_lines);
        Lexer.LexAll();
        _doc.Changing += OnChanging;
        _doc.Changed += OnChanged;
        editor.TextArea.TextView.LineTransformers.Add(this);
    }

    public IncrementalLexer Lexer { get; }

    /// <summary>마지막 편집에서 다시 렉싱한 줄 수(시험·계측용).</summary>
    public int LastRelexedLines { get; private set; }

    public void Detach()
    {
        _doc.Changing -= OnChanging;
        _doc.Changed -= OnChanged;
        _editor.TextArea.TextView.LineTransformers.Remove(this);
    }

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        _changeFirstLine = _doc.GetLineByOffset(e.Offset).LineNumber;
        _changeOldLastLine = _doc.GetLineByOffset(Math.Min(e.Offset + e.RemovalLength, _doc.TextLength)).LineNumber;
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        int newLast = _doc.GetLineByOffset(Math.Min(e.Offset + e.InsertionLength, _doc.TextLength)).LineNumber;
        SyncLines(_changeFirstLine - 1, _changeOldLastLine - _changeFirstLine + 1, newLast - _changeFirstLine + 1);
        var (relexed, last0) = Lexer.ApplyEdit(_changeFirstLine - 1, _changeOldLastLine - _changeFirstLine + 1, newLast - _changeFirstLine + 1);
        LastRelexedLines = relexed;

        // 편집한 줄 밖까지 상태가 번졌으면(예: 여는 주석) 그 줄들도 다시 그린다. 편집한 줄은 TextView 가 알아서 다시 그린다.
        if (last0 + 1 > newLast)
        {
            var a = _doc.GetLineByNumber(newLast);
            var b = _doc.GetLineByNumber(Math.Min(last0 + 1, _doc.LineCount));
            _editor.TextArea.TextView.Redraw(a.Offset, b.EndOffset - a.Offset);
        }
    }

    /// <summary>줄 복사본에서 [first, first+oldCount) 를 문서의 새 줄 newCount 개로 바꾼다.</summary>
    private void SyncLines(int first, int oldCount, int newCount)
    {
        var fresh = new string[newCount];
        for (int i = 0; i < newCount; i++)
        {
            fresh[i] = _doc.GetText(_doc.GetLineByNumber(first + 1 + i));
        }

        _lines.Lines.RemoveRange(first, oldCount);
        _lines.Lines.InsertRange(first, fresh);
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int idx = line.LineNumber - 1;
        if (idx >= Lexer.LineCount)
        {
            return;
        }

        var palette = _editor.ActualThemeVariant == ThemeVariant.Dark ? Dark : Light;
        foreach (var t in Lexer.TokensOf(idx))
        {
            IBrush? brush = t.Kind switch
            {
                TokenKind.Keyword => palette.Keyword,
                TokenKind.StringLiteral => palette.String,
                TokenKind.Comment => palette.Comment,
                TokenKind.Number => palette.Number,
                TokenKind.Bind or TokenKind.Substitution => palette.Bind,
                TokenKind.Hint => palette.Hint,
                TokenKind.QuotedIdent => palette.Quoted,
                TokenKind.Identifier when IsFunctionCall(line, t) => palette.Function,
                _ => null,
            };
            if (brush is null)
            {
                continue;
            }

            int start = line.Offset + t.Start;
            int end = Math.Min(start + t.Length, line.EndOffset);
            if (end > start)
            {
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    /// <summary>식별자 바로 뒤(공백 허용)에 여는 괄호가 오면 함수 호출로 본다.</summary>
    private bool IsFunctionCall(DocumentLine line, Token t)
    {
        int pos = line.Offset + t.End;
        while (pos < line.EndOffset && _doc.GetCharAt(pos) == ' ')
        {
            pos++;
        }

        return pos < line.EndOffset && _doc.GetCharAt(pos) == '(';
    }

    private static ImmutableSolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
