using Oranda.Core.Composition;
using Oranda.Core.Extensibility;
using Oranda.Core.Language;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// <see cref="IEditorContext"/> 구현(APP-09, TRD 4.10). 활성 SQL 편집기 탭을 읽고, 제안 편집은 차이 미리보기를 거쳐
/// 사용자가 받아들일 때만 문서에 넣는다(실행 취소 1단계). 실행은 이 객체가 하지 않는다.
/// UI 스레드에서 호출한다.
/// </summary>
[OrandaService(typeof(IEditorContext))]
public sealed class EditorContext : IEditorContext
{
    private SqlEditorHost? _host;

    // 생성자는 매개변수 없이 둔다. 셸의 생성자 고르기(ServiceContainer.Create)는 등록되지 않은 참조 형식 매개변수를
    // 만나면 지연 어셈블리를 훑어 시작 때 Oranda.Oracle·Oranda.Language 를 로드한다(D-006, 시작 시험이 막는다).
    // 호스트를 바꿔야 하는 시험은 <see cref="Host"/> 를 초기화 구문으로 준다.

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    public EditorSnapshot? GetActiveDocument()
    {
        var tab = Host.Active;
        if (tab is null)
        {
            return null;
        }

        return new EditorSnapshot(tab.TabId, tab.Text, new TextRange(tab.SelectionStart, tab.SelectionLength), tab.CaretOffset)
        {
            Title = tab.Title,
            ConnectionProfileId = tab.ConnectionProfileId,
        };
    }

    public SqlStatement? GetCurrentStatement() => Host.Active?.GetStatementAtCaret();

    public async Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var tab = Host.Active;
        if (tab is null || tab.IsReadOnly || !edit.Range.IsWithin(tab.Document.TextLength))
        {
            return false;
        }

        var versionAtPreview = tab.Version;
        var before = tab.Document.GetText(edit.Range.Start, edit.Range.Length);
        var accepted = await Host.Dialogs
            .PreviewEditAsync(edit.Description ?? "제안된 편집", before, edit.NewText, ct)
            .ConfigureAwait(true);
        if (!accepted)
        {
            return false;
        }

        // 미리보기를 보는 사이 문서가 바뀌었으면 범위가 어긋났을 수 있으니 적용하지 않는다.
        if (tab.Version != versionAtPreview || !Host.Tabs.Contains(tab))
        {
            return false;
        }

        return tab.Apply(new TextOpResult(edit.Range.Start, edit.Range.Length, edit.NewText, edit.Range.Start, edit.NewText.Length));
    }
}
