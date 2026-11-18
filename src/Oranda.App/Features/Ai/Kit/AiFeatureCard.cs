using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oranda.App.Features.Ai.Kit;

/// <summary>
/// 채팅창 기능 카드(설계 §2 「기능 카드」, <c>ChatCardEntry</c> 로 넣는다): 정해진 답 틀의 결과 · 답한 모델 이름 · 카드 단추(「편집기에 넣기」·「복사」·「더 깊게」).
/// 편집기에 넣을 글은 기능이 기존 제안 막대 경로로 넣는다(<see cref="Insert"/>). AI 는 아무것도 실행하지 않는다. 모양은 <c>AiChatView.axaml</c> 의 DataTemplate.
/// </summary>
public sealed partial class AiFeatureCard : ObservableObject
{
    /// <param name="title">카드 제목(「상황 풀이」).</param>
    /// <param name="modelLabel">답한 모델 이름.</param>
    /// <param name="text">받은 글 그대로(틀이 깨졌으면 이 글을 보인다).</param>
    /// <param name="answer">틀대로 읽은 답(못 읽었으면 null).</param>
    public AiFeatureCard(string title, string modelLabel, string text, AiTemplatedAnswer? answer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title;
        ModelLabel = modelLabel ?? string.Empty;
        Text = text ?? string.Empty;
        Answer = answer;
    }

    public string Title { get; }

    public string ModelLabel { get; }

    public string Text { get; }

    public AiTemplatedAnswer? Answer { get; }

    /// <summary>틀대로 읽은 칸(못 읽었으면 빈 목록 — 화면은 <see cref="Text"/> 를 보인다).</summary>
    public IReadOnlyList<AiAnswerPart> Parts => Answer?.Parts ?? [];

    public bool HasParts => Answer is not null;

    public bool ShowsRawText => Answer is null;

    /// <summary>편집기에 넣을 글(없으면 「편집기에 넣기」 를 숨긴다).</summary>
    public string? InsertText { get; init; }

    /// <summary>편집기 제안 막대에 넣는다(넣었으면 true).</summary>
    public Func<string, bool>? Insert { get; init; }

    /// <summary>클립보드에 쓴다.</summary>
    public Func<string, Task>? CopyText { get; init; }

    /// <summary>「더 깊게」(주 모델로 채팅창 새 대화). null 이면 숨긴다.</summary>
    public Func<Task>? Deeper { get; init; }

    public bool CanInsertVisible => InsertText is not null && Insert is not null;

    public bool HasDeeper => Deeper is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InsertEditorCommand))]
    private bool _isInserted;

    private bool CanInsert() => CanInsertVisible && !IsInserted;

    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void InsertEditor()
    {
        if (InsertText is { } text && Insert is { } insert && insert(text))
        {
            IsInserted = true;
        }
    }

    [RelayCommand]
    private Task Copy() => CopyText?.Invoke(InsertText ?? Text) ?? Task.CompletedTask;

    [RelayCommand]
    private Task GoDeeper() => Deeper?.Invoke() ?? Task.CompletedTask;
}
