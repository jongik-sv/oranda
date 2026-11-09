-- 플래시백 버전 조회(VERSIONS BETWEEN). {columns} 열 목록, {table} 표, {range} 구간 식, {conditions} 키 조건.
-- id: dbinfo.flashback_versions
-- min-version: 11
-- license: none
SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, {columns}
FROM {table} VERSIONS BETWEEN {range} AND MAXVALUE
WHERE {conditions}
ORDER BY VERSIONS_STARTSCN DESC NULLS LAST
