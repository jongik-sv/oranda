-- AI 사용자 함수 확인: 동의어가 가리키는 곳. 열 = owner, synonym_name, 대상 소유자, 대상 이름, DB 링크(없으면 NULL).
-- @PAIRS@ = (owner = 'X' AND synonym_name = 'Y') 를 OR 로 이은 조건.
-- id: app.user_fn_synonyms
-- min-version: 11
-- requires: all_synonyms
-- license: none
SELECT owner, synonym_name, table_owner, table_name, db_link FROM all_synonyms WHERE (@PAIRS@)
