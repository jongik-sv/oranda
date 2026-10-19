using Oranda.Core.Text;

namespace Oranda.App.Features.SqlEditor;

/// <summary>편집기가 쓰는 대화상자 모음. 시험에서는 가짜로 바꾼다. 기본 구현은 <see cref="AvaloniaEditorDialogs"/>.</summary>
public interface IEditorDialogs
{
    /// <summary>열 파일 경로. 취소하면 null.</summary>
    Task<string?> PickOpenFileAsync(CancellationToken ct);

    /// <summary>저장할 파일 경로. 취소하면 null.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct);

    /// <summary>한 줄 입력. 취소하면 null.</summary>
    Task<string?> PromptAsync(string title, string initial, CancellationToken ct);

    /// <summary>목록에서 하나 고르기. 취소하면 null.</summary>
    Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct);

    /// <summary>예·아니오.</summary>
    Task<bool> ConfirmAsync(string message, CancellationToken ct);

    /// <summary>
    /// 제안 편집 미리보기(AI 확장 지점, TRD 4.10). <paramref name="before"/>·<paramref name="after"/> 는 바뀌는 부분의 앞뒤 글.
    /// 사용자가 받아들이면 true.
    /// </summary>
    Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct);

    /// <summary>작업 공간 파일(.md, 열 때는 MyBatis .xml 도) 고르기. 기본은 일반 파일 고르기와 같다.</summary>
    Task<string?> PickWorkspaceFileAsync(bool save, string suggestedName, CancellationToken ct)
        => save ? PickSaveFileAsync(suggestedName, ct) : PickOpenFileAsync(ct);

    /// <summary>알림 한 개(확인 단추만). 기본은 아무것도 하지 않는다.</summary>
    Task ShowMessageAsync(string title, string message, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>대화상자가 없는 환경(시험·헤드리스)의 기본값: 모두 취소·거절로 답한다. 제안 편집은 절대 자동 적용하지 않는다.</summary>
public sealed class NoDialogs : IEditorDialogs
{
    public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

    public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);
}

/// <summary><see cref="TextRange"/> 확장.</summary>
internal static class TextRangeExtensions
{
    public static bool IsWithin(this TextRange r, int length) => r.Start >= 0 && r.Length >= 0 && r.End <= length;
}
