using Oranda.App.Features.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>
/// 계획 글 대조(PlanSqlMatch)를 실제 서버(로컬 시험 DB, Oracle 26ai Free)가 낸 DBMS_XPLAN.DISPLAY_CURSOR 글로 고정한다.
/// 각 항목 = (이름, 힌트 사본 원문, 서버에 간 글(ExecutionResult.Statement.Text), 서버가 낸 글).
/// 서버가 보인 모양: 줄바꿈·탭은 공백이 되고, 80자쯤에서 줄이 끊기며, 1000자쯤에서 본문이 낱말 중간에서 잘리고,
/// cursor_sharing=FORCE 세션은 리터럴을 :"SYS_B_n" 으로 바꾼다. 치환 변수는 치환한 글이 간다.
/// </summary>
public class PlanSqlMatchRealServerTests
{
    private static readonly (string Name, string Copy, string Sent, string Shown)[] Real =
    [
        ("short",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual",
         "SQL_ID  ga9g5hadhgxg5, child number 0\n-------------------------------------\nSELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual\n \nPlan hash value: 272002086\n \n--------------------------------------------------------------------------\n| Id  | Operation         | Name | Rows  | Bytes | Cost (%CPU)| Time     |\n--------------------------------------------------------------------------\n|   0 | SELECT STATEMENT  |      |       |       |     2 (100)|          |\n|   1 |  TABLE ACCESS FULL| DUAL |     1 |     2 |     2   (0)| 00:00:01 |\n--------------------------------------------------------------------------\n \n"),
        ("long200",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy AS a_rather_long_alias_name_number_01,\n       dummy AS a_rather_long_alias_name_number_02,\n\tdummy AS a_rather_long_alias_name_number_03,\n       dummy AS a_rather_long_alias_name_number_04\n  FROM dual\n WHERE dummy = 'X' OR 1 = 1",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy AS a_rather_long_alias_name_number_01,\n       dummy AS a_rather_long_alias_name_number_02,\n\tdummy AS a_rather_long_alias_name_number_03,\n       dummy AS a_rather_long_alias_name_number_04\n  FROM dual\n WHERE dummy = 'X' OR 1 = 1",
         "SQL_ID  9jkktrvzfbbqw, child number 0\n-------------------------------------\nSELECT /*+ GATHER_PLAN_STATISTICS */ dummy AS \na_rather_long_alias_name_number_01,        dummy AS \na_rather_long_alias_name_number_02,  dummy AS \na_rather_long_alias_name_number_03,        dummy AS \na_rather_long_alias_name_number_04   FROM dual  WHERE dummy = 'X' OR 1 \n= 1\n \nPlan hash value: 272002086\n \n--------------------------------------------------------------------------\n| Id  | Operation         | Name | Rows  | Bytes | Cost (%CPU)| Time     |\n--------------------------------------------------------------------------\n|   0 | SELECT STATEMENT  |      |       |       |     2 (100)|          |\n|   1 |  TABLE ACCESS FULL| DUAL |     1 |     2 |     2   (0)| 00:00:01 |\n--------------------------------------------------------------------------\n \n"),
        ("long1500",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy AS a_rather_long_alias_name_number_01,\n       dummy AS a_rather_long_alias_name_number_02,\n       dummy AS a_rather_long_alias_name_number_03,\n       dummy AS a_rather_long_alias_name_number_04,\n       dummy AS a_rather_long_alias_name_number_05,\n       dummy AS a_rather_long_alias_name_number_06,\n       dummy AS a_rather_long_alias_name_number_07,\n       dummy AS a_rather_long_alias_name_number_08,\n       dummy AS a_rather_long_alias_name_number_09,\n       dummy AS a_rather_long_alias_name_number_10,\n       dummy AS a_rather_long_alias_name_number_11,\n       dummy AS a_rather_long_alias_name_number_12,\n       dummy AS a_rather_long_alias_name_number_13,\n       dummy AS a_rather_long_alias_name_number_14,\n       dummy AS a_rather_long_alias_name_number_15,\n       dummy AS a_rather_long_alias_name_number_16,\n       dummy AS a_rather_long_alias_name_number_17,\n       dummy AS a_rather_long_alias_name_number_18,\n       dummy AS a_rather_long_alias_name_number_19,\n       dummy AS a_rather_long_alias_name_number_20,\n       dummy AS a_rather_long_alias_name_number_21,\n       dummy AS a_rather_long_alias_name_number_22,\n       dummy AS a_rather_long_alias_name_number_23,\n       dummy AS a_rather_long_alias_name_number_24,\n       dummy AS a_rather_long_alias_name_number_25,\n       dummy AS a_rather_long_alias_name_number_26,\n       dummy AS a_rather_long_alias_name_number_27,\n       dummy AS a_rather_long_alias_name_number_28,\n       dummy AS a_rather_long_alias_name_number_29,\n       dummy AS a_rather_long_alias_name_number_30,\n       dummy AS a_rather_long_alias_name_number_31,\n       dummy AS a_rather_long_alias_name_number_32,\n       dummy AS a_rather_long_alias_name_number_33,\n       dummy AS a_rather_long_alias_name_number_34,\n       dummy AS a_rather_long_alias_name_number_35,\n       dummy AS a_rather_long_alias_name_number_36,\n       dummy AS a_rather_long_alias_name_number_37,\n       dummy AS a_rather_long_alias_name_number_38,\n       dummy AS a_rather_long_alias_name_number_39,\n       dummy AS a_rather_long_alias_name_number_40\n  FROM dual",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy AS a_rather_long_alias_name_number_01,\n       dummy AS a_rather_long_alias_name_number_02,\n       dummy AS a_rather_long_alias_name_number_03,\n       dummy AS a_rather_long_alias_name_number_04,\n       dummy AS a_rather_long_alias_name_number_05,\n       dummy AS a_rather_long_alias_name_number_06,\n       dummy AS a_rather_long_alias_name_number_07,\n       dummy AS a_rather_long_alias_name_number_08,\n       dummy AS a_rather_long_alias_name_number_09,\n       dummy AS a_rather_long_alias_name_number_10,\n       dummy AS a_rather_long_alias_name_number_11,\n       dummy AS a_rather_long_alias_name_number_12,\n       dummy AS a_rather_long_alias_name_number_13,\n       dummy AS a_rather_long_alias_name_number_14,\n       dummy AS a_rather_long_alias_name_number_15,\n       dummy AS a_rather_long_alias_name_number_16,\n       dummy AS a_rather_long_alias_name_number_17,\n       dummy AS a_rather_long_alias_name_number_18,\n       dummy AS a_rather_long_alias_name_number_19,\n       dummy AS a_rather_long_alias_name_number_20,\n       dummy AS a_rather_long_alias_name_number_21,\n       dummy AS a_rather_long_alias_name_number_22,\n       dummy AS a_rather_long_alias_name_number_23,\n       dummy AS a_rather_long_alias_name_number_24,\n       dummy AS a_rather_long_alias_name_number_25,\n       dummy AS a_rather_long_alias_name_number_26,\n       dummy AS a_rather_long_alias_name_number_27,\n       dummy AS a_rather_long_alias_name_number_28,\n       dummy AS a_rather_long_alias_name_number_29,\n       dummy AS a_rather_long_alias_name_number_30,\n       dummy AS a_rather_long_alias_name_number_31,\n       dummy AS a_rather_long_alias_name_number_32,\n       dummy AS a_rather_long_alias_name_number_33,\n       dummy AS a_rather_long_alias_name_number_34,\n       dummy AS a_rather_long_alias_name_number_35,\n       dummy AS a_rather_long_alias_name_number_36,\n       dummy AS a_rather_long_alias_name_number_37,\n       dummy AS a_rather_long_alias_name_number_38,\n       dummy AS a_rather_long_alias_name_number_39,\n       dummy AS a_rather_long_alias_name_number_40\n  FROM dual",
         "SQL_ID  4b8kygaj2cg1j, child number 0\n-------------------------------------\nSELECT /*+ GATHER_PLAN_STATISTICS */ dummy AS \na_rather_long_alias_name_number_01,        dummy AS \na_rather_long_alias_name_number_02,        dummy AS \na_rather_long_alias_name_number_03,        dummy AS \na_rather_long_alias_name_number_04,        dummy AS \na_rather_long_alias_name_number_05,        dummy AS \na_rather_long_alias_name_number_06,        dummy AS \na_rather_long_alias_name_number_07,        dummy AS \na_rather_long_alias_name_number_08,        dummy AS \na_rather_long_alias_name_number_09,        dummy AS \na_rather_long_alias_name_number_10,        dummy AS \na_rather_long_alias_name_number_11,        dummy AS \na_rather_long_alias_name_number_12,        dummy AS \na_rather_long_alias_name_number_13,        dummy AS \na_rather_long_alias_name_number_14,        dummy AS \na_rather_long_alias_name_number_15,        dummy AS \na_rather_long_alias_name_number_16,        dummy AS \na_rather_long_alias_name_number_17,        dummy AS \na_rather_long_alias_name_number_18,        dummy AS a_rather_long_alia\n \nPlan hash value: 272002086\n \n--------------------------------------------------------------------------\n| Id  | Operation         | Name | Rows  | Bytes | Cost (%CPU)| Time     |\n--------------------------------------------------------------------------\n|   0 | SELECT STATEMENT  |      |       |       |     2 (100)|          |\n|   1 |  TABLE ACCESS FULL| DUAL |     1 |     2 |     2   (0)| 00:00:01 |\n--------------------------------------------------------------------------\n \n"),
        ("subst",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ '&v' AS c, dummy FROM dual",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ 'hello' AS c, dummy FROM dual",
         "SQL_ID  65hpkru17cycw, child number 0\n-------------------------------------\nSELECT /*+ GATHER_PLAN_STATISTICS */ 'hello' AS c, dummy FROM dual\n \nPlan hash value: 272002086\n \n--------------------------------------------------------------------------\n| Id  | Operation         | Name | Rows  | Bytes | Cost (%CPU)| Time     |\n--------------------------------------------------------------------------\n|   0 | SELECT STATEMENT  |      |       |       |     2 (100)|          |\n|   1 |  TABLE ACCESS FULL| DUAL |     1 |     2 |     2   (0)| 00:00:01 |\n--------------------------------------------------------------------------\n \n"),
        ("subst2",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ &c AS c FROM dual",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy AS c FROM dual",
         "SQL_ID  dp6167982cs1k, child number 0\n-------------------------------------\nSELECT /*+ GATHER_PLAN_STATISTICS */ dummy AS c FROM dual\n \nPlan hash value: 272002086\n \n--------------------------------------------------------------------------\n| Id  | Operation         | Name | Rows  | Bytes | Cost (%CPU)| Time     |\n--------------------------------------------------------------------------\n|   0 | SELECT STATEMENT  |      |       |       |     2 (100)|          |\n|   1 |  TABLE ACCESS FULL| DUAL |     1 |     2 |     2   (0)| 00:00:01 |\n--------------------------------------------------------------------------\n \n"),
        ("force",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual WHERE dummy = 'X' AND 1 = 1 AND 'abc' = 'abc'",
         "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual WHERE dummy = 'X' AND 1 = 1 AND 'abc' = 'abc'",
         "SQL_ID  2rwxv3jmf42s0, child number 0\n-------------------------------------\nSELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual WHERE dummy = \n:\"SYS_B_0\" AND :\"SYS_B_1\" = :\"SYS_B_2\" AND :\"SYS_B_3\" = :\"SYS_B_4\"\n \nPlan hash value: 3752461848\n \n---------------------------------------------------------------------------\n| Id  | Operation          | Name | Rows  | Bytes | Cost (%CPU)| Time     |\n---------------------------------------------------------------------------\n|   0 | SELECT STATEMENT   |      |       |       |     2 (100)|          |\n|*  1 |  FILTER            |      |       |       |            |          |\n|*  2 |   TABLE ACCESS FULL| DUAL |     1 |     2 |     2   (0)| 00:00:01 |\n---------------------------------------------------------------------------\n \nPredicate Information (identified by operation id):\n---------------------------------------------------\n \n   1 - filter((:SYS_B_3=:SYS_B_4 AND :SYS_B_1=:SYS_B_2))\n   2 - filter(\"DUMMY\"=:SYS_B_0)\n \n"),
        ("withcte",
         "WITH a AS (SELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual)\nSELECT * FROM a",
         "WITH a AS (SELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual)\nSELECT * FROM a",
         "SQL_ID  53yksywcy40n6, child number 0\n-------------------------------------\nWITH a AS (SELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual) SELECT \n* FROM a\n \nPlan hash value: 272002086\n \n--------------------------------------------------------------------------\n| Id  | Operation         | Name | Rows  | Bytes | Cost (%CPU)| Time     |\n--------------------------------------------------------------------------\n|   0 | SELECT STATEMENT  |      |       |       |     2 (100)|          |\n|   1 |  TABLE ACCESS FULL| DUAL |     1 |     2 |     2   (0)| 00:00:01 |\n--------------------------------------------------------------------------\n \n"),
    ];

