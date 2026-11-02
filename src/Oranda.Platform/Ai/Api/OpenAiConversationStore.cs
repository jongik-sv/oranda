using Oranda.Core.Ai.Contracts;

namespace Oranda.Platform.Ai.Api;

/// <summary>API 경로의 한도와 시간 제한. 값은 레인 판단이며 시험은 작은 값을 주입한다.</summary>
/// <param name="ResponseTimeout">
/// 응답 머리글을 받을 때까지, 그리고 스트림 줄 사이의 무응답 제한. 슬롯이 하나인 서버는 앞 요청이 끝날 때까지 새 요청을 붙잡고 있고
/// 긴 문맥은 첫 토큰까지 오래 걸리므로 넉넉히 잡는다.
/// </param>
/// <param name="MaxModelCalls">한 번의 실행에서 모델을 부르는 횟수 상한(설계 §6.2).</param>
/// <param name="MaxToolCalls">한 번의 실행에서 도구를 부르는 횟수 상한(설계 §6.2).</param>
/// <param name="MaxMessages">대화 하나가 가질 수 있는 메시지 수.</param>
/// <param name="MaxBytes">대화 하나의 메시지 총 크기(JSON 글자 수, 이미지 data URL 포함). 설계가 허용한 5 MB 이미지 5장(base64 약 3,500만 자)이 첫 요청에 들어가는 크기다.</param>
/// <param name="MaxConversations">메모리에 보관하는 대화 수. 넘으면 오래 안 쓴 것부터 버린다.</param>
internal sealed record OpenAiApiOptions(
    TimeSpan ResponseTimeout,
    int MaxModelCalls = 40,
    int MaxToolCalls = 60,
    int MaxMessages = 400,
    long MaxBytes = 36L * 1024 * 1024,
    int MaxConversations = 12)
{
    public OpenAiApiOptions()
        : this(TimeSpan.FromMinutes(10))
    {
    }
}

