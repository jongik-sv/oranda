using Oranda.Core.Catalog;
using Oranda.Core.Metadata;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Metadata;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-14-22. Java 저장 객체(JAVA SOURCE·JAVA CLASS·JAVA RESOURCE) 목록·상세 카탈로그와 글 만들기.
[Trait("Category", "Catalog")]
public class JavaCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData("meta.java_available")]
    [InlineData("meta.java_objects")]
    [InlineData("meta.java_longname")]
    [InlineData("meta.java_class")]
    [InlineData("meta.java_source_object")]
    [InlineData("meta.java_source_classes")]
    [InlineData("meta.java_methods")]
    [InlineData("meta.java_arguments")]
    [InlineData("meta.java_fields")]
    [InlineData("meta.java_implements")]
    [InlineData("meta.java_inners")]
    public void Queries_run_from_11g_without_a_license_or_fetch_first(string id)
    {
        foreach (var version in new[] { 11, 19, 23 })
        {
            var q = Catalog.Get(id, version);
            Assert.Equal(11, q.Header.MinVersion);
            Assert.Equal(CatalogLicense.None, q.Header.License);
            Assert.Empty(CatalogParser.FindPre12cFeatures(q.Sql));
        }
    }

    [Fact]
    public void Object_list_asks_one_owner_only_and_cuts_rows_before_resolving_long_names()
    {
        var sql = Catalog.Get("meta.java_objects", 23).Sql;

        Assert.Contains("o.owner = :owner", sql, StringComparison.Ordinal); // 소유자 없이 전체 DB 를 훑지 않는다
        Assert.Contains("'JAVA SOURCE', 'JAVA CLASS', 'JAVA RESOURCE'", sql, StringComparison.Ordinal);
        Assert.Contains(":types", sql, StringComparison.Ordinal);
        Assert.Contains(":name_pattern", sql, StringComparison.Ordinal);
        Assert.Contains(":invalid_only", sql, StringComparison.Ordinal);
        Assert.Contains("ROWNUM <= :max_rows", sql, StringComparison.Ordinal);

        // 긴 이름은 바깥(자른 뒤), 사전이 줄인 이름('/' 로 시작)에만 푼다. 푸는 함수는 호출 쪽이 넣는다(권한 없으면 짧은 이름).
        var outer = sql[..sql.IndexOf("FROM (", StringComparison.Ordinal)];
        Assert.Contains("CASE WHEN j.object_name LIKE '/%' THEN @LONGNAME@ ELSE j.object_name END", outer, StringComparison.Ordinal);
        Assert.DoesNotContain("DBMS_JAVA", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Jvm_check_looks_up_the_dbms_java_package_by_owner_and_name()
    {
        var sql = Catalog.Get("meta.java_available", 23).Sql;

        Assert.Contains("o.owner = 'SYS'", sql, StringComparison.Ordinal);
        Assert.Contains("o.object_name = 'DBMS_JAVA'", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("meta.java_class", "@PFX@_java_classes")]
    [InlineData("meta.java_methods", "@PFX@_java_methods")]
    [InlineData("meta.java_arguments", "@PFX@_java_arguments")]
    [InlineData("meta.java_fields", "@PFX@_java_fields")]
    [InlineData("meta.java_implements", "@PFX@_java_implements")]
    [InlineData("meta.java_inners", "@PFX@_java_inners")]
    [InlineData("meta.java_source_classes", "@PFX@_java_classes")]
    public void Class_detail_reads_the_java_dictionary_view_by_owner_and_long_name(string id, string view)
    {
        var sql = Catalog.Get(id, 23).Sql;

        Assert.Contains(view, sql, StringComparison.Ordinal);
        Assert.Contains(".owner = :owner", sql, StringComparison.Ordinal);
        Assert.Contains(":name", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dbinfo.object_search", 19)]
    [InlineData("dbinfo.object_search", 11)]
    public void Object_search_finds_java_objects_only_when_a_java_type_is_chosen(string id, int version)
    {
        var sql = Catalog.Get(id, version).Sql;

        Assert.Contains("(o.object_type NOT LIKE 'JAVA %' OR INSTR(:types, '|JAVA ') > 0)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'JAVA CLASS', 'JAVA RESOURCE', 'JAVA SOURCE'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_object_list_still_leaves_java_out_of_the_cache()
    {
        // 전체 객체 목록(캐시)은 Java 를 넣지 않는다 — 스키마 브라우저는 meta.java_objects 로 따로 묻는다.
        foreach (var version in new[] { 11, 19 })
        {
            Assert.Contains("'JAVA CLASS', 'JAVA RESOURCE', 'JAVA SOURCE'", Catalog.Get("meta.objects", version).Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Java_source_ddl_names_the_long_name_and_joins_lines_with_or_without_line_ends()
    {
        var ddl = OracleMetadataProvider.JavaSourceDdl("SCOTT", "com/example/Hello",
            ["package com.example;\n", "public class Hello {", "}"], terminator: true);

        Assert.Equal("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"SCOTT\".\"com/example/Hello\" AS\npackage com.example;\npublic class Hello {\n}\n/", ddl);
        Assert.EndsWith("}", OracleMetadataProvider.JavaSourceDdl("SCOTT", "A", ["class A {}\n", "}\n\n"], terminator: false), StringComparison.Ordinal);
        Assert.Contains("\"we\"\"ird\"", OracleMetadataProvider.JavaSourceDdl("SCOTT", "we\"ird", ["x"], false), StringComparison.Ordinal);
    }

    [Fact]
    public void Java_types_read_like_java()
    {
        Assert.Equal("java.lang.String", OracleMetadataProvider.JavaType(0, "class", "java/lang/String"));
        Assert.Equal("int[][]", OracleMetadataProvider.JavaType(2, "int", null));
        Assert.Equal("void", OracleMetadataProvider.JavaType(null, "void", null));
        Assert.Null(OracleMetadataProvider.JavaType(0, null, null));
        Assert.Equal("com.example.Outer", OracleMetadataProvider.JavaTypeName("com/example/Outer"));
    }

    [Fact]
    public void Method_signature_lists_argument_types_of_that_method()
    {
        object?[][] args =
        [
            [1m, 0m, 0m, "class", "java/lang/String"],
            [1m, 1m, 1m, "int", null],
            [2m, 0m, 0m, "long", null],
        ];

        Assert.Equal("hi(java.lang.String, int[])", OracleMetadataProvider.JavaSignature("hi", 1, 2, args));
        Assert.Equal("none()", OracleMetadataProvider.JavaSignature("none", 3, 0, args));
        Assert.Equal("hi(… 2개)", OracleMetadataProvider.JavaSignature("hi", 1, 2, null)); // 인자 뷰를 못 읽었다
    }

    [Fact]
    public void Class_and_resource_ddl_is_a_note_not_a_statement()
    {
        Assert.StartsWith("--", OracleMetadataProvider.JavaBinaryDdlNote(new DbObjectRef("SCOTT", "A", "JAVA CLASS")), StringComparison.Ordinal);
        Assert.Contains("JAVA RESOURCE", OracleMetadataProvider.JavaBinaryDdlNote(new DbObjectRef("SCOTT", "r.txt", "JAVA RESOURCE")), StringComparison.Ordinal);
    }
}
