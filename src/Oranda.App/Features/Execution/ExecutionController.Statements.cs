using System.Globalization;
using System.Text;
using Oranda.App.Features.Binds;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Features.Execution;

// ExecutionController 의 일부: 문장 고르기(ExecutionController.cs 에서 나눔, 동작 불변).
public sealed partial class ExecutionController
{
    // ---- 문장 고르기 -------------------------------------------------------------------------

    private SplitOptions Options => new() { SplitOnBlankLine = State.BlankLineDelimiter };

    /// <summary>실행 범위의 문장 목록. 두 번째 값이 참이면 스크립트 실행(문장이 하나여도 스크립트로 취급하는 경우).</summary>
    public (IReadOnlyList<SqlStatement> Statements, bool Script) SelectStatements(SqlEditorViewModel tab, RunMode mode)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var lang = _d.Language();
        var text = tab.Text;
        if (mode == RunMode.Subquery && tab.HasMultiPieceSelection)
        {
            mode = RunMode.Selection; // 열 블록·여러 커서 선택은 하위 쿼리를 가르지 않고 선택된 글만 실행한다
        }

        if (mode == RunMode.Subquery)
        {
            if (FindSubquery(lang, tab) is { } sub)
            {
                return ([sub], false);
            }

            mode = RunMode.Statement; // 하위 쿼리 밖: 현재 문장 실행과 같다
        }

        switch (mode)
        {
            case RunMode.Script:
                return (NonEmpty(lang.Split(text, Options)), true);

            case RunMode.Selection:
                return (SplitSelection(lang, tab), false);

            case RunMode.Statement when tab.HasSelection:
                return (SplitSelection(lang, tab), false);

            case RunMode.FromCursor:
                {
                    var all = NonEmpty(lang.Split(text, Options));
                    int caret = tab.CaretOffset;
                    int index = -1;
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (all[i].Range.End >= caret)
                        {
                            index = i;
                            break;
                        }
                    }

                    return (index < 0 ? [] : [.. all.Skip(index)], true);
                }

