using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Shell;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Features.AiSetup.AskAdmin;

/// <summary>
/// 「담당자에게 묻기」(갈래 C 마지막 단계). 보낼 글(<see cref="AiSetupDiagnostics.ForAdmin"/>: 판·OS·claude 설치 여부, 비밀 없음)을 보이고
/// 「복사」 하면 끝난다. 복사하지 못하면 직접 복사하라고 안내하고 끝내지 않는다.
/// </summary>
public sealed partial class AskAdminViewModel : ObservableObject, IDisposable
{
    private AiSetupDiagnosticInfo _info;
    private readonly Func<string, Task<bool>> _copy;
    private readonly IShellText? _shellText;
    private readonly AskAdminText _t;

    /// <param name="info">글에 넣을 값(판·OS·claude). claude 는 단계에 들어올 때 <see cref="UpdateClaude"/> 로 다시 채운다.</param>
    /// <param name="copy">글을 클립보드에 둔다(창이 연결). 못 두면 false.</param>
    /// <param name="text">한/영 글. 언어가 바뀌면 글을 다시 만든다.</param>
    public AskAdminViewModel(AiSetupDiagnosticInfo info, Func<string, Task<bool>> copy, IShellText? text)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(copy);
        _info = info;
        _copy = copy;
        _shellText = text;
        _t = new AskAdminText(text);
        if (text is not null)
        {
            text.LanguageChanged += OnLanguageChanged;
        }
    }

    public string Title => _t.Title;

    public string Intro => _t.Intro;

    public string CopyLabel => _t.CopyButton;

    public string Message => AiSetupDiagnostics.ForAdmin(_info, _t.IsEnglish);

    /// <summary>claude 를 다시 찾은 결과로 글을 고친다.</summary>
    public void UpdateClaude(bool found, string? version)
    {
        _info = _info with { ClaudeFound = found, ClaudeVersion = found ? version : null };
        OnPropertyChanged(nameof(Message));
    }

    /// <summary>복사를 마쳐 이 단계가 끝났다.</summary>
    [ObservableProperty]
    private bool _isComplete;

    /// <summary>복사 결과 한 줄(처음엔 빈 글).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    private bool _copiedOk;
    private bool _copying;

    /// <summary>글을 클립보드에 둔다. 한 번 성공하면 단계는 끝난 채로 남는다(다시 눌러 실패해도 안내만 바뀐다).</summary>
    [RelayCommand]
    public async Task CopyAsync()
    {
        if (_copying)
        {
            return;
        }

        _copying = true;
        bool ok;
        try
        {
            ok = await _copy(Message).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 다른 프로그램이 클립보드를 쥐고 있어도 단계는 안내만 하고 계속된다.
        catch (Exception)
#pragma warning restore CA1031
        {
            ok = false;
        }
        finally
        {
            _copying = false;
        }

        _copiedOk = ok;
        StatusText = ok ? _t.Copied : _t.CopyFailed;
        if (ok)
        {
            IsComplete = true;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(CopyLabel));
        OnPropertyChanged(nameof(Message));
        if (StatusText.Length > 0)
        {
            StatusText = _copiedOk ? _t.Copied : _t.CopyFailed;
        }
    }

    public void Dispose()
    {
        if (_shellText is not null)
        {
            _shellText.LanguageChanged -= OnLanguageChanged;
        }
    }
}
