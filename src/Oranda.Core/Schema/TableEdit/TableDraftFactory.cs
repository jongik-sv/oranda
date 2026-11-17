using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>편집 모델 만들기(설계 7.1·7.5).</summary>
public static class TableDraftFactory
{
    /// <summary>
    /// 기존 테이블의 원본 모델. 가상 열은 <see cref="ColumnInfo.DefaultValue"/> 를 <see cref="ColumnDraft.VirtualExpression"/> 으로 옮기고(TE-30),
    /// 서버 이름의 NOT NULL CHECK 는 빼며(설계 7.3), 제약 인덱스는 <see cref="IndexDraft.ConstraintName"/> 을 채운다. 줄 키는 종류·순번.
    /// 화면은 이 결과를 원본으로 두고 같은 값을 편집 모델로 쓴다(불변이라 그대로 나눠 써도 된다).
    /// </summary>
    public static TableDraft FromTable(TableInfo table, ITableEditDialect dialect) => throw new NotImplementedException("T1b");

    /// <summary>새 테이블의 빈 원본(<see cref="TableDraft.IsNewTable"/>, 이름 없음).</summary>
    public static TableDraft Empty(string owner) => throw new NotImplementedException("T1b");

    /// <summary>
    /// 「이 구조로 새 테이블…」(설계 7.5): 열(가상 포함)·주석·제약·인덱스를 복사한 새 테이블 편집 모델. 제약·인덱스 이름은 새 이름으로 규칙대로 다시 만들고
    /// 줄 키는 모두 새 키다. 원본은 <see cref="Empty"/> 와 짝이다.
    /// </summary>
    public static TableDraft CopyStructure(TableInfo source, string owner, string name, ITableEditDialect dialect, int maxIdentifierBytes) =>
        throw new NotImplementedException("T1b");
}
