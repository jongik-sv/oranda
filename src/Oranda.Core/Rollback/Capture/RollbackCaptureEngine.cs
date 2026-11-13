using System.Collections.Concurrent;
using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback.Literals;
using Oranda.Core.Security;
using Oranda.Core.Sessions;

namespace Oranda.Core.Rollback.Capture;

/// <summary>
/// 원복 스크립트 캡처 엔진(되돌리기 지점 설계 6장·7.1, RB-02). 공급자가 <see cref="IRollbackChannel"/>·<see cref="IRollbackDialect"/> 를 대고
/// 13.5 의 순서대로 부른다. 사용자 문장을 막지 않는다: 준비·시작·마무리의 내부 실패는 「만들지 않음」·「불완전」으로 담고, 사용자 취소만 전파한다.
/// </summary>
public sealed class RollbackCaptureEngine : IRollbackCapture
{
    /// <summary>한 행 INSERT 에 붙이는 출력 바인드 이름(<c>RETURNING ROWID INTO :ORANDA_RID</c>, RD-1).</summary>
    public const string ReturningBind = "ORANDA_RID";

    /// <summary>직후 값 읽기 한 번에 넣는 ROWID 수(설계 6.2 의 3단계).</summary>
    public const int RowIdChunk = 1000;

    /// <summary>문장마다 남기는 끝 판정 표본 수(설계 7.3).</summary>
    public const int MaxProbes = 5;

    private readonly IRollbackLedger _ledger;
    private readonly Func<RollbackSettings> _settings;
    private readonly IMetadataProvider? _metadata;
    private readonly IRollbackStore? _store;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, int> _pointMethod = new();

