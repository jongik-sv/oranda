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
            UpdatePhase.Error => Format(text, service.ErrorWhileDownloading ? "Update.state.error.download" : "Update.state.error.check", service.ErrorDetail),
            UpdatePhase.NotInstalled => text.Lookup("Update.state.notInstalled"),
            UpdatePhase.NoFeed => text.Lookup("Update.state.noFeed"),
            UpdatePhase.InvalidFeed => Format(text, "Update.state.invalidFeed", text.Lookup(UpdateFeedParser.ErrorKey(service.FeedError))),
            _ => string.Empty,
        };
    }

    private static string Format(IShellText text, string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, text.Lookup(key), args);
}
