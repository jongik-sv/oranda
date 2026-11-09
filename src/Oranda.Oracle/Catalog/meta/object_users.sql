-- 이 객체를 쓰고 있는 다른 세션 수. {owner}·{name} = 이스케이프한 리터럴 글.
-- id: meta.object_users
-- min-version: 11
-- requires: v$access
-- license: none
SELECT COUNT(DISTINCT sid) FROM v$access WHERE owner = '{owner}' AND object = '{name}' AND sid <> TO_NUMBER(SYS_CONTEXT('USERENV', 'SID'))
