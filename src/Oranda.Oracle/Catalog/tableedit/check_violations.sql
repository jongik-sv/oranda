-- 테이블 편집 검사: CHECK 조건을 어기는 행 수(ORA-02293). NOT (UNKNOWN) 은 UNKNOWN 이라 세지 않는다(서버 규칙과 같음). @CONDITION@ = 사용자 조건 원문.
-- 사용자 원문 자리는 앞뒤에 줄을 바꿔 둔다: 원문 끝의 `-- 주석` 이 뒤 문장을 주석으로 만들지 않게.
-- id: tableedit.check_violations
-- min-version: 11
-- license: none
SELECT COUNT(*) FROM @TABLE@ WHERE NOT (
@CONDITION@
)
