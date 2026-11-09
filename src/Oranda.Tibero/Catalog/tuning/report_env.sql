-- 튜닝 보고서 환경 절(Tibero). 열은 Oracle tuning.report_env 와 같다(kind, name, value).
-- VERSION: Tibero V$VERSION 은 NAME·VALUE 행(PRODUCT_MAJOR·BUILD_NUMBER·STABLE_VERSION …)이라 한 줄 배너로 엮는다.
-- PARAMETER: V$PARAMETERS(Oracle V$PARAMETER 아님)의 옵티마이저·메모리 관련 Tibero 매개변수(이름은 소문자로). 없는 이름은 행이 없을 뿐이다.
-- NLS: NLS_DATABASE_PARAMETERS(Tibero 에는 문자 집합 행이 없다). 바인드 없음
-- id: tuning.report_env
-- dbms: tibero
-- min-version: 6
-- requires: v$version, v$parameters, nls_database_parameters
-- license: none
SELECT kind, name, value
  FROM (SELECT 1 AS ord, 'VERSION' AS kind, 'banner' AS name,
               'Tibero ' || MAX(CASE v.name WHEN 'PRODUCT_MAJOR' THEN v.value END)
               || ' (build ' || MAX(CASE v.name WHEN 'BUILD_NUMBER' THEN v.value END)
               || ', ' || MAX(CASE v.name WHEN 'STABLE_VERSION' THEN v.value END) || ')' AS value
          FROM v$version v
        UNION ALL
        SELECT 2, 'PARAMETER', LOWER(p.name), p.value
          FROM v$parameters p
         WHERE p.name IN ('OPTIMIZER_MODE', 'OPTIMIZER_USE_INVISIBLE_INDEXES', 'OPTIMIZER_TRACE', 'CURSOR_SHARING',
                          'DB_FILE_MULTIBLOCK_READ_COUNT', 'GATHER_SQL_PLAN_STAT', 'KEEP_STAT_PER_SQL', 'SQL_STAT_HISTORY',
                          'PARALLEL_DEFAULT_DOP', 'MEMORY_TARGET', 'MEMORY_MAX_TARGET', 'EX_MEMORY_AUTO_MANAGEMENT', 'OPEN_CURSORS')
        UNION ALL
        SELECT 3, 'NLS', d.parameter, d.value
          FROM nls_database_parameters d
         WHERE d.parameter IN ('NLS_CHARACTERSET', 'NLS_NCHAR_CHARACTERSET', 'NLS_LANGUAGE', 'NLS_TERRITORY', 'NLS_DATE_FORMAT',
                               'NLS_SORT', 'NLS_COMP', 'NLS_LENGTH_SEMANTICS', 'NLS_LANG_AT_BOOT'))
 ORDER BY ord, name
