using System.Globalization;
using Avalonia.Threading;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.GridEdit;

/// <summary>저장 알림 종류. 색이 갈린다(성공 = 보통, 실패 = 오류 색).</summary>
public enum SaveFeedbackKind
{
    Success,
    Failure,
}

/// <summary>상태 표시줄에 잠깐 보이는 저장 알림. <paramref name="FilePath"/> 가 있으면 「열기」·「폴더 열기」 를 함께 보인다(파일 내보내기).</summary>
public sealed record SaveNotice(string Text, SaveFeedbackKind Kind, string? FilePath = null);

/// <summary>
/// TSK-13-18 저장 효과: 편집 반영·파일 내보내기가 끝난 순간을 상태 표시줄 알림으로 알린다.
/// 알림은 입력을 막지 않고 시간이 지나면 저절로 사라진다. 새 알림은 이전 알림을 바꾸고 사라질 시각을 다시 센다.
/// </summary>
public static class SaveFeedback
{
    private static int _version;

    /// <summary>지금 보이는 알림. 없으면 null.</summary>
    public static SaveNotice? Current { get; private set; }

    /// <summary>알림이 생기거나 사라질 때(UI 스레드가 아닐 수 있다).</summary>
    public static event EventHandler? Changed;

    /// <summary>성공 알림이 머무는 시간.</summary>
    internal static TimeSpan SuccessLifetime { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>실패 알림이 머무는 시간(읽을 틈을 더 준다).</summary>
    internal static TimeSpan FailureLifetime { get; set; } = TimeSpan.FromSeconds(12);

    public static void Show(SaveNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var version = Interlocked.Increment(ref _version);
        Current = notice;
        Changed?.Invoke(null, EventArgs.Empty);
        var life = notice.Kind == SaveFeedbackKind.Failure ? FailureLifetime : SuccessLifetime;
        _ = Task.Delay(life).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (Volatile.Read(ref _version) == version)
                {
                    Clear();
                }
            }),
            TaskScheduler.Default);
    }

    public static void Clear()
    {
        Interlocked.Increment(ref _version);
        if (Current is null)
        {
            return;
        }

        Current = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>편집 반영 결과 알림. 실패가 하나라도 있으면 실패 색.</summary>
    public static void ShowApplied(int appliedRows, int failedRows, bool committed)
    {
        if (failedRows > 0)
        {
            Show(new SaveNotice(appliedRows == 0
                ? Format("SaveFeedback.failed", "{0}행 저장하지 못함", failedRows)
                : Format("SaveFeedback.partial", "{0}행 저장됨, {1}행 저장하지 못함", appliedRows, failedRows), SaveFeedbackKind.Failure));
            return;
        }

        Show(new SaveNotice(committed
            ? Format("SaveFeedback.appliedCommitted", "{0}행 저장·커밋됨", appliedRows)
            : Format("SaveFeedback.applied", "{0}행 저장됨", appliedRows), SaveFeedbackKind.Success));
    }

    /// <summary>편집 반영 중 예외로 끝났을 때의 실패 알림.</summary>
    public static void ShowApplyFailed() =>
        Show(new SaveNotice(UiText.Get("SaveFeedback.applyFailed", "저장하지 못함"), SaveFeedbackKind.Failure));

    /// <summary>파일 내보내기가 끝났을 때 알림. 파일 이름과 열기 동작을 함께 보인다.</summary>
    public static void ShowExported(long rows, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Show(new SaveNotice(Format("SaveFeedback.exported", "{0}행 내보냄: {1}", rows.ToString("N0", CultureInfo.CurrentCulture), Path.GetFileName(path)), SaveFeedbackKind.Success, path));
    }

    /// <summary>파일 내보내기가 실패했을 때 알림.</summary>
    public static void ShowExportFailed(string reason) =>
        Show(new SaveNotice(Format("SaveFeedback.exportFailed", "내보내기 실패: {0}", reason), SaveFeedbackKind.Failure));

    private static string Format(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);
}
