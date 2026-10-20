using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Shell;
using Oranda.App.ViewModels;

namespace Oranda.App.Features.Update;

/// <summary>업데이트 창 글자 조회(<c>{Binding Strings[키]}</c>): 키 <c>x</c> → 리소스 <c>Update.x</c>. 창을 열 때의 언어로 고정된다.</summary>
public sealed class UpdateStrings(IShellText text)
{
    public string this[string key] => text.Lookup("Update." + key);
}

/// <summary>
/// 업데이트 창(도움말 &gt; 업데이트 확인…)의 모델. 상태·버튼은 모두 <see cref="UpdateService"/> 의 지금 값에서 읽고,
/// 서비스가 바뀔 때마다 UI 스레드에서 다시 읽는다. 창을 열면 아직 받은 것이 없을 때 곧바로 확인한다.
/// </summary>
public sealed partial class UpdateViewModel : ViewModelBase, IDisposable
{
    private readonly UpdateService _service;
    private readonly IShellText _text;

    public UpdateViewModel(UpdateService service, IShellText text)
    {
        _service = service;
        _text = text;
        Strings = new UpdateStrings(text);
        service.Changed += OnServiceChanged;
    }

    public UpdateStrings Strings { get; }

    /// <summary>창을 닫아 달라는 요청(「지금 다시 시작」으로 앱이 끝나거나 「설정 열기」를 누른 뒤).</summary>
    public event EventHandler? CloseRequested;

    public string TitleText => _text.Lookup("Update.title");

    public string CurrentVersion => _service.CurrentVersionText;

    public string NewVersion
    {
        get
        {
            var version = _service.Phase == UpdatePhase.Ready ? _service.ReadyVersion : _service.Offer?.Version;
            return version ?? "-";
        }
    }

    public string Notes => _service.Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Error ? _service.Offer?.NotesMarkdown ?? string.Empty : string.Empty;

    public bool HasNotes => Notes.Length > 0;

    public bool ShowVersions => _service.Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Ready or UpdatePhase.UpToDate;

    public bool ShowProgress => _service.Phase == UpdatePhase.Downloading;

    public int Progress => _service.Progress;

    public bool IsBusy => _service.Phase is UpdatePhase.Checking or UpdatePhase.Downloading;

    public string StatusText => UpdateTexts.Status(_service, _text);

    public bool CanDownload => _service.Phase == UpdatePhase.Available || (_service.Phase == UpdatePhase.Error && _service.Offer is not null);

    public bool CanCancelDownload => _service.Phase == UpdatePhase.Downloading;

    public bool CanApply => _service.Phase == UpdatePhase.Ready;

    public bool CanSkip => _service.Phase is UpdatePhase.Available or UpdatePhase.Error && _service.Offer is not null && !_service.IsSkipped;

    public bool CanOpenSettings => _service.Phase is UpdatePhase.NoFeed or UpdatePhase.InvalidFeed;

    public bool CanRecheck => _service.Phase is UpdatePhase.Error or UpdatePhase.UpToDate or UpdatePhase.Idle;

    /// <summary>창을 열 때 한 번: 받은 것·받는 중인 것이 없으면 확인한다.</summary>
    public Task StartAsync() =>
        _service.Phase is UpdatePhase.Ready or UpdatePhase.Downloading or UpdatePhase.Available or UpdatePhase.Checking
            ? Task.CompletedTask
            : _service.CheckAsync(manual: true);

    [RelayCommand]
    private Task RecheckAsync() => _service.CheckAsync(manual: true);

    [RelayCommand]
    private Task DownloadAsync() => _service.DownloadAsync();

    [RelayCommand]
    private void CancelDownload() => _service.CancelDownload();

    [RelayCommand]
    private void ApplyOnExit()
    {
        _service.ApplyOnExit();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ApplyNowAsync()
    {
        // 메인 창의 닫기 흐름이 먼저 돈다. 사용자가 취소하면 false 라서 이 창은 그대로 둔다.
        if (await _service.ApplyAndRestartAsync().ConfigureAwait(true))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private Task SkipAsync() => _service.SkipVersionAsync();

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        await ShellCommandRunner.RunAsync("oranda.tools.settings").ConfigureAwait(true);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _service.Changed -= OnServiceChanged;

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            OnPropertyChanged(string.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => OnPropertyChanged(string.Empty));
        }
    }
}
