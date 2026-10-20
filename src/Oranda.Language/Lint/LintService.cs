using System.Collections.Concurrent;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Language.Completion;
using Oranda.Language.Lexing;

namespace Oranda.Language.Lint;

/// <summary>
/// SQL 점검 서비스(TSK-13-04). 문서를 L1 로 문장 단위로 나눈 뒤, 조회·변경 문장만 토큰으로 구조(블록·FROM·조건)를 세워 규칙 8개를 돌린다.
/// ANTLR 을 쓰지 않고(전역 파스 트리를 켜지 않는다) DB 도 부르지 않는다: 메타데이터 규칙은 자동완성 캐시에 이미 든 표 정의만 쓴다.
/// 구조는 문장 원문을 키로 캐시해 바뀐 문장만 다시 세운다. 소유: TSK-13-04.
/// </summary>
[OrandaService(typeof(ILintService))]
internal sealed class LintService : ILintService
{
    /// <summary>문장 결과 캐시 상한(문장 수). 자동완성·문법 분석 캐시와 같은 규모의 큰 문서를 담는다.</summary>
    private const int CacheLimit = 16_384;

    /// <summary>캐시에 든 문장 원문의 글자 수 상한. 큰 문장을 고칠 때마다 예전 원문이 쌓여 메모리가 커지는 것을 막는다.</summary>
    private const long CacheCharLimit = 8_000_000;

    private readonly MetadataLookup? _lookup;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private long _cacheChars;

    // 생성자는 하나만 공개한다(컨테이너가 고르는 방식). 자동완성 제공자가 가진 메타데이터 캐시를 함께 쓴다.
    public LintService(ICompletionProvider completion)
        : this((completion as CompletionProvider)?.Lookup)
    {
    }

    internal LintService(MetadataLookup? lookup) => _lookup = lookup;

    public IReadOnlyList<Diagnostic> Lint(string text, ISession? session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<Diagnostic>();
        foreach (var statement in StatementSplitter.Split(text, SplitOptions.Default))
        {
            ct.ThrowIfCancellationRequested();
            var range = statement.Range;
            if (statement.Kind is not (StatementKind.Query or StatementKind.Dml) || range.Length <= 0 || range.End > text.Length)
            {
                continue;
            }

            foreach (var d in Cached(statement.Text, session))
            {
                result.Add(d with { Range = new TextRange(range.Start + d.Range.Start, d.Range.Length) });
            }
        }

        return result;
    }

    /// <summary>
    /// 문장 하나의 결과. 메타데이터 규칙이 본 표 정의(키와 그때의 값)를 함께 기억해 두고, 지금 캐시의 값이 그대로일 때만 지난 결과를 쓴다
    /// (표 정의가 나중에 도착하거나 바뀌면 다시 계산한다).
    /// </summary>
    private IReadOnlyList<Diagnostic> Cached(string statementText, ISession? session)
    {
        string dbKey = session?.DbKey ?? string.Empty;
        if (_cache.TryGetValue(statementText, out var hit) && hit.DbKey == dbKey && hit.StillValid(_lookup))
        {
            return hit.Diagnostics;
        }

        var catalog = _lookup is not null && session is not null ? new SessionCatalog(_lookup, session) : null;
        IReadOnlyList<Diagnostic> diagnostics;
        try
        {
            diagnostics = LintRules.Run(LintModel.Parse(statementText), catalog);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            diagnostics = []; // 이 문장만 건너뛴다: 한 문장의 예기치 못한 모양이 문서 전체 점검을 멈추지 않게
        }

        if (_cache.Count >= CacheLimit || Interlocked.Read(ref _cacheChars) > CacheCharLimit)
        {
            _cache.Clear();
            Interlocked.Exchange(ref _cacheChars, 0);
        }

        _cache[statementText] = new CacheEntry(dbKey, diagnostics, catalog?.Seen ?? []);
        Interlocked.Add(ref _cacheChars, statementText.Length);
        return diagnostics;
    }

    private sealed record CacheEntry(string DbKey, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<(string Key, TableInfo? Info)> Seen)
    {
        public bool StillValid(MetadataLookup? lookup)
        {
            foreach (var (key, info) in Seen)
            {
                TableInfo? now = null;
                if (lookup is not null && lookup.TryPeek<TableInfo>(key, out var found))
                {
                    now = found;
                }

                if (!ReferenceEquals(now, info))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>세션의 메모리 캐시에서 표 정의를 꺼낸다(기다리거나 DB 를 부르지 않는다). 본 키와 값을 기록한다.</summary>
    private sealed class SessionCatalog(MetadataLookup lookup, ISession session) : ILintCatalog
    {
        private readonly string _defaultOwner = (session.Server.SessionUser ?? session.Profile.User).ToUpperInvariant();
        private readonly List<(string Key, TableInfo? Info)> _seen = [];

        public IReadOnlyList<(string Key, TableInfo? Info)> Seen => _seen;

        public TableInfo? Table(string? owner, string name)
        {
            var key = MetadataLookup.TableKey(session, owner ?? _defaultOwner, name);
            TableInfo? info = lookup.TryPeek<TableInfo>(key, out var found) ? found : null;
            _seen.Add((key, info));
            return info;
        }
    }
}