    /// <summary>한글 문장(UTF-8 글자당 3바이트): 이름, 보낸 글의 바이트 수, 서버에 간 글, 서버가 낸 글. 긴 두 항목은 서버가 본문을 잘라 보인 것이다.</summary>
    private static readonly (string Name, int Bytes, string Sent, string Shown)[] RealKorean =
    [
        ("ko_short", 92,
         "SELECT /*+ GATHER_PLAN_STATISTICS */ /* 한글 주석 */ '한글 리터럴' AS 값 FROM dual",
         "SQL_ID  d3g1xcchmykkx, child number 0\n-------------------------------------\nSELECT /*+ GATHER_PLAN_STATISTICS */ /* 한글 주석 */ '한글 리터럴' AS 값 FROM dual\n \nPlan hash value: 1388734953\n \n-----------------------------------------------------------------\n| Id  | Operation        | Name | Rows  | Cost (%CPU)| Time     |\n-----------------------------------------------------------------\n|   0 | SELECT STATEMENT |      |       |     2 (100)|          |\n|   1 |  FAST DUAL       |      |     1 |     2   (0)| 00:00:01 |\n-----------------------------------------------------------------\n \n"),
        ("ko_long", 3986,
         "SELECT /*+ GATHER_PLAN_STATISTICS */ /* 한글 주석: 긴 문장 시험 */\n       '가나다라마바사아자차카타파하01' AS 열_01,\n       '가나다라마바사아자차카타파하02' AS 열_02,\n       '가나다라마바사아자차카타파하03' AS 열_03,\n       '가나다라마바사아자차카타파하04' AS 열_04,\n       '가나다라마바사아자차카타파하05' AS 열_05,\n       '가나다라마바사아자차카타파하06' AS 열_06,\n       '가나다라마바사아자차카타파하07' AS 열_07,\n       '가나다라마바사아자차카타파하08' AS 열_08,\n       '가나다라마바사아자차카타파하09' AS 열_09,\n       '가나다라마바사아자차카타파하10' AS 열_10,\n       '가나다라마바사아자차카타파하11' AS 열_11,\n       '가나다라마바사아자차카타파하12' AS 열_12,\n       '가나다라마바사아자차카타파하13' AS 열_13,\n       '가나다라마바사아자차카타파하14' AS 열_14,\n       '가나다라마바사아자차카타파하15' AS 열_15,\n       '가나다라마바사아자차카타파하16' AS 열_16,\n       '가나다라마바사아자차카타파하17' AS 열_17,\n       '가나다라마바사아자차카타파하18' AS 열_18,\n       '가나다라마바사아자차카타파하19' AS 열_19,\n       '가나다라마바사아자차카타파하20' AS 열_20,\n       '가나다라마바사아자차카타파하21' AS 열_21,\n       '가나다라마바사아자차카타파하22' AS 열_22,\n       '가나다라마바사아자차카타파하23' AS 열_23,\n       '가나다라마바사아자차카타파하24' AS 열_24,\n       '가나다라마바사아자차카타파하25' AS 열_25,\n       '가나다라마바사아자차카타파하26' AS 열_26,\n       '가나다라마바사아자차카타파하27' AS 열_27,\n       '가나다라마바사아자차카타파하28' AS 열_28,\n       '가나다라마바사아자차카타파하29' AS 열_29,\n       '가나다라마바사아자차카타파하30' AS 열_30,\n       '가나다라마바사아자차카타파하31' AS 열_31,\n       '가나다라마바사아자차카타파하32' AS 열_32,\n       '가나다라마바사아자차카타파하33' AS 열_33,\n       '가나다라마바사아자차카타파하34' AS 열_34,\n       '가나다라마바사아자차카타파하35' AS 열_35,\n       '가나다라마바사아자차카타파하36' AS 열_36,\n       '가나다라마바사아자차카타파하37' AS 열_37,\n       '가나다라마바사아자차카타파하38' AS 열_38,\n       '가나다라마바사아자차카타파하39' AS 열_39,\n       '가나다라마바사아자차카타파하40' AS 열_40,\n       '가나다라마바사아자차카타파하41' AS 열_41,\n       '가나다라마바사아자차카타파하42' AS 열_42,\n       '가나다라마바사아자차카타파하43' AS 열_43,\n       '가나다라마바사아자차카타파하44' AS 열_44,\n       '가나다라마바사아자차카타파하45' AS 열_45,\n       '가나다라마바사아자차카타파하46' AS 열_46,\n       '가나다라마바사아자차카타파하47' AS 열_47,\n       '가나다라마바사아자차카타파하48' AS 열_48,\n       '가나다라마바사아자차카타파하49' AS 열_49,\n       '가나다라마바사아자차카타파하50' AS 열_50,\n       '가나다라마바사아자차카타파하51' AS 열_51,\n       '가나다라마바사아자차카타파하52' AS 열_52,\n       '가나다라마바사아자차카타파하53' AS 열_53,\n       '가나다라마바사아자차카타파하54' AS 열_54,\n       '가나다라마바사아자차카타파하55' AS 열_55,\n       '가나다라마바사아자차카타파하56' AS 열_56,\n       '가나다라마바사아자차카타파하57' AS 열_57,\n       '가나다라마바사아자차카타파하58' AS 열_58,\n       '가나다라마바사아자차카타파하59' AS 열_59,\n       '가나다라마바사아자차카타파하60' AS 열_60\n  FROM dual",
         "SQL_ID  c98fytxt7vjfm, child number 0\n-------------------------------------\nSELECT /*+ GATHER_PLAN_STATISTICS */ /* 한글 주석: 긴 문장 시험 */        \n'가나다라마바사아자차카타파하01' AS 열_01,        '가나다라마바사아자차카타파하02' AS 열_02,        \n'가나다라마바사아자차카타파하03' AS 열_03,        '가나다라마바사아자차카타파하04' AS 열_04,        \n'가나다라마바사아자차카타파하05' AS 열_05,        '가나다라마바사아자차카타파하06' AS 열_06,        \n'가나다라마바사아자차카타파하07' AS 열_07,        '가나다라마바사아자차카타파하08' AS 열_08,        \n'가나다라마바사아자차카타파하09' AS 열_09,        '가나다라마바사아자차카타파하10' AS 열_10,        \n'가나다라마바사아자차카타파하11' AS 열_11,        '가나다라마바사아자차카타파하12' AS 열_12,        \n'가나다라마바사아자차카타파하13' AS 열_13,        '가나다라마바사아자차카타파하14' AS 열_14,        \n'가나\n \nPlan hash value: 1388734953\n \n-----------------------------------------------------------------\n| Id  | Operation        | Name | Rows  | Cost (%CPU)| Time     |\n-----------------------------------------------------------------\n|   0 | SELECT STATEMENT |      |       |     2 (100)|          |\n|   1 |  FAST DUAL       |      |     1 |     2   (0)| 00:00:01 |\n-----------------------------------------------------------------\n \n"),
        ("ko_comment_long", 3499,
         "SELECT /*+ GATHER_PLAN_STATISTICS */ /* 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하  */ dummy FROM dual",
         "SQL_ID  4av2faptj51cr, child number 0\n-------------------------------------\nSELECT /*+ GATHER_PLAN_STATISTICS */ /* 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 \n가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 \n가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 \n가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 \n가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 \n가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라마바사아자차카타파하 가나다라\n \nPlan hash value: 272002086\n \n--------------------------------------------------------------------------\n| Id  | Operation         | Name | Rows  | Bytes | Cost (%CPU)| Time     |\n--------------------------------------------------------------------------\n|   0 | SELECT STATEMENT  |      |       |       |     2 (100)|          |\n|   1 |  TABLE ACCESS FULL| DUAL |     1 |     2 |     2   (0)| 00:00:01 |\n--------------------------------------------------------------------------\n \n"),
    ];

