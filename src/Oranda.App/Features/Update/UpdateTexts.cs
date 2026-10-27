using System.Globalization;
using Oranda.App.Shell;

namespace Oranda.App.Features.Update;

/// <summary>업데이트 상태를 사람이 읽을 문장으로 바꾼다(업데이트 창과 설정 화면이 같은 문구를 쓴다).</summary>
public static class UpdateTexts
{
    public static string Status(UpdateService service, IShellText text)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(text);
        var version = service.Phase == UpdatePhase.Ready ? service.ReadyVersion : service.Offer?.Version;
        return service.Phase switch
        {
            UpdatePhase.Checking => text.Lookup("Update.state.checking"),
            UpdatePhase.UpToDate => text.Lookup("Update.state.upToDate"),
            UpdatePhase.Available when service.IsSkipped => Format(text, "Update.state.availableSkipped", version),
            UpdatePhase.Available => Format(text, "Update.state.available", version),
            UpdatePhase.Downloading => Format(text, "Update.state.downloading", version, service.Progress),
            UpdatePhase.Ready => Format(text, service.ApplyMode == UpdateApplyMode.Restart ? "Update.state.readyRestart" : "Update.state.ready", version),
            UpdatePhase.Error => Format(text, service.ErrorWhileDownloading ? "Update.state.error.download" : "Update.state.error.check", Reason(service, text)),
            UpdatePhase.NotInstalled => text.Lookup("Update.state.notInstalled"),
            UpdatePhase.NoFeed => text.Lookup("Update.state.noFeed"),
            UpdatePhase.InvalidFeed => Format(text, "Update.state.invalidFeed", text.Lookup(UpdateFeedParser.ErrorKey(service.FeedError))),
            _ => string.Empty,
        };
    }

    /// <summary>오류일 때 툴팁에 보일 원문(예외 형식과 메시지). 오류가 아니면 null.</summary>
    public static string? ErrorToolTip(UpdateService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.Phase == UpdatePhase.Error ? service.ErrorDetail : null;
    }

    // 종류를 아는 실패는 쉬운 문구, 모르는 실패는 원문.
    private static string? Reason(UpdateService service, IShellText text) =>
        service.ErrorKind == UpdateFailure.Other ? service.ErrorDetail : text.Lookup(UpdateFailures.TextKey(service.ErrorKind));

    private static string Format(IShellText text, string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, text.Lookup(key), args);
}
