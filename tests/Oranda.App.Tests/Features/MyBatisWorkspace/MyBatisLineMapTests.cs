using System.Diagnostics;
using System.Globalization;
using Oranda.App.Features.MyBatisWorkspace.Expanded;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>MyBatis 쿼리 탭 원문 ↔ 펼친 SQL 줄 대응표(<see cref="MyBatisLineMap"/>) 시험. 표본은 평가기 실측 결과를 그대로 옮겼다.</summary>
public sealed class MyBatisLineMapTests
{
    // MapperSamples.EmpMapper 의 selectEmpList 원문.
    private const string SampleSource = """
        <select id="selectEmpList" parameterType="map" resultMap="empMap">
          SELECT <include refid="empCols"/>
            FROM EMP E
            JOIN DEPT D ON D.DEPTNO = E.DEPTNO
          <where>
            <if test="empNo != null">
              AND E.EMPNO = #{empNo}
            </if>
            <if test="ename != null and ename != ''">
              AND E.ENAME LIKE '%' || #{ename} || '%'
            </if>
            <if test="deptIds != null and deptIds.size() > 0">
              AND E.DEPTNO IN
              <foreach collection="deptIds" item="id" open="(" separator="," close=")">
                #{id}
              </foreach>
            </if>
          </where>
          ORDER BY ${orderBy}
        </select>
        """;

    // 같은 문장을 평가해 펼친 SQL. 줄 끝 공백과 공백뿐인 줄까지 실측 그대로 둔다.
    private static readonly string SampleExpanded = string.Join(
        "\n",
        "SELECT  E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME ",
        "    FROM EMP E",
        "    JOIN DEPT D ON D.DEPTNO = E.DEPTNO",
        "   WHERE  E.EMPNO = :empNo",
        "    ",
        "    ",
        "      AND E.ENAME LIKE '%' || :ename || '%'",
        "    ",
        "    ",
        "      AND E.DEPTNO IN",
        "      (",
        "        :id_1",
        "      ,",
        "        :id_2",
        "      ) ",
        "  ORDER BY E.EMPNO");

    private static MyBatisLineMap SampleMap() => MyBatisLineMap.Build(SampleSource, SampleExpanded);

    [Fact]
    public void Sample_mapper_lines_that_look_alike_map_one_to_one()
    {
        var map = SampleMap();

        Assert.Equal(20, map.SourceLineCount);
        Assert.Equal(16, map.ExpandedLineCount);

        // (원문 줄, 펼친 줄): FROM EMP E, JOIN, AND E.ENAME LIKE, AND E.DEPTNO IN.
        (int Source, int Expanded)[] pairs = [(2, 1), (3, 2), (9, 6), (12, 9)];
        foreach (var (s, e) in pairs)
        {
            Assert.Equal(e, map.ToExpanded(s), 6);
            Assert.Equal(s, map.ToSource(e), 6);

            // 같은 줄 안쪽도 1:1 로 따라간다.
            Assert.Equal(e + 0.5, map.ToExpanded(s + 0.5), 6);
        }
    }

    [Fact]
    public void Ends_map_to_ends_and_out_of_range_values_are_clamped()
    {
        var map = SampleMap();

        Assert.Equal(0, map.ToExpanded(0), 6);
        Assert.Equal(map.ExpandedLineCount, map.ToExpanded(map.SourceLineCount), 6);
        Assert.Equal(0, map.ToSource(0), 6);
        Assert.Equal(map.SourceLineCount, map.ToSource(map.ExpandedLineCount), 6);

        Assert.Equal(0, map.ToExpanded(-5), 6);
        Assert.Equal(map.ExpandedLineCount, map.ToExpanded(map.SourceLineCount + 10), 6);
        Assert.Equal(0, map.ToSource(-0.5), 6);
        Assert.Equal(map.SourceLineCount, map.ToSource(map.ExpandedLineCount + 3), 6);
    }

    [Fact]
    public void Mapping_never_decreases_in_either_direction()
    {
        var map = SampleMap();

        // 기준점은 두 축 모두 엄격히 증가한다.
        for (int k = 1; k < map.Anchors.Count; k++)
        {
            Assert.True(map.Anchors[k].Source >= map.Anchors[k - 1].Source);
            Assert.True(map.Anchors[k].Expanded >= map.Anchors[k - 1].Expanded);
            Assert.NotEqual(map.Anchors[k - 1], map.Anchors[k]);
        }

        double prev = double.NegativeInfinity;
        for (int k = 0; k <= map.SourceLineCount * 4; k++)
        {
            double y = map.ToExpanded(k / 4.0);
            Assert.True(y >= prev);
            prev = y;
        }

        prev = double.NegativeInfinity;
        for (int k = 0; k <= map.ExpandedLineCount * 4; k++)
        {
            double x = map.ToSource(k / 4.0);
            Assert.True(x >= prev);
            prev = x;
        }
    }

    [Fact]
    public void A_match_on_the_first_or_last_line_keeps_its_own_anchor()
    {
        // 원문 0줄은 태그뿐이고 1줄 SELECT 가 펼친 0줄과 같다: 원문 1줄 맨 위 = 펼친 맨 위(반 줄 어긋나지 않는다).
        var map = MyBatisLineMap.Build("<select id=\"x\">\nSELECT 1\nFROM DUAL\n</select>", "SELECT 1\nFROM DUAL");
        Assert.Equal(0, map.ToExpanded(1), 6);
        Assert.Equal(1, map.ToExpanded(2), 6);
        Assert.Equal(2, map.ToExpanded(3), 6); // FROM DUAL 끝 = 펼친 끝
        Assert.Equal(map.ExpandedLineCount, map.ToExpanded(map.SourceLineCount), 6);
        Assert.Equal(1, map.ToSource(0), 6);
        Assert.Equal(2, map.ToSource(1), 6);
        Assert.False(double.IsNaN(map.ToSource(2)));
    }

