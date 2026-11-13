using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Rollback;
using Oranda.Core.Storage;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// 설정 창 「되돌리기」 탭(되돌리기 지점 설계 10.4, 화면 5). <see cref="RollbackSettings"/> 여섯 항목과, 아래쪽의 보관 중인 묶음 수·크기·「모두 지우기…」.
/// 저장은 설정 창의 확인 단추가 <see cref="ToSettings"/> 로 한다(섹션 <c>rollback</c> 이 바뀌면 저장소가 <c>SettingsChanged</c> 를 낸다).
/// 저장소(<see cref="IRollbackStore"/>)가 없으면(앱 밖·RB-21 전) 원복 스크립트 항목을 흐리게 하고 보관 요약은 비워 둔다.
/// </summary>
public sealed partial class RollbackSettingsPage : ObservableObject
{
    /// <summary>보관 요약을 셀 때 읽는 묶음 수 상한.</summary>
    public const int SummaryLimit = 100_000;

    private readonly IShellText _text;
    private readonly IRollbackStore? _store;
    private readonly string? _directory;
    private readonly Func<DateTimeOffset> _now;
    private int _savedRetentionDays;
    private DateTimeOffset[] _groupTimes = [];

    [ObservableProperty]
    private bool _recordPoint;

    [ObservableProperty]
    private bool _scriptOnProduction;