            default:
                {
                    var one = lang.GetStatementAt(text, tab.CaretOffset, Options);
                    return (one is null || string.IsNullOrWhiteSpace(one.Text) ? [] : [one], false);
                }
        }
    }

    private List<SqlStatement> SplitSelection(ILanguageService lang, SqlEditorViewModel tab)
    {
        var pieces = tab.SelectionPieces;
        if (pieces.Count > 1)
        {
            return SplitPieces(lang, tab, pieces);
        }

        int start = pieces.Count == 1 ? pieces[0].Start : tab.SelectionStart;
        var sel = tab.Document.GetText(start, pieces.Count == 1 ? pieces[0].Length : tab.SelectionLength);
        var parts = NonEmpty(lang.Split(sel, Options));
        return [.. parts.Select(p => p with { Range = new TextRange(p.Range.Start + start, p.Range.Length) })];
    }

    /// <summary>
    /// 열 블록(또는 여러 커서) 선택: 조각들을 줄바꿈으로 이어 한 글로 만들어 나눈다. 블록 밖 글은 이어 붙이지 않는다.
    /// 문장마다 Range = 걸친 조각들을 감싸는 문서 범위, 실제 글이 놓인 조각은 <see cref="SqlStatement.SourcePieces"/> 에 실어
    /// 실행 범위 강조와 오류 위치가 문서의 같은 글자를 가리키게 한다.
    /// </summary>
    private List<SqlStatement> SplitPieces(ILanguageService lang, SqlEditorViewModel tab, IReadOnlyList<TextRange> pieces)
    {
        var joined = new StringBuilder();
        var joinedStarts = new int[pieces.Count];
        for (int i = 0; i < pieces.Count; i++)
        {
            joinedStarts[i] = joined.Length;
            joined.Append(tab.Document.GetText(pieces[i].Start, pieces[i].Length));
            if (i < pieces.Count - 1)
            {
                joined.Append('\n');
            }
        }

        var result = new List<SqlStatement>();
        // 빈 줄 구분은 끈다: 조각을 이은 글의 빈 줄은 문서의 빈 줄이 아니라서(블록 안이 공백뿐인 줄), 켜 두면 DELETE 와 WHERE 가 따로 실행될 수 있다.
        // 반대로 붙는 쪽은 세미콜론이 없으면 서버가 오류로 거절하므로 안전하다.
        foreach (var part in NonEmpty(lang.Split(joined.ToString(), Options with { SplitOnBlankLine = false })))
        {
            int from = part.Range.Start, to = part.Range.End;
            var map = new List<StatementPiece>();
            for (int i = 0; i < pieces.Count; i++)
            {
                int a = Math.Max(from, joinedStarts[i]);
                int b = Math.Min(to, joinedStarts[i] + pieces[i].Length);
                if (b > a)
                {
                    map.Add(new StatementPiece(a - from, pieces[i].Start + (a - joinedStarts[i]), b - a));
                }
            }

            if (map.Count == 0)
            {
                continue;
            }

            int docStart = map[0].DocumentOffset;
            int docEnd = map[^1].DocumentOffset + map[^1].Length;
            result.Add(part with { Range = new TextRange(docStart, docEnd - docStart), SourcePieces = map });
        }

        return result;
    }

    /// <summary>문장 글이 문서에서 떨어진 조각들로 이루어졌는가(열 블록 실행). 이 문장 글의 위치는 <see cref="DocumentOffset"/> 으로만 문서 위치가 된다.</summary>
    internal static bool HasSourcePieces(SqlStatement stmt)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        return stmt.SourcePieces is not null;
    }

    /// <summary>문장 글 안의 위치(오류 위치 등)가 가리키는 문서 위치. 조각으로 이루어진 문장이면 조각을 따라 옮기고, 아니면 Range.Start 에 더한다.</summary>
    internal static int DocumentOffset(SqlStatement stmt, int textOffset)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        textOffset = Math.Max(0, textOffset);
        if (stmt.SourcePieces is not { Count: > 0 } pieces)
        {
            return stmt.Range.Start + textOffset;
        }

        var hit = pieces[0];
        foreach (var piece in pieces)
        {
            if (piece.TextOffset > textOffset)
            {
                break;
            }

            hit = piece;
        }

        return hit.DocumentOffset + Math.Min(textOffset - hit.TextOffset, hit.Length);
    }

    /// <summary>실행 범위를 편집기에 잠깐 칠한다. 조각으로 이루어진 문장은 조각마다 칠하고(블록 밖 글은 칠하지 않는다), 아니면 첫 문장 시작부터 끝 문장 끝까지 한 범위로 칠한다.</summary>
    private static void Flash(SqlEditorViewModel tab, IReadOnlyList<SqlStatement> statements)
    {
        if (statements.Any(s => s.SourcePieces is not null))
        {
            tab.FlashRanges([.. statements.SelectMany(s => s.SourcePieces is { } pieces
                ? pieces.Select(p => new TextRange(p.DocumentOffset, p.Length))
                : [s.Range])]);
            return;
        }

        Flash(tab, statements[0].Range.Start, statements[^1].Range.End);
    }

    /// <summary>
    /// 커서(선택)를 품은 가장 안쪽 하위 쿼리를 문서 위치를 가진 문장으로 만든다. 본문을 언어 서비스로 다시 나눠
    /// 분류·바인드 이름이 채워진다(바인드는 보통 실행과 같은 목록 값으로 푼다). 없거나 한 문장이 아니면 null.
    /// </summary>
    private SqlStatement? FindSubquery(ILanguageService lang, SqlEditorViewModel tab)
    {
        var text = tab.Text;
        int from = tab.HasSelection ? tab.SelectionStart : tab.CaretOffset;
        int to = from + tab.SelectionLength;
        if (lang.GetStatementAt(text, from, Options) is not { Range: { Length: > 0 } outer } || outer.End > text.Length)
        {
            return null;
        }

        // 현재 문장 안에서만 찾는다(짝이 어긋난 괄호가 문장을 넘지 않게).
        if (SqlStructure.FindSubqueryAt(text.Substring(outer.Start, outer.Length), from - outer.Start, to - outer.Start) is not { } inner)
        {
            return null;
        }

        int start = outer.Start + inner.Start;
        var parts = NonEmpty(lang.Split(text.Substring(start, inner.Length), Options with { SplitOnBlankLine = false }));
        return parts.Count == 1 ? parts[0] with { Range = new TextRange(parts[0].Range.Start + start, parts[0].Range.Length) } : null;
    }

    private static List<SqlStatement> NonEmpty(IEnumerable<SqlStatement> all) => [.. all.Where(s => !string.IsNullOrWhiteSpace(s.Text))];

    private void MoveToNextStatement(SqlEditorViewModel tab, SqlStatement done)
    {
        // 다음 문장이 있으면 그 시작으로, 없으면 현재 자리.
        var next = NonEmpty(_d.Language().Split(tab.Text, Options)).FirstOrDefault(s => s.Range.Start >= done.Range.End);
        if (next is not null)
        {
            tab.Select(next.Range.Start, 0);
        }
    }
}
