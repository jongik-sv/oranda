using Oranda.App.Features.SqlEditor;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

/// <summary>
/// 「문장」 창 묶음 종류. Language 의 <see cref="MyBatisElementKind"/> 를 App 형 필드에 두지 않으려고 따로 둔다
/// (셸 훑기가 App 형의 값 형식 필드를 읽으면 시작 때 Oranda.Language 가 로드된다, design.md 9장 R21).
/// </summary>
public enum MyBatisStatementKind
{
    Select,
    Insert,
    Update,
    Delete,
    Sql,
    ResultMap,
    Other,
}

/// <summary>
/// 세션 안 최상위 요소 하나의 상태: 기준 요소(연 때·마지막 저장), 지금 id·원문, 새 문장·지움 표시, 열린 문장 탭.
/// 원문은 탭이 열려 있으면 탭 글이 정본이고, 탭을 닫으면 그때 글을 <see cref="StoredText"/> 에 남긴다.
/// </summary>
public sealed class MyBatisStatementEntry
{
    internal MyBatisStatementEntry(MyBatisElement? baseline, MyBatisStatementKind kind, string? id, string text, bool ambiguous)
    {
        Baseline = baseline;
        Kind = kind;
        Id = id;
        StoredText = text;
        IsAmbiguous = ambiguous;
    }

    /// <summary>기준 문서의 요소. 새 문장이면 null.</summary>
    public MyBatisElement? Baseline { get; internal set; }

    /// <summary>기준 문서에서의 id(편집 목록이 가리키는 id). 새 문장이면 null.</summary>
    public string? OriginalId => Baseline?.Id;

    public MyBatisStatementKind Kind { get; }

    /// <summary>지금 id(id 바꾸기 뒤의 id). id 가 없는 요소(cache 등)는 null.</summary>
    public string? Id { get; internal set; }

    /// <summary>요소 이름(<c>select</c>·<c>resultMap</c>·<c>cache</c> …).</summary>
    public string Name => Baseline?.Name ?? KindName(Kind);

    /// <summary>탭이 닫혀 있을 때의 지금 원문.</summary>
    internal string StoredText { get; set; }

    /// <summary>열린 문장 탭(없으면 null).</summary>
    internal SqlEditorViewModel? Tab { get; set; }

    /// <summary>지금 원문(탭이 열려 있으면 탭 글).</summary>
    public string Text => Tab?.Text ?? StoredText;

    public bool IsNew => Baseline is null;

    public bool IsDeleted { get; internal set; }

    /// <summary>
    /// 같은 id 가 기준 문서에 여럿이다(databaseId 만 다른 문장 등). 02-01 Apply 가 id 로 고를 수 없어 편집하지 않는다(「편집 불가」).
    /// </summary>
    public bool IsAmbiguous { get; }

    /// <summary>문장(select·insert·update·delete)인가.</summary>
    public bool IsStatement => Kind is MyBatisStatementKind.Select or MyBatisStatementKind.Insert
        or MyBatisStatementKind.Update or MyBatisStatementKind.Delete;

    /// <summary>문장 탭으로 열 수 있다: 문장·sql 조각이고 id 가 있다. resultMap·그 밖 요소는 목록에만 보인다.</summary>
    public bool CanOpen => Id is not null && (IsStatement || Kind == MyBatisStatementKind.Sql);

    /// <summary>편집할 수 있다(열 수 있고 id 가 하나뿐).</summary>
    public bool IsEditable => CanOpen && !IsAmbiguous;

    /// <summary>기준과 다르다(새 문장·지움·원문 바뀜).</summary>
    public bool IsModified => IsNew ? !IsDeleted : IsDeleted || !TextEquals(Baseline!.Text);

    private bool TextEquals(string baseline) =>
        Tab is { } tab ? tab.Document.TextLength == baseline.Length && string.Equals(tab.Text, baseline, StringComparison.Ordinal)
            : string.Equals(StoredText, baseline, StringComparison.Ordinal);

    internal static MyBatisStatementKind KindOf(MyBatisElementKind kind) => kind switch
    {
        MyBatisElementKind.Select => MyBatisStatementKind.Select,
        MyBatisElementKind.Insert => MyBatisStatementKind.Insert,
        MyBatisElementKind.Update => MyBatisStatementKind.Update,
        MyBatisElementKind.Delete => MyBatisStatementKind.Delete,
        MyBatisElementKind.Sql => MyBatisStatementKind.Sql,
        MyBatisElementKind.ResultMap => MyBatisStatementKind.ResultMap,
        _ => MyBatisStatementKind.Other,
    };

    internal static MyBatisElementKind ElementKindOf(MyBatisStatementKind kind) => kind switch
    {
        MyBatisStatementKind.Select => MyBatisElementKind.Select,
        MyBatisStatementKind.Insert => MyBatisElementKind.Insert,
        MyBatisStatementKind.Update => MyBatisElementKind.Update,
        MyBatisStatementKind.Delete => MyBatisElementKind.Delete,
        MyBatisStatementKind.Sql => MyBatisElementKind.Sql,
        MyBatisStatementKind.ResultMap => MyBatisElementKind.ResultMap,
        _ => MyBatisElementKind.Other,
    };

    /// <summary>새 문장 요소 이름(<c>select</c>·<c>insert</c>·<c>update</c>·<c>delete</c>·<c>sql</c>).</summary>
    internal static string KindName(MyBatisStatementKind kind) => kind switch
    {
        MyBatisStatementKind.Select => "select",
        MyBatisStatementKind.Insert => "insert",
        MyBatisStatementKind.Update => "update",
        MyBatisStatementKind.Delete => "delete",
        MyBatisStatementKind.Sql => "sql",
        MyBatisStatementKind.ResultMap => "resultMap",
        _ => "other",
    };
}
