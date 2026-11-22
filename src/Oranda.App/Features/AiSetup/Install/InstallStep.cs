using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;

namespace Oranda.App.Features.AiSetup.Install;

/// <summary>설치 단계 등록.</summary>
[OrandaService(typeof(IAiSetupStepFactory))]
public sealed class InstallStepFactory : IAiSetupStepFactory
{
    public AiSetupStepId StepId => AiSetupStepId.Install;

    public IAiSetupStep Create(AiSetupContext context) => new InstallStep(context);
}

/// <summary>설치 단계 상태.</summary>
public enum InstallState
{
    /// <summary>찾는 중.</summary>
    Checking,

    /// <summary>설치되어 있음(끝남).</summary>
    Installed,

    /// <summary>없음(「설치하기」).</summary>
    Missing,

    /// <summary>설치 중.</summary>
    Installing,

    /// <summary>설치 실패(직접 설치 한 줄).</summary>
    Failed,

    /// <summary>준비 서비스가 없음(직접 설치만).</summary>
    Unavailable,
}

/// <summary>
/// 설치 단계(갈래 A): 들어오면 claude 를 찾고, 있으면 판과 함께 끝남. 없으면 「설치하기」 → 서비스가 숨은 창에서 공식 설치 명령을 돌리고
/// 흐르는 막대·진행 글(접힘)을 보인다. 끝나면 다시 찾아 끝남. 실패면 쉬운 글 + 직접 설치 한 줄·「복사」·「다시 확인」(찾기만).
/// 설치 중 [이전]·창 닫기는 창 틀이 먼저 묻고(<see cref="IAiSetupLeaveGuard"/>), 떠나면 설치를 취소한다.
/// </summary>
public sealed class InstallStep : AiSetupStepBase, IAiSetupLeaveGuard
{
    private const int MaxLogLines = 40;

    private readonly List<string> _log = [];
    private InstallState _state = InstallState.Checking;
    private string? _version;
    private AiSetupErrorKind? _error;
    private string? _note;
    private CancellationTokenSource? _work;
    private int _generation;
    private int _findGeneration;

    public InstallStep(AiSetupContext context)
        : base(context)
    {
        ManualCommand = context.Claude?.ManualInstallCommand(context.Platform) ?? ClaudeInstallCommands.ManualFor(context.Platform);
    }

    public override AiSetupStepId StepId => AiSetupStepId.Install;

