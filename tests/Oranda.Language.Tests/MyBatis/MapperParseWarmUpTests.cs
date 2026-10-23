using Oranda.Language.MyBatis;

namespace Oranda.Language.Tests.MyBatis;

/// <summary>
/// TSK-03-11: 매퍼를 연 뒤 그 문장 모양으로 파서 예측 캐시를 데우는 <see cref="MapperParseWarmUp"/>.
/// 시간은 재지 않는다(성능 수치는 TSK-04-02 결과서). 무엇을 몇 번 분석하는지·취소·오류 문장만 본다.
/// </summary>
public class MapperParseWarmUpTests
{
    private const string Mapper = """
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="t.Emp">
          <sql id="cols">E.EMPNO, E.ENAME</sql>
          <resultMap id="m" type="map"><id column="EMPNO" property="empNo"/></resultMap>
          <select id="withIf" resultType="map">
            SELECT <include refid="cols"/> FROM EMP E
            <where><if test="ename != null">AND E.ENAME = #{ename}</if></where>
          </select>
          <select id="plain" resultType="map">SELECT * FROM DEPT</select>
          <update id="qualifiedInclude">UPDATE EMP SET SAL = 1 WHERE EMPNO IN (SELECT <include refid="t.Emp.cols"/> FROM EMP)</update>
          <delete id="missingInclude">DELETE FROM EMP WHERE <include refid="other.Mapper.where"/></delete>
          <insert id="broken">INSERT INTO EMP VALUES (</insert>
        </mapper>
        """;

    private static IMyBatisMapperDocument Parse(string text) => new MapperParser().Parse(text);

    [Fact]
    public void Each_statement_is_parsed_once_per_distinct_shape()
    {
        // withIf: 모두 켬(조합 1번)과 모두 끔이 달라 2번, 나머지 4문장은 조건이 없어 한 모양 = 1번씩. sql·resultMap 은 문장이 아니다.
        Assert.Equal(6, MapperParseWarmUp.Run(Parse(Mapper), CancellationToken.None));
    }

    [Fact]
    public void A_mapper_without_statements_parses_nothing()
    {
        Assert.Equal(0, MapperParseWarmUp.Run(Parse("<mapper namespace=\"t.E\"><sql id=\"a\">X</sql></mapper>"), CancellationToken.None));
    }

    [Fact]
    public void Same_sql_in_two_statements_is_parsed_once()
    {
        var text = "<mapper namespace=\"t.E\"><select id=\"a\">SELECT 1 FROM dual</select><select id=\"b\">SELECT 1 FROM dual</select></mapper>";
        Assert.Equal(1, MapperParseWarmUp.Run(Parse(text), CancellationToken.None));
    }

    [Fact]
    public void A_cancelled_warm_up_stops_with_operation_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MapperParseWarmUp.Run(Parse(Mapper), cts.Token));
    }

    [Fact]
    public async Task Start_runs_on_a_low_priority_background_thread_and_reports_the_count()
    {
        int caller = Environment.CurrentManagedThreadId;
        var task = MapperParseWarmUp.Start(Parse(Mapper));
        Assert.Same(task, MapperParseWarmUp.Last);
        try
        {
            Assert.Equal(6, await task.WaitAsync(TimeSpan.FromSeconds(60)));
            var (id, priority, background) = MapperParseWarmUp.LastThread!.Value;
            Assert.NotEqual(caller, id);
            Assert.Equal(ThreadPriority.BelowNormal, priority);
            Assert.True(background);
        }
        catch (TaskCanceledException)
        {
            // 다른 시험이 같은 때 새 데우기를 시작하면 이것은 취소된다(데우기는 앱에 하나).
        }
    }

    [Fact]
    public async Task Cancel_stops_a_running_warm_up()
    {
        var text = "<mapper namespace=\"t.E\">"
            + string.Concat(Enumerable.Range(0, 400).Select(i => $"<select id=\"s{i}\">SELECT C{i} FROM T{i} WHERE A{i} = #{{a}}</select>"))
            + "</mapper>";
        var task = MapperParseWarmUp.Start(Parse(text));
        MapperParseWarmUp.Cancel();

        // 다 돌기 전에 멈추면 취소, 그 전에 끝났으면 400개 모두 분석한 결과다.
        try
        {
            Assert.Equal(400, await task.WaitAsync(TimeSpan.FromSeconds(60)));
        }
        catch (TaskCanceledException)
        {
            Assert.True(task.IsCanceled);
        }
    }

    [Fact]
    public void Start_rejects_null()
    {
        Action start = () => MapperParseWarmUp.Start(null!); // 작업을 돌려주기 전에 바로 던진다
        Assert.Throws<ArgumentNullException>(start);
    }
}
