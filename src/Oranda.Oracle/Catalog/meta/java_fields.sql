-- Java 클래스의 필드(all_java_fields). 바인드: owner, name(긴 이름).
-- id: meta.java_fields
-- min-version: 11
-- requires: dba_java_fields, all_java_fields, jvm
-- license: none
SELECT f.field_index, f.field_name, f.accessibility, f.is_static, f.is_final, f.array_depth, f.base_type, f.field_class
  FROM @PFX@_java_fields f
 WHERE f.owner = :owner
   AND f.name = :name
 ORDER BY f.field_index
