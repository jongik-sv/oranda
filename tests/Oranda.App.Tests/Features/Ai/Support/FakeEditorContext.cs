using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// AI 시험용 편집기 대역: 활성 문서와 커서 문장을 함수로 정해 둔다(안 정하면 문서·문장 없음). 제안(<see cref="ProposeEditAsync"/>)은 기록하지 않고
/// 정해 둔 값(<c>proposeResult</c>, 기본 false = 거절)을 돌려준다. 제안을 기록하거나 글을 바꾸는 시험은 자기 대역을 따로 둔다.
/// </summary>
internal sealed class FakeEditorContext(Func<EditorSnapshot?>? document = null, Func<SqlStatement?>? statement = null, bool proposeResult = false) : IEditorContext
{
    /// <summary>글 하나를 커서 문장으로 하는 대역(범위는 0 부터 글 끝까지).</summary>
    public static FakeEditorContext WithStatement(string text) => new(statement: () => new SqlStatement(text, new TextRange(0, text.Length), StatementKind.Query));

    public EditorSnapshot? GetActiveDocument() => document?.Invoke();

    public SqlStatement? GetCurrentStatement() => statement?.Invoke();

    public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(proposeResult);
}
