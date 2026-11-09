using Oranda.App.Features;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Transfer;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Tuning;

namespace Oranda.App.Shell.Features;

/// <summary>
/// 활성 세션의 DBMS 에 영역별 서비스 구현이 아직 없을 때(Tibero 의 모니터·튜닝·메타데이터 …) 그 영역의 명령·메뉴를 「비활성 + 이유」로 보이게 하는 공용 부품(Tibero U1, 결정 D-046).
/// <see cref="DbmsFeatureKeys"/> 가 「DBMS 가 못 하는 서버 기능」이라면 이쪽은 「이 빌드에 아직 구현이 없는 영역」이다 — 안 막으면 디스패처가 <see cref="DbmsNotSupportedException"/> 으로 터진다.
/// <para>
/// 문맥 키는 <b>구현 없음</b> 쪽이 참이다(<c>noImplMonitor</c>). 키가 없으면(접속 전)·Oracle 이면 구현이 있는 것으로 본다 —
/// 그래서 Oracle 동작은 이 부품이 없을 때와 같고, 명령은 <c>Enablement = "… &amp;&amp; " + <see cref="Enabled"/>(area)</c> 로 쓴다.
/// </para>
/// <para>
/// 켜기: 활성 세션이 바뀔 때 <see cref="Apply"/> 로 서비스 공급자와 그 DBMS 를 알린다. 이유 문구: 메뉴 모델이 문구 조회를 <see cref="WrapPhrase"/> 로 감싸
/// <c>Menu.disabled.not.noImpl*</c> 키를 활성 DBMS 이름이 든 문장(<c>Menu.disabled.noImplOn.Tibero</c>)으로 바꾼다. 메뉴가 아닌 단추·패널은 <see cref="Reason(IServiceProvider?, Dbms?, DbmsServiceArea, Func{string, string?}?)"/> 를 쓴다.
/// </para>
/// </summary>
public static class DbmsServiceKeys
{
    /// <summary>문맥 키 머리.</summary>
    public const string KeyPrefix = "noImpl";

    /// <summary>부정 키의 이유 문구 키 머리(<see cref="Oranda.App.Shell.ContextKeyReasons"/> 규칙: <c>Menu.disabled.not.</c> + 키).</summary>
    public const string PhrasePrefix = "Menu.disabled.not." + KeyPrefix;

    /// <summary>DBMS 이름이 든 이유 문구 키: <c>Menu.disabled.noImplOn.Tibero</c>.</summary>
    public const string DbmsPhrasePrefix = "Menu.disabled.noImplOn.";

    /// <summary>
    /// 영역 ↔ 서비스 형식 표(한 곳). 한 영역의 형식 중 하나라도 DBMS 구현이 없으면 그 영역은 「구현 없음」이다.
    /// Core <c>Composition/Dispatch</c> 의 디스패처가 감싸는 형식은 여기 있거나 <see cref="NotAnArea"/> 에 있어야 한다(시험이 잡는다).
    /// </summary>
    public static IReadOnlyDictionary<DbmsServiceArea, Type[]> Areas { get; } = new Dictionary<DbmsServiceArea, Type[]>
    {
        [DbmsServiceArea.Monitor] = [typeof(IMonitorService)],
        [DbmsServiceArea.ExecutionPlan] = [typeof(IExecutionPlanService)],
        [DbmsServiceArea.Tuning] = [typeof(ITuningService)],
        [DbmsServiceArea.Metadata] = [typeof(IMetadataProvider), typeof(ISchemaTreeProvider)],
        [DbmsServiceArea.DataTransfer] = [typeof(IDataTransferService), typeof(ICopyRunner), typeof(ICopyPrecheckService)],
        [DbmsServiceArea.DatabaseInfo] = [typeof(IDatabaseInfoProvider)],
    };

    /// <summary>
    /// 디스패처는 있으나 영역 키를 만들지 않는 서비스 형식(실행·세션 관리·LOB 처럼 모든 화면의 바탕이거나, 영역 안 일부 화면이 따로 다룬다).
    /// 새 디스패처가 생기면 <see cref="Areas"/> 나 여기에 넣어야 시험이 통과한다.
    /// </summary>
    public static IReadOnlyCollection<Type> NotAnArea { get; } =
    [
        typeof(Oranda.Core.Sessions.ISessionManager),
        typeof(Oranda.Core.Execution.IExecutionService),
        typeof(Oranda.Core.Execution.IBulkExecutionService),
        typeof(Oranda.Core.Execution.ISqlPlusCommandProcessor),
        typeof(Oranda.Core.Catalog.ISqlCatalog),
        typeof(Oranda.Core.Grid.ILobAccessor),
        typeof(IColumnReorderProvider),
        typeof(ISchemaAdminProvider),
        typeof(ITableKeyDetailProvider),
    ];

    private static Dbms? _active;
    private static IServiceProvider? _services;

    /// <summary>마지막으로 <see cref="Apply"/> 한 DBMS. 접속이 없으면 null.</summary>
    public static Dbms? Active => _active;

