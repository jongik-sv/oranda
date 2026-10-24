-- 튜닝 보고서 환경 절(TN-17): 버전 배너, optimizer 관련 매개변수 일부, DB NLS 일부. 없는 매개변수 이름(버전 차)은 행이 없을 뿐이다.
-- id: tuning.report_env
-- min-version: 11
-- requires: v$version, v$parameter, nls_database_parameters
-- license: none
SELECT kind, name, value
  FROM (SELECT 1 AS ord, 'VERSION' AS kind, 'banner' AS name, v.banner AS value
          FROM v$version v
         WHERE v.banner LIKE 'Oracle%'
        UNION ALL
        SELECT 2, 'PARAMETER', p.name, p.display_value
          FROM v$parameter p
         WHERE p.name IN ('optimizer_features_enable', 'optimizer_mode', 'optimizer_index_cost_adj', 'optimizer_index_caching',
                          'optimizer_dynamic_sampling', 'optimizer_adaptive_plans', 'optimizer_adaptive_statistics',
                          'optimizer_adaptive_features', 'optimizer_capture_sql_plan_baselines', 'optimizer_use_sql_plan_baselines',
                          'optimizer_use_invisible_indexes', 'optimizer_use_pending_statistics', 'cursor_sharing', 'statistics_level',
                          'db_file_multiblock_read_count', 'parallel_degree_policy', 'workarea_size_policy', 'pga_aggregate_target',
                          'sga_target', 'memory_target', 'compatible')
        UNION ALL
        SELECT 3, 'NLS', d.parameter, d.value
          FROM nls_database_parameters d
         WHERE d.parameter IN ('NLS_CHARACTERSET', 'NLS_NCHAR_CHARACTERSET', 'NLS_LANGUAGE', 'NLS_TERRITORY', 'NLS_DATE_FORMAT',
                               'NLS_SORT', 'NLS_COMP', 'NLS_LENGTH_SEMANTICS'))
 ORDER BY ord, name