    /// <param name="ledger">원장(마무리에서 문장 기록을 넘긴다)</param>
    /// <param name="settings">지금 설정(실행할 때마다 읽는다)</param>
    /// <param name="metadata">표 정보·시노님 풀기(준비 단계, 명령 잠금 밖). null 이면 원복 스크립트를 만들지 않는다</param>
    /// <param name="store">묶음 저장소. null 이거나 <see cref="IRollbackStore.CanStoreScripts"/> 가 거짓이면 「안전한 저장소 없음」</param>
    /// <param name="time">시각(시험용)</param>
    public RollbackCaptureEngine(IRollbackLedger ledger, Func<RollbackSettings> settings, IMetadataProvider? metadata = null,
                                 IRollbackStore? store = null, TimeProvider? time = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _metadata = metadata;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary><c>COUNT(*)</c> 예상 행 수의 제한 시간(RD-11, 변경 일지와 같은 값).</summary>
    public TimeSpan CountTimeout { get; init; } = ChangeCapture.Timeout;

    private RollbackSettings Settings => (_settings() ?? new RollbackSettings()).Normalized();

    public async Task<ConfirmExtras> PrepareAsync(RollbackRequest request, IRollbackChannel gatedChannel, IRollbackDialect dialect, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gatedChannel);
        ArgumentNullException.ThrowIfNull(dialect);
        try
        {
            return await PrepareCoreAsync(request, gatedChannel, dialect, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // 반목표 1: 준비 실패가 사용자 문장을 막지 않는다.
            return request.Shape is null ? ConfirmExtras.None
                : Offer(RollbackOffer.NotApplicable(NotCreatedReason.Failed, FirstLine(e.Message)) with { Shape = request.Shape });
        }
    }

    private async Task<ConfirmExtras> PrepareCoreAsync(RollbackRequest request, IRollbackChannel gatedChannel, IRollbackDialect dialect, CancellationToken ct)
    {
        var s = request.Session;
        if (RollbackFlow.RecordingFor(s, request.Options) != ChangeRecording.Normal || request.Shape is not { } shape
            || s.Profile.Environment != ConnectionEnvironment.Production || !RollbackFlow.IsRecordedWrite(request.Statement))
        {
            return ConfirmExtras.None;
        }

        if (NotTargetReason(shape) is { } notTarget)
        {
            return Offer(RollbackOffer.NotApplicable(NotCreatedReason.NotTarget, notTarget) with { Shape = shape });
        }

        if (_store is not { CanStoreScripts: true })
        {
            return Offer(RollbackOffer.NotApplicable(NotCreatedReason.NoSecureStore, "안전한 저장소 없음") with { Shape = shape });
        }

        if (_metadata is null)
        {
            return Offer(RollbackOffer.NotApplicable(NotCreatedReason.Failed, "표 정보를 읽을 수 없음") with { Shape = shape });
        }

        var settings = Settings;
        try
        {
            var table = await LoadTableAsync(s, shape, gatedChannel, dialect, ct).ConfigureAwait(false);
            if (table is null)
            {
                return Offer(RollbackOffer.NotApplicable(NotCreatedReason.NotTarget, "뷰") with { Shape = shape });
            }

            var warnings = new List<string>(table.SideEffects);
            var large = shape.Verb switch
            {
                DmlVerb.Delete => table.Columns.Where(c => c.Kind == RollbackValueKind.Large && !c.IsVirtual).ToList(),
                DmlVerb.Update => table.Columns.Where(c => c.Kind == RollbackValueKind.Large
                                                           && shape.SetColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList(),
                _ => [],
            };
            if (large.Count > 0)
            {
                warnings.Add(shape.Verb == DmlVerb.Update
                    ? $"큰 열({string.Join(", ", large.Select(c => c.Name))})은 원복 스크립트로 되돌리지 않습니다"
                    : $"큰 열({string.Join(", ", large.Select(c => c.DataType).Distinct())})이 있어 일부 행은 원복 문장을 만들 수 없습니다");
            }

            var estimated = shape.Verb == DmlVerb.InsertValues ? 1 : await CountAsync(shape, table, request.Binds, gatedChannel, ct).ConfigureAwait(false);
            var over = estimated > settings.MaxRowsPerStatement;
            return Offer(new RollbackOffer(true, NotCreatedReason.None)
            {
                Shape = shape,
                Table = table,
                EstimatedRows = estimated,
                OverLimit = over,
                DefaultOn = settings.ScriptOnProduction,
                Warnings = warnings,
                Message = over ? $"상한 넘음(예상 {estimated:N0}행 > {settings.MaxRowsPerStatement:N0})" : null,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RollbackChannelException e) when (e.Error.Kind == DbErrorKind.InsufficientPrivileges)
        {
            return Offer(RollbackOffer.NotApplicable(NotCreatedReason.Failed, "권한 없음") with { Shape = shape });
        }
        catch (SynonymResolutionException e)
        {
            return Offer(RollbackOffer.NotApplicable(NotCreatedReason.NotTarget, e.Message) with { Shape = shape });
        }
        catch (Exception e)
        {
            return Offer(RollbackOffer.NotApplicable(NotCreatedReason.Failed, FirstLine(e.Message)) with { Shape = shape });
        }
    }

    public async Task<IStatementCapture> BeginAsync(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer,
                                                    IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(dialect);
        extras ??= ConfirmExtras.None;
        answer ??= ConfirmAnswer.Default(extras);
        try
        {
            return await BeginCoreAsync(request, extras, answer, channel, dialect, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 반목표 1: 시작 실패가 사용자 문장을 막지 않는다(저장점을 건 뒤의 실패는 StatementCapture 가 스스로 푼다).
            return new PassThroughStatementCapture(request.Statement, request.Binds);
        }
    }

    private async Task<IStatementCapture> BeginCoreAsync(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer,
                                                         IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct)
    {
        var recording = RollbackFlow.RecordingFor(request.Session, request.Options);
        if (recording == ChangeRecording.None || !RollbackFlow.IsRecordedWrite(request.Statement))
        {
            return new PassThroughStatementCapture(request.Statement, request.Binds);
        }

        var settings = Settings;
        var point = settings.RecordPoint ? await ReadPointCoreAsync(request.Session.Id, channel, dialect, ct).ConfigureAwait(false) : null;
        var plan = Plan(request, extras, answer, recording, settings);
        var production = request.Session.Profile.Environment == ConnectionEnvironment.Production;
        if (point is null && plan.Reason != NotCreatedReason.None && plan.Incomplete is null
            && (!production || plan.Reason == NotCreatedReason.Suppressed))
        {
            // 남길 것(시점·원복)이 없다. 운영 접속의 「만들지 않음」(상한·거절·대상 아님)은 묶음이 불완전함을 알리도록 늘 기록한다.
            return new PassThroughStatementCapture(request.Statement, request.Binds);
        }

        var capture = new StatementCapture(this, request, extras.Rollback, plan, point, channel, dialect, settings, _time.GetUtcNow());
        await capture.StartAsync(ct).ConfigureAwait(false);
        return capture;
    }

    public async Task<TransactionEndKind> ProbeEndAsync(Guid sessionId, IRollbackChannel channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var probes = _ledger.Probes(sessionId, MaxProbes);
        if (probes.Count == 0)
        {
            return TransactionEndKind.Unknown;
        }

        TransactionEndKind? verdict = null;
        try
        {
            foreach (var probe in probes)
            {
                var committed = await ScalarAsync(channel, probe.CommittedIfOne, ct).ConfigureAwait(false) == 1;
                var rolledBack = await ScalarAsync(channel, probe.RolledBackIfOne, ct).ConfigureAwait(false) == 1;
                var one = committed == rolledBack ? TransactionEndKind.Unknown : committed ? TransactionEndKind.Commit : TransactionEndKind.Rollback;
                if (one == TransactionEndKind.Unknown || (verdict is { } v && v != one))
                {
                    return TransactionEndKind.Unknown;
                }

                verdict = one;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return TransactionEndKind.Unknown;
        }

        return verdict ?? TransactionEndKind.Unknown;
    }

    public Task<ChangePoint?> ReadPointAsync(Guid sessionId, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(dialect);
        return ReadPointCoreAsync(sessionId, channel, dialect, ct);
    }

    /// <summary>원복 대상이 아닌 이유(설계 6.3 끝 줄). null = 대상.</summary>
    internal static string? NotTargetReason(DmlShape shape)
    {
        if (shape.NotTarget is { } reason)
        {
            return reason;
        }

        if (shape.DbLink is not null)
        {
            return "DB 링크";
        }

        return shape.Verb switch
        {
            DmlVerb.Update or DmlVerb.Delete or DmlVerb.InsertValues when shape.Name is not null => null,
            DmlVerb.InsertSelect => "INSERT … SELECT",
            DmlVerb.Merge => "MERGE",
            DmlVerb.PlSql => "PL/SQL",
            _ => "대상 아님",
        };
    }

    private static ConfirmExtras Offer(RollbackOffer offer) => new(offer, null);

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 200 ? line : line[..200];
    }

    /// <summary>원복 스크립트를 만들지와 그 까닭(설계 6.1·10.1).</summary>
    private static CapturePlan Plan(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer, ChangeRecording recording, RollbackSettings settings)
    {
        var shape = request.Shape;
        if (request.Session.Profile.Environment != ConnectionEnvironment.Production)
        {
            return new CapturePlan(NotCreatedReason.NotProduction);
        }

        if (recording == ChangeRecording.PointOnly)
        {
            return new CapturePlan(NotCreatedReason.Suppressed);
        }

        if (shape is not null && shape.Verb == DmlVerb.InsertSelect && shape.NotTarget is null && shape.DbLink is null)
        {
            return new CapturePlan(NotCreatedReason.NotTarget, "INSERT … SELECT"); // 6.3: 「불완전 · INSERT … SELECT」, 시점 비교로 넘긴다
        }

        if (extras.Rollback is not { } offer || shape is null)
        {
            return new CapturePlan(shape is null ? NotCreatedReason.NotTarget : NotCreatedReason.Failed);
        }

        if (!offer.Applicable || offer.Table is null)
        {
            return new CapturePlan(offer.Applicable ? NotCreatedReason.Failed : offer.Reason) { Message = offer.Message };
        }

        if (offer.OverLimit)
        {
            return new CapturePlan(NotCreatedReason.OverLimit) { Message = offer.Message, Rows = offer.EstimatedRows };
        }

        if (!answer.CreateScript)
        {
            return new CapturePlan(settings.ScriptOnProduction ? NotCreatedReason.Declined : NotCreatedReason.SettingOff);
        }

        return new CapturePlan(NotCreatedReason.None) { Table = offer.Table };
    }

    private async Task<RollbackTable?> LoadTableAsync(ISession s, DmlShape shape, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct)
    {
        var metadata = _metadata!;
        var owner = shape.Owner ?? s.Server.SessionUser ?? s.Profile.User;
        var written = new DbObjectRef(owner, shape.Name!, "TABLE");
        var resolved = await SynonymResolver.ResolveAsync((o, n) => metadata.GetNameStepAsync(s, o, n, ct), written, shape.Owner is not null,
                                                          (name, link) => $"DB 링크 시노님({name}@{link})").ConfigureAwait(false);
        var info = await metadata.GetTableAsync(s, resolved, ct).ConfigureAwait(false);
        if (info.Table.ObjectType.Contains("VIEW", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (info.Columns.Count == 0)
        {
            throw new InvalidOperationException("열 정보 없음");
        }

        var columns = info.Columns.OrderBy(c => c.Position)
            .Select(c => new RollbackColumn(c.Name, c.DataType, dialect.KindOf(c))
            {
                Scale = c.Scale,
                IsVirtual = c.IsVirtual,
                IsIdentity = c.IsIdentity,
                IsInvisible = c.IsInvisible,
            })
            .ToList();
        var keys = info.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey)?.Columns ?? [];
        var table = new DbObjectRef(info.Table.Owner, info.Table.Name, "TABLE");
        return new RollbackTable(table, columns, keys)
        {
            SideEffects = await SideEffectsAsync(table, shape.Verb, channel, dialect, ct).ConfigureAwait(false),
        };
    }

    /// <summary>트리거·ON DELETE CASCADE 안내(설계 10.1). 찾지 못해도 실행은 계속한다.</summary>
    private static async Task<IReadOnlyList<string>> SideEffectsAsync(DbObjectRef table, DmlVerb verb, IRollbackChannel channel, IRollbackDialect dialect,
                                                                      CancellationToken ct)
    {
        if (dialect.SideEffectsSql is not { } sql)
        {
            return [];
        }

        try
        {
            var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["owner"] = new(BindDataType.Varchar2, table.Owner),
                ["name"] = new(BindDataType.Varchar2, table.Name),
            };
            var rows = await channel.QueryAsync(sql, binds, 200, ct).ConfigureAwait(false);
            var triggers = 0;
            var cascades = new List<string>();
            foreach (var row in rows.Rows)
            {
                var kind = row.Length > 0 ? Convert.ToString(row[0], CultureInfo.InvariantCulture) : null;
                var name = row.Length > 1 ? Convert.ToString(row[1], CultureInfo.InvariantCulture) : null;
                if (string.Equals(kind, "TRIGGER", StringComparison.OrdinalIgnoreCase))
                {
                    triggers++;
                }
                else if (name is not null && verb == DmlVerb.Delete)
                {
                    cascades.Add(name);
                }
            }

            var result = new List<string>();
            if (triggers > 0)
            {
                result.Add($"대상 테이블에 트리거 {triggers}개가 있습니다. 트리거가 바꾼 것은 원복 스크립트에 들어가지 않습니다");
            }

            foreach (var child in cascades)
            {
                result.Add($"ON DELETE CASCADE 로 자식 테이블 {child} 가 함께 지워집니다. 원복 스크립트에 들어가지 않습니다");
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>같은 WHERE 의 <c>COUNT(*)</c>(RD-11). 제한 시간·권한·그 밖 오류면 null(모름)이고 실행은 계속한다.</summary>
    private async Task<long?> CountAsync(DmlShape shape, RollbackTable table, IReadOnlyDictionary<string, BindValue> binds, IRollbackChannel channel,
                                         CancellationToken ct)
    {
        var sql = "SELECT COUNT(*) FROM " + (shape.TableText ?? RollbackSql.Table(table.Table)) + Where(shape.WhereText);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CountTimeout);
        try
        {
            var rows = await channel.QueryAsync(sql, RollbackFlow.BindsFor(shape.WhereText, binds), 1, limit.Token).ConfigureAwait(false);
            return rows.Rows.Count > 0 && rows.Rows[0].Length > 0 && rows.Rows[0][0] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Where(string? where) => string.IsNullOrWhiteSpace(where) ? string.Empty : " WHERE " + where;

    private async Task<ChangePoint?> ReadPointCoreAsync(Guid sessionId, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct)
    {
        var queries = dialect.PointQueries;
        var start = _pointMethod.TryGetValue(sessionId, out var remembered) ? remembered : 0;
        for (var i = start; i < queries.Count; i++)
        {
            try
            {
                var rows = await channel.QueryAsync(queries[i], new Dictionary<string, BindValue>(), 1, ct).ConfigureAwait(false);
                if (rows.Rows.Count > 0 && rows.Rows[0].Length > 0 && rows.Rows[0][0] is { } v and not DBNull)
                {
                    var number = Convert.ToInt64(v, CultureInfo.InvariantCulture);
                    _pointMethod[sessionId] = i;
                    return new ChangePoint(number, dialect.PointName, _time.GetUtcNow(), queries[i]);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 다음 방법(7.1). 모두 실패하면 기록만 건너뛴다.
            }
        }

        return null;
    }

    private static async Task<long?> ScalarAsync(IRollbackChannel channel, string sql, CancellationToken ct)
    {
        var rows = await channel.QueryAsync(sql, new Dictionary<string, BindValue>(), 1, ct).ConfigureAwait(false);
        return rows.Rows.Count > 0 && rows.Rows[0].Length > 0 && rows.Rows[0][0] is { } v and not DBNull
            ? Convert.ToInt64(v, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>원장에 넘기는 바인드 값 글(비밀로 보이는 이름은 값을 남기지 않는다, data-model 5.2).</summary>
    internal static IReadOnlyList<RollbackBind> RecordBinds(IReadOnlyDictionary<string, BindValue> binds) =>
    [
        .. binds.Where(b => b.Value.Direction is BindDirection.In or BindDirection.InOut)
            .Select(b => new RollbackBind(b.Key, b.Value.Type, SensitiveBindNames.IsSensitive(b.Key) ? null : BindText(b.Value.Value))),
    ];

    private static string? BindText(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private void Record(RollbackStatementRecord record) => _ledger.Record(record);

    /// <summary>시작 단계가 정한 일. <see cref="Reason"/> 이 None 이면 원복 스크립트를 만든다.</summary>
    private sealed record CapturePlan(NotCreatedReason Reason, string? Incomplete = null)
    {
        public RollbackTable? Table { get; init; }

        public string? Message { get; init; }

        public long? Rows { get; init; }
    }

    /// <summary>문장 하나의 캡처(설계 6.2 의 0~3단계).</summary>
    private sealed class StatementCapture : IStatementCapture
    {
        private readonly RollbackCaptureEngine _engine;
        private readonly RollbackRequest _request;
        private readonly DmlShape? _shape;
        private readonly IRollbackChannel _channel;
        private readonly IRollbackDialect _dialect;
        private readonly RollbackSettings _settings;
        private readonly ChangePoint? _point;
        private readonly DateTimeOffset _executedAt;
        private readonly List<string> _incomplete = [];
        private CapturePlan _plan;
        private bool _savepointSet;
        private IReadOnlyList<object?[]> _locked = [];
        private IReadOnlyList<RollbackColumn> _readColumns = [];

        public StatementCapture(RollbackCaptureEngine engine, RollbackRequest request, RollbackOffer? offer, CapturePlan plan, ChangePoint? point,
                                IRollbackChannel channel, IRollbackDialect dialect, RollbackSettings settings, DateTimeOffset executedAt)
        {
            _engine = engine;
            _request = request;
            _shape = request.Shape ?? offer?.Shape;
            _plan = plan;
            _point = point;
            _channel = channel;
            _dialect = dialect;
            _settings = settings;
            _executedAt = executedAt;
            StatementToSend = request.Statement;
            BindsToSend = request.Binds;
            if (plan.Incomplete is { } why)
            {
                _incomplete.Add(why);
            }
        }

        public SqlStatement StatementToSend { get; private set; }

        public IReadOnlyDictionary<string, BindValue> BindsToSend { get; private set; }

        private bool Creating => _plan.Reason == NotCreatedReason.None && _plan.Table is not null;

        private RollbackTable Table => _plan.Table!;

        private bool ReturningAppended { get; set; }

        /// <summary>0·1단계(UPDATE·DELETE) 또는 RETURNING 붙이기(한 행 INSERT).</summary>
        public async Task StartAsync(CancellationToken ct)
        {
            if (!Creating || _shape is null)
            {
                return;
            }

            if (_shape.Verb == DmlVerb.InsertValues)
            {
                AppendReturning();
                return;
            }

            if (!ResolveColumns(out var why))
            {
                Stop(NotCreatedReason.Failed, why);
                return;
            }

            try
            {
                await _channel.ExecuteAsync("SAVEPOINT " + _dialect.SavepointName, ct).ConfigureAwait(false);
                _savepointSet = true;
                var rows = await _channel.QueryAsync(LockSql(), RollbackFlow.BindsFor(_shape.WhereText, _request.Binds),
                                                     _settings.MaxRowsPerStatement + 1, ct).ConfigureAwait(false);
                if (rows.Truncated || rows.Rows.Count > _settings.MaxRowsPerStatement)
                {
                    // 6.5: 읽기를 멈추고 원복 스크립트 없이 실행한다. 이미 건 잠금은 사용자 문장이 어차피 건다.
                    Stop(NotCreatedReason.OverLimit, $"상한 넘음(잠금 {rows.Rows.Count:N0}행 이상 > {_settings.MaxRowsPerStatement:N0})");
                    return;
                }

                _locked = rows.Rows;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ReleaseAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception e)
            {
                await ReleaseAsync().ConfigureAwait(false);
                Stop(NotCreatedReason.Failed,
                     e is RollbackChannelException { Error.Kind: DbErrorKind.InsufficientPrivileges } ? "권한 없음" : FirstLine(e.Message));
            }
        }

        public async Task<RollbackStatementSummary> CompleteAsync(long? rowsAffected, IReadOnlyDictionary<string, object?> outValues,
                                                                  string? transactionId, CancellationToken ct)
        {
            var steps = new List<RollbackStep>();
            var probes = new List<RollbackProbe>();
            var touched = new List<string>();
            if (Creating && _shape is not null)
            {
                try
                {
                    switch (_shape.Verb)
                    {
                        case DmlVerb.Update:
                            await BuildUpdateAsync(rowsAffected, steps, probes, touched, ct).ConfigureAwait(false);
                            break;
                        case DmlVerb.Delete:
                            BuildDelete(rowsAffected, steps, probes, touched);
                            break;
                        case DmlVerb.InsertValues:
                            await BuildInsertAsync(outValues ?? new Dictionary<string, object?>(), steps, probes, touched, ct).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception e)
                {
                    // 14장: 3단계 실패는 사용자 문장을 되돌리지 않는다. 만든 데까지 두고 불완전으로 둔다.
                    _incomplete.Add("실행 직후 값을 읽지 못함: " + FirstLine(e.Message));
                }
            }

            var state = Creating
                ? (_incomplete.Count > 0 ? RollbackScriptState.Incomplete : RollbackScriptState.Open)
                : (_incomplete.Count > 0 ? RollbackScriptState.Incomplete : RollbackScriptState.NotCreated);
            var record = new RollbackStatementRecord
            {
                SessionId = _request.Session.Id,
                ProfileId = _request.Session.Profile.Id,
                ProfileName = _request.Session.Profile.Name,
                DbKey = _request.Session.DbKey,
                ServerName = _request.Session.Server.DbUniqueName ?? _request.Session.Server.DatabaseName,
                Environment = _request.Session.Profile.Environment,
                Dbms = _request.Session.Server.Dbms,
                Source = _request.Options.Source,
                ExecutedAt = _executedAt,
                Text = _request.Statement.Text,
                Binds = RecordBinds(_request.Binds),
                Verb = _shape?.Verb ?? (_request.Statement.Kind == StatementKind.PlSql ? DmlVerb.PlSql : DmlVerb.Other),
                Table = Creating ? Table.Table : _shape is { Name: { } name } ? new DbObjectRef(_shape.Owner ?? string.Empty, name, "TABLE") : null,
                RowsAffected = rowsAffected,
                LockedRows = _savepointSet && Creating ? _locked.Count : null,
                BeforePoint = _point,
                TransactionId = transactionId,
                State = state,
                Reason = Creating ? NotCreatedReason.None : _plan.Reason,
                IncompleteReason = _incomplete.Count > 0 ? string.Join(" · ", _incomplete) : _plan.Message,
                Steps = steps,
                Probes = probes,
                TouchedRowIds = touched,
                ReturningAppended = ReturningAppended,
            };

            string? warning = null;
            try
            {
                _engine.Record(record);
            }
            catch (Exception e)
            {
                warning = "원복 스크립트를 저장하지 못했습니다: " + FirstLine(e.Message);
            }

            var mismatch = _incomplete.FirstOrDefault(r => r.StartsWith("행 수 다름", StringComparison.Ordinal));
            return new RollbackStatementSummary(state, record.Reason, steps.Count)
            {
                BeforePoint = _point,
                LogLine = LogLine(state, steps.Count),
                Warning = warning ?? (mismatch is null ? null : $"원복 스크립트 불완전: {mismatch} — 커밋 전에 확인하세요"),
                ReturningAppended = ReturningAppended,
            };
        }

        public async Task<RollbackStatementSummary> AbandonAsync(DbError? failure, CancellationToken ct)
        {
            if (!_savepointSet)
            {
                return RollbackStatementSummary.None with { BeforePoint = _point };
            }

            var released = await ReleaseAsync().ConfigureAwait(false);
            return RollbackStatementSummary.None with
            {
                BeforePoint = _point,
                LogLine = released ? "원복 스크립트용 잠금은 풀었습니다" : null,
                Warning = released ? null : "원복 스크립트용 잠금을 풀지 못했습니다. 트랜잭션을 끝내면 풀립니다",
            };
        }

        private string? LogLine(RollbackScriptState state, int steps) => state switch
        {
            RollbackScriptState.Open => $"원복 스크립트 {steps}문장 (운영 접속, 커밋하면 확정)",
            RollbackScriptState.Incomplete => $"원복 스크립트 불완전: {string.Join(" · ", _incomplete)} — 커밋 전에 확인하세요",
            _ when _plan.Reason == NotCreatedReason.OverLimit => $"원복 스크립트를 만들지 않았습니다: {_plan.Message ?? "상한 넘음"}",
            _ when _plan.Reason == NotCreatedReason.Failed => $"원복 스크립트를 만들지 않았습니다: {_plan.Message ?? "실패"}",
            _ => null, // SCN 만 기록한 문장은 줄을 남기지 않는다(10.2)
        };

        private void Stop(NotCreatedReason reason, string? message) => _plan = new CapturePlan(reason) { Message = message };

        private async Task<bool> ReleaseAsync()
        {
            if (!_savepointSet)
            {
                return true;
            }

            try
            {
                await _channel.ExecuteAsync("ROLLBACK TO SAVEPOINT " + _dialect.SavepointName, CancellationToken.None).ConfigureAwait(false);
                _savepointSet = false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void AppendReturning()
        {
            if (!_dialect.SupportsReturningRowId)
            {
                _incomplete.Add("RETURNING 미지원");
                return;
            }

            if (_request.Binds.Keys.Any(k => string.Equals(k, ReturningBind, StringComparison.OrdinalIgnoreCase))
                || RollbackFlow.BindNamesIn(_request.Statement.Text).Any(n => string.Equals(n, ReturningBind, StringComparison.OrdinalIgnoreCase)))
            {
                _incomplete.Add("바인드 이름 " + ReturningBind + " 이 이미 있음");
                return;
            }

            StatementToSend = _request.Statement with
            {
                // 끝의 줄 주석(-- …) 안으로 들어가지 않게 줄을 바꿔 붙인다.
                Text = _request.Statement.Text.TrimEnd() + "\nRETURNING ROWID INTO :" + ReturningBind,
                // 원래 BindNames 가 비고 바인드 사전만 있는 문장(만든 쪽이 이름 목록을 채우지 않음)에 출력 바인드만 넣으면 사용자 바인드가 빠진다.
                // 그래서 이름 목록이 비었으면 사전 키를 먼저 이름으로 옮긴 뒤 덧붙인다.
                BindNames = [.. (_request.Statement.BindNames.Count > 0 ? _request.Statement.BindNames : _request.Binds.Keys), ReturningBind],
            };
            BindsToSend = new Dictionary<string, BindValue>(_request.Binds, StringComparer.OrdinalIgnoreCase)
            {
                [ReturningBind] = new(BindDataType.Varchar2, null, BindDirection.Out),
            };
            ReturningAppended = true;
        }

        /// <summary>읽을 열: UPDATE = 키 + SET 열, DELETE = 가상 열을 뺀 모든 열.</summary>
        private bool ResolveColumns(out string? why)
        {
            why = null;
            var shape = _shape!;
            if (shape.Verb == DmlVerb.Delete)
            {
                _readColumns = [.. Table.Columns.Where(c => !c.IsVirtual)];
                return true;
            }

            var list = new List<RollbackColumn>();
            foreach (var name in Table.KeyColumns.Concat(shape.SetColumns))
            {
                if (Find(name) is not { } column)
                {
                    why = $"열을 찾지 못함({name})";
                    return false;
                }

                if (!list.Contains(column))
                {
                    list.Add(column);
                }
            }

            _readColumns = list;
            return true;
        }

        private RollbackColumn? Find(string name) =>
            Table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            ?? Table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        private string Column(RollbackColumn column) =>
            string.IsNullOrEmpty(_shape!.Alias) ? RollbackSql.Ident(column.Name) : _shape.Alias + "." + RollbackSql.Ident(column.Name);

        private string LockSql()
        {
            var shape = _shape!;
            var rowId = string.IsNullOrEmpty(shape.Alias) ? "ROWIDTOCHAR(ROWID)" : $"ROWIDTOCHAR({shape.Alias}.ROWID)";
            var list = string.Join(", ", _readColumns.Select(c => _dialect.ReadExpression(c, Column(c))).Prepend(rowId));
            return $"SELECT {list} FROM {shape.TableText ?? RollbackSql.Table(Table.Table)}{Where(shape.WhereText)} FOR UPDATE";
        }

        private string ReadSql(int count, IReadOnlyList<RollbackColumn> columns)
        {
            var list = string.Join(", ", columns.Select(c => _dialect.ReadExpression(c, RollbackSql.Ident(c.Name))).Prepend("ROWIDTOCHAR(ROWID)"));
            var marks = string.Join(", ", Enumerable.Range(1, count).Select(i => ":r" + i.ToString(CultureInfo.InvariantCulture)));
            return $"SELECT {list} FROM {RollbackSql.Table(Table.Table)} WHERE ROWID IN ({marks})";
        }

        private async Task<Dictionary<string, object?[]>> ReadAfterAsync(List<string> rowIds, IReadOnlyList<RollbackColumn> columns, CancellationToken ct)
        {
            var after = new Dictionary<string, object?[]>(StringComparer.Ordinal);
            for (var start = 0; start < rowIds.Count; start += RowIdChunk)
            {
                var chunk = rowIds.Skip(start).Take(RowIdChunk).ToList();
                var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < chunk.Count; i++)
                {
                    binds["r" + (i + 1).ToString(CultureInfo.InvariantCulture)] = new BindValue(BindDataType.Varchar2, chunk[i]);
                }

                var rows = await _channel.QueryAsync(ReadSql(chunk.Count, columns), binds, chunk.Count, ct).ConfigureAwait(false);
                foreach (var row in rows.Rows)
                {
                    if (row.Length > 0 && Convert.ToString(row[0], CultureInfo.InvariantCulture) is { } id)
                    {
                        after[id] = row;
                    }
                }
            }

            return after;
        }

        private void CheckCounts(long? rowsAffected)
        {
            if (rowsAffected is { } n && n != _locked.Count)
            {
                _incomplete.Add($"행 수 다름(잠금 {_locked.Count:N0}, 실행 {n:N0})");
            }
        }

        private string? Lit(RollbackColumn column, object? value)
        {
            var literal = _dialect.Literal(column, value, out var why);
            if (literal is null && why is not null && !_incomplete.Contains(why))
            {
                _incomplete.Add(why);
            }

            return literal;
        }

        private async Task BuildUpdateAsync(long? rowsAffected, List<RollbackStep> steps, List<RollbackProbe> probes, List<string> touched,
                                            CancellationToken ct)
        {
            CheckCounts(rowsAffected);
            var shape = _shape!;
            var allSet = shape.SetColumns.Select(n => Find(n)!).Distinct().ToList();
            var keyColumns = Table.KeyColumns.Select(n => Find(n)!).ToList();
            var pkChanged = keyColumns.Any(allSet.Contains);
            var usePk = keyColumns.Count > 0 && !pkChanged;
            // 큰 열은 NULL 여부만 읽으므로 값이 바뀌어도 같게 보이고 리터럴로 되돌릴 수도 없다(6.3). 그 열은 되돌리지 않고 불완전으로 알린다.
            var setColumns = allSet.Where(c => c.Kind != RollbackValueKind.Large).ToList();
            if (allSet.Count != setColumns.Count)
            {
                _incomplete.Add($"큰 열({string.Join(", ", allSet.Except(setColumns).Select(c => c.Name))})은 되돌리지 않음");
            }
            var index = _readColumns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i + 1);
            var rowIds = _locked.Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture)!).ToList();
            if (!usePk)
            {
                touched.AddRange(rowIds); // ROWID 로 찾는 원복(RD-13 겹침 검사)
            }

            var after = await ReadAfterAsync(rowIds, _readColumns, ct).ConfigureAwait(false);
            var missing = 0;
            var table = RollbackSql.Table(Table.Table);
            foreach (var before in _locked)
            {
                var rowId = Convert.ToString(before[0], CultureInfo.InvariantCulture)!;
                if (!after.TryGetValue(rowId, out var now))
                {
                    missing++;
                    continue;
                }

                if (setColumns.Count == 0 || setColumns.All(c => Equals(before[index[c]], now[index[c]])))
                {
                    continue; // 바뀐 것이 없다(6.3)
                }

                var sets = new List<string>();
                var afterPreds = new List<string>();
                var beforePreds = new List<string>();
                var ok = true;
                foreach (var c in setColumns)
                {
                    var old = Lit(c, before[index[c]]);
                    var cur = Lit(c, now[index[c]]);
                    if (old is null || cur is null)
                    {
                        ok = false;
                        break;
                    }

                    sets.Add(RollbackSql.Ident(c.Name) + " = " + old);
                    afterPreds.Add(OracleFamilyLiterals.Predicate(RollbackSql.Ident(c.Name), cur));
                    beforePreds.Add(OracleFamilyLiterals.Predicate(RollbackSql.Ident(c.Name), old));
                }

                var key = ok ? Key(usePk, keyColumns, index, now, rowId) : null;
                if (key is null)
                {
                    continue;
                }

                steps.Add(new RollbackStep($"UPDATE {table} SET {string.Join(", ", sets)} WHERE {key} AND {string.Join(" AND ", afterPreds)}"));
                if (probes.Count < MaxProbes)
                {
                    probes.Add(new RollbackProbe($"SELECT COUNT(*) FROM {table} WHERE {key} AND {string.Join(" AND ", afterPreds)}",
                                                 $"SELECT COUNT(*) FROM {table} WHERE {key} AND {string.Join(" AND ", beforePreds)}"));
                }
            }

            if (missing > 0)
            {
                _incomplete.Add($"실행 직후 값을 읽지 못한 행 {missing:N0}개(행 이동)");
            }
        }

        private void BuildDelete(long? rowsAffected, List<RollbackStep> steps, List<RollbackProbe> probes, List<string> touched)
        {
            CheckCounts(rowsAffected);
            var keyColumns = Table.KeyColumns.Select(n => Find(n)!).ToList();
            var index = _readColumns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i + 1);
            var table = RollbackSql.Table(Table.Table);
            var names = string.Join(", ", _readColumns.Select(c => RollbackSql.Ident(c.Name)));
            var identity = _readColumns.FirstOrDefault(c => c.IsIdentity);
            var note = identity is null ? null : $"불완전: ID 열 {identity.Name}(GENERATED ALWAYS 면 ORA-32795 로 넣을 수 없음)";
            if (identity is not null)
            {
                _incomplete.Add($"ID 열({identity.Name})");
            }

            var largeSkipped = 0;
            foreach (var row in _locked)
            {
                var rowId = Convert.ToString(row[0], CultureInfo.InvariantCulture)!;
                touched.Add(rowId); // 되살린 행은 ROWID 가 바뀐다: 같은 묶음의 앞 문장이 ROWID 로 찾으면 못 찾는다(RD-13)

                var values = new List<string>();
                var ok = true;
                foreach (var c in _readColumns)
                {
                    var v = row[index[c]];
                    if (c.Kind == RollbackValueKind.Large)
                    {
                        // 읽는 식은 NULL 이면 1. 값이 있는 큰 열은 리터럴로 담을 수 없다(RD-12, 행 단위).
                        if (v is null or DBNull || Convert.ToInt64(v, CultureInfo.InvariantCulture) != 1)
                        {
                            ok = false;
                            largeSkipped++;
                            break;
                        }

                        values.Add("NULL");
                        continue;
                    }

                    if (Lit(c, v) is not { } literal)
                    {
                        ok = false;
                        break;
                    }

                    values.Add(literal);
                }

                if (!ok)
                {
                    continue; // 이유는 큰 열이면 아래에서, 리터럴 실패면 Lit 이 _incomplete 에 남겼다
                }

                steps.Add(new RollbackStep($"INSERT INTO {table} ({names}) VALUES ({string.Join(", ", values)})") { Note = note });
                if (probes.Count < MaxProbes && Key(keyColumns.Count > 0, keyColumns, index, row, rowId) is { } key)
                {
                    probes.Add(new RollbackProbe($"SELECT CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END FROM {table} WHERE {key}",
                                                 $"SELECT COUNT(*) FROM {table} WHERE {key}"));
                }
            }

            if (largeSkipped > 0 && !_incomplete.Any(r => r.StartsWith("큰 열", StringComparison.Ordinal)))
            {
                _incomplete.Add($"큰 열 값이 있는 행 {largeSkipped:N0}개");
            }
        }

        private async Task BuildInsertAsync(IReadOnlyDictionary<string, object?> outValues, List<RollbackStep> steps, List<RollbackProbe> probes,
                                            List<string> touched, CancellationToken ct)
        {
            if (!ReturningAppended)
            {
                return; // 이유는 이미 _incomplete 에 있다
            }

            var rowId = outValues.FirstOrDefault(kv => string.Equals(kv.Key, ReturningBind, StringComparison.OrdinalIgnoreCase)).Value is { } v and not DBNull
                ? Convert.ToString(v, CultureInfo.InvariantCulture)
                : null;
            if (string.IsNullOrEmpty(rowId))
            {
                _incomplete.Add("넣은 행의 ROWID 를 받지 못함");
                return;
            }

            var columns = Table.Columns.Where(c => !c.IsVirtual).ToList();
            var after = await ReadAfterAsync([rowId], columns, ct).ConfigureAwait(false);
            if (!after.TryGetValue(rowId, out var row))
            {
                _incomplete.Add("넣은 행을 읽지 못함");
                return;
            }

            var table = RollbackSql.Table(Table.Table);
            var keyNames = new HashSet<string>(Table.KeyColumns, StringComparer.Ordinal);
            var preds = new List<string>();
            var lossy = false;
            for (var i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                if (c.Kind == RollbackValueKind.Large)
                {
                    // 큰 열은 값으로 견주지 않는다(6.3). NULL 이면 IS NULL 만 넣고, 값이 있으면 이 원복 문장의 반대 문장(RD-8)이 그 값을 잃으므로 만들지 않게 표시한다.
                    if (row[i + 1] is { } flag and not DBNull && Convert.ToInt64(flag, CultureInfo.InvariantCulture) == 1)
                    {
                        preds.Add(RollbackSql.Ident(c.Name) + " IS NULL");
                    }
                    else
                    {
                        lossy = true;
                    }

                    continue;
                }

                var literal = _dialect.Literal(c, row[i + 1], out var why);
                if (literal is null)
                {
                    if (keyNames.Contains(c.Name) || keyNames.Count == 0)
                    {
                        // 키 값이 없거나(PK 없는 표에서 조건이 빠지면 ROWNUM = 1 이 다른 행을 지울 수 있다) 만들 수 없다.
                        _incomplete.Add(why ?? "값을 리터럴로 쓸 수 없음");
                        return;
                    }

                    // PK 로 행을 찾으므로 지우는 데는 지장이 없지만, 그 열을 남이 바꿨는지는 보지 못한다.
                    _incomplete.Add(why ?? $"값을 리터럴로 쓸 수 없음({c.Name})");
                    continue;
                }

                preds.Add(OracleFamilyLiterals.Predicate(RollbackSql.Ident(c.Name), literal));
            }

            if (keyNames.Count == 0)
            {
                preds.Add("ROWNUM = 1"); // RD-1: PK 없는 표에서 같은 값의 다른 행을 함께 지우지 않는다
                touched.Add(rowId);
            }

            steps.Add(new RollbackStep($"DELETE FROM {table} WHERE {string.Join(" AND ", preds)}") { Note = lossy ? RollbackSql.NoReverseNote : null });
            var byRowId = "ROWID = " + _dialect.RowIdLiteral(rowId);
            probes.Add(new RollbackProbe($"SELECT COUNT(*) FROM {table} WHERE {byRowId}",
                                         $"SELECT CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END FROM {table} WHERE {byRowId}"));
        }

        /// <summary>행을 찾는 조건: PK 가 있으면 PK = 값, 아니면 ROWID. 리터럴로 쓸 수 없으면 null.</summary>
        private string? Key(bool usePk, IReadOnlyList<RollbackColumn> keys, Dictionary<RollbackColumn, int> index, object?[] row, string rowId)
        {
            if (!usePk)
            {
                return "ROWID = " + _dialect.RowIdLiteral(rowId);
            }

            var parts = new List<string>();
            foreach (var k in keys)
            {
                if (Lit(k, row[index[k]]) is not { } literal)
                {
                    return null;
                }

                parts.Add(OracleFamilyLiterals.Predicate(RollbackSql.Ident(k.Name), literal));
            }

            return string.Join(" AND ", parts);
        }
    }
}
