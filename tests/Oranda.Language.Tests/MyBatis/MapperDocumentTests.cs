using System.Text;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.Language.Tests.MyBatis;

/// <summary>
/// TSK-02-01 매퍼 원문 모델: 왕복(바이트 같음)·요소 범위·편집(바꾸기·넣기·지우기·id 바꾸기)·쓰기 전 검사.
/// 소유 = TSK-02-01(docs/oranda1/design.md 「기능 Task 편집 지점」). 1MB 파싱 시간은 <see cref="MapperDocumentPerfTests"/>.
/// </summary>
public class MapperDocumentTests
{
    private static readonly MapperParser Parser = new();

    public static TheoryData<string, bool> SamplesWithBom
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var name in MapperSamples.All.Keys)
            {
                data.Add(name, false);
                data.Add(name, true);
            }

            return data;
        }
    }

    public static TheoryData<string> SampleNames => Names(MapperSamples.All.Keys);

    // ── Parse·왕복 ──

    [Theory]
    [MemberData(nameof(SamplesWithBom))]
    public void Roundtrip_Unchanged_BytesEqual(string name, bool bom)
    {
        var bytes = MapperSamples.Bytes(MapperSamples.All[name], bom);
        var doc = Parser.Parse(bytes);

        var written = Encode(doc.Apply([]), doc.Format);

        Assert.Equal(bytes, written);
        Assert.Equal(bom, doc.Format.HasBom);
    }

    [Fact]
    public void Roundtrip_EucKr_BytesEqual()
    {
        var text = "<?xml version=\"1.0\" encoding=\"EUC-KR\"?>\n<mapper namespace=\"k\">\n  <select id=\"a\">SELECT '한글' FROM DUAL</select>\n</mapper>\n";
        var bytes = EucKr().GetBytes(text);

        var doc = Parser.Parse(bytes);

        Assert.Equal("euc-kr", doc.Format.EncodingName);
        Assert.Equal(text, doc.Text);
        Assert.Equal(bytes, Encode(doc.Apply([]), doc.Format));
    }

    [Fact]
    public void Roundtrip_MixedNewLines_BytesEqual()
    {
        var text = "<?xml version=\"1.0\"?>\r\n<mapper>\n  <select id=\"a\">\r\n  X\n  </select>\r\n</mapper>";
        var bytes = Encoding.UTF8.GetBytes(text);

        var doc = Parser.Parse(bytes);

        Assert.Equal(bytes, Encode(doc.Apply([]), doc.Format));
        Assert.Equal("<select id=\"a\">\r\n  X\n  </select>", doc.Elements[0].Text);
        Assert.Equal("\r\n", doc.Format.NewLine);
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Parse_Elements_RangesMatchText(string name)
    {
        var doc = Parser.Parse(MapperSamples.All[name]);

        Assert.NotEmpty(doc.Elements);
        foreach (var e in doc.Elements)
        {
            Assert.Equal(e.Text, doc.Text.Substring(e.ElementRange.Start, e.ElementRange.Length));
            Assert.StartsWith("<" + e.Name, e.Text, StringComparison.Ordinal);
            Assert.True(e.BodyRange.Start >= e.ElementRange.Start && e.BodyRange.End <= e.ElementRange.End);
        }
    }

    [Fact]
    public void Parse_EmpMapper_ElementsInOrder()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        Assert.Equal(
            ["empCols", "empMap", "selectEmpList", "selectEmp", "countEmp", "insertEmp", "updateEmpSal", "deleteEmp"],
            doc.Elements.Select(e => e.Id));
        Assert.Equal(
            [MyBatisElementKind.Sql, MyBatisElementKind.ResultMap, MyBatisElementKind.Select, MyBatisElementKind.Select,
                MyBatisElementKind.Select, MyBatisElementKind.Insert, MyBatisElementKind.Update, MyBatisElementKind.Delete],
            doc.Elements.Select(e => e.Kind));
        Assert.Equal("com.acme.hr.EmpMapper", doc.MapperNamespace);

        var list = doc.Find("selectEmpList")!;
        Assert.Equal(["id", "parameterType", "resultMap"], list.Attributes.Select(a => a.Key));
        Assert.StartsWith("<select id=\"selectEmpList\"", list.Text, StringComparison.Ordinal);
        Assert.EndsWith("</select>", list.Text, StringComparison.Ordinal);
        Assert.StartsWith("\n  SELECT <include", doc.Text.Substring(list.BodyRange.Start, list.BodyRange.Length), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CommentsCDataEntities_KeptVerbatim()
    {
        var comments = Parser.Parse(MapperSamples.WithComments);
        var cdata = Parser.Parse(MapperSamples.WithCData);
        var entities = Parser.Parse(MapperSamples.WithEntities);

        Assert.Single(comments.Elements);
        Assert.Contains("<!-- 문장 안 xml 주석 -->", comments.Elements[0].Text, StringComparison.Ordinal);
        Assert.Contains("<![CDATA[", cdata.Elements[0].Text, StringComparison.Ordinal);
        Assert.Contains("SAL &gt; #{lo} AND SAL &lt; #{hi} AND ENAME LIKE 'A&amp;B%'", entities.Elements[0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(MapperSamples.WithBom), "\n", true)]
    [InlineData(nameof(MapperSamples.Crlf), "\r\n", true)]
    [InlineData(nameof(MapperSamples.NoTrailingNewLine), "\n", false)]
    public void Parse_Format_ReadsNewLineAndTrailing(string name, string newLine, bool endsWithNewLine)
    {
        var doc = Parser.Parse(MapperSamples.Bytes(MapperSamples.All[name]));

        Assert.Equal(new MapperFileFormat("utf-8", false, newLine, endsWithNewLine), doc.Format);
    }

    [Fact]
    public void Parse_NoNamespace_NullNamespace()
    {
        Assert.Null(Parser.Parse(MapperSamples.NoNamespace).MapperNamespace);
    }

    [Fact]
    public void Parse_LoneCrTabSurrogate_OffsetsExact()
    {
        var text = "<?xml version=\"1.0\"?>\r<mapper>\r\t<!-- 😀😀 --><select id=\"x\">A</select>\n\t<!-- 한글 --> <update id=\"y\"/></mapper>";

        var doc = Parser.Parse(text);

        Assert.Equal(["<select id=\"x\">A</select>", "<update id=\"y\"/>"], doc.Elements.Select(e => e.Text));
        Assert.Equal(0, doc.Elements[1].BodyRange.Length);
    }

    [Fact]
    public void Parse_QuotedGtInAttribute_FindsStartTagEnd()
    {
        var doc = Parser.Parse("<mapper><select id=\"a\" test='x > 1 \"q\"'>B</select></mapper>");

        var e = doc.Elements[0];
        Assert.Equal("B", doc.Text.Substring(e.BodyRange.Start, e.BodyRange.Length));
        Assert.Equal("x > 1 \"q\"", e.Attributes[1].Value);
    }

    [Fact]
    public void Parse_NestedSameName_UsesDepth()
    {
        var doc = Parser.Parse("<mapper><sql id=\"s\"><if test=\"a\"><sql/></if></sql><select id=\"t\"/></mapper>");

        Assert.Equal(["<sql id=\"s\"><if test=\"a\"><sql/></if></sql>", "<select id=\"t\"/>"], doc.Elements.Select(e => e.Text));
    }

    [Theory]
    [InlineData("<mapper/>")]
    [InlineData("<mapper></mapper>")]
    [InlineData("<mapper namespace=\"e\">\n</mapper>\n")]
    public void Parse_EmptyRoot_NoElements(string text)
    {
        Assert.Empty(Parser.Parse(text).Elements);
    }

    [Fact]
    public void Parse_StringWithLeadingFeff_StripsAndTreatsAsNoBom()
    {
        var doc = Parser.Parse("\uFEFF<mapper><select id=\"a\"/></mapper>");

        Assert.False(doc.Format.HasBom);
        Assert.Equal("<mapper><select id=\"a\"/></mapper>", doc.Text);
        Assert.Equal(8, doc.Elements[0].ElementRange.Start);
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Parse_Invalid_ThrowsWithLineAndColumn(string name)
    {
        var ex = Assert.Throws<FormatException>(() => Parser.Parse(MapperSamples.Invalid[name]));

        Assert.Contains("줄", ex.Message, StringComparison.Ordinal);
        Assert.Contains("칸", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidNames => Names(MapperSamples.Invalid.Keys);

    [Fact]
    public void Parse_NotMapper_SaysRootIsNotMapper()
    {
        var ex = Assert.Throws<FormatException>(() => Parser.Parse(MapperSamples.NotMapper));

        Assert.Contains("mapper", ex.Message, StringComparison.Ordinal);
        Assert.Contains("beans", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExternalEntity_NotResolved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oranda-xxe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "SECRET");
        try
        {
            var text = $"<?xml version=\"1.0\"?><!DOCTYPE mapper [<!ENTITY x SYSTEM \"{new Uri(path).AbsoluteUri}\">]><mapper><select id=\"a\">&x;</select></mapper>";

            var ex = Assert.Throws<FormatException>(() => Parser.Parse(text));

            Assert.DoesNotContain("SECRET", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_UndeclaredEntity_Throws()
    {
        Assert.Throws<FormatException>(() => Parser.Parse("<mapper><select id=\"a\">&nbsp;</select></mapper>"));
    }

    [Fact]
    public void Parse_InvalidUtf8_ThrowsInsteadOfReplacing()
    {
        var bytes = Encoding.UTF8.GetBytes("<mapper><select id=\"a\">X</select></mapper>");
        bytes[24] = 0xFF;

        Assert.Throws<FormatException>(() => Parser.Parse(bytes));
    }

    [Fact]
    public void Parse_Utf16Bom_Throws()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<mapper/>")];

        Assert.Throws<FormatException>(() => Parser.Parse(bytes));
    }

    [Fact]
    public void Parse_Hash_IsMapperHashOfTextRegardlessOfBom()
    {
        var plain = Parser.Parse(MapperSamples.Bytes(MapperSamples.WithBom));
        var withBom = Parser.Parse(MapperSamples.Bytes(MapperSamples.WithBom, bom: true));

        Assert.Equal(MapperHash.Compute(MapperSamples.WithBom), plain.Hash);
        Assert.Equal(plain.Hash, withBom.Hash);
    }

    [Fact]
    public void Roundtrip_EucKrDeclaredWithCp949Extension_BytesEqual()
    {
        var text = "<?xml version=\"1.0\" encoding=\"euc-kr\"?>\n<mapper>\n  <select id=\"a\">SELECT '똠햏' FROM DUAL</select>\n</mapper>\n";
        var bytes = EucKr().GetBytes(text);

        var doc = Parser.Parse(bytes);

        Assert.Equal("euc-kr", doc.Format.EncodingName);
        Assert.Contains("똠햏", doc.Elements[0].Text, StringComparison.Ordinal);
        Assert.Equal(bytes, Encode(doc.Apply([]), doc.Format));
    }

    [Fact]
    public void Parse_EncodingAlias_StoresWebName()
    {
        var doc = Parser.Parse("<?xml version=\"1.0\" encoding=\"Unicode-2-0-UTF-8\"?><mapper/>");

        Assert.Equal("utf-8", doc.Format.EncodingName);
    }

    [Theory]
    [InlineData("UTF-16")]
    [InlineData("utf-32")]
    [InlineData("no-such-encoding")]
    public void Parse_StringDeclaringUnsupportedEncoding_Throws(string encoding)
    {
        Assert.Throws<FormatException>(() => Parser.Parse($"<?xml version=\"1.0\" encoding=\"{encoding}\"?><mapper/>"));
    }

    [Fact]
    public void Parse_InvalidUtf8AfterBom_ReportsFileOffset()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("<mapper><select id=\"a\">X</select></mapper>")];
        bytes[27] = 0xFF;

        var ex = Assert.Throws<FormatException>(() => Parser.Parse(bytes));

        Assert.Contains("27바이트째", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CrOnlyDocument_NewLineIsCr()
    {
        var doc = Parser.Parse("<mapper>\r  <select id=\"a\"/>\r</mapper>\r");

        var result = doc.Apply([new MapperEdit.InsertElement("b", "<select id=\"b\">\n  X\n  </select>")]);

        Assert.Equal("\r", doc.Format.NewLine);
        Assert.Equal("<mapper>\r  <select id=\"a\"/>\r  <select id=\"b\">\r  X\r  </select>\r</mapper>\r", result);
    }

    [Fact]
    public void Parse_LargeCrlfDocument_AllRangesMatch()
    {
        // XmlReader 버퍼(수 KB)를 여러 번 넘는 CRLF 문서, 여러 줄 DOCTYPE, CDATA·주석·탭·서로게이트 포함.
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<!DOCTYPE mapper\r\n  PUBLIC \"-//mybatis.org//DTD Mapper 3.0//EN\"\r\n  \"http://mybatis.org/dtd/mybatis-3-mapper.dtd\">\r\n<mapper namespace=\"big\">\r\n");
        for (int i = 0; i < 2000; i++)
        {
            sb.Append("\t<!-- 문장 ").Append(i).Append(" <select> 모양 글 -->\r\n")
                .Append("\t<select id=\"s").Append(i).Append("\" test='a > b'\r\n\t\tresultType=\"map\">\r\n")
                .Append("\t\t<![CDATA[ SELECT '한글😀' FROM T WHERE A < 1 ]]></select >\r\n");
        }

        sb.Append("</mapper>\r\n");
        var doc = Parser.Parse(sb.ToString());

        Assert.Equal(2000, doc.Elements.Count);
        foreach (var e in doc.Elements)
        {
            Assert.Equal(e.Text, doc.Text.Substring(e.ElementRange.Start, e.ElementRange.Length));
            Assert.EndsWith("]]></select >", e.Text, StringComparison.Ordinal);
            Assert.StartsWith("\r\n\t\t<![CDATA[", doc.Text.Substring(e.BodyRange.Start, e.BodyRange.Length), StringComparison.Ordinal);
        }
    }

    // ── Apply ──

    [Fact]
    public void Apply_ReplaceKeepsUnicodeLineSeparatorsInLiterals()
    {
        // U+2028(LS)·U+0085(NEL)·폼 피드는 줄 끝으로 보지 않는다.
        var literal = "가" + (char)0x2028 + "나" + (char)0x85 + "다" + (char)0x0C;
        var doc = Parser.Parse(MapperSamples.Crlf);

        var result = doc.Apply([new MapperEdit.ReplaceElement("a", "<select id=\"a\">\n  SELECT '" + literal + "' FROM DUAL\n</select>")]);

        Assert.Contains("<select id=\"a\">\r\n  SELECT '" + literal + "' FROM DUAL\r\n</select>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_InsertAfterLastAndBeforeClose_SameOffset_AfterComesFirst()
    {
        var doc = Parser.Parse("<mapper><select id=\"a\"/></mapper>");

        var result = doc.Apply([new MapperEdit.InsertElement("z", "<select id=\"z\"/>"), new MapperEdit.InsertElement("b", "<select id=\"b\"/>", "a")]);

        Assert.Equal(["a", "b", "z"], Parser.Parse(result).Elements.Select(e => e.Id));
    }

    [Fact]
    public void Apply_InsertAfter_ElementWithTrailingComment_GoesAfterComment()
    {
        var doc = Parser.Parse("<mapper>\n  <select id=\"a\"/> <!-- a 설명 -->\n</mapper>\n");

        var result = doc.Apply([new MapperEdit.InsertElement("b", "<select id=\"b\"/>", "a")]);

        Assert.Equal("<mapper>\n  <select id=\"a\"/> <!-- a 설명 -->\n  <select id=\"b\"/>\n</mapper>\n", result);
    }

    [Fact]
    public void Apply_Rename_WhitespaceInId_RoundtripsExactly()
    {
        var doc = Parser.Parse("<mapper><select id=\"a\">X</select></mapper>");

        var result = doc.Apply([new MapperEdit.RenameElement("a", "x\ty\nz")]);

        Assert.Contains("id=\"x&#9;y&#10;z\"", result, StringComparison.Ordinal);
        Assert.NotNull(Parser.Parse(result).Find("x\ty\nz"));
    }

    [Fact]
    public void DatabaseId_SameIdDifferentDatabase_CheckOkButEditAmbiguous()
    {
        var doc = Parser.Parse("<mapper><select id=\"x\" databaseId=\"oracle\">A</select><select id=\"x\" databaseId=\"mysql\">B</select><select id=\"y\" databaseId=\"oracle\"/><select id=\"y\" databaseId=\"oracle\"/></mapper>");

        var check = doc.Check(doc.Text, doc.Hash);

        Assert.Equal(["y"], check.DuplicateIds);
        Assert.Throws<ArgumentException>(() => doc.Apply([new MapperEdit.RenameElement("x", "x2")]));
    }


    [Fact]
    public void Apply_ReplaceBody_DiffOnlyInsideElementBody()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);
        var e = doc.Find("selectEmpList")!;

        var result = doc.Apply([new MapperEdit.ReplaceElement("selectEmpList", e.Text.Replace("ORDER BY ${orderBy}", "ORDER BY E.ENAME", StringComparison.Ordinal))]);

        AssertChangedOnlyWithin(doc.Text, result, e.BodyRange.Start, e.BodyRange.End);
        var before = doc.Text.Split('\n');
        var after = result.Split('\n');
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(["  ORDER BY E.ENAME"], after.Where((line, i) => line != before[i]));
    }

    [Fact]
    public void Apply_ReplaceWithLfIntoCrlfDocument_KeepsCrlf()
    {
        var doc = Parser.Parse(MapperSamples.Crlf);

        var result = doc.Apply([new MapperEdit.ReplaceElement("b", "<select id=\"b\" resultType=\"int\">\n    SELECT 22\n      FROM DUAL\n  </select>")]);

        Assert.Equal(MapperSamples.Crlf.Replace("SELECT 2\r\n", "SELECT 22\r\n", StringComparison.Ordinal), result);
        var b = doc.Find("b")!;
        AssertChangedOnlyWithin(doc.Text, result, b.BodyRange.Start, b.BodyRange.End);
    }

    [Fact]
    public void Apply_Insert_BeforeMapperClose_UsesPreviousIndent()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        var result = doc.Apply([new MapperEdit.InsertElement("newOne", "<select id=\"newOne\">SELECT 1 FROM DUAL</select>")]);

        Assert.Equal(
            MapperSamples.EmpMapper.Replace("\n\n</mapper>", "\n\n  <select id=\"newOne\">SELECT 1 FROM DUAL</select>\n</mapper>", StringComparison.Ordinal),
            result);
        Assert.Equal("newOne", Parser.Parse(result).Elements[^1].Id);
    }

    [Fact]
    public void Apply_Insert_AfterId_PlacedAfterElement()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);
        int end = doc.Find("selectEmp")!.ElementRange.End;

        var result = doc.Apply([new MapperEdit.InsertElement("x", "<select id=\"x\">\n    SELECT 1\n  </select>", "selectEmp")]);

        Assert.Equal(doc.Text.Insert(end, "\n  <select id=\"x\">\n    SELECT 1\n  </select>"), result);
    }

    [Fact]
    public void Apply_Insert_CrlfDocument_UsesCrlf()
    {
        var doc = Parser.Parse(MapperSamples.Crlf);

        var result = doc.Apply([new MapperEdit.InsertElement("c", "<select id=\"c\">\n    SELECT 3\n  </select>")]);

        Assert.Equal(
            MapperSamples.Crlf.Replace("</mapper>", "  <select id=\"c\">\r\n    SELECT 3\r\n  </select>\r\n</mapper>", StringComparison.Ordinal),
            result);
    }

    [Fact]
    public void Apply_Insert_NoTrailingNewLine_Preserved()
    {
        var doc = Parser.Parse(MapperSamples.NoTrailingNewLine);

        var result = doc.Apply([new MapperEdit.InsertElement("x", "<select id=\"x\"/>")]);

        Assert.Equal(MapperSamples.NoTrailingNewLine.Replace("</mapper>", "  <select id=\"x\"/>\n</mapper>", StringComparison.Ordinal), result);
        Assert.EndsWith("</mapper>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_Insert_AfterTrailingComment()
    {
        var doc = Parser.Parse(MapperSamples.WithComments);

        var result = doc.Apply([new MapperEdit.InsertElement("x", "<select id=\"x\"/>")]);

        Assert.Equal(
            MapperSamples.WithComments.Replace("  <!-- 끝 주석 -->\n</mapper>", "  <!-- 끝 주석 -->\n  <select id=\"x\"/>\n</mapper>", StringComparison.Ordinal),
            result);
    }

    [Theory]
    [InlineData("<mapper></mapper>")]
    [InlineData("<mapper/>")]
    public void Apply_Insert_EmptyRoot(string text)
    {
        var doc = Parser.Parse(text);
        var nl = Environment.NewLine;

        var result = doc.Apply([new MapperEdit.InsertElement("x", "<select id=\"x\"/>")]);

        Assert.Equal($"<mapper>{nl}  <select id=\"x\"/>{nl}</mapper>", result);
        Assert.Equal("x", Parser.Parse(result).Elements.Single().Id);
    }

    [Fact]
    public void Apply_Insert_TwoAtSamePlace_KeepsListOrder()
    {
        var doc = Parser.Parse(MapperSamples.WithBom);

        var result = doc.Apply(
        [
            new MapperEdit.InsertElement("a", "<select id=\"a\"/>"),
            new MapperEdit.InsertElement("b", "<select id=\"b\"/>"),
            new MapperEdit.InsertElement("c", "<select id=\"c\"/>", "one"),
            new MapperEdit.InsertElement("d", "<select id=\"d\"/>", "one"),
        ]);

        Assert.Equal(["one", "c", "d", "a", "b"], Parser.Parse(result).Elements.Select(e => e.Id));
    }

    [Theory]
    [InlineData("empCols", "  <sql id=\"empCols\">E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME</sql>\n\n")]
    [InlineData("countEmp", "  <select id=\"countEmp\" resultType=\"int\">\n    SELECT COUNT(*) FROM EMP\n  </select>\n\n")]
    [InlineData("deleteEmp", "  <delete id=\"deleteEmp\" parameterType=\"long\">\n    DELETE FROM EMP WHERE EMPNO = #{empNo}\n  </delete>\n\n")]
    public void Apply_Delete_FirstMiddleLast_RemovesLinesAndOneBlankLine(string id, string removed)
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        var result = doc.Apply([new MapperEdit.DeleteElement(id)]);

        Assert.Equal(MapperSamples.EmpMapper.Replace(removed, string.Empty, StringComparison.Ordinal), result);
        Assert.Null(Parser.Parse(result).Find(id));
    }

    [Fact]
    public void Apply_Delete_Crlf_RemovesCrlfLines()
    {
        var doc = Parser.Parse(MapperSamples.Crlf);

        var result = doc.Apply([new MapperEdit.DeleteElement("a")]);

        Assert.Equal(
            MapperSamples.Crlf.Replace("  <select id=\"a\" resultType=\"int\">\r\n    SELECT 1\r\n      FROM DUAL\r\n  </select>\r\n", string.Empty, StringComparison.Ordinal),
            result);
    }

    [Fact]
    public void Apply_Delete_KeepsLeadingComment()
    {
        var doc = Parser.Parse(MapperSamples.WithComments);

        var result = doc.Apply([new MapperEdit.DeleteElement("a")]);

        Assert.Contains("  <!-- 문장 앞 주석 -->\n  <!-- 끝 주석 -->\n", result, StringComparison.Ordinal);
        Assert.Empty(Parser.Parse(result).Elements);
    }

    [Fact]
    public void Apply_Delete_AdjacentElements_NoOverlap()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        var result = doc.Apply([new MapperEdit.DeleteElement("updateEmpSal"), new MapperEdit.DeleteElement("deleteEmp")]);

        Assert.EndsWith("  </insert>\n\n</mapper>\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_Rename_OnlyIdValueChanges()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        var result = doc.Apply([new MapperEdit.RenameElement("selectEmp", "selectEmployee")]);

        Assert.Equal(MapperSamples.EmpMapper.Replace("<select id=\"selectEmp\" ", "<select id=\"selectEmployee\" ", StringComparison.Ordinal), result);
    }

    [Fact]
    public void Apply_Rename_SingleQuotes_EscapesAndKeepsOrder()
    {
        var doc = Parser.Parse("<mapper><select resultType='int' id='a'>X</select></mapper>");

        var result = doc.Apply([new MapperEdit.RenameElement("a", "b'&<\"")]);

        Assert.Equal("<mapper><select resultType='int' id='b&apos;&amp;&lt;\"'>X</select></mapper>", result);
        Assert.NotNull(Parser.Parse(result).Find("b'&<\""));
    }

    [Fact]
    public void Apply_MultipleEdits_AppliedFromBack()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);
        const string newCount = "<select id=\"countEmp\" resultType=\"int\">\n    SELECT COUNT(*) FROM EMP WHERE SAL > 0\n  </select>";

        var result = doc.Apply(
        [
            new MapperEdit.ReplaceElement("countEmp", newCount),
            new MapperEdit.RenameElement("insertEmp", "addEmp"),
            new MapperEdit.DeleteElement("deleteEmp"),
            new MapperEdit.InsertElement("newLast", "<select id=\"newLast\"/>"),
            new MapperEdit.InsertElement("afterList", "<select id=\"afterList\"/>", "selectEmpList"),
        ]);

        var parsed = Parser.Parse(result);
        Assert.Equal(
            ["empCols", "empMap", "selectEmpList", "afterList", "selectEmp", "countEmp", "addEmp", "updateEmpSal", "newLast"],
            parsed.Elements.Select(e => e.Id));
        Assert.Equal(newCount, parsed.Find("countEmp")!.Text);
        foreach (var id in new[] { "empCols", "empMap", "selectEmpList", "selectEmp", "updateEmpSal" })
        {
            Assert.Equal(doc.Find(id)!.Text, parsed.Find(id)!.Text);
        }
    }

    [Fact]
    public void Apply_UnknownId_Throws()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        Assert.Throws<ArgumentException>(() => doc.Apply([new MapperEdit.DeleteElement("nope")]));
        Assert.Throws<ArgumentException>(() => doc.Apply([new MapperEdit.InsertElement("x", "<select id=\"x\"/>", "nope")]));
    }

    [Fact]
    public void Apply_TwoEditsSameElement_Throws()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        Assert.Throws<ArgumentException>(() => doc.Apply(
            [new MapperEdit.ReplaceElement("selectEmp", "<select id=\"selectEmp\"/>"), new MapperEdit.RenameElement("selectEmp", "y")]));
    }

    [Fact]
    public void Apply_DeleteAndInsertAfterSameElement_Throws()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        Assert.Throws<ArgumentException>(() => doc.Apply(
            [new MapperEdit.DeleteElement("countEmp"), new MapperEdit.InsertElement("x", "<select id=\"x\"/>", "countEmp")]));
    }

    [Fact]
    public void Apply_AmbiguousDuplicateStatementId_Throws()
    {
        var doc = Parser.Parse(MapperSamples.DuplicateIds);

        Assert.Equal(MyBatisElementKind.Select, doc.Find("dup")!.Kind);
        Assert.Throws<ArgumentException>(() => doc.Apply([new MapperEdit.RenameElement("dup", "dup2")]));
    }

    [Fact]
    public void Apply_SameIdInOtherGroup_PicksStatement()
    {
        var doc = Parser.Parse("<mapper><resultMap id=\"x\" type=\"map\"/><select id=\"x\">S</select></mapper>");

        var result = doc.Apply([new MapperEdit.RenameElement("x", "y")]);

        Assert.Equal(MyBatisElementKind.Select, doc.Find("x")!.Kind);
        Assert.Equal("<mapper><resultMap id=\"x\" type=\"map\"/><select id=\"y\">S</select></mapper>", result);
    }

    // ── Check ──

    [Fact]
    public void Check_BrokenNewText_XmlErrorAndCannotWrite()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);
        var broken = doc.Apply([new MapperEdit.ReplaceElement("selectEmp", "<select id=\"selectEmp\">SELECT 1")]);

        var check = doc.Check(broken, doc.Hash);

        Assert.NotNull(check.XmlError);
        Assert.Contains("줄", check.XmlError, StringComparison.Ordinal);
        Assert.Contains("칸", check.XmlError, StringComparison.Ordinal);
        Assert.False(check.CanWrite);
    }

    [Fact]
    public void Check_NotMapperNewText_XmlError()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        var check = doc.Check(MapperSamples.NotMapper, doc.Hash);

        Assert.False(check.IsWellFormed);
        Assert.False(check.CanWrite);
    }

    [Fact]
    public void Check_DuplicateIdsInSameGroup_ListedSorted()
    {
        var dup = Parser.Parse(MapperSamples.DuplicateIds);
        var emp = Parser.Parse(MapperSamples.EmpMapper);
        var withDup = emp.Apply(
        [
            new MapperEdit.InsertElement("countEmp", "<delete id=\"countEmp\"/>"),
            new MapperEdit.InsertElement("empCols", "<sql id=\"empCols\"/>"),
        ]);

        var dupCheck = dup.Check(dup.Text, dup.Hash);
        var empCheck = emp.Check(withDup, emp.Hash);

        Assert.Equal(["dup"], dupCheck.DuplicateIds);
        Assert.False(dupCheck.CanWrite);
        Assert.Equal(["countEmp", "empCols"], empCheck.DuplicateIds);
    }

    [Fact]
    public void Check_SameIdInOtherGroups_NotDuplicate()
    {
        var doc = Parser.Parse("<mapper><resultMap id=\"x\" type=\"map\"/><sql id=\"x\">A</sql><select id=\"x\">S</select></mapper>");

        var check = doc.Check(doc.Text, doc.Hash);

        Assert.Empty(check.DuplicateIds);
        Assert.True(check.CanWrite);
    }

    [Fact]
    public void Check_DiskHashDiffers_ChangedOnDiskButCanWrite()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        var check = doc.Check(doc.Text, MapperHash.Compute(MapperSamples.EmpMapper + " "));

        Assert.True(check.ChangedOnDisk);
        Assert.True(check.CanWrite);
    }

    [Fact]
    public void Check_CleanText_CanWrite()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);

        var check = doc.Check(doc.Apply([new MapperEdit.RenameElement("countEmp", "countAll")]), doc.Hash);

        Assert.Null(check.XmlError);
        Assert.Empty(check.DuplicateIds);
        Assert.False(check.ChangedOnDisk);
        Assert.True(check.CanWrite);
    }

    // ── 도우미 ──

    /// <summary>원문과 결과의 공통 앞·뒤를 빼면 바뀐 곳이 [start, end) 안에 있다(그 밖 글자는 같다).</summary>
    private static void AssertChangedOnlyWithin(string before, string after, int start, int end)
    {
        int prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix
            && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        Assert.InRange(prefix, start, end);
        Assert.InRange(before.Length - suffix, start, end);
    }

    private static TheoryData<string> Names(IEnumerable<string> names)
    {
        var data = new TheoryData<string>();
        foreach (var name in names)
        {
            data.Add(name);
        }

        return data;
    }

    // euc-kr 선언은 CP949 로 읽고 쓴다(KS X 1001 글자는 같은 바이트).
    private static Encoding EucKr() => CodePagesEncodingProvider.Instance.GetEncoding(949)!;

    private static byte[] Encode(string text, MapperFileFormat format)
    {
        var encoding = format.EncodingName == "utf-8" ? new UTF8Encoding(false) : EucKr();
        var body = encoding.GetBytes(text);
        return format.HasBom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }
}