    [ObservableProperty]
    private decimal _maxRowsPerStatement;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionNotice), nameof(HasRetentionNotice))]
    private decimal _retentionDays;

    [ObservableProperty]
    private bool _warnRowCountMismatch;

    [ObservableProperty]
    private bool _suggestBackupBeforeDataLossDdl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionNotice), nameof(HasRetentionNotice))]
    private string _storageSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskClearCommand))]
    private bool _hasStoredGroups;

    [ObservableProperty]
    private bool _isConfirmingClear;

    [ObservableProperty]
    private string _clearMessage = string.Empty;

    /// <param name="store">null = 저장소 없음(앱 밖, 저장소 항목 전). 원복 스크립트 항목이 흐려진다.</param>
    /// <param name="paths">파일 크기를 셀 자료 폴더. null 이면 크기를 세지 않는다.</param>
    public RollbackSettingsPage(RollbackSettings settings, IShellText text, IRollbackStore? store, IAppDataPaths? paths, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        _store = store;
        _directory = paths?.RollbackDirectory;
        _now = now ?? (() => DateTimeOffset.Now);
        var s = settings.Normalized();
        _recordPoint = s.RecordPoint;
        _scriptOnProduction = s.ScriptOnProduction;
        _maxRowsPerStatement = s.MaxRowsPerStatement;
        _retentionDays = s.RetentionDays;
        _savedRetentionDays = s.RetentionDays;
        _warnRowCountMismatch = s.WarnRowCountMismatch;
        _suggestBackupBeforeDataLossDdl = s.SuggestBackupBeforeDataLossDdl;
        CanStoreScripts = store?.CanStoreScripts ?? false;
    }

    /// <summary>이 PC 에 원복 스크립트를 안전하게 둘 수 있다(<see cref="IRollbackStore.CanStoreScripts"/>). 거짓이면 둘째 항목이 흐려진다.</summary>
    public bool CanStoreScripts { get; }

    public bool NoSecureStore => !CanStoreScripts;

    /// <summary>보관 기간을 줄였을 때 다음 시작 때 지워질 묶음 수 안내. 줄이지 않았거나 지울 것이 없으면 빈 글.</summary>
    public string RetentionNotice
    {
        get
        {
            var days = (int)Math.Clamp(RetentionDays, RollbackSettings.MinRetentionDays, RollbackSettings.MaxRetentionDays);
            if (days >= _savedRetentionDays)
            {
                return string.Empty;
            }

            var cutoff = _now().AddDays(-days);
            var count = _groupTimes.Count(t => t < cutoff);
            return count == 0 ? string.Empty : Format("Shell.settings.rollback.retentionNotice", count);
        }
    }

    public bool HasRetentionNotice => RetentionNotice.Length > 0;

    /// <summary>탭의 지금 값으로 만든 설정(범위 밖 값은 범위 안으로 당긴다).</summary>
    public RollbackSettings ToSettings(RollbackSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return (current with
        {
            RecordPoint = RecordPoint,
            ScriptOnProduction = ScriptOnProduction,
            MaxRowsPerStatement = (int)Math.Clamp(MaxRowsPerStatement, RollbackSettings.MinMaxRows, RollbackSettings.MaxMaxRows),
            RetentionDays = (int)Math.Clamp(RetentionDays, RollbackSettings.MinRetentionDays, RollbackSettings.MaxRetentionDays),
            WarnRowCountMismatch = WarnRowCountMismatch,
            SuggestBackupBeforeDataLossDdl = SuggestBackupBeforeDataLossDdl,
        }).Normalized();
    }

    /// <summary>저장한 뒤: 지금 보관 기간을 저장한 값으로 삼는다(창이 열린 채 남아도 줄인 기간 안내가 다시 뜨지 않게).</summary>
    public void AcceptSaved()
    {
        _savedRetentionDays = (int)Math.Clamp(RetentionDays, RollbackSettings.MinRetentionDays, RollbackSettings.MaxRetentionDays);
        OnPropertyChanged(nameof(RetentionNotice));
        OnPropertyChanged(nameof(HasRetentionNotice));
    }

    /// <summary>보관 중인 묶음 수·파일 크기를 읽어 요약 줄을 채운다. 실패하면 요약을 비운다(설정 창을 막지 않는다).</summary>
    public async Task RefreshSummaryAsync(CancellationToken ct = default)
    {
        if (_store is null)
        {
            StorageSummary = string.Empty;
            HasStoredGroups = false;
            return;
        }

        try
        {
            var groups = await _store.SearchAsync(new RollbackQuery { Limit = SummaryLimit }, ct).ConfigureAwait(true);
            _groupTimes = groups.Select(g => g.EndedAt ?? g.StartedAt).ToArray();
            var bytes = await Task.Run(() => FileBytes(_directory), ct).ConfigureAwait(true);
            HasStoredGroups = groups.Count > 0;
            StorageSummary = Format("Shell.settings.rollback.storage", groups.Count.ToString("N0", CultureInfo.CurrentCulture), SizeText(bytes));
            OnPropertyChanged(nameof(RetentionNotice));
            OnPropertyChanged(nameof(HasRetentionNotice));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _groupTimes = [];
            HasStoredGroups = false;
            StorageSummary = string.Empty;
        }
    }

    /// <summary>「모두 지우기…」: 바로 지우지 않고 확인 줄을 보인다.</summary>
    [RelayCommand(CanExecute = nameof(HasStoredGroups))]
    private void AskClear()
    {
        ClearMessage = string.Empty;
        IsConfirmingClear = true;
    }

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    /// <summary>확인한 뒤: 보관 중인 묶음의 행과 파일을 모두 지운다.</summary>
    [RelayCommand]
    private async Task ConfirmClearAsync()
    {
        IsConfirmingClear = false;
        if (_store is null)
        {
            return;
        }

        try
        {
            // 보관 기간 0일 = 지금보다 앞의 묶음 전부(계약에 「모두 지우기」 메서드가 없어 PruneAsync(0) 으로 대신한다).
            await _store.PruneAsync(0, CancellationToken.None).ConfigureAwait(true);
            ClearMessage = _text.Lookup("Shell.settings.rollback.cleared");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ClearMessage = Format("Shell.settings.rollback.clearFailed", ex.Message);
        }

        await RefreshSummaryAsync().ConfigureAwait(true);
    }

    private static long FileBytes(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            return new DirectoryInfo(directory).EnumerateFiles("*.orb", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>「3.1 MB」 처럼 사람이 읽는 크기.</summary>
    internal static string SizeText(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.CurrentCulture) + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " MB",
        _ => (bytes / (1024d * 1024 * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " GB",
    };

    private string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, _text.Lookup(key), args);
}
