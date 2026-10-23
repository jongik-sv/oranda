using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Oranda.App.Features.MyBatisWorkspace.Editor.Highlighting;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-01 F4: MyBatis 문장 원문 강조. 분류기(<see cref="MyBatisXmlClassifier"/>)는 순수 함수로 시험하고,
/// 변환기(<see cref="MyBatisSyntaxColorizer"/>)는 줄 시작 상태 배열이 증분 갱신 뒤에도 전체 다시 분류와 같은지 본다.
/// </summary>
public sealed class WorkspaceHighlightTests
{
    private static string SelectEmpList()
    {
        var text = MapperSamples.EmpMapper;
        int start = text.IndexOf("<select id=\"selectEmpList\"", StringComparison.Ordinal);
        int end = text.IndexOf("</select>", start, StringComparison.Ordinal) + "</select>".Length;
        return text[start..end];
    }

    /// <summary>줄마다 (글, 종류) 목록. 글은 그 줄에서 잘라 낸 구간 글자다.</summary>
    private static List<List<(string Text, MyBatisSpanKind Kind)>> Classify(string text)
    {
        var lines = text.Split('\n');
        var spans = MyBatisXmlClassifier.ClassifyAll(text);
        return [.. spans.Select((list, i) => list.Select(s => (lines[i].Substring(s.Start, s.Length), s.Kind)).ToList())];
    }

    private static bool Has(List<(string Text, MyBatisSpanKind Kind)> line, MyBatisSpanKind kind, string text) =>
        line.Contains((text, kind));

    [Fact]
    public void Select_statement_tag_attribute_names_and_values_are_classified()
    {
        var lines = Classify(SelectEmpList());

        var open = lines[0];
        Assert.Equal(
            [
                ("<select", MyBatisSpanKind.XmlTag),
                ("id", MyBatisSpanKind.XmlAttrName),
                ("\"selectEmpList\"", MyBatisSpanKind.XmlAttrValue),
                ("parameterType", MyBatisSpanKind.XmlAttrName),
                ("\"map\"", MyBatisSpanKind.XmlAttrValue),
                ("resultMap", MyBatisSpanKind.XmlAttrName),
                ("\"empMap\"", MyBatisSpanKind.XmlAttrValue),
                (">", MyBatisSpanKind.XmlTag),
            ],
            open);
        Assert.True(Has(lines[^1], MyBatisSpanKind.XmlTag, "</select"));
    }

    [Fact]
    public void Parameter_and_substitution_markers_are_classified()
    {
        var all = Classify(SelectEmpList()).SelectMany(l => l).ToList();

        Assert.Contains(("#{empNo}", MyBatisSpanKind.Param), all);
        Assert.Contains(("#{id}", MyBatisSpanKind.Param), all);
        Assert.Contains(("${orderBy}", MyBatisSpanKind.Substitution), all);
    }

    [Fact]
    public void Sql_keywords_and_string_literals_between_tags_are_classified()
    {
        var lines = Classify(SelectEmpList());
        var like = lines.Single(l => l.Any(s => s.Text == "LIKE"));

        Assert.Contains(("AND", MyBatisSpanKind.SqlKeyword), like);
        Assert.Contains(("LIKE", MyBatisSpanKind.SqlKeyword), like);
        Assert.Contains(("'%'", MyBatisSpanKind.SqlString), like);
        Assert.Contains(("#{ename}", MyBatisSpanKind.Param), like);
        Assert.Contains(("SELECT", MyBatisSpanKind.SqlKeyword), lines[1]);
        Assert.Contains(("<include", MyBatisSpanKind.XmlTag), lines[1]);
    }

    [Fact]
    public void Less_than_inside_a_cdata_section_is_not_a_tag()
    {
        var lines = Classify(MapperSamples.WithCData);
        var body = lines.Single(l => l.Any(s => s.Text == "#{max}"));

        Assert.DoesNotContain(body, s => s.Kind is MyBatisSpanKind.XmlTag or MyBatisSpanKind.XmlAttrName);
        Assert.Contains(("SELECT", MyBatisSpanKind.SqlKeyword), body);
        Assert.Contains(("#{max}", MyBatisSpanKind.Param), body);
        Assert.Contains(("<![CDATA[", MyBatisSpanKind.XmlCData), lines.SelectMany(l => l));
        Assert.Contains(("]]>", MyBatisSpanKind.XmlCData), lines.SelectMany(l => l));
    }

