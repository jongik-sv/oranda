using System.Text;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Workspaces;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisLog;

/// <summary>붙여 넣은 결과: 넣은 문장 수와 바인드 수.</summary>
public sealed record MyBatisLogPasteResult(int Statements, int Binds);

/// <summary>
/// 「MyBatis 로그 붙여 넣기」(TSK-13-03)의 편집기·바인드 창 쪽. 로그 글을 <see cref="MyBatisLogParser"/> 로 읽어
/// 바인드 변수를 쓴 SQL 을 커서 위치(선택이 있으면 선택을 대신해서)에 넣고, 값과 타입은 바인드 창에 채운다.
/// 문장이 여럿이면 각각 <c>;</c> 로 끝내 빈 줄로 나눈다. 한 번에 실행 취소된다.
/// </summary>
public static class MyBatisLogPaste
{
    /// <summary>로그에서 쌍을 못 찾았거나 편집기가 읽기 전용이면 null.</summary>
    public static MyBatisLogPasteResult? Apply(SqlEditorViewModel tab, BindVariableStore binds, string logText)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(binds);
        ArgumentNullException.ThrowIfNull(logText);
        if (tab.IsReadOnly)
        {
            return null;
        }

        var statements = MyBatisLogParser.Parse(logText);
        if (statements.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        var restored = new List<WorkspaceBind>();
        foreach (var s in statements)
        {
            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append(s.Sql.TrimEnd().TrimEnd(';'));
            if (statements.Count > 1)
            {
                sb.Append(';');
            }

            restored.AddRange(s.Params.Select(p => new WorkspaceBind { Name = p.Name, Type = p.Type, Value = p.Value, IsNull = p.IsNull }));
        }

        int start = tab.SelectionLength > 0 ? tab.SelectionStart : tab.CaretOffset;
        int length = tab.SelectionLength > 0 ? tab.SelectionLength : 0;
        var text = sb.ToString();
        tab.Document.Replace(start, length, text);
        tab.Select(start + text.Length, 0);
        binds.Restore(restored);
        return new MyBatisLogPasteResult(statements.Count, restored.Count);
    }
}