/// <summary>
/// 이어 말하기 대화 보관소: <c>ResumeId → 메시지 목록</c>(JSON 글). 메모리에만 두며 앱을 끝내면 사라진다.
/// 같은 대화를 동시에 두 번 실행하지 못하게 사용 중 표시를 두고, 실행 도중에는 사본에서 일하다 끝에서 한 번에 확정한다(<see cref="Lease.Commit"/>).
/// </summary>
internal sealed class OpenAiConversationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
    private readonly OpenAiApiOptions _options;
    private long _clock;

    public OpenAiConversationStore(OpenAiApiOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// 대화를 연다. <paramref name="resumeId"/> 가 없으면 새 대화(<paramref name="systemMessage"/> 한 개로 시작)를 만들고, 있으면 그 대화를 잇는다.
    /// <paramref name="owner"/> 는 지금 실행의 전송 대상이며 대화를 시작한 것과 다르면 <c>resume_route_mismatch</c>(CLI 경로와 같은 코드)다.
    /// 모르는 id 는 <c>resume_unknown</c>, 다른 실행이 쓰는 중이면 <c>resume_busy</c>, 입력 토큰 한도를 넘긴 대화면 <c>context_limit</c> 이다.
    /// </summary>
    public (Lease? Lease, AiError? Error) Open(string? resumeId, string owner, string systemMessage)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(resumeId))
            {
                var id = "api-" + Guid.NewGuid().ToString("N");
                var created = new Conversation(id, owner, [systemMessage], systemMessage.Length) { Busy = true, LastUsed = ++_clock };
                _conversations[id] = created;
                Evict();
                return (new Lease(this, created, [systemMessage]), null);
            }

            if (!_conversations.TryGetValue(resumeId, out var existing))
            {
                return (null, new AiError("이어 말할 대화를 찾지 못했습니다. 앱을 다시 시작했거나 오래된 대화입니다. 새 대화로 시작하세요.", "resume_unknown"));
            }

            // 다른 프로필·서버 주소가 시작한 대화를 이어 말하면 앞 대화(SQL·이미지)가 지금 서버로 전송된다. 보내기 전에 거절한다.
            if (!string.Equals(existing.Owner, owner, StringComparison.Ordinal))
            {
                return (null, new AiError("이 대화는 지금 선택한 AI 모델이 시작한 것이 아니어서 이어 말할 수 없습니다. 새 대화로 시작하세요.", Cli.ClaudeSessionOwners.MismatchCode));
            }

            if (existing.Busy)
            {
                return (null, new AiError("이 대화는 다른 요청을 실행하는 중입니다. 끝난 뒤에 다시 보내세요.", "resume_busy"));
            }

            if (existing.SizeExceeded)
            {
                return (null, LimitError(resumed: true));
            }

            if (existing.ContextExceeded)
            {
                return (null, new AiError("이 대화는 입력 토큰이 프로필의 입력 한도를 넘었습니다. 새 대화로 시작하세요.", "context_limit"));
            }

            existing.Busy = true;
            existing.LastUsed = ++_clock;
            return (new Lease(this, existing, [.. existing.Messages]), null);
        }
    }

    /// <summary>메시지 수·크기가 한도를 넘는가.</summary>
    public bool Exceeds(int messageCount, long bytes) => messageCount > _options.MaxMessages || bytes > _options.MaxBytes;

    /// <summary>
    /// 상한 오류. <paramref name="resumed"/> 가 거짓이면 새 대화의 첫 요청이라 대화가 길어서가 아니라 이번 요청(첨부 이미지)이 커서이므로 그렇게 안내한다.
    /// </summary>
    public static AiError LimitError(bool resumed) =>
        resumed
            ? new AiError("이 대화가 너무 길어졌습니다(메시지 수나 이미지 크기 상한). 새 대화로 시작하세요.", "context_limit")
            : new AiError("이번 요청이 너무 큽니다(첨부한 이미지의 총 크기 상한). 이미지 수나 크기를 줄여 다시 보내세요.", "context_limit");

    // 사용 중이 아닌 대화 중 오래 안 쓴 것부터 버린다. 호출하는 쪽이 잠금을 잡고 있다.
    private void Evict()
    {
        while (_conversations.Count > _options.MaxConversations)
        {
            var oldest = _conversations.Values.Where(c => !c.Busy).OrderBy(c => c.LastUsed).FirstOrDefault();
            if (oldest is null)
            {
                return;
            }

            _conversations.Remove(oldest.Id);
        }
    }

    internal sealed class Conversation(string id, string owner, List<string> messages, long bytes)
    {
        public string Id { get; } = id;

        /// <summary>대화를 시작한 전송 대상(프로필 id 와 정규화한 서버 주소).</summary>
        public string Owner { get; } = owner;

        public List<string> Messages { get; set; } = messages;

        public long Bytes { get; set; } = bytes;

        public bool Busy { get; set; }

        public bool ContextExceeded { get; set; }

        /// <summary>메시지 수·크기 상한을 넘어 대화를 비웠다(입력 토큰 한도와 구분해 안내한다).</summary>
        public bool SizeExceeded { get; set; }

        /// <summary>마지막 실행이 끝(<see cref="Lease.Commit"/>)에 닿지 못하고 중간 지점까지만 확정됐다.</summary>
        public bool Interrupted { get; set; }

        public long LastUsed { get; set; }
    }

    /// <summary>열린 대화 한 개를 쓰는 권한. 끝내면(<see cref="Dispose"/>) 사용 중 표시를 푼다. 확정하지 않고 끝내면 대화는 열기 전 상태로 남는다.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly OpenAiConversationStore _store;
        private readonly Conversation _conversation;
        private readonly bool _interruptedAtOpen;
        private bool _disposed;

        internal Lease(OpenAiConversationStore store, Conversation conversation, List<string> snapshot)
        {
            _store = store;
            _conversation = conversation;
            _interruptedAtOpen = conversation.Interrupted;
            Messages = snapshot;
        }

        public string Id => _conversation.Id;

        /// <summary>
        /// 앞 실행이 오류·취소로 중간에 멈췄고 거기까지만 확정돼 있다(<see cref="Checkpoint"/>). 이어 말하기는 사용자 메시지가 연달아 오지 않게
        /// 호출한 쪽이 사이에 짧은 assistant 메시지를 끼운다.
        /// </summary>
        public bool Interrupted => _interruptedAtOpen;

        /// <summary>열 때 확정돼 있던 메시지의 사본. 실행은 이 목록에 덧붙여 일하다 <see cref="Commit"/> 으로 확정한다.</summary>
        public List<string> Messages { get; }

        /// <summary>
        /// 실행이 끝났다: 메시지를 대화에 확정한다. <paramref name="contextExceeded"/> 는 마지막 응답의 입력 토큰이 프로필 한도를 넘었다는 뜻이며,
        /// 참이면 다음 이어 말하기를 막는다. 메시지 수·크기가 한도를 넘어도 이번 응답은 이미 화면에 나갔으므로 오류로 바꾸지 않고,
        /// 메모리를 비운 채 다음 이어 말하기만 막는다(<c>context_limit</c>).
        /// </summary>
        public void Commit(IReadOnlyList<string> messages, long bytes, bool contextExceeded)
        {
            lock (_store._gate)
            {
                _conversation.Interrupted = false;
                if (_store.Exceeds(messages.Count, bytes))
                {
                    _conversation.Messages = [];
                    _conversation.Bytes = 0;
                    _conversation.SizeExceeded = true;
                    return;
                }

                _conversation.Messages = [.. messages];
                _conversation.Bytes = bytes;
                _conversation.ContextExceeded = contextExceeded;
            }
        }

        /// <summary>
        /// 앞 지점까지 확정한다: 사용자 메시지를 넣은 직후와 도구 한 바퀴(assistant 의 tool_calls 와 모든 tool 메시지)가 끝난 직후처럼 기록이 어긋나지 않는 곳에서만 부른다.
        /// 실행이 오류·취소로 끝나도 이 지점까지는 남아, 같은 id 로 이어 말하면 첫 질문(SQL)과 확인한 도구 결과를 모델이 계속 본다.
        /// 한도를 넘으면 확정하지 않는다(<see cref="Commit"/> 이 막는다).
        /// </summary>
        public void Checkpoint(IReadOnlyList<string> messages, long bytes)
        {
            lock (_store._gate)
            {
                if (_store.Exceeds(messages.Count, bytes))
                {
                    return;
                }

                _conversation.Messages = [.. messages];
                _conversation.Bytes = bytes;
                _conversation.Interrupted = true;
            }
        }

        public void Dispose()
        {
            lock (_store._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _conversation.Busy = false;
                _store.Evict();
            }
        }
    }
}