    [Fact]
    public void Xml_comments_are_comment_spans_and_sql_comments_stay_sql_comments()
    {
        var lines = Classify(MapperSamples.WithComments);

        Assert.Equal([("<!-- 맨 앞 주석 -->", MyBatisSpanKind.XmlComment)], lines[1]);
        var mixed = lines.Single(l => l.Any(s => s.Text == "/* SQL 주석 */"));
        Assert.Contains(("/* SQL 주석 */", MyBatisSpanKind.SqlComment), mixed);
        Assert.Contains(("<!-- 문장 안 xml 주석 -->", MyBatisSpanKind.XmlComment), mixed);
        Assert.Contains(("FROM", MyBatisSpanKind.SqlKeyword), mixed);
    }

    [Fact]
    public void Entities_are_classified_even_inside_a_sql_string()
    {
        var lines = Classify(MapperSamples.WithEntities);
        var body = lines.Single(l => l.Any(s => s.Text == "&gt;"));

        Assert.Contains(("&gt;", MyBatisSpanKind.XmlEntity), body);
        Assert.Contains(("&lt;", MyBatisSpanKind.XmlEntity), body);
        Assert.Contains(("&amp;", MyBatisSpanKind.XmlEntity), body);
        Assert.Contains(("'A&amp;B%'", MyBatisSpanKind.SqlString), body);
        // 문자열 구간 위에 엔티티 구간이 겹쳐 그려지도록 엔티티가 뒤에 온다.
        Assert.True(body.IndexOf(("&amp;", MyBatisSpanKind.XmlEntity)) > body.IndexOf(("'A&amp;B%'", MyBatisSpanKind.SqlString)));
    }

    [Fact]
    public void An_apostrophe_inside_a_multiline_xml_comment_does_not_turn_the_sql_below_into_a_string()
    {
        const string text = "<select id=\"a\">\n  <!-- don't\n  stop -->\n  SELECT 1 FROM DUAL\n</select>";

        var lines = Classify(text);

        Assert.Equal([("<!-- don't", MyBatisSpanKind.XmlComment)], lines[1]);
        Assert.Equal([("  stop -->", MyBatisSpanKind.XmlComment)], lines[2]);
        Assert.DoesNotContain(lines[3], s => s.Kind == MyBatisSpanKind.SqlString);
        Assert.Contains(("SELECT", MyBatisSpanKind.SqlKeyword), lines[3]);
        Assert.Contains(("FROM", MyBatisSpanKind.SqlKeyword), lines[3]);
    }

    [Fact]
    public void An_apostrophe_inside_a_tag_attribute_does_not_turn_the_sql_below_into_a_string()
    {
        const string text = "<if test=\"name != ''\">\n  AND ENAME = 1\n</if>";

        var lines = Classify(text);

        Assert.DoesNotContain(lines[1], s => s.Kind == MyBatisSpanKind.SqlString);
        Assert.Contains(("AND", MyBatisSpanKind.SqlKeyword), lines[1]);
    }

    [Fact]
    public void Substitution_inside_a_sql_string_is_drawn_last_so_it_wins_over_the_string_color()
    {
        var line = Classify("  WHERE ENAME LIKE '%${name}%'")[0];

        int str = line.IndexOf(("'%${name}%'", MyBatisSpanKind.SqlString));
        int sub = line.IndexOf(("${name}", MyBatisSpanKind.Substitution));
        Assert.True(str >= 0, "SQL 문자열 구간이 있어야 한다.");
        Assert.True(sub > str, "치환 구간이 문자열 구간 뒤(위)에 그려져야 한다.");
    }

    [Fact]
    public void A_multiline_attribute_value_keeps_the_tag_open_until_the_closing_quote()
    {
        const string text = "<if test=\"a != null\n and b != null\">\n  AND X = 1\n</if>";

        var lines = Classify(text);

        Assert.Contains(("\"a != null", MyBatisSpanKind.XmlAttrValue), lines[0]);
        Assert.Contains((" and b != null\"", MyBatisSpanKind.XmlAttrValue), lines[1]);
        Assert.Contains((">", MyBatisSpanKind.XmlTag), lines[1]);
        Assert.Contains(("AND", MyBatisSpanKind.SqlKeyword), lines[2]);
    }