    /// <summary>영역 하나의 문맥 키(구현이 없으면 참).</summary>
    public static string KeyOf(DbmsServiceArea area) => KeyPrefix + area;

    /// <summary>명령의 <c>Enablement</c> 에 이어 붙이는 식(<c>!noImplMonitor</c>).</summary>
    public static string Enabled(DbmsServiceArea area) => "!" + KeyOf(area);

    /// <summary><paramref name="dbms"/> 에 <paramref name="area"/> 의 구현이 모두 있는가. 접속 없음(null)·Oracle 은 있다고 본다.</summary>
    public static bool HasImplementation(IServiceProvider? services, Dbms? dbms, DbmsServiceArea area)
    {
        if (dbms is not { } d || d == Dbms.Oracle)
        {
            return true;
        }

        return Areas[area].All(type => Find(services, d, type) is not null);
    }

    /// <summary>접속한 DBMS 로 모든 영역 키를 맞춘다. <paramref name="dbms"/> 가 null(접속 없음)이면 모두 구현 있음으로 되돌린다.</summary>
    public static void Apply(ContextKeyService keys, IServiceProvider? services, Dbms? dbms)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _active = dbms;
        _services = services;
        keys.SetMany(Enum.GetValues<DbmsServiceArea>()
            .Select(a => (KeyOf(a), !HasImplementation(services, dbms, a))));
    }

    /// <summary>
    /// 이 DBMS 에서 <paramref name="area"/> 를 못 쓸 때 보일 이유(현재 언어). 구현이 있으면 null.
    /// <paramref name="phrase"/> 는 <see cref="DbmsPhrasePrefix"/> + DBMS 로 글자를 찾는 함수(없으면 null) — 못 찾으면 한국어 기본 문구를 쓴다.
    /// </summary>
    public static string? Reason(IServiceProvider? services, Dbms? dbms, DbmsServiceArea area, Func<string, string?>? phrase = null)
        => HasImplementation(services, dbms, area) ? null : phrase?.Invoke(DbmsPhrasePrefix + dbms) ?? NotImplementedText(dbms!.Value);

    /// <summary>마지막 <see cref="Apply"/> 의 공급자·DBMS 로 한 <see cref="Reason(IServiceProvider?, Dbms?, DbmsServiceArea, Func{string, string?}?)"/>.</summary>
    public static string? Reason(DbmsServiceArea area, Func<string, string?>? phrase = null)
        => Reason(_services, _active, area, phrase);

    /// <summary>문구 자원을 못 찾았을 때의 기본 문장.</summary>
    public static string NotImplementedText(Dbms dbms) => $"{dbms} 에서는 아직 지원하지 않음";

    /// <summary>
    /// 문구 조회 함수를 감싼다: <c>Menu.disabled.not.noImpl*</c> 키는 활성 DBMS 이름이 든 문장으로, 그 밖의 키는 그대로 <paramref name="inner"/> 에 맡긴다. <paramref name="active"/> 가 없으면 마지막 <see cref="Apply"/> 한 DBMS.
    /// </summary>
    public static Func<string, string?> WrapPhrase(Func<string, string?> inner, Func<Dbms?>? active = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return key => key.StartsWith(PhrasePrefix, StringComparison.Ordinal) && (active is null ? _active : active()) is { } d
            ? inner(DbmsPhrasePrefix + d) ?? NotImplementedText(d)
            : inner(key);
    }

    private static object? Find(IServiceProvider? services, Dbms dbms, Type serviceType)
    {
        // DbmsServices.Find<T> 는 형식 인자라 형식 표(Type)에서는 선택기를 직접 부른다 — 같은 규칙(선택기가 없으면 null).
        if (DbmsServices.SelectorOf(services) is not { } selector)
        {
            return null;
        }

        return typeof(IDbmsServiceSelector).GetMethod(nameof(IDbmsServiceSelector.Find))!
            .MakeGenericMethod(serviceType)
            .Invoke(selector, [dbms]);
    }
}

/// <summary>DBMS 별 구현 유무로 켜고 끄는 영역(<see cref="DbmsServiceKeys.Areas"/>).</summary>
public enum DbmsServiceArea
{
    /// <summary>세션·Lock·SQL 모니터(<c>IMonitorService</c>).</summary>
    Monitor,

    /// <summary>실행 계획(<c>IExecutionPlanService</c>).</summary>
    ExecutionPlan,

    /// <summary>튜닝: 통계·트레이스·AI 튜닝(<c>ITuningService</c>).</summary>
    Tuning,

    /// <summary>스키마 트리·객체 상세 메타데이터(<c>IMetadataProvider</c>·<c>ISchemaTreeProvider</c>).</summary>
    Metadata,

    /// <summary>데이터 전송·복사(<c>IDataTransferService</c>·<c>ICopyRunner</c>·<c>ICopyPrecheckService</c>).</summary>
    DataTransfer,

    /// <summary>DB 정보·객체 검색(<c>IDatabaseInfoProvider</c>).</summary>
    DatabaseInfo,
}
