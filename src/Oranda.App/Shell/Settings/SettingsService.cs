using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// 실행 중 앱이 쓰는 설정 한 벌(<see cref="Current"/>)과 바꾸고 저장하는 길(<see cref="UpdateAsync"/>).
/// 저장하면 <see cref="ISettingsStore"/> 가 바뀐 섹션마다 <c>SettingsChanged</c> 를 발행한다.
/// 처음 읽을 때 settings.json 이 없었으면 새 설치(<see cref="IsFirstRun"/>, TSK-09-09·D-018)로 본다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "SemaphoreSlim 은 대기 핸들을 만들지 않으면 정리할 자원이 없고, 종료 중 늦은 저장이 정리된 세마포어를 만나지 않게 둔다.")]
public sealed class SettingsService
{
    /// <summary>글자 크게/작게 한 단계(pt).</summary>
    public const double ZoomStep = 1;

    public const double MinEditorFontSize = 8;

    public const double MaxEditorFontSize = 32;

    public const double DefaultEditorFontSize = 13;

    public const double DefaultGridFontSize = 13;

    private readonly ISettingsStore _store;
    private readonly Func<bool>? _settingsFileExists;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private AppSettings _current = new();
    private int _loaded;

    /// <summary>파일 위치를 모르는 설정(시험의 메모리 저장소). 새 설치로 보지 않는다.</summary>
    public SettingsService(ISettingsStore store)
        : this(store, settingsFileExists: null)
    {
    }

    /// <summary>앱 조립이 쓰는 생성자: <see cref="IAppDataPaths.SettingsFile"/> 가 없으면 새 설치다.</summary>
    public SettingsService(ISettingsStore store, IAppDataPaths paths)
        : this(store, () => File.Exists(paths.SettingsFile))
    {
    }

    /// <param name="store">설정 저장소.</param>
    /// <param name="settingsFileExists">처음 읽기 직전에 settings.json 이 있는지 묻는다. null 이면 새 설치로 보지 않는다.</param>
    public SettingsService(ISettingsStore store, Func<bool>? settingsFileExists)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _settingsFileExists = settingsFileExists;
    }

    public AppSettings Current => Volatile.Read(ref _current);

    /// <summary>설정 파일이 새 버전이라 저장하지 않는다.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>파일을 읽을 때 알린 말(손상 백업 등). 없으면 null.</summary>
    public string? Notice { get; private set; }

    /// <summary>
    /// 새 설치: 처음 <see cref="LoadAsync"/> 할 때 settings.json 이 없었다. 앱이 도는 동안 바뀌지 않는다(뒤에 파일이 생겨도, 다른 프로세스가 만들어도).
    /// 시작 화면 「주로 하는 일은?」 카드의 조건이다(D-018).
    /// </summary>
    public bool IsFirstRun { get; private set; }

    /// <summary>이번 실행에서 「주로 하는 일은?」 카드를 「나중에」 로 닫았다(다음 실행은 파일이 있어 새 설치가 아니다).</summary>
    public bool FirstRunCardDismissed { get; set; }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 0)
        {
            IsFirstRun = _settingsFileExists is { } exists && !exists();
        }

        var doc = await _store.LoadAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _current, doc.Value);
        IsReadOnly = doc.IsReadOnly;
        Notice = doc.Notice;
        return doc.Value;
    }

    /// <summary>값을 바꾸고 저장한다. 바뀐 값이 먼저 <see cref="Current"/> 가 된 뒤 이벤트가 나간다.</summary>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        // 계산·대입·저장을 한 번에 하나씩 한다: 백그라운드(업데이트 확인 시각 기록 등)와 화면의 저장이 겹쳐도 서로의 변경을 덮지 않는다.
        await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = change(Current);
            Volatile.Write(ref _current, next);
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public Task SetThemeAsync(Theme theme, CancellationToken ct = default) =>
        UpdateAsync(s => s with { Ui = s.Ui with { Theme = theme } }, ct);

    /// <summary>에디터·그리드·출력 글자 크기를 한 단계 바꾼다(8~32).</summary>
    public Task ZoomAsync(double delta, CancellationToken ct = default) =>
        UpdateAsync(s => s with { Editor = s.Editor with { FontSize = Math.Clamp(s.Editor.FontSize + delta, MinEditorFontSize, MaxEditorFontSize) } }, ct);

    public Task ZoomResetAsync(CancellationToken ct = default) =>
        UpdateAsync(s => s with { Editor = s.Editor with { FontSize = DefaultEditorFontSize } }, ct);

    /// <summary>결과 그리드 글자 크기를 한 단계 바꾼다(8~32). 모든 그리드가 같은 크기를 쓴다.</summary>
    public Task GridZoomAsync(double delta, CancellationToken ct = default) =>
        SetGridFontSizeAsync(Current.Grid.FontSize + delta, ct);

    public Task GridZoomResetAsync(CancellationToken ct = default) =>
        SetGridFontSizeAsync(DefaultGridFontSize, ct);

    public Task SetGridFontSizeAsync(double size, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(size, MinEditorFontSize, MaxEditorFontSize);
        return UpdateAsync(s => s with { Grid = s.Grid with { FontSize = clamped } }, ct);
    }
}
