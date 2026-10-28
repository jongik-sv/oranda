using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oranda.Language.Ai.Tools;

/// <summary>도구 응답 직렬화 규칙: snake_case, null 필드 생략. 토큰 어림에도 같은 글을 쓴다.</summary>
public static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>응답 값을 JSON 노드로 만든다(AgentToolOutput.Result 용).</summary>
    public static System.Text.Json.Nodes.JsonNode ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, Options) ?? throw new InvalidOperationException("응답을 직렬화하지 못했습니다.");

    /// <summary>글자 수로 어림한 토큰 수(영문·식별자가 많은 JSON 은 글자 3개를 토큰 1개로 본다).</summary>
    public static int EstimateTokens<T>(T value) => (Serialize(value).Length + 2) / 3;
}

/// <summary>
/// 실행 한 번 동안 같은 조회를 다시 하지 않게 하는 캐시(설계 §5.1-5). 키는 접속·소유자.이름·도구·인자다.
/// 설계는 키에 <c>last_analyzed</c>·<c>last_ddl_time</c> 도 넣자고 했지만 기존 조회 서비스가 <c>last_ddl_time</c> 을 주지 않고,
/// 한 번의 에이전트 실행마다 새 캐시를 만들면 실행 중 통계가 바뀌어도 한 실행 안의 답은 일관되므로 수명으로 낡음을 막는다.
/// 실패한 조회는 남기지 않는다. 같은 조회를 기다리는 호출자는 각자의 취소 토큰으로만 취소된다:
/// 조회를 시작한 호출자가 취소돼 조회가 취소로 끝나도, 취소하지 않은 호출자는 조회를 다시 시작한다.
/// </summary>
public sealed class ToolRunCache
{
    private readonly ConcurrentDictionary<string, Task> _items = new(StringComparer.Ordinal);
    private int _hits;

    /// <summary>캐시에서 답한 횟수(시험·진단용).</summary>
    public int Hits => Volatile.Read(ref _hits);

    public void Clear() => _items.Clear();

    /// <param name="factory">조회를 시작한다. 취소 토큰은 조회를 시작하는 호출자의 것이다.</param>
    public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task<T> task;
            if (_items.TryGetValue(key, out var existing))
            {
                task = (Task<T>)existing;
                Interlocked.Increment(ref _hits);
            }
            else
            {
                var created = factory();
                var winner = _items.GetOrAdd(key, created);
                task = (Task<T>)winner;
                if (!ReferenceEquals(winner, created))
                {
                    Interlocked.Increment(ref _hits);
                }
            }

            try
            {
                return await task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && task.IsCanceled)
            {
                _items.TryRemove(new KeyValuePair<string, Task>(key, task)); // 다른 호출자의 취소였다: 다시 시작
            }
            catch when (task.IsFaulted)
            {
                _items.TryRemove(new KeyValuePair<string, Task>(key, task));
                throw;
            }
        }
    }
}
