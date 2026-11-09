namespace Oranda.Tibero.Schema;

/// <summary>
/// Tibero 트리거 생성 DDL 조립기(세션 없는 정적 부품, 글 조립은 <see cref="TiberoDdlText"/> 가 한다). Tibero 의 <c>DBMS_METADATA.GET_DDL('TRIGGER')</c> 는 동작하지 않아
/// (<c>ODP-14001</c>, docs/design/tibero-catalog-map.md) <c>*_SOURCE</c>(TYPE = 'TRIGGER') 글로 만든다. T3 DDL 추출과 T5 표 다시 만들기(TB4)가 같이 쓴다.
/// <list type="bullet">
/// <item>다른 길은 없다(6.2.15 실측): <c>*_TRIGGERS</c> 에는 Oracle 의 DESCRIPTION·TRIGGER_BODY 열이 없다
/// (열 = OWNER, TRIGGER_NAME, TRIGGER_TYPE, TRIGGERING_EVENT, TABLE_OWNER, TABLE_NAME, REFERENCING_NAMES, WHEN_CLAUSE, STATUS).</item>
/// <item>결과는 <c>CREATE OR REPLACE TRIGGER "OWNER"."NAME" …</c> 생성 문장 하나다. 끝의 <c>/</c>·상태 문장은 붙이지 않는다 —
/// Oracle 다시 만들기도 생성 문장만 남기고 사용 여부는 따로 다룬다(<see cref="DisableStatement"/>).</item>
/// <item>트리거 이름 자리는 늘 <paramref name="owner"/>·<paramref name="name"/> 으로 바꿔 쓴다(다른 스키마로 다시 만들 때 소유자를 바꿀 수 있게).
/// 대상 표 이름(<c>ON …</c>)은 원문 그대로 둔다.</item>
/// </list>
/// </summary>
internal static class TiberoTriggerDdl
{
    /// <summary>
    /// <c>*_SOURCE</c>(TYPE = 'TRIGGER') 줄 → 생성 DDL. 원문 첫머리는 <c>TRIGGER 이름 …</c> 이다(Oracle 과 같은 모양). 앞에
    /// <c>CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE]</c> 가 붙어 와도 받는다. 줄이 없거나 <c>TRIGGER</c> 로 시작하지 않으면 null.
    /// </summary>
    internal static string? FromSource(string owner, string name, IReadOnlyList<string?> lines) =>
        TiberoDdlText.FromSource("TRIGGER", owner, name, lines);

    /// <summary>사용 안 함 상태로 되돌리는 문장(<c>ALTER TRIGGER "OWNER"."NAME" DISABLE</c>).</summary>
    internal static string DisableStatement(string owner, string name) => $"ALTER TRIGGER {Qualified(owner, name)} DISABLE";

    /// <summary>따옴표로 감싼 <c>"OWNER"."NAME"</c>(안의 따옴표는 두 번).</summary>
    internal static string Qualified(string owner, string name) => TiberoDdlText.Qualified(owner, name);
}
