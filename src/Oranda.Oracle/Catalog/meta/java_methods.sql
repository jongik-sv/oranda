-- Java 클래스의 메서드(all_java_methods). 반환형은 base_type(원시형) 또는 return_class(클래스), array_depth 만큼 []. 바인드: owner, name(긴 이름).
-- id: meta.java_methods
-- min-version: 11
-- requires: dba_java_methods, all_java_methods
-- license: none
SELECT m.method_index, m.method_name, m.accessibility, m.is_static, m.is_abstract, m.is_final,
       m.arguments, m.array_depth, m.base_type, m.return_class
  FROM @PFX@_java_methods m
 WHERE m.owner = :owner
   AND m.name = :name
 ORDER BY m.method_index
