using System.Text;

namespace Oranda.Testing.MyBatis;

/// <summary>
/// MyBatis 매퍼 xml 표본 모음(TSK-01-02, design.md 9장). 읽기·파싱·저장 시험이 같은 원문을 쓰도록 한곳에 둔다.
/// 모든 표본은 소스 파일 줄 끝과 무관하게 LF 로 정규화한다(<see cref="Crlf"/> 만 명시적으로 CRLF).
/// BOM 은 글자가 아니라 바이트이므로 <see cref="Bytes"/> 로 붙인다.
/// </summary>
public static class MapperSamples
{
    /// <summary>
    /// 시안 매퍼 원문(LF, BOM 없음, 끝 줄바꿈 있음). <c>selectEmpList</c> 는 시안(Main.dc.html)의 RAW 배열 그대로,
    /// <c>updateEmpSal</c> 은 시안(Combos.dc.html)처럼 <c>&lt;set&gt;</c> 없이 SET 뒤에 if 두 개를 둬서 조합 검사 오류 2개가 나는 원문이다.
    /// </summary>
    public static string EmpMapper { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE mapper PUBLIC "-//mybatis.org//DTD Mapper 3.0//EN" "http://mybatis.org/dtd/mybatis-3-mapper.dtd">
        <!-- 사원 조회·변경 매퍼(시안) -->
        <mapper namespace="com.acme.hr.EmpMapper">

          <sql id="empCols">E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME</sql>

          <resultMap id="empMap" type="map">
            <id column="EMPNO" property="empNo"/>
            <result column="ENAME" property="ename"/>
            <result column="JOB" property="job"/>
            <result column="SAL" property="sal"/>
            <result column="DNAME" property="dname"/>
          </resultMap>

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

          <select id="selectEmp" parameterType="long" resultMap="empMap">
            SELECT <include refid="empCols"/>
              FROM EMP E
              JOIN DEPT D ON D.DEPTNO = E.DEPTNO
             WHERE E.EMPNO = #{empNo}
          </select>

          <select id="countEmp" resultType="int">
            SELECT COUNT(*) FROM EMP
          </select>

          <insert id="insertEmp" parameterType="map">
            INSERT INTO EMP (EMPNO, ENAME, JOB, SAL, DEPTNO)
            VALUES (#{empNo}, #{ename}, #{job}, #{sal}, #{deptNo})
          </insert>

        <update id="updateEmpSal" parameterType="map">
          UPDATE EMP
             SET
          <if test="sal != null">
               SAL = #{sal},
          </if>
          <if test="comm != null">
               COMM = #{comm}
          </if>
           WHERE EMPNO = #{empNo}
        </update>

          <delete id="deleteEmp" parameterType="long">
            DELETE FROM EMP WHERE EMPNO = #{empNo}
          </delete>

        </mapper>
        """) + "\n";

    /// <summary>BOM 시험용 짧은 매퍼. BOM 은 <see cref="Bytes"/>(text, bom: true) 로 붙인다.</summary>
    public static string WithBom { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="t.WithBom">
          <select id="one" resultType="int">SELECT 1 FROM DUAL</select>
        </mapper>
        """) + "\n";

    /// <summary>줄 끝이 CRLF 인 매퍼(원문에 <c>\r\n</c> 을 명시적으로 넣는다).</summary>
    public static string Crlf { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="t.Crlf">
          <select id="a" resultType="int">
            SELECT 1
              FROM DUAL
          </select>
          <select id="b" resultType="int">
            SELECT 2
              FROM DUAL
          </select>
        </mapper>
        """).Replace("\n", "\r\n") + "\r\n";

    /// <summary>CDATA 구역 안에 <c>&lt;</c> 가 든 문장.</summary>
    public static string WithCData { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="t.WithCData">
          <select id="lowSal" resultType="map">
            <![CDATA[
            SELECT EMPNO, SAL FROM EMP WHERE SAL < #{max} AND COMM <> 0
            ]]>
          </select>
        </mapper>
        """) + "\n";

    /// <summary>주석이 여러 군데(매퍼 앞·안, 문장 안)에 있는 매퍼.</summary>
    public static string WithComments { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <!-- 맨 앞 주석 -->
        <mapper namespace="t.WithComments">
          <!-- 문장 앞 주석 -->
          <select id="a" resultType="int">
            SELECT 1 /* SQL 주석 */ FROM DUAL <!-- 문장 안 xml 주석 -->
          </select>
          <!-- 끝 주석 -->
        </mapper>
        """) + "\n";

    /// <summary>엔티티(<c>&amp;lt;</c>·<c>&amp;gt;</c>·<c>&amp;amp;</c>)가 든 문장.</summary>
    public static string WithEntities { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="t.WithEntities">
          <select id="range" resultType="map">
            SELECT EMPNO FROM EMP WHERE SAL &gt; #{lo} AND SAL &lt; #{hi} AND ENAME LIKE 'A&amp;B%'
          </select>
        </mapper>
        """) + "\n";

    /// <summary>한글 주석·문자열 리터럴이 든 매퍼(id 는 영문).</summary>
    public static string Korean { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <!-- 한글 주석: 사원 이름 검색 -->
        <mapper namespace="t.Korean">
          <select id="byName" resultType="map">
            SELECT EMPNO, '홍길동' AS NAME_KO FROM EMP WHERE ENAME = #{ename} -- 이름으로 찾기
          </select>
        </mapper>
        """) + "\n";

    /// <summary>DOCTYPE 이 있는 최소 매퍼.</summary>
    public static string WithDoctype { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE mapper PUBLIC "-//mybatis.org//DTD Mapper 3.0//EN" "http://mybatis.org/dtd/mybatis-3-mapper.dtd">
        <mapper namespace="t.WithDoctype">
          <select id="one" resultType="int">SELECT 1 FROM DUAL</select>
        </mapper>
        """) + "\n";

    /// <summary>namespace 속성이 없는 매퍼.</summary>
    public static string NoNamespace { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper>
          <select id="one" resultType="int">SELECT 1 FROM DUAL</select>
        </mapper>
        """) + "\n";

    /// <summary>끝 줄바꿈이 없는 매퍼(마지막 글자가 <c>&gt;</c>).</summary>
    public static string NoTrailingNewLine { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="t.NoTrailingNewLine">
          <select id="one" resultType="int">SELECT 1 FROM DUAL</select>
        </mapper>
        """);

    /// <summary>잘 짜인 xml 이지만 매퍼가 아닌 파일(루트 <c>&lt;beans&gt;</c>).</summary>
    public static string NotMapper { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <beans>
          <bean id="dataSource" class="x.Y"/>
        </beans>
        """) + "\n";

    /// <summary>닫히지 않은 태그가 있는 깨진 xml.</summary>
    public static string Broken { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="t.Broken">
          <select id="a" resultType="int">
            SELECT 1 FROM DUAL
        </mapper>
        """) + "\n";

    /// <summary>같은 id 의 문장이 둘인 매퍼(select 와 update).</summary>
    public static string DuplicateIds { get; } = Lf("""
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="t.DuplicateIds">
          <select id="dup" resultType="int">SELECT 1 FROM DUAL</select>
          <update id="dup">UPDATE EMP SET SAL = SAL</update>
        </mapper>
        """) + "\n";

    /// <summary>잘 짜인 xml 매퍼 표본 이름 → 원문(시험 Theory 용). <see cref="NotMapper"/>·<see cref="Broken"/> 제외.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(EmpMapper)] = EmpMapper,
        [nameof(WithBom)] = WithBom,
        [nameof(Crlf)] = Crlf,
        [nameof(WithCData)] = WithCData,
        [nameof(WithComments)] = WithComments,
        [nameof(WithEntities)] = WithEntities,
        [nameof(Korean)] = Korean,
        [nameof(WithDoctype)] = WithDoctype,
        [nameof(NoNamespace)] = NoNamespace,
        [nameof(NoTrailingNewLine)] = NoTrailingNewLine,
        [nameof(DuplicateIds)] = DuplicateIds,
    };

    /// <summary>매퍼로 읽을 수 없는 표본 이름 → 원문(<see cref="NotMapper"/>, <see cref="Broken"/>).</summary>
    public static IReadOnlyDictionary<string, string> Invalid { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(NotMapper)] = NotMapper,
        [nameof(Broken)] = Broken,
    };

    /// <summary>글자를 UTF-8 바이트로 바꾼다. <paramref name="bom"/> 이 참이면 앞에 BOM(EF BB BF)을 붙인다.</summary>
    public static byte[] Bytes(string text, bool bom = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var body = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        if (!bom)
        {
            return body;
        }

        var result = new byte[body.Length + 3];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        body.CopyTo(result, 3);
        return result;
    }

    // 소스 파일의 줄 끝(CRLF 일 수 있음)에 기대지 않도록 LF 로 통일한다.
    private static string Lf(string text) => text.Replace("\r\n", "\n");
}
