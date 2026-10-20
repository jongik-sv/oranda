namespace Oranda.Core.Catalog;

/// <summary>카탈로그 파일 머리 주석(<c>-- key: value</c>) 메타데이터. 형식은 docs/design/catalog.md.</summary>
/// <param name="Id">점으로 나눈 소문자 이름. 첫 마디는 소유 폴더 이름과 같다(예: <c>session.list</c>)</param>
/// <param name="MinVersion">이 파일을 쓸 수 있는 가장 낮은 서버 주 버전. <see cref="LowestMinVersion"/> 이상</param>
/// <param name="Requires">필요한 권한·뷰 이름(접속 점검 결과와 대조). 없으면 빈 목록</param>
/// <param name="License">필요한 관리 팩</param>
/// <param name="Rac">RAC 에서 어느 뷰 계열을 쓰는 쿼리인지</param>
/// <param name="Path">원본 파일 경로(Catalog/ 아래 상대, '/' 구분)</param>
public sealed record CatalogHeader(
    string Id,
    int MinVersion,
    IReadOnlyList<string> Requires,
    CatalogLicense License,
    CatalogRac Rac,
    string Path)
{
    /// <summary>지원 서버 하한(PRD D-06, D-026: 11.2 추가). 구버전을 더하면 이 값만 내리고 같은 id 의 낮은 min-version 파일을 더한다.</summary>
    public const int LowestMinVersion = 11;

    /// <summary>
    /// 이 값보다 낮은 min-version 파일(11g 용)은 12c 이후 문법(<c>FETCH FIRST</c>·<c>OFFSET … ROWS</c>)과
    /// 12c 이후 열·뷰를 쓰면 안 된다(<see cref="CatalogRules.Pre12cFeature"/>).
    /// </summary>
    public const int Release12 = 12;
}

/// <summary>
/// 쿼리가 요구하는 Oracle 관리 팩(TC-06, SAFE-07). 접속 프로필의 보유 선언(<c>LicensePack</c>)과 대조는
/// 정책 관문이 한다: <see cref="Diagnostics"/> 는 Diagnostics 이상, <see cref="Tuning"/> 은 DiagnosticsAndTuning 필요.
/// </summary>
public enum CatalogLicense
{
    /// <summary><c>license: none</c> — 팩 없이 쓸 수 있다.</summary>
    None,

    /// <summary><c>license: diagnostics</c> — Diagnostics Pack(ASH·AWR 등).</summary>
    Diagnostics,

    /// <summary><c>license: tuning</c> — Tuning Pack(SQL Monitor 등). Diagnostics 를 전제한다.</summary>
    Tuning,
}

/// <summary>RAC 에서의 뷰 계열(<c>rac:</c> 필드). 생략하면 <see cref="V"/>.</summary>
public enum CatalogRac
{
    /// <summary><c>rac: v</c> — 접속한 인스턴스의 <c>v$</c> 뷰만 본다.</summary>
    V,

    /// <summary><c>rac: gv</c> — <c>gv$</c> 뷰로 모든 인스턴스를 본다(inst_id 열 포함).</summary>
    Gv,
}
