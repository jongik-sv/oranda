-- 이름이 :pattern(접두어 + %) 으로 시작하는 객체(모든 스키마, 이름순). 바인드: pattern(호출한 쪽이 접두어의 % _ \ 를 \ 로 이스케이프하고 끝에 % 를 붙인 값), max_rows
-- 필터는 meta.objects 와 같다(같은 객체 집합). 정렬은 이름 Ordinal 순서(IMetadataProvider.FindObjectsByPrefixAsync 기본 구현의 OrderBy(Name, Ordinal))에 맞추려고
-- 세션 NLS_SORT=BINARY 를 가정한다(6.2.15 기본값). 소유자·유형은 동률 해소. 이름 비교는 대소문자를 구분한다.
-- id: meta.objects_by_prefix
-- dbms: tibero
-- min-version: 6
-- requires: dba_objects, all_objects
-- license: none
SELECT owner, object_name, object_type
  FROM (SELECT o.owner, o.object_name, o.object_type
          FROM @PFX@_objects o
         WHERE o.object_name LIKE :pattern ESCAPE '\'
           AND o.object_type NOT IN ('LOB', 'TABLE PARTITION', 'TABLE SUBPARTITION', 'INDEX PARTITION', 'INDEX SUBPARTITION',
                                     'LOB PARTITION', 'JAVA CLASS', 'JAVA RESOURCE', 'JAVA SOURCE')
           AND o.object_name NOT LIKE 'BIN$%'
         ORDER BY o.object_name, o.owner, o.object_type)
 WHERE ROWNUM <= :max_rows
