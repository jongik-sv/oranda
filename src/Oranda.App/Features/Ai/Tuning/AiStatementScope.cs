using System;
using Oranda.Core.Extensibility;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>활성 편집기에서 AI 로 보낼 문장을 고른 결과.</summary>
public enum AiStatementSource
{
    /// <summary>활성 SQL 탭의 문장을 쓸 수 있다.</summary>
    Found,

    /// <summary>활성 SQL 탭에 문장이 없다.</summary>
    NoStatement,

    /// <summary>활성 문서가 지금 접속의 SQL 탭이 아니다(PL/SQL 편집기·객체 상세·다른 접속의 탭, 또는 판단할 수 없다).</summary>
    OtherDocument,
}

/// <summary>
/// AI 로 보낼 편집기 문장을 고르는 판단 한 곳(일반 대화와 튜닝 시작이 같이 쓴다). 편집기 맥락(<see cref="IEditorContext"/>)은 활성 문서와 무관하게
/// 마지막 SQL 탭의 문장을 돌려주므로, 그대로 쓰면 다른 접속 탭의 문장(리터럴 포함)이 지금 접속의 가림·허용 수준 아래로 나간다.
/// 그래서 활성 문서가 SQL 작업 공간 문서이고 그 탭의 접속이 지금 접속과 같을 때만 문장을 돌려준다. 판단할 수 없으면(<c>sqlDocumentActive</c> 가 null) 닫는다.
/// </summary>
public static class AiStatementScope
{
    /// <param name="editor">활성 편집기.</param>
    /// <param name="sqlDocumentActive">활성 문서가 SQL 작업 공간 문서인가(<c>SessionRouter.SqlDocumentActive</c>). 모르면 null.</param>
    /// <param name="connectionId">지금 접속(보이는 세션)의 id. 모르면 null.</param>
    public static (AiStatementSource Source, SqlStatement? Statement) Resolve(IEditorContext editor, bool? sqlDocumentActive, Guid? connectionId)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var document = editor.GetActiveDocument();
        if (sqlDocumentActive != true || document is null
            || (document.ConnectionProfileId is { } tabConnection && connectionId != tabConnection))
        {
            return (AiStatementSource.OtherDocument, null);
        }

        return editor.GetCurrentStatement() is { } statement && !string.IsNullOrWhiteSpace(statement.Text)
            ? (AiStatementSource.Found, statement)
            : (AiStatementSource.NoStatement, null);
    }
}
