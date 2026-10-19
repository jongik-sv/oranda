using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.Core.Extensibility;

/// <summary>
/// AI 확장 지점(TRD 4.10): 활성 편집기 읽기와 제안 삽입. 제안은 차이 미리보기를 거쳐 사용자가 받아들일 때만 들어간다.
/// 실행은 이 인터페이스가 아니라 <c>IExecutionService</c> 로만. 구현: Oranda.App(TSK-04-02, UI 계약 문서).
/// </summary>
public interface IEditorContext
{
    /// <summary>활성 편집기 탭. 없으면 null.</summary>
    EditorSnapshot? GetActiveDocument();

    /// <summary>커서 위치의 문장. 문서가 없거나 문장 밖이면 null.</summary>
    SqlStatement? GetCurrentStatement();

    /// <summary>차이 미리보기 후 사용자가 받아들이면 바꾸고 true.</summary>
    Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct);
}

/// <summary>활성 문서의 한 시점 모습.</summary>
public sealed record EditorSnapshot(Guid TabId, string Text, TextRange Selection, int CaretOffset)
{
    public string? Title { get; init; }

    public Guid? ConnectionProfileId { get; init; }

    public Guid? SessionId { get; init; }
}

/// <summary>바꿀 범위와 새 텍스트. <see cref="Description"/> 은 미리보기 창 제목.</summary>
public sealed record TextEdit(TextRange Range, string NewText)
{
    public string? Description { get; init; }
}
