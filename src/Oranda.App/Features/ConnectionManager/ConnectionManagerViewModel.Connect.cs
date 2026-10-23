using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

// ConnectionManagerViewModel 의 일부: 접속 시험·저장·접속(ConnectionManagerViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class ConnectionManagerViewModel
{
    /// <summary>
    /// 접속 시험: 지금 폼 값(저장 안 한 값 포함)으로 임시 연결을 열어 서버 버전·걸린 시간을 상태 줄에 보이고 바로 닫는다.
    /// 비밀번호 칸이 비면 저장된 값을, 그것도 없으면 비밀번호 대화상자를 쓴다. 비밀번호는 저장하지 않는다.
    /// 시험하는 동안 이 명령은 꺼지고 <c>TestCancelCommand</c> 로 멈출 수 있다. 시간 제한은 폼의 「접속 제한(초)」.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanTest))]
    private async Task TestAsync(CancellationToken cancel)
    {
        if (Editor is null)
        {
            return;
        }

        var profile = Editor.TryBuild(out var errors);
        if (profile is null)
        {
            SetStatus(StatusKind.Error, string.Join(Environment.NewLine, errors));
            return;
        }

        var typed = string.IsNullOrEmpty(Editor.Password) ? null : Editor.Password;
        var limit = TimeSpan.FromSeconds(profile.ConnectTimeoutSeconds);
        IsBusy = true;
        try
        {
            while (true)
            {
                SetStatus(StatusKind.Info, $"'{profile.Name}' 접속을 시험하는 중입니다…");
                ConnectionTestOutcome outcome;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    timeout.CancelAfter(limit);
                    try
                    {
                        outcome = await _service.TestConnectionAsync(profile, typed, timeout.Token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        SetStatus(
                            cancel.IsCancellationRequested ? StatusKind.Info : StatusKind.Error,
                            cancel.IsCancellationRequested ? "접속 시험을 취소했습니다." : $"접속 시험이 제한 시간({limit.TotalSeconds:0}초)을 넘겨 멈췄습니다.");
                        return;
                    }
                }

                if (outcome.Status != ConnectStatus.PasswordRequired)
                {
                    LastTestOutcome = outcome;
                    SetStatus(outcome.Succeeded ? StatusKind.Info : StatusKind.Error, outcome.Message);
                    return;
                }

                if (AskPassword is null || await AskPassword(profile).ConfigureAwait(true) is not { } pw)
                {
                    SetStatus(StatusKind.Info, "접속 시험을 취소했습니다.");
                    return;
                }

                typed = pw;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanTest() => Editor is { HasRequiredFields: true } && !IsBusy;

    /// <summary>고른 접속을 지우거나 복제할 수 있는지(시험·접속 중에는 끈다).</summary>
    private bool CanChangeSelection() => HasSelection && !IsBusy;

    private bool CanDelete() => HasAnySelection && !IsBusy;

    private bool CanConnect() => Editor is { HasRequiredFields: true } && !IsBusy;

    /// <summary>마지막 접속 시험 결과(시험용).</summary>
    public ConnectionTestOutcome? LastTestOutcome { get; private set; }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => await SaveCurrentAsync().ConfigureAwait(true);

    private bool CanSave() => Editor is { IsDirty: true } && !IsBusy;

    /// <summary>편집 중인 내용을 저장한다. 성공하면 true.</summary>
    public async Task<bool> SaveCurrentAsync() => await SaveAndGetAsync().ConfigureAwait(true) is not null;

    /// <summary>편집 중인 내용을 저장하고 저장된 프로필을 돌려준다. 검증에 걸리거나 실패하면 null(상태 줄에 이유).</summary>
    private async Task<ConnectionProfile?> SaveAndGetAsync()
    {
        if (Editor is null)
        {
            return null;
        }

        var profile = Editor.TryBuild(out var errors);
        if (profile is null)
        {
            SetStatus(StatusKind.Error, string.Join(Environment.NewLine, errors));
            return null;
        }

        try
        {
            var saved = await _service.SaveProfileAsync(profile, Editor.Password, Editor.SavePassword, CancellationToken.None).ConfigureAwait(true);
            Editor.Password = string.Empty;
            var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            Rebuild(doc.Value, saved.Id);
            SetStatus(StatusKind.Info, $"'{saved.Name}' 을(를) 저장했습니다.");
            return saved;
        }
        catch (ConnectionValidationException ex)
        {
            SetStatus(StatusKind.Error, ex.Message);
            return null;
        }
    }

    /// <summary>편집 내용을 저장하고(필요하면) 접속한다. 성공하면 <see cref="CloseRequested"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // 저장 안 한 값이 있으면 먼저 저장한다. 비밀번호 칸의 값은 저장하지 않는 설정이어도 이번 접속에는 쓴다.
            var typed = string.IsNullOrEmpty(editor.Password) ? null : editor.Password;
            var profile = editor.IsNew || editor.IsDirty ? await SaveAndGetAsync().ConfigureAwait(true) : editor.Original;
            if (profile is null)
            {
                return;
            }

            var outcome = await ConnectWithPromptsAsync(profile, typed).ConfigureAwait(true);
            if (outcome?.IsConnected == true)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>접속을 시도하고, 비밀번호가 필요하거나 만료되면 묻는다. 취소하면 null.</summary>
    public async Task<ConnectOutcome?> ConnectWithPromptsAsync(ConnectionProfile profile, string? password = null)
    {
        var typed = password;
        var changedOnce = false;
        while (true)
        {
            var outcome = await _service.ConnectAsync(profile, typed, CancellationToken.None).ConfigureAwait(true);
            LastOutcome = outcome;
            switch (outcome.Status)
            {
                case ConnectStatus.Connected:
                    SetStatus(StatusKind.Info, outcome.Message);
                    return outcome;
                case ConnectStatus.PasswordRequired:
                    if (AskPassword is null || await AskPassword(profile).ConfigureAwait(true) is not { } pw)
                    {
                        SetStatus(StatusKind.Info, "접속을 취소했습니다.");
                        return null;
                    }

                    typed = pw;
                    break;
                case ConnectStatus.PasswordExpired when !changedOnce && AskChangePassword is not null:
                    var request = await AskChangePassword(profile, typed).ConfigureAwait(true);
                    if (request is not { } change)
                    {
                        SetStatus(StatusKind.Error, outcome.Message);
                        return null;
                    }

                    var result = await _service.ChangePasswordAsync(profile, change.Old ?? typed, change.New, CancellationToken.None).ConfigureAwait(true);
                    if (!result.Succeeded)
                    {
                        SetStatus(StatusKind.Error, result.Message);
                        return null;
                    }

                    changedOnce = true;
                    typed = change.New;
                    break;
                default:
                    SetStatus(StatusKind.Error, outcome.Message);
                    return outcome;
            }
        }
    }

    /// <summary>ADB 지갑 zip 을 끌어 놓았을 때: 프로필을 만들고 선택한 뒤, 비밀번호를 물어 접속한다.</summary>
    public async Task ImportWalletAsync(string zipPath, bool thenConnect = true)
    {
        if (!await ResolveUnsavedAsync().ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            ConnectionProfile created;
            try
            {
                created = await _service.ImportWalletAsync(zipPath, null, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is WalletFormatException or ConnectionValidationException or System.IO.IOException or UnauthorizedAccessException)
            {
                SetStatus(StatusKind.Error, ex.Message);
                return;
            }

            var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            Rebuild(doc.Value, created.Id);
            SetStatus(StatusKind.Info, $"지갑으로 접속 '{created.Name}' 을(를) 만들었습니다. 사용자와 비밀번호를 확인하세요.");

            if (thenConnect)
            {
                var outcome = await ConnectWithPromptsAsync(created).ConfigureAwait(true);
                if (outcome?.IsConnected == true)
                {
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>마지막 접속(<see cref="IConnectionService.LastProfile"/>)으로 다시 접속한다. 비밀번호가 필요하면 묻는다.</summary>
    public async Task<ConnectOutcome?> ReconnectAsync()
    {
        if (_service.LastProfile is not { } last)
        {
            SetStatus(StatusKind.Error, "다시 접속할 접속 정보가 없습니다.");
            return null;
        }

        // 저장소의 최신 값(이름·주소를 고쳤을 수 있다)을 우선한다.
        var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        var profile = doc.Value.Profiles.FirstOrDefault(p => p.Id == last.Id) ?? last;
        return await ConnectWithPromptsAsync(profile).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
