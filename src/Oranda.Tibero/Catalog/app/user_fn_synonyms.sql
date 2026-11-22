-- AI 사용자 함수 확인: 동의어가 가리키는 곳. 열 = owner, synonym_name, 대상 소유자, 대상 이름, DB 링크(없으면 NULL). 열 순서는 Oracle 판과 같다.
-- Oracle 과 달라진 점: Tibero *_synonyms 의 대상 열은 org_object_owner/org_object_name 이고 db_link 열이 없다. 원격 동의어는 org_object_name 에
-- "이름"@링크 로 적혀 있어(2026-11-21 실측, meta.marks_synonyms 와 같은 규칙) 마지막 @ 뒤에 큰따옴표가 없으면 그 뒤를 링크로 낸다.
-- @PAIRS@ = (owner = 'X' AND synonym_name = 'Y') 를 OR 로 이은 조건.
-- id: app.user_fn_synonyms
-- dbms: tibero
-- min-version: 6
-- requires: all_synonyms
-- license: none
SELECT owner, synonym_name, org_object_owner, org_object_name,
       CASE WHEN INSTR(org_object_name, '@', -1) > 0
             AND INSTR(SUBSTR(org_object_name, INSTR(org_object_name, '@', -1) + 1), '"') = 0
            THEN SUBSTR(org_object_name, INSTR(org_object_name, '@', -1) + 1) END AS db_link
  FROM all_synonyms WHERE (@PAIRS@)
