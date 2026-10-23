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
        int start = tab.SelectionStart;
        var sel = tab.Document.GetText(start, tab.SelectionLength);
        var parts = NonEmpty(lang.Split(sel, Options));
        return [.. parts.Select(p => p with { Range = new TextRange(p.Range.Start + start, p.Range.Length) })];
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