    public static IEnumerable<object[]> KoreanNames() => RealKorean.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(KoreanNames))]
    public void Korean_statements_over_1000_bytes_match_the_real_display_cursor_text(string name)
    {
        var c = RealKorean.Single(x => x.Name == name);
        Assert.True(PlanSqlMatch.Matches(c.Shown, c.Sent), name);

        // 한글이 다르면(보인 앞부분 안의 글자) 거부한다.
        var key = name == "ko_short" ? "한글 리터럴" : "가나다라";
        var at = c.Sent.IndexOf(key, StringComparison.Ordinal);
        var other = c.Sent[..at] + "다라마바" + c.Sent[(at + key.Length)..];
        Assert.NotEqual(c.Sent, other);
        Assert.False(PlanSqlMatch.Matches(c.Shown, other), name);
    }

    [Fact]
    public void The_long_korean_texts_are_cut_by_the_server_on_a_character_boundary_and_the_prefix_still_matches()
    {
        var replacement = ((char)0xFFFD).ToString(); // 깨진 글자의 대체 문자
        var long1 = RealKorean.Where(x => x.Bytes > 1000).ToList();
        Assert.Equal(2, long1.Count);
        foreach (var c in long1)
        {
            var body = PlanSqlMatch.ExtractSql(c.Shown)!.TrimEnd();
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(c.Sent) > System.Text.Encoding.UTF8.GetByteCount(body), c.Name); // 잘렸다
            Assert.DoesNotContain(replacement, body, StringComparison.Ordinal); // 26ai 는 글자 경계에서 잘랐다: 깨진 글자가 없다
            Assert.True(PlanSqlMatch.Matches(c.Shown, c.Sent), c.Name);
        }
    }

    public static IEnumerable<object[]> Names() => Real.Select(c => new object[] { c.Name });

    private static (string Name, string Copy, string Sent, string Shown) Case(string name) => Real.Single(c => c.Name == name);

    [Theory]
    [MemberData(nameof(Names))]
    public void The_real_display_cursor_text_matches_the_statement_that_was_sent(string name)
    {
        var c = Case(name);
        Assert.True(PlanSqlMatch.Matches(c.Shown, c.Sent), name);
    }

    [Fact]
    public void A_long_statement_is_cut_mid_word_by_the_server_and_still_matches_as_a_prefix()
    {
        var c = Case("long1500");
        var body = PlanSqlMatch.ExtractSql(c.Shown)!;
        Assert.EndsWith("a_rather_long_alia", body.TrimEnd(), StringComparison.Ordinal); // 낱말 중간에서 잘렸다
        Assert.True(body.Length < c.Sent.Length);
        Assert.True(PlanSqlMatch.Matches(c.Shown, c.Sent));
    }

    [Fact]
    public void Line_breaks_the_server_inserts_inside_words_are_ignored_because_all_whitespace_is_dropped()
    {
        var c = Case("long200");
        var broken = c.Shown.Replace("a_rather_long_alias_name_number_02", "a_rather_long_ali\nas_name_number_02", StringComparison.Ordinal);
        Assert.True(PlanSqlMatch.Matches(broken, c.Sent));
    }

    [Fact]
    public void A_substitution_statement_matches_the_substituted_text_not_the_text_before_substitution()
    {
        var c = Case("subst");
        Assert.Contains("&v", c.Copy, StringComparison.Ordinal);
        Assert.DoesNotContain("&v", c.Sent, StringComparison.Ordinal);
        Assert.True(PlanSqlMatch.Matches(c.Shown, c.Sent)); // result.Statement.Text
        Assert.False(PlanSqlMatch.Matches(c.Shown, c.Copy)); // 치환 전 글과는 맞지 않는다(그래서 치환한 글로 대조한다)
    }

    [Fact]
    public void Cursor_sharing_force_placeholders_match_any_literal_but_not_other_text()
    {
        var c = Case("force");
        Assert.Contains(":\"SYS_B_0\"", c.Shown, StringComparison.Ordinal);
        Assert.True(PlanSqlMatch.Matches(c.Shown, c.Sent));
        // 자리 하나가 어떤 값이든 허용: 문자열·숫자·따옴표 두 개('')·큰 숫자.
        Assert.True(PlanSqlMatch.Matches(c.Shown, c.Sent.Replace("'X'", "'it''s'", StringComparison.Ordinal)));
        Assert.True(PlanSqlMatch.Matches(c.Shown, c.Sent.Replace("1 = 1", "10.5 = 3E+2", StringComparison.Ordinal)));
        // 리터럴이 아닌 자리에 다른 글이 있거나 리터럴이 모자라면 거부.
        Assert.False(PlanSqlMatch.Matches(c.Shown, c.Sent.Replace("WHERE dummy", "WHERE other", StringComparison.Ordinal)));
        Assert.False(PlanSqlMatch.Matches(c.Shown, "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy FROM dual WHERE dummy = "));
        Assert.False(PlanSqlMatch.Matches(c.Shown, c.Sent.Replace("'X'", "dummy", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_plan_of_another_statement_is_rejected()
    {
        var c = Case("short");
        Assert.False(PlanSqlMatch.Matches(c.Shown, Case("long200").Sent));
        Assert.False(PlanSqlMatch.Matches(c.Shown, "SELECT /*+ GATHER_PLAN_STATISTICS */ dummy, 1 FROM dual"));
    }

    [Fact]
    public void A_header_with_an_empty_body_cannot_be_confirmed_and_is_rejected_while_no_header_is_not_judged()
    {
        const string header = "SQL_ID  abc, child number 0\n-------------------------------------\n";
        Assert.False(PlanSqlMatch.Matches(header + " \nPlan hash value: 1\n", "select 1 from dual"));
        Assert.False(PlanSqlMatch.Matches(header, "select 1 from dual"));
        Assert.False(PlanSqlMatch.Matches(header + "select 1 from dual", string.Empty)); // 보낸 글이 비었는데 본문이 있다
        Assert.True(PlanSqlMatch.Matches("Plan hash value: 1\n", "select 1 from dual"));
        Assert.True(PlanSqlMatch.Matches(null, "select 1 from dual"));
    }
}
