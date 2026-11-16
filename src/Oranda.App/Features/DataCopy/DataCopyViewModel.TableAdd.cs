using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

public sealed partial class DataCopyViewModel
{
    /// <summary>
    /// 「테이블 찾기」 팝업의 모델을 만든다: 원본 세션을 열어(필요하면 비밀번호를 묻는다) 계정 목록을 읽고,
    /// 계정마다 표·시노님과 테이블 명을 읽는 함수를 건넨다. 작업이 없거나 원본 세션을 열 수 없으면 null(이유는 상태 줄).
    /// </summary>
    public async Task<TableFinderViewModel?> CreateTableFinderAsync(CancellationToken ct = default)
    {
        if (RunInProgress() || CurrentJob is null)
        {
            return null;
        }

        if (Editor.Source is null)
        {
            Say(DataCopyText.SourceMissing, error: true);
            return null;
        }

        var session = await SourceSessionAsync(ct).ConfigureAwait(true);
        if (session is null || _services.Metadata is not { } metadata)
        {
            Say(session is null ? DataCopyText.SynonymNeedsSource : DataCopyText.MetadataMissing, error: true);
            return null;
        }

        IReadOnlyList<string> owners;
        try
        {
            owners = await metadata.ListSchemasAsync(session, ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            owners = [];
        }

        var defaultOwner = Editor.SourceOwner.Length > 0 ? Editor.SourceOwner : OpenedWith(session).User.ToUpperInvariant();
        return new TableFinderViewModel(owners, defaultOwner,
            async (owner, token) =>
            {
                var objects = await metadata.ListObjectsAsync(session, new ObjectQuery { Owner = owner, ObjectTypes = CandidateTypes }, token).ConfigureAwait(true);
                IReadOnlyDictionary<string, string> comments;
                try
                {
                    comments = await metadata.ListTableCommentsAsync(session, owner, token).ConfigureAwait(true);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    comments = new Dictionary<string, string>(StringComparer.Ordinal); // 테이블 명 없이도 고를 수 있다
                }

                return [.. objects.Select(o => new TableFinderSource(o.Owner, o.Name, o.ObjectType, comments.GetValueOrDefault(o.Name)))];
            },
            (owner, table) => Editor.Rules.Any(r => CopyOwners.SameOwner(Editor.EffectiveSource(r), owner) && string.Equals(r.Table, table, StringComparison.Ordinal)));
    }

    /// <summary>「테이블 찾기」에서 고른 표를 한 번에 규칙으로 더한다(시노님은 실제 표로 푼다). 끝나면 몇 개 더했는지 알린다.</summary>
    public async Task AddFoundTablesAsync(IReadOnlyList<DbObjectRef> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count == 0)
        {
            return;
        }

        var before = Editor.Rules.Count;
        var problems = new List<string>();
        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            await AddTableAsync(table, null, ct).ConfigureAwait(true);
            if (StatusIsError)
            {
                problems.Add(StatusText);
            }
        }

