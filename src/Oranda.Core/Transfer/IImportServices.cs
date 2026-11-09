using Oranda.Core.Grid;
using Oranda.Core.Metadata;

namespace Oranda.Core.Transfer;

/// <summary>
/// 가져오기 원본(CSV·TSV·xlsx) 행 읽기 계약 — 초안(tb-base backlog b, T5 첫 항목이 구현). 구현 = <c>TransferFileReaderService</c>(Core, 키 없음).
/// 파일 해석은 DBMS 와 무관하므로 T5 에서 구현을 Core 로 옮겨 키 없는 서비스 하나로 등록한다. 셀 값 규칙: 문자열(CSV·xlsx 문자·숫자 원문),
/// xlsx 날짜 서식 셀 = <see cref="DateTime"/>, 불리언 = <see cref="bool"/>, 빈 셀 = null.
/// </summary>
public interface ITransferFileReader
{
    /// <summary>원본의 모든 행을 앞에서부터 흘려 읽는다. xlsx 는 첫 시트. 지원하지 않는 형식은 <see cref="NotSupportedException"/>.</summary>
    IEnumerable<object?[]> ReadRows(TransferFormat format, Stream source);

    /// <summary>앞에서 <paramref name="maxRows"/> 행만(헤더 포함) 읽는다.</summary>
    IReadOnlyList<object?[]> Preview(TransferFormat format, Stream source, int maxRows);
}

/// <summary>
/// 가져오기 셀 값 → 대상 열 타입의 SQL 리터럴 계약 — 초안(tb-base backlog b, T5 가 구현). 리터럴 문법·빈 문자열 규칙·타입 이름이 DBMS 마다 달라
/// <c>[OrandaService(typeof(IImportValueConverter), Dbms = …)]</c> 키 등록 대상이다. 구현 = Oranda.Oracle <c>OracleImportValueConverter</c>(정적 <c>ImportValueConverter</c> 를 감쌈); 호출 쪽은 세션 DBMS 로 선택기에서 꺼낸다(디스패처 없음).
/// </summary>
public interface IImportValueConverter
{
    /// <summary>대상 열 타입 이름 → 표시 분류.</summary>
    ColumnDataKind KindOf(string dataType);

    /// <summary>셀 값 하나를 대상 열의 SQL 리터럴로. 값이 열 타입에 안 맞으면 <see cref="FormatException"/>.</summary>
    string ToLiteral(ColumnInfo column, object? cell);

    /// <summary>미리보기용: 변환이 되면 리터럴, 안 되면 false 와 이유.</summary>
    bool TryConvert(ColumnInfo column, object? cell, out string literal, out string? reason);
}