    [Fact]
    public Task Editing_one_line_keeps_the_line_start_states_equal_to_a_full_reclassification() => Ui.Run(() =>
    {
        var doc = new TextDocument(SelectEmpList());
        var editor = new TextEditor { Document = doc };
        using var colorizer = new MyBatisSyntaxColorizer(editor);
        Assert.Equal(MyBatisXmlClassifier.StartStates(doc.Text), colorizer.LineStartStates);

        // SQL 한 줄 안의 글자만 바꾸면 그 줄만 다시 분류한다.
        var like = doc.Lines.First(l => doc.GetText(l).Contains("LIKE", StringComparison.Ordinal));
        doc.Replace(like.Offset, 3, "OR ");
        Assert.Equal(1, colorizer.LastReclassifiedLines);
        Assert.Equal(MyBatisXmlClassifier.StartStates(doc.Text), colorizer.LineStartStates);

        // 줄머리에 xml 주석을 열면 아래 줄들의 상태가 바뀌어 더 많은 줄을 다시 분류한다.
        var second = doc.GetLineByNumber(2);
        doc.Insert(second.Offset, "<!-- ");
        Assert.True(colorizer.LastReclassifiedLines > 1);
        Assert.Equal(MyBatisXmlClassifier.StartStates(doc.Text), colorizer.LineStartStates);
        Assert.Equal(MyBatisXmlMode.InComment, colorizer.LineStartStates[2].Xml);

        // 주석을 닫으면 상태가 원래대로 돌아온다.
        doc.Insert(second.Offset + "<!-- ".Length, "--> ");
        Assert.Equal(MyBatisXmlClassifier.StartStates(doc.Text), colorizer.LineStartStates);
        Assert.Equal(MyBatisXmlMode.Text, colorizer.LineStartStates[2].Xml);
    });

    [Fact]
    public Task Inserting_and_removing_lines_keeps_the_line_start_states_equal_to_a_full_reclassification() => Ui.Run(() =>
    {
        var doc = new TextDocument(SelectEmpList());
        var editor = new TextEditor { Document = doc };
        using var colorizer = new MyBatisSyntaxColorizer(editor);

        doc.Insert(doc.GetLineByNumber(3).Offset, "  <!--\n  a\n  b\n");
        Assert.Equal(MyBatisXmlClassifier.StartStates(doc.Text), colorizer.LineStartStates);

        doc.Remove(doc.GetLineByNumber(3).Offset, "  <!--\n  a\n".Length);
        Assert.Equal(MyBatisXmlClassifier.StartStates(doc.Text), colorizer.LineStartStates);

        doc.Text = MapperSamples.WithComments;
        Assert.Equal(MyBatisXmlClassifier.StartStates(doc.Text), colorizer.LineStartStates);
    });

    [Fact]
    public Task The_colorizer_adds_itself_to_the_line_transformers_and_leaves_when_disposed() => Ui.Run(() =>
    {
        var editor = new TextEditor { Document = new TextDocument(SelectEmpList()) };
        var transformers = editor.TextArea.TextView.LineTransformers;

        var colorizer = new MyBatisSyntaxColorizer(editor);
        Assert.Contains(colorizer, transformers);

        colorizer.Dispose();
        Assert.DoesNotContain(colorizer, transformers);
        colorizer.Dispose(); // 두 번 불러도 괜찮다
    });

    [Fact]
    public Task Colors_come_from_the_sql_palette_for_sql_spans_and_from_theme_keys_for_xml_spans() => Ui.Run(() =>
    {
        var editor = new TextEditor { Document = new TextDocument(SelectEmpList()) };
        using var colorizer = new MyBatisSyntaxColorizer(editor);
        var variant = editor.ActualThemeVariant ?? Application.Current!.ActualThemeVariant;
        var palette = MyBatisSyntaxColorizer.IsDark(variant) ? SqlSyntaxColorizer.Dark : SqlSyntaxColorizer.Light;

        Assert.Same(palette.Keyword, colorizer.BrushOf(MyBatisSpanKind.SqlKeyword));
        Assert.Same(palette.String, colorizer.BrushOf(MyBatisSpanKind.SqlString));
        Assert.Same(palette.String, colorizer.BrushOf(MyBatisSpanKind.XmlAttrValue));
        Assert.Same(palette.Comment, colorizer.BrushOf(MyBatisSpanKind.XmlComment));

        // xml 쪽은 앱 리소스의 아이콘 톤 브러시여야 한다(값을 직접 쓰지 않는다).
        foreach (var kind in new[] { MyBatisSpanKind.XmlTag, MyBatisSpanKind.XmlAttrName, MyBatisSpanKind.XmlCData, MyBatisSpanKind.XmlEntity, MyBatisSpanKind.Param, MyBatisSpanKind.Substitution })
        {
            Assert.NotNull(colorizer.BrushOf(kind));
        }

        Assert.True(Application.Current!.TryGetResource("OrandaIconPurpleBrush", variant, out var purple));
        Assert.Same(purple, colorizer.BrushOf(MyBatisSpanKind.XmlTag));
        Assert.True(Application.Current!.TryGetResource("OrandaIconOrangeBrush", variant, out var orange));
        Assert.Same(orange, colorizer.BrushOf(MyBatisSpanKind.Substitution));
        Assert.IsAssignableFrom<IBrush>(colorizer.BrushOf(MyBatisSpanKind.Param));
    });
}
