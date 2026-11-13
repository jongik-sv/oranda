using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Shell.Settings;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Rollback.Ledger;

namespace Oranda.App.Shell.Composition;

// 되돌리기 지점 조립(되돌리기 지점 설계 13.1). Core 구현은 스캔 대상이 아니므로 앱 어셈블리(스캔 대상)의 얇은 등록 형식이 Core 구현을 올린다.
// 엔진은 RB-02 RollbackCaptureEngine, 원장은 RB-04 RollbackLedger.
// 컨테이너는 같은 서비스의 마지막 등록을 고르므로, 나중에 훑는 Language 의 IDmlAnalyzer(RB-30)가 여기 끔 분석기를 이긴다.
// 저장소·금고(IRollbackStore·IRollbackVault)는 Platform(RB-20·RB-21)이 올린다. 여기서 대신 등록하지 않는다(앱 어셈블리가 Platform 뒤에 훑여 실구현을 가린다).

/// <summary>원복 캡처 엔진 등록(RB-02 <see cref="RollbackCaptureEngine"/>). 설정은 실행할 때마다 <see cref="SettingsService.Current"/> 에서 읽는다.</summary>
[OrandaService(typeof(IRollbackCapture))]
internal sealed class AppRollbackCapture(IRollbackLedger ledger, SettingsService settings, IMetadataProvider metadata, IEventBus events,
                                         IRollbackStore? store = null)
    : IRollbackCapture
{
    private readonly RollbackCaptureEngine _inner = new(ledger, () => settings.Current.Rollback, metadata, store, events: events);

    public Task<ConfirmExtras> PrepareAsync(RollbackRequest request, IRollbackChannel gatedChannel, IRollbackDialect dialect, CancellationToken ct) =>
        _inner.PrepareAsync(request, gatedChannel, dialect, ct);

    public Task<IStatementCapture> BeginAsync(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer,
                                              IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct) =>
        _inner.BeginAsync(request, extras, answer, channel, dialect, ct);

    public Task<TransactionEndKind> ProbeEndAsync(Guid sessionId, IRollbackChannel channel, CancellationToken ct) =>
        _inner.ProbeEndAsync(sessionId, channel, ct);

    public Task<ChangePoint?> ReadPointAsync(Guid sessionId, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct) =>
        _inner.ReadPointAsync(sessionId, channel, dialect, ct);
}

/// <summary>원장 등록(RB-04 <see cref="RollbackLedger"/>). 저장소가 아직 없으면(Platform RB-21 전) 메모리에서만 묶음을 든다.</summary>
[OrandaService(typeof(IRollbackLedger))]
internal sealed class AppRollbackLedger(IEventBus events, SettingsService settings, IRollbackStore? store = null) : IRollbackLedger
{
    private readonly RollbackLedger _inner = new(store, events, () => settings.Current.Rollback);

    public void Record(RollbackStatementRecord record) => _inner.Record(record);

    public void MarkSavepoint(Guid sessionId, string name) => _inner.MarkSavepoint(sessionId, name);

    public RollbackOpenState? Open(Guid sessionId) => _inner.Open(sessionId);

    public IReadOnlyList<RollbackProbe> Probes(Guid sessionId, int max = 5) => _inner.Probes(sessionId, max);

    public Task BeforeEndAsync(Guid sessionId, CancellationToken ct) => _inner.BeforeEndAsync(sessionId, ct);

    public Task EndAsync(TransactionEnded ended, CancellationToken ct) => _inner.EndAsync(ended, ct);

    public void RecordDerived(Guid sessionId, RollbackOrigin origin, long originGroupId, RollbackStatementRecord record) =>
        _inner.RecordDerived(sessionId, origin, originGroupId, record);
}

/// <summary>문장 모양 분석기의 기본 등록(<see cref="NullDmlAnalyzer"/>). Language 의 실구현이 들어오면 그것이 이긴다.</summary>
[OrandaService(typeof(IDmlAnalyzer))]
internal sealed class AppNullDmlAnalyzer : IDmlAnalyzer
{
    public DmlShape? Analyze(string text) => NullDmlAnalyzer.Instance.Analyze(text);
}
