-- 복사 사전 점검: 초기화 파라미터 값(undo_retention, max_string_size). 열 = VALUE. 바인드: PNAME(소문자 파라미터 이름).
-- 볼 권한이 없으면 ORA-00942 로 실패하고 호출한 쪽이 「알 수 없음」으로 본다. 접속한 인스턴스의 값만 본다.
-- id: copy.parameter_value
-- min-version: 11
-- requires: v$parameter
-- license: none
-- rac: v
SELECT VALUE FROM V$PARAMETER WHERE NAME = :PNAME