        var added = Editor.Rules.Count - before;
        var text = $"테이블 {added.ToString(System.Globalization.CultureInfo.InvariantCulture)}개 규칙을 추가했습니다.";
        Say(problems.Count == 0 ? text : $"{text} 더하지 못한 것: {string.Join(" / ", problems)}", problems.Count > 0);
    }

    /// <summary>
    /// 스키마 브라우저 「데이터 복사에 추가」: 열려 있는 작업에 표 규칙을 더한다. 열린 작업이 없으면 새 작업을 만들고,
    /// 원본 접속이 비어 있으면 표를 고른 접속(<paramref name="sourceProfile"/>)을 원본으로 둔다.
    /// </summary>
    public async Task AddTableAsync(DbObjectRef table, ConnectionProfile? sourceProfile = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (RunInProgress())
        {
            return; // 도는 동안에는 화면을 바꾸지 않는다
        }

        if (CurrentJob is null)
        {
            var created = await Jobs.NewAsync("새 작업 " + _services.Time.GetLocalNow().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(true);
            if (created is null)
            {
                Say(Jobs.Notice, error: true);
                return;
            }

            await OpenJobAsync(created, ct).ConfigureAwait(true);
        }

        if (Editor.Source is null && sourceProfile is not null)
        {
            Editor.Source = Editor.SourceOptions.FirstOrDefault(o => o.Profile.Id == sourceProfile.Id && o.Profile.Name == sourceProfile.Name);
        }

        Step = DataCopyStep.RuleEdit;
        var session = await SourceSessionAsync(ct).ConfigureAwait(true);
        if (!string.Equals(table.ObjectType, "SYNONYM", StringComparison.Ordinal))
        {
            await AddResolvedAsync(new CopyTableName(table.Owner, table.Name), session, ct).ConfigureAwait(true);
            return;
        }

        // 시노님 노드: 원본 세션으로 실제 계정·표를 푼다(PUBLIC 시노님은 계정 PUBLIC). 풀 수 없으면 더하지 않는다.
        if (session is null || _services.Metadata is not { } metadata)
        {
            Say(session is null ? DataCopyText.SynonymNeedsSource : DataCopyText.MetadataMissing, error: true);
            return;
        }

        var resolution = await CopyTableInput.ResolveAsync((o, n) => metadata.GetNameStepAsync(session, o, n, ct),
            CopyTableInput.Format(table.Owner, table.Name), Editor.SourceOwner, OpenedWith(session).User.ToUpperInvariant()).ConfigureAwait(true);
        await AddResolutionAsync(resolution, session, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 창의 표 입력(<c>표</c> 또는 <c>계정.표</c>, 시노님 가능): 원본 세션이 열리면 그 세션으로 실제 계정·표를 풀어 더한다(묶음 B 설계 3장).
    /// 풀리지 않거나 DB 링크를 거치면 더하지 않고 이유를 알린다. 원본 세션이 없으면(원본 없음·비밀번호 취소) 적은 계정, 없으면 기본 계정으로 더한다.
    /// </summary>
    public async Task AddTableInputAsync(string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (RunInProgress() || CurrentJob is null)
        {
            return;
        }

        Step = DataCopyStep.RuleEdit;
        var session = await SourceSessionAsync(ct).ConfigureAwait(true);
        if (session is not null && _services.Metadata is { } metadata)
        {
            input = await MatchNameCaseAsync(input, session, metadata, ct).ConfigureAwait(true);
            var resolution = await CopyTableInput.ResolveAsync((o, n) => metadata.GetNameStepAsync(session, o, n, ct),
                input, Editor.SourceOwner, OpenedWith(session).User.ToUpperInvariant()).ConfigureAwait(true);
            await AddResolutionAsync(resolution, session, ct).ConfigureAwait(true);
            return;
        }

        var (owner, name, problem) = CopyTableInput.Parse(input);
        if (problem is not null)
        {
            Say(problem, error: true);
            return;
        }

        var fallback = owner ?? (Editor.SourceOwner.Length > 0 ? Editor.SourceOwner : Editor.Source?.Profile.User.ToUpperInvariant() ?? string.Empty);
        await AddResolvedAsync(new CopyTableName(fallback, name), null, ct).ConfigureAwait(true);
        if (session is not null)
        {
            // 원본 세션은 열렸지만 메타데이터 서비스가 없어 시노님을 풀지 못했다: 적은 이름 그대로 더했음을 알린다.
            Say(DataCopyText.SynonymNotResolved(Editor.SelectedRule?.SourceName ?? name), error: true);
        }
    }

    /// <summary>자동 완성 후보 수 상한(드롭다운에 한 번에 보일 만큼).</summary>
    internal const int TableCandidateLimit = 50;

    private static readonly string[] CandidateTypes = ["TABLE", "SYNONYM"];

    // 자동 완성 후보: 이 원본 세션에서 읽은 계정별 표·시노님 이름(키 입력마다 사전을 읽지 않는다). 세션이 바뀌면 비운다.
    private ISession? _candidateSession;
    private readonly Dictionary<string, IReadOnlyList<string>> _candidateNames = new(StringComparer.Ordinal);

    // 계정별로 읽는 중인 후보 목록(같은 계정을 두 번 읽지 않는다). 끝나면 _candidateNames 로 옮긴다.
    private readonly Dictionary<string, Task<IReadOnlyList<string>>> _candidateLoads = new(StringComparer.Ordinal);

    // 표 입력 칸에 들어가며 원본 세션을 열어 본 원본(취소해도 같은 원본으로는 다시 묻지 않는다).
    private ConnectionProfile? _candidateTried;

    /// <summary>
    /// 표 입력 칸에 들어갈 때: 원본 세션이 아직 없으면 한 번 연다(자동 완성 후보를 읽을 세션, 표를 더할 때도 이 세션을 쓴다).
    /// 같은 원본으로 이미 열어 봤으면(비밀번호 입력을 취소했어도) 다시 묻지 않는다.
    /// </summary>
    public async Task PrepareTableCandidatesAsync(CancellationToken ct = default)
    {
        if (CurrentJob is null || Run.IsRunning || Editor.Source is not { } source)
        {
            return;
        }

        var session = OpenSourceSession();
        if (session is null)
        {
            if (_candidateTried is { } tried && SameSnapshot(tried, source.Profile))
            {
                return;
            }

            _candidateTried = source.Profile;
            session = await SourceSessionAsync(ct).ConfigureAwait(true);
        }

        // 기본 원본 계정의 후보를 미리 읽기 시작한다(첫 글자를 칠 때 이미 읽고 있거나 끝나 있게). 기다리지 않는다.
        var owner = Editor.SourceOwner.Length > 0 ? Editor.SourceOwner : (session is null ? string.Empty : OpenedWith(session).User.ToUpperInvariant());
        if (session is not null && _services.Metadata is { } metadata && owner.Length > 0)
        {
            _ = OwnerTableNamesAsync(session, metadata, owner, CancellationToken.None);
        }
    }

    /// <summary>
    /// 표 입력 자동 완성 후보. 이미 열린 원본 세션으로만 찾는다(키 입력 중에 비밀번호를 묻지 않는다). 「계정.」을 적었으면 그 계정, 아니면 기본 원본 계정의
    /// 표·시노님 가운데 이름이 적은 글로 시작하는 것, 그다음 글을 품은 것(대소문자 무시, 최대 <see cref="TableCandidateLimit"/>개). 세션·사전이 없으면 빈 목록.
    /// </summary>
    public async Task<IReadOnlyList<string>> TableCandidatesAsync(string? text, CancellationToken ct = default)
    {
        var typed = (text ?? string.Empty).Trim();
        if (typed.Length == 0 || typed.Contains('"', StringComparison.Ordinal) || OpenSourceSession() is not { } session
            || _services.Metadata is not { } metadata)
        {
            return [];
        }

        var dot = typed.IndexOf('.', StringComparison.Ordinal);
        var owner = dot >= 0 ? typed[..dot].ToUpperInvariant() : (Editor.SourceOwner.Length > 0 ? Editor.SourceOwner : OpenedWith(session).User.ToUpperInvariant());
        var prefix = dot >= 0 ? typed[(dot + 1)..] : typed;
        if (owner.Length == 0)
        {
            return [];
        }

        var names = await OwnerTableNamesAsync(session, metadata, owner, ct).ConfigureAwait(true);
        var hits = names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Concat(names.Where(n => prefix.Length > 0 && !n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
            .Take(TableCandidateLimit);
        // 대문자가 아닌 이름(따옴표로 만든 "Orders")은 따옴표로 감싸 보인다: 골라 더할 때 그 이름 그대로 찾게.
        return [.. hits.Select(n => CopyTableInput.Format(dot >= 0 ? owner : null, n))];
    }

    /// <summary>
    /// 테이블 이름은 대소문자를 가리지 않는다: 따옴표 없이 적은 이름이 사전에 대문자로는 없고 대소문자만 다른 이름(따옴표로 만든 <c>"Orders"</c>)이
    /// 하나뿐이면 그 이름으로 바꾼다. 계정을 적지 않았으면 기본 원본 계정에서 찾는다. 못 찾으면 적은 글 그대로.
    /// </summary>
    private async Task<string> MatchNameCaseAsync(string input, ISession session, IMetadataProvider metadata, CancellationToken ct)
    {
        if (input.Contains('"', StringComparison.Ordinal) || CopyTableInput.Parse(input) is not (var owner, var name, null))
        {
            return input;
        }

        var lookIn = owner ?? (Editor.SourceOwner.Length > 0 ? Editor.SourceOwner : OpenedWith(session).User.ToUpperInvariant());
        var names = await OwnerTableNamesAsync(session, metadata, lookIn, ct).ConfigureAwait(true);
        if (names.Contains(name, StringComparer.Ordinal))
        {
            return input;
        }

        var matches = names.Where(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? CopyTableInput.Format(owner, matches[0]) : input;
    }

    private async Task<IReadOnlyList<string>> OwnerTableNamesAsync(ISession session, IMetadataProvider metadata, string owner, CancellationToken ct)
    {
        if (!ReferenceEquals(_candidateSession, session))
        {
            _candidateSession = session;
            _candidateNames.Clear();
            _candidateLoads.Clear();
        }

        if (_candidateNames.TryGetValue(owner, out var cached))
        {
            return cached;
        }

        // 읽기는 기다리는 쪽의 취소와 따로 끝까지 간다: 첫 읽기(객체 목록 캐시가 없으면 서버 전체 목록)는 몇 초 걸리는데,
        // 키를 누를 때마다 자동 완성이 앞 요청을 취소해 읽기가 끝나지 못하고 후보가 끝내 안 나오던 문제를 막는다.
        if (!_candidateLoads.TryGetValue(owner, out var load))
        {
            load = LoadOwnerTableNamesAsync(session, metadata, owner);
            _candidateLoads[owner] = load;
        }

        return await load.WaitAsync(ct).ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<string>> LoadOwnerTableNamesAsync(ISession session, IMetadataProvider metadata, string owner)
    {
        IReadOnlyList<string> names;
        try
        {
            var objects = await metadata.ListObjectsAsync(session, new ObjectQuery { Owner = owner, ObjectTypes = CandidateTypes }, CancellationToken.None).ConfigureAwait(true);
            names = [.. objects.Select(o => o.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }
        catch (Exception)
        {
            names = []; // 후보를 못 읽어도 이름을 적어 더하는 길은 그대로다(다음 입력에서 다시 읽는다)
            if (ReferenceEquals(_candidateSession, session))
            {
                _candidateLoads.Remove(owner);
            }

            return names;
        }

        if (ReferenceEquals(_candidateSession, session))
        {
            _candidateNames[owner] = names;
            _candidateLoads.Remove(owner);
        }

        return names;
    }

    // 이미 열린 원본 세션(점검용 쌍의 원본 또는 열 정보용). 없으면 null — 열지 않는다.
    private ISession? OpenSourceSession()
    {
        if (Editor.Source is not { } source)
        {
            return null;
        }

        if (_sessions is { } pair && SameConnection(pair.Source, source.Profile, readOnlySource: true))
        {
            return pair.Source;
        }

        return _metadataSession is { } open && SameConnection(open, source.Profile, readOnlySource: true) ? open : null;
    }

    private async Task AddResolutionAsync(CopyTableResolution resolution, ISession session, CancellationToken ct)
    {
        if (resolution.Table is not { } table)
        {
            Say(resolution.Problem ?? string.Empty, error: true);
            return;
        }

        await AddResolvedAsync(table, session, ct).ConfigureAwait(true);
    }

    private async Task AddResolvedAsync(CopyTableName table, ISession? session, CancellationToken ct)
    {
        var rule = await Editor.AddTableAsync(table, session, ct).ConfigureAwait(true);
        var added = table.ViaSynonym ? $"{rule.SourceName} 규칙을 추가했습니다(시노님 {table.WrittenAs})." : $"{rule.SourceName} 규칙을 추가했습니다.";
        Say(Editor.Notice.Length > 0 ? Editor.Notice : added, Editor.Notice.Length > 0);
    }

    /// <summary>
    /// 화면의 작업이 저장된 작업과 다르다(규칙·변수·계정·옵션 등 무엇이든). 작업이 없거나 읽기 전용이면 false.
    /// 저장 파일과 같은 JSON 으로 비교한다(목록 필드는 참조가 아니라 내용으로).
    /// </summary>
    public bool HasUnsavedChanges
    {
        get
        {
            if (CurrentJob is not { } saved || IsReadOnlyJob)
            {
                return false;
            }

            try
            {
                return !SameJobContent(Editor.BuildJob(), saved);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