    public InstallState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                IsComplete = value == InstallState.Installed;
                OnPropertyChanged(nameof(StatusKind));
                OnPropertyChanged(nameof(StatusTitle));
                OnPropertyChanged(nameof(StatusDetail));
                OnPropertyChanged(nameof(IsInstalling));
                OnPropertyChanged(nameof(CanRecheck));
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(ShowsManualOpen));
                OnPropertyChanged(nameof(LeaveQuestion));
            }
        }
    }

    /// <summary>찾은 판(설치됨일 때).</summary>
    public string? Version => _version;

    /// <summary>실패 종류(실패일 때).</summary>
    public AiSetupErrorKind? Error => _error;

    public AiSetupStatusKind StatusKind => _state switch
    {
        InstallState.Installed => AiSetupStatusKind.Ok,
        InstallState.Checking or InstallState.Installing => AiSetupStatusKind.Wait,
        InstallState.Failed or InstallState.Unavailable => AiSetupStatusKind.Bad,
        _ => AiSetupStatusKind.Idle,
    };

    public string StatusTitle => _state switch
    {
        InstallState.Checking => InstallText.Checking(English),
        InstallState.Installed => InstallText.Installed(_version, English),
        InstallState.Installing => InstallText.Installing(English),
        InstallState.Failed => InstallText.Failed(English),
        InstallState.Unavailable => InstallText.Failed(English),
        _ => InstallText.NotInstalled(English),
    };

    public string? StatusDetail => _note ?? _state switch
    {
        InstallState.Installed => InstallText.InstalledDetail(English),
        InstallState.Installing => InstallText.InstallingDetail(English),
        InstallState.Failed => AiSetupErrorText.For(_error ?? AiSetupErrorKind.Unknown, English),
        InstallState.Unavailable => InstallText.Unavailable(English),
        InstallState.Missing => InstallText.NotInstalledDetail(English),
        _ => null,
    };

    /// <summary>설치 중(흐르는 막대).</summary>
    public bool IsInstalling => _state == InstallState.Installing;

    /// <summary>「다시 확인」 켜짐(설치 중에는 꺼짐).</summary>
    public bool CanRecheck => !IsInstalling;

    /// <summary>「설치하기」 켜짐.</summary>
    public bool CanInstall => Context.Claude is not null && _state is InstallState.Missing or InstallState.Failed;

    /// <summary>직접 설치 안내를 펼쳐 둔다(실패·서비스 없음).</summary>
    public bool ShowsManualOpen => _state is InstallState.Failed or InstallState.Unavailable;

    /// <summary>직접 설치 한 줄(OS 별).</summary>
    public string ManualCommand { get; }

    /// <summary>직접 설치 방법 한 줄 안내.</summary>
    public string ManualHow => InstallText.ManualHow(Context.Platform, English);

    /// <summary>진행 글(마지막 몇 줄).</summary>
    public string LogText => string.Join(Environment.NewLine, _log);

    /// <summary>진행 글이 있다.</summary>
    public bool HasLog => _log.Count > 0;

    /// <summary>「복사했습니다」 같은 짧은 알림. 없으면 null.</summary>
    public string? CopyNote
    {
        get => _copyNote;
        private set => Set(ref _copyNote, value);
    }

    private string? _copyNote;

    public string? LeaveQuestion => IsInstalling ? InstallText.LeaveQuestion(English) : null;

    public override Task EnterAsync(CancellationToken ct) => FindAsync(ct);

    public override Task LeaveAsync(CancellationToken ct)
    {
        // 설치·찾기를 멈춘다(늦게 온 결과는 버린다).
        _generation++;
        _findGeneration++;
        var work = Interlocked.Exchange(ref _work, null);
        if (work is not null)
        {
            work.Cancel();
            work.Dispose();
        }

        if (_state is InstallState.Installing or InstallState.Checking)
        {
            State = Context.Claude is null ? InstallState.Unavailable : InstallState.Missing;
        }

        return Task.CompletedTask;
    }

    /// <summary>「다시 확인」: 찾기만 다시.</summary>
    public async Task RecheckAsync(CancellationToken ct = default)
    {
        if (IsInstalling)
        {
            return;
        }

        await FindAsync(ct).ConfigureAwait(true);
        if (_state is InstallState.Missing or InstallState.Failed)
        {
            SetNote(InstallText.StillMissing(English));
        }
    }

    /// <summary>「설치하기」.</summary>
    public async Task InstallAsync()
    {
        if (!CanInstall || Context.Claude is not { } claude)
        {
            return;
        }

        var generation = ++_generation;
        var work = new CancellationTokenSource();
        if (Interlocked.Exchange(ref _work, work) is { } previous)
        {
            previous.Cancel();
            previous.Dispose();
        }
        _log.Clear();
        NotifyLog();
        SetNote(null);
        _error = null;
        State = InstallState.Installing;
        var progress = new LineProgress(this, SynchronizationContext.Current, generation);
        ClaudeInstallResult result;
        try
        {
            result = await claude.InstallAsync(progress, work.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            result = new ClaudeInstallResult(false, null, AiSetupErrorKind.Unknown, ManualCommand);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _work, null, work) == work)
            {
                work.Dispose();
            }
        }

        if (generation != _generation || IsDisposed)
        {
            return;
        }

        if (result.Success && result.Installation is { } installed)
        {
            _version = installed.Version;
            OnPropertyChanged(nameof(Version));
            State = InstallState.Installed;
            OnPropertyChanged(nameof(StatusTitle));
            return;
        }

        // 설치가 「실패」라도 실제로는 깔렸을 수 있다: 한 번 더 찾는다.
        await FindAsync(CancellationToken.None, failure: result.ErrorKind ?? AiSetupErrorKind.Unknown).ConfigureAwait(true);
    }

    /// <summary>직접 설치 한 줄 복사.</summary>
    public async Task CopyManualAsync()
    {
        try
        {
            await Context.CopyText(ManualCommand).ConfigureAwait(true);
            CopyNote = InstallText.Copied(English);
        }
        catch (Exception)
        {
            CopyNote = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _generation++;
        _findGeneration++;
        var work = Interlocked.Exchange(ref _work, null);
        work?.Cancel();
        work?.Dispose();
        base.Dispose(disposing);
    }

    protected override Control BuildView()
    {
        var install = AiSetupUi.Button(InstallText.InstallButton(English), accent: true);
        install.Bind(Visual.IsVisibleProperty, new Binding(nameof(CanInstall)));
        install.Click += async (_, _) => await InstallAsync().ConfigureAwait(true);
        InstallButton = install;

        var bar = new ProgressBar { IsIndeterminate = true, Height = 6, MinHeight = 6 };
        bar.Bind(Visual.IsVisibleProperty, new Binding(nameof(IsInstalling)));

        var log = new SelectableTextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontFamily = new Avalonia.Media.FontFamily("Consolas, Menlo, monospace"), FontSize = 12 };
        log.Bind(TextBlock.TextProperty, new Binding(nameof(LogText)));
        var logBox = new Expander { Header = InstallText.Log(English), Content = log, HorizontalAlignment = HorizontalAlignment.Stretch };
        logBox.Bind(Visual.IsVisibleProperty, new Binding(nameof(HasLog)));

        var copyNote = AiSetupUi.Caption(string.Empty);
        copyNote.Bind(TextBlock.TextProperty, new Binding(nameof(CopyNote)));
        copyNote.Bind(Visual.IsVisibleProperty, new Binding(nameof(CopyNote)) { Converter = StringConverters.IsNotNullOrEmpty });
        var recheck = AiSetupUi.Button(InstallText.Recheck(English));
        recheck.Bind(Avalonia.Input.InputElement.IsEnabledProperty, new Binding(nameof(CanRecheck)));
        recheck.Click += async (_, _) => await RecheckAsync().ConfigureAwait(true);
        RecheckButton = recheck;
        var manual = new Expander
        {
            Header = InstallText.ManualHeader(English),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    AiSetupUi.Text(ManualHow),
                    AiSetupUi.CopyLine(nameof(ManualCommand), InstallText.Copy(English), () => _ = CopyManualAsync()),
                    copyNote,
                    recheck,
                },
            },
        };
        manual.Bind(Expander.IsExpandedProperty, new Binding(nameof(ShowsManualOpen)) { Mode = BindingMode.OneWay });
        ManualBox = manual;

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                AiSetupUi.Caption(InstallText.Intro(English)),
                AiSetupUi.Status(nameof(StatusKind), nameof(StatusTitle), nameof(StatusDetail)),
                install,
                bar,
                logBox,
                manual,
            },
        };
    }

    internal Button? InstallButton { get; private set; }

    internal Button? RecheckButton { get; private set; }

    internal Expander? ManualBox { get; private set; }

    private async Task FindAsync(CancellationToken ct, AiSetupErrorKind? failure = null)
    {
        if (Context.Claude is not { } claude)
        {
            State = InstallState.Unavailable;
            return;
        }

        var generation = ++_findGeneration;
        var wasFailed = _state == InstallState.Failed;
        SetNote(null);
        if (failure is null)
        {
            State = InstallState.Checking;
        }

        ClaudeInstallation? found;
        try
        {
            found = await Task.Run(() => claude.FindAsync(ct), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            found = null;
        }

        if (generation != _findGeneration || IsDisposed)
        {
            return;
        }

        if (found is not null)
        {
            _version = found.Version;
            OnPropertyChanged(nameof(Version));
            State = InstallState.Installed;
            OnPropertyChanged(nameof(StatusTitle));
            return;
        }

        if (failure is { } kind)
        {
            _error = kind;
            OnPropertyChanged(nameof(Error));
            State = InstallState.Failed;
            OnPropertyChanged(nameof(StatusDetail));
            return;
        }

        // 실패 뒤 다시 확인해도 못 찾으면 직접 설치 안내를 그대로 둔다.
        State = wasFailed ? InstallState.Failed : InstallState.Missing;
    }

    private void SetNote(string? note)
    {
        if (_note == note)
        {
            return;
        }

        _note = note;
        OnPropertyChanged(nameof(StatusDetail));
    }

    private void AddLine(string line, int generation)
    {
        if (generation != _generation || IsDisposed || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _log.Add(line.TrimEnd());
        if (_log.Count > MaxLogLines)
        {
            _log.RemoveAt(0);
        }

        NotifyLog();
    }

    private void NotifyLog()
    {
        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(HasLog));
    }

    /// <summary>설치 진행 줄을 화면 스레드로 옮긴다(맥락이 없으면 그 자리에서).</summary>
    private sealed class LineProgress(InstallStep owner, SynchronizationContext? context, int generation) : IProgress<string>
    {
        public void Report(string value)
        {
            if (context is null || SynchronizationContext.Current == context)
            {
                owner.AddLine(value, generation);
            }
            else
            {
                context.Post(_ => owner.AddLine(value, generation), null);
            }
        }
    }
}
