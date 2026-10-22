using Oranda.App.Features.SqlEditor;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// 테이블 레이아웃 창 하나의 상태: 표·옵션과 「편집기에 넣기」. 창(View)은 이 클래스만 부른다.
/// 넣은 글은 편집기 실행 취소 한 단계이고 넣은 뒤 커서는 글 끝이다.
/// </summary>
public sealed class TableLayoutSession
{
    private readonly Action<TableLayoutSettings>? _save;

    public TableLayoutSession(SqlEditorViewModel tab, TableLayoutModel model, TableLayoutOptions options, Action<TableLayoutSettings>? save = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        Tab = tab;
        Model = model;
        Options = options;
        _save = save;
    }

    public SqlEditorViewModel Tab { get; }

    public TableLayoutModel Model { get; }

    public TableLayoutOptions Options { get; private set; }

    /// <summary>옵션을 바꾼다. 확인란 4개가 달라졌으면 설정으로 저장한다(별칭만 바뀌면 저장하지 않는다).</summary>
    public void SetOptions(TableLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var changed = options.ToSettings() != Options.ToSettings();
        Options = options;
        if (changed)
        {
            _save?.Invoke(options.ToSettings());
        }
    }

    /// <summary>행들을 주어진 순서대로 편집기 커서 자리(선택이 있으면 선택을 대체)에 넣는다. 넣었으면 true.</summary>
    public bool Paste(IReadOnlyList<TableLayoutRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = TableLayoutPaste.Build(rows, Options);
        if (text.Length == 0)
        {
            return false;
        }

        var length = Tab.Text.Length;
        var start = Math.Clamp(Tab.SelectionLength > 0 ? Tab.SelectionStart : Tab.CaretOffset, 0, length);
        var replace = Math.Clamp(Tab.SelectionLength, 0, length - start);
        var end = start + text.Length;
        var applied = Tab.Apply(new TextOpResult(start, replace, text, end, 0));
        if (applied)
        {
            Tab.Surface?.FocusEditor();
        }

        return applied;
    }
}
