using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사가 보내는 문장 가운데 DBMS 마다 글이 다른 것(DDL·시퀀스 소비). 사전 점검의 미리 보기와 실행기가 같은 출처를 써야 한다:
/// 화면이 미리 받은 확인은 글을 서수로 비교하므로 한 글자라도 다르면 확인이 묻힌다. 조회·DELETE 는 <see cref="CopyCommonStatements"/>.
/// 문장만 만들고 실행하지 않는다. 구현은 DBMS 어셈블리에 두고 하위 형식이 생성자에서 넘긴다. 키 서비스가 아니다.
/// </summary>
public interface ICopyDialect
{
    /// <summary>대상 트리거 끄기.</summary>
    SqlStatement DisableTrigger(string owner, string trigger);

    /// <summary>대상 외래키 끄기.</summary>
    SqlStatement DisableConstraint(DisabledObject fk);

    /// <summary>「테이블 다시 만들기」의 DROP(위험 명령, 대상 이름 재입력 확인).</summary>
    SqlStatement DropTable(string owner, string table);

    /// <summary>시퀀스 값을 한 번에 여러 개 소비하는 조회(바인드 = 개수).</summary>
    SqlStatement NextValues(string owner, string sequence);
}
