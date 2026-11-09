using Oranda.Core.Connection;

namespace Oranda.App.Shell.Features;

/// <summary>
/// DBMS 가 지원하지 않는 서버 기능(<see cref="DbmsFeature"/>)을 메뉴·명령에서 「비활성 + 이유」로 보이게 하는 공용 부품(Tibero A5, docs/design/tibero.md 2장).
/// 항목을 숨기지 않고 회색으로 두며 툴팁에 「Tibero 에서는 지원하지 않음」을 보인다.
/// <para>
/// 문맥 키는 <b>지원 안 함</b> 쪽이 참이다(<c>unsupportedAwrReport</c>). 키가 없으면(접속 전·Oracle) 지원하는 것으로 본다 —
/// 그래서 Oracle 동작은 이 부품이 없을 때와 같고, 명령은 <c>Enablement = "… &amp;&amp; " + <see cref="Enabled"/>(feature)</c> 로 쓴다.
/// </para>
/// <para>
/// 켜기: 활성 세션이 바뀔 때 <see cref="Apply"/> 로 그 DBMS 를 알린다. 이유 문구: 메뉴 모델이 문구 조회를 <see cref="WrapPhrase"/> 로 감싸
/// <c>Menu.disabled.not.unsupported*</c> 키를 활성 DBMS 이름이 든 문장으로 바꾼다. 메뉴가 아닌 단추·패널은 <see cref="Reason(Dbms?, DbmsFeature)"/> 를 쓴다.
/// </para>
/// </summary>
public static class DbmsFeatureKeys
{
    /// <summary>문맥 키 머리.</summary>
    public const string KeyPrefix = "unsupported";

    /// <summary>부정 키의 이유 문구 키 머리(<see cref="Oranda.App.Shell.ContextKeyReasons"/> 규칙: <c>Menu.disabled.not.</c> + 키).</summary>
    public const string PhrasePrefix = "Menu.disabled.not." + KeyPrefix;

    /// <summary>DBMS 이름이 든 이유 문구 키: <c>Menu.disabled.unsupportedOn.Tibero</c>.</summary>
    public const string DbmsPhrasePrefix = "Menu.disabled.unsupportedOn.";

    private static Dbms? _active;

    /// <summary>마지막으로 <see cref="Apply"/> 한 DBMS. 접속이 없으면 null.</summary>
    public static Dbms? Active => _active;

    /// <summary>기능 하나의 문맥 키(지원하지 않으면 참).</summary>
    public static string KeyOf(DbmsFeature feature) => KeyPrefix + feature;

    /// <summary>명령의 <c>Enablement</c> 에 이어 붙이는 식(<c>!unsupportedAwrReport</c>).</summary>
    public static string Enabled(DbmsFeature feature) => "!" + KeyOf(feature);

    /// <summary>접속한 DBMS 로 모든 기능 키를 맞춘다. <paramref name="dbms"/> 가 null(접속 없음)이면 모두 지원으로 되돌린다.</summary>
    public static void Apply(ContextKeyService keys, Dbms? dbms)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _active = dbms;
        keys.SetMany(Enum.GetValues<DbmsFeature>()
            .Select(f => (KeyOf(f), dbms is { } d && !DbmsSupport.Supports(d, f))));
    }

    /// <summary>
    /// 이 DBMS 에서 <paramref name="feature"/> 를 못 쓸 때 보일 이유(현재 언어). 지원하면 null.
    /// <paramref name="phrase"/> 는 <see cref="DbmsPhrasePrefix"/> + DBMS 로 글자를 찾는 함수(없으면 null) — 못 찾으면 Core 의 한국어 문구를 쓴다.
    /// </summary>
    public static string? Reason(Dbms? dbms, DbmsFeature feature, Func<string, string?>? phrase = null)
    {
        if (dbms is not { } d || DbmsSupport.Supports(d, feature))
        {
            return null;
        }

        return phrase?.Invoke(DbmsPhrasePrefix + d) ?? DbmsSupport.NotSupportedText(d);
    }

    /// <summary>
    /// 문구 조회 함수를 감싼다: <c>Menu.disabled.not.unsupported*</c> 키는 활성 DBMS 이름이 든 문장으로, 그 밖의 키는 그대로 <paramref name="inner"/> 에 맡긴다. <paramref name="active"/> 가 없으면 마지막 <see cref="Apply"/> 한 DBMS.
    /// </summary>
    public static Func<string, string?> WrapPhrase(Func<string, string?> inner, Func<Dbms?>? active = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return key => key.StartsWith(PhrasePrefix, StringComparison.Ordinal) && (active is null ? _active : active()) is { } d
            ? inner(DbmsPhrasePrefix + d) ?? DbmsSupport.NotSupportedText(d)
            : inner(key);
    }
}
