using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace EditorSpike.L1;

/// <summary>AvaloniaEdit TextDocument 를 줄 원본으로 쓰고 문서 변경마다 증분 렉싱한 뒤 바뀐 줄을 다시 그린다.</summary>
public sealed class L1Colorizer : DocumentColorizingTransformer, ILineSource
{
    readonly TextEditor _editor;
    readonly TextDocument _doc;
    public IncrementalLexer Lexer { get; }
    int _chgFirst, _chgOldLast;

    public long LastRelexTicks { get; private set; }
    public int LastRelexLines { get; private set; }

    static readonly IBrush KwBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x00, 0x33, 0xB3));
    static readonly IBrush StrBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x06, 0x7D, 0x17));
    static readonly IBrush ComBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));
    static readonly IBrush NumBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x17, 0x50, 0xEB));
    static readonly IBrush BindBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x87, 0x10, 0x94));
    static readonly IBrush HintBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x9E, 0x88, 0x0D));

    public L1Colorizer(TextEditor editor)
    {
        _editor = editor;
        _doc = editor.Document;
        Lexer = new IncrementalLexer(this);
        Lexer.LexAll();
        _doc.Changing += (_, e) =>
        {
            _chgFirst = _doc.GetLineByOffset(e.Offset).LineNumber;
            _chgOldLast = _doc.GetLineByOffset(e.Offset + e.RemovalLength).LineNumber;
        };
        _doc.Changed += (_, e) =>
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int newLast = _doc.GetLineByOffset(e.Offset + e.InsertionLength).LineNumber;
            int first0 = _chgFirst - 1;
            var (relexed, last0) = Lexer.ApplyEdit(first0, _chgOldLast - _chgFirst + 1, newLast - _chgFirst + 1);
            LastRelexTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            LastRelexLines = relexed;
            // 편집 줄 밖까지 상태가 번졌으면 그 줄들도 다시 그린다(편집 줄은 TextView 가 알아서 다시 그림).
            if (last0 + 1 > newLast)
            {
                var a = _doc.GetLineByNumber(newLast);
                var b = _doc.GetLineByNumber(last0 + 1);
                _editor.TextArea.TextView.Redraw(a.Offset, b.EndOffset - a.Offset);
            }
        };
        editor.TextArea.TextView.LineTransformers.Add(this);
    }

    int ILineSource.LineCount => _doc.LineCount;
    string ILineSource.GetLine(int index) => _doc.GetText(_doc.GetLineByNumber(index + 1));

    protected override void ColorizeLine(DocumentLine line)
    {
        int idx = line.LineNumber - 1;
        if (idx >= Lexer.LineCount) return;
        foreach (var t in Lexer.TokensOf(idx))
        {
            IBrush? b = t.Kind switch
            {
                TokKind.Keyword => KwBrush,
                TokKind.String => StrBrush,
                TokKind.Comment => ComBrush,
                TokKind.Number => NumBrush,
                TokKind.Bind => BindBrush,
                TokKind.Hint => HintBrush,
                _ => null,
            };
            if (b is null) continue;
            int s = line.Offset + t.Start, e = Math.Min(s + t.Length, line.EndOffset);
            if (e <= s) continue;
            ChangeLinePart(s, e, el => el.TextRunProperties.SetForegroundBrush(b));
        }
    }
}
