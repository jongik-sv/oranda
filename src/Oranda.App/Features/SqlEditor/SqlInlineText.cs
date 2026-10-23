using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 한 줄 SQL 글을 편집기 강조와 같은 렉서·색으로 칠해 <see cref="TextBlock.Inlines"/> 에 넣는 붙임 속성(조합 검사 표의 펼친 SQL 칸·실행 이력 목록의 SQL 칸).
/// 줄마다 편집기를 두지 않고 Run 만 만든다. 같은 글은 토큰화 결과를 모아 두어 목록이 줄을 다시 쓸 때(가상화) 다시 렉싱하지 않는다.
/// 줄바꿈이 없는 글만 다룬다(여러 줄 주석·문자열이 줄 밖으로 번지는 경우는 한 줄 기준으로만 분류한다).
/// </summary>
public static class SqlInlineText
{
    private const int CacheLimit = 2048;

    public static readonly AttachedProperty<string?> SqlProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Sql", typeof(SqlInlineText));

    private static readonly Dictionary<string, Segment[]> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<TextBlock> ThemeHooked = [];
    private static int _tokenizations;

    static SqlInlineText()
    {
        SqlProperty.Changed.AddClassHandler<TextBlock>((box, _) => Rebuild(box));
    }

    /// <summary>지금까지 렉서를 돌린 횟수(시험·계측용). 같은 글을 다시 쓰면 늘지 않는다.</summary>
    internal static int Tokenizations => Volatile.Read(ref _tokenizations);

    public static string? GetSql(TextBlock box) => box.GetValue(SqlProperty);

    public static void SetSql(TextBlock box, string? value) => box.SetValue(SqlProperty, value);

    private enum Kind
    {
        Plain,
        Keyword,
        String,
        Comment,
        Number,
        Bind,
        Hint,
        Function,
        Quoted,
    }

    private readonly record struct Segment(int Start, int Length, Kind Kind);

    private static void Rebuild(TextBlock box)
    {
        if (ThemeHooked.Add(box))
        {
            // 테마가 바뀌면 같은 글을 새 팔레트로 다시 칠한다. 상자가 사라지면 같이 정리한다.
            void OnTheme(object? s, EventArgs e) => Rebuild(box);
            void OnDetached(object? s, VisualTreeAttachmentEventArgs e)
            {
                box.ActualThemeVariantChanged -= OnTheme;
                box.DetachedFromVisualTree -= OnDetached;
                ThemeHooked.Remove(box);
            }

            box.ActualThemeVariantChanged += OnTheme;
            box.DetachedFromVisualTree += OnDetached;
        }

        box.Inlines?.Clear();
        var text = GetSql(box);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var palette = box.ActualThemeVariant == ThemeVariant.Dark ? SqlSyntaxColorizer.Dark : SqlSyntaxColorizer.Light;
        box.Inlines ??= [];
        foreach (var seg in SegmentsOf(text))
        {
            var run = new Avalonia.Controls.Documents.Run(text.Substring(seg.Start, seg.Length));
            if (BrushOf(seg.Kind, palette) is { } brush)
            {
                run.Foreground = brush;
            }

            box.Inlines.Add(run);
        }
    }

    private static IBrush? BrushOf(Kind kind, SqlSyntaxColorizer.Palette p) => kind switch
    {
        Kind.Keyword => p.Keyword,
        Kind.String => p.String,
        Kind.Comment => p.Comment,
        Kind.Number => p.Number,
        Kind.Bind => p.Bind,
        Kind.Hint => p.Hint,
        Kind.Function => p.Function,
        Kind.Quoted => p.Quoted,
        _ => null,
    };

    private static Segment[] SegmentsOf(string text)
    {
        if (Cache.TryGetValue(text, out var hit))
        {
            return hit;
        }

        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        return Cache[text] = Classify(text);
    }

    // 렉서 토큰을 칠할 종류로 옮기고, 토큰 사이 빈틈(공백·연산자)은 Plain 으로 채워 글 전체를 빠짐없이 덮는다.
    private static Segment[] Classify(string text)
    {
        Interlocked.Increment(ref _tokenizations);
        var segments = new List<Segment>();
        int pos = 0;
        foreach (var t in SqlLexer.Tokenize(text))
        {
            var kind = SqlTokenColors.Of(t, text) switch
            {
                SqlColorKind.Keyword => Kind.Keyword,
                SqlColorKind.StringLiteral => Kind.String,
                SqlColorKind.Comment => Kind.Comment,
                SqlColorKind.Number => Kind.Number,
                SqlColorKind.Bind => Kind.Bind,
                SqlColorKind.Hint => Kind.Hint,
                SqlColorKind.Quoted => Kind.Quoted,
                SqlColorKind.Function => Kind.Function,
                _ => Kind.Plain,
            };
            int start = Math.Max(t.Start, pos);
            int end = Math.Min(t.End, text.Length);
            if (end <= start)
            {
                continue;
            }

            if (start > pos)
            {
                Add(segments, pos, start - pos, Kind.Plain);
            }

            Add(segments, start, end - start, kind);
            pos = end;
        }

        if (pos < text.Length)
        {
            Add(segments, pos, text.Length - pos, Kind.Plain);
        }

        return [.. segments];
    }

    // 같은 종류가 이어지면 Run 하나로 합친다.
    private static void Add(List<Segment> list, int start, int length, Kind kind)
    {
        if (list.Count > 0 && list[^1].Kind == kind && list[^1].Start + list[^1].Length == start)
        {
            list[^1] = list[^1] with { Length = list[^1].Length + length };
            return;
        }

        list.Add(new Segment(start, length, kind));
    }
}
