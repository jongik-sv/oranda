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

// ExecutionController 의 일부: 바인드(ExecutionController.cs 에서 나눔, 동작 불변).
public sealed partial class ExecutionController
{
    // ---- 바인드 ----------------------------------------------------------------------------

    /// <summary>탭의 바인드 목록(탭마다 하나, TSK-04-11). 탭이 없으면 도구 창용 기본 목록.</summary>
    private BindVariableStore BindsOf(SqlEditorViewModel? tab) => tab?.Binds ?? _d.Binds;

    /// <summary>
    /// 바인드 변수 창(<c>oranda.exec.bindVariables</c>): 창을 보이고, 커서 문장의 바인드 이름을 목록에 채워 첫 값 칸으로 간다.
    /// 실행은 하지 않는다(오렌지 Bind Variable List).
    /// </summary>
    public Task<bool> EditBindsAsync(SqlEditorViewModel? tab = null, CancellationToken ct = default)
    {
        tab ??= Host.Active;
        _d.ActivatePanel(BindsPanelId);
        if (tab?.GetStatementAtCaret() is not { BindNames.Count: > 0 } stmt)
        {
            Log.AddMessage(MessageKind.Info, "현재 문장에 바인드 변수가 없습니다. 「바인드 변수」 창에서 직접 더할 수 있습니다.");
            return Task.FromResult(false);
        }

        var binds = BindsOf(tab);
        binds.Prune(tab.ReferencedBindNames);
        binds.Ensure(stmt.BindNames);
        binds.RequestFocus(stmt.BindNames[0]);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 실행할 바인드 값을 「바인드 변수」 목록에서 만든다(묻는 창 없음). 처음 보는 이름은 목록에 더하고 실행하지 않는다:
    /// 값 없이 실행하면 INSERT·UPDATE 가 NULL 로 돌 수 있어서다. 형식에 맞지 않는 값도 실행하지 않는다.
    /// </summary>
    private Dictionary<string, BindValue>? ResolveBinds(SqlEditorViewModel tab, IEnumerable<string> names, ResultTabsViewModel? results)
    {
        var store = BindsOf(tab);
        // 탭 글에서 사라진 이름(고치기 전 :EMP 등)은 목록에서 뺀다(TSK-04-12).
        store.Prune(tab.ReferencedBindNames.Concat(names));

        var list = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        }

        string? problem = null;
        var added = store.Ensure(list);
        if (added.Count > 0)
        {
            problem = $"바인드 변수 {string.Join(", ", added.Select(n => ":" + n))} 의 값을 「바인드 변수」 창에 넣은 뒤 다시 실행하세요. (빈 값은 NULL)";
            store.RequestFocus(added[0]);
        }
        else if (!store.TryBuild(list, out var values, out var error))
        {
            problem = error ?? "바인드 값을 읽을 수 없습니다.";
        }
        else
        {
            return values;
        }

        _d.ActivatePanel(BindsPanelId);
        Log.AddMessage(added.Count > 0 ? MessageKind.Warning : MessageKind.Error, problem);
        if (results is not null)
        {
            results.Clear();
            results.AddMessage("바인드 변수", problem, isError: added.Count == 0);
        }

        return null;
    }

    /// <summary>
    /// 편집기의 바인드 변수를 목록의 값으로 바꿔 넣는다(오렌지 「Paste Bind Variables Into Editor」, <c>oranda.exec.pasteBindValues</c>).
    /// 렉서의 바인드 토큰만 바꾼다(문자열·주석 안은 그대로). 목록에 없거나 값을 읽을 수 없는 변수는 남긴다. 한 번에 실행 취소된다.
    /// </summary>
    public int PasteBindValues(SqlEditorViewModel? tab = null)
    {
        tab ??= Host.Active;
        if (tab is null || tab.IsReadOnly)
        {
            return 0;
        }

        var text = tab.Text;
        var binds = BindsOf(tab);
        var edits = new List<(int Start, int Length, string Literal)>();
        foreach (var t in Oranda.Language.Lexing.SqlLexer.Tokenize(text))
        {
            if (t.Kind != Oranda.Language.Lexing.TokenKind.Bind || t.Length < 2)
            {
                continue;
            }

            var name = text.Substring(t.Start + 1, t.Length - 1);
            if (binds.Find(name) is { } row && BindVariableStore.ToLiteral(row) is { } literal)
            {
                edits.Add((t.Start, t.Length, literal));
            }
        }

        if (edits.Count == 0)
        {
            Log.AddMessage(MessageKind.Info, "바꿀 바인드 변수가 없습니다(목록에 값이 있는 변수만 바꿉니다).");
            return 0;
        }

        var doc = tab.Document;
        doc.BeginUpdate();
        try
        {
            for (int i = edits.Count - 1; i >= 0; i--)
            {
                doc.Replace(edits[i].Start, edits[i].Length, edits[i].Literal);
            }
        }
        finally
        {
            doc.EndUpdate();
        }

        Log.AddMessage(MessageKind.Info, $"바인드 변수 {edits.Count.ToString(CultureInfo.InvariantCulture)}곳을 값으로 바꿨습니다.");
        return edits.Count;
    }
}