    [Fact]
    public void Round_trip_returns_to_the_same_position()
    {
        var map = SampleMap();

        for (int k = 0; k <= map.SourceLineCount * 8; k++)
        {
            double x = k / 8.0;
            Assert.True(Math.Abs(map.ToSource(map.ToExpanded(x)) - x) < 1e-6);
        }

        for (int k = 0; k <= map.ExpandedLineCount * 8; k++)
        {
            double y = k / 8.0;
            Assert.True(Math.Abs(map.ToExpanded(map.ToSource(y)) - y) < 1e-6);
        }
    }

    [Fact]
    public void Empty_and_single_line_texts_do_not_throw_or_produce_NaN()
    {
        (string Source, string Expanded)[] cases = [("", ""), ("", "SELECT 1"), ("SELECT 1", ""), ("SELECT 1", "SELECT 1"), ("<select id=\"a\"/>", "x")];
        foreach (var (source, expanded) in cases)
        {
            var map = MyBatisLineMap.Build(source, expanded);
            foreach (double v in new[] { -1.0, 0.0, 0.5, 1.0, 7.0 })
            {
                Assert.False(double.IsNaN(map.ToExpanded(v)));
                Assert.False(double.IsNaN(map.ToSource(v)));
            }
        }

        // 한쪽이 0줄이면 항상 0.
        var oneSided = MyBatisLineMap.Build("", "a\nb");
        Assert.Equal(0, oneSided.SourceLineCount);
        Assert.Equal(2, oneSided.ExpandedLineCount);
        Assert.Equal(0, oneSided.ToExpanded(0.5), 6);
        Assert.Equal(0, oneSided.ToSource(1.5), 6);

        Assert.Equal(0, MyBatisLineMap.Empty.SourceLineCount);
        Assert.Equal(0, MyBatisLineMap.Empty.ExpandedLineCount);
        Assert.Equal(0, MyBatisLineMap.Empty.ToExpanded(3), 6);

        // 같은 한 줄끼리는 그대로 대응한다.
        var same = MyBatisLineMap.Build("SELECT 1", "SELECT 1");
        Assert.Equal(1, same.SourceLineCount);
        Assert.Equal(0.5, same.ToExpanded(0.5), 6);
    }

    [Fact]
    public void Line_endings_are_split_and_a_trailing_newline_counts_as_a_line()
    {
        Assert.Equal(2, MyBatisLineMap.Build("a\n", "x").SourceLineCount);
        Assert.Equal(4, MyBatisLineMap.Build("a\r\nb\rc\n", "x").SourceLineCount);
        Assert.Equal(1, MyBatisLineMap.Build("a", "x").SourceLineCount);
        Assert.Equal(3, MyBatisLineMap.Build("a", "x\r\n\r\ny").ExpandedLineCount);
    }

    [Fact]
    public void Tags_params_binds_and_entities_are_normalized_before_matching()
    {
        // 원문의 태그 줄(속성 값 안에 「>」 포함)·#{…}·&lt; 가 펼친 SQL 의 :b·&lt; 해석 결과와 같은 줄로 맞아야 한다.
        const string source = "SELECT 1\n<if test=\"x.size() > 0\">\n  AND A &lt; #{b}\n</if>\nFROM X";
        const string expanded = "SELECT 1\n  AND A < :b\n\nFROM X";
        var map = MyBatisLineMap.Build(source, expanded);

        // (4, 3) 은 FROM X 줄끼리의 기준점이다. 비율 방식이면 3.2 가 된다.
        Assert.Equal(3, map.ToExpanded(4), 6);
        Assert.Equal(4, map.ToSource(3), 6);
    }

    [Fact]
    public void Lines_without_any_match_fall_back_to_proportional_mapping()
    {
        var map = MyBatisLineMap.Build("a\nb\nc\nd", "x\ny");

        Assert.Equal(2, map.Anchors.Count);
        Assert.Equal(1, map.ToExpanded(2), 6);
        Assert.Equal(2, map.ToSource(1), 6);
        Assert.Equal(0.5, map.ToExpanded(1), 6);
    }

    [Fact]
    public void Inputs_over_the_cell_limit_skip_lcs_and_keep_only_two_anchors()
    {
        const int lines = 2100; // 2100 * 2100 > MaxLcsCells
        Assert.True((long)lines * lines > MyBatisLineMap.MaxLcsCells);

        string source = string.Join("\n", Enumerable.Range(0, lines).Select(i => string.Create(CultureInfo.InvariantCulture, $"src line {i}")));
        string expanded = string.Join("\n", Enumerable.Range(0, lines).Select(i => string.Create(CultureInfo.InvariantCulture, $"exp line {i}")));

        var watch = Stopwatch.StartNew();
        var map = MyBatisLineMap.Build(source, expanded);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(2, map.Anchors.Count);
        Assert.Equal(lines, map.SourceLineCount);
        Assert.Equal(lines, map.ExpandedLineCount);
        Assert.Equal(1050, map.ToExpanded(1050), 6);
    }
}
