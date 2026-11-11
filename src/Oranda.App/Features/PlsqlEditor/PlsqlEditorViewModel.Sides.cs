using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>「구성원」 목록 한 줄: 명세·본문에서 찾은 같은 구성원(종류·이름·몇 번째 겹쳐 쓰기)을 묶는다(PKG1).</summary>
public sealed record PlsqlMemberEntry(PlsqlMemberKind Kind, string Name, int? SpecOffset, int? BodyOffset)
{
    public bool InSpec => SpecOffset is not null;

    public bool InBody => BodyOffset is not null;

    /// <summary>종류 글(프로시저·함수·타입·커서).</summary>
    public string KindText => Kind switch
    {
        PlsqlMemberKind.Procedure => UiText.Get("Plsql.member.procedure", "프로시저"),
        PlsqlMemberKind.Function => UiText.Get("Plsql.member.function", "함수"),
        PlsqlMemberKind.Type => UiText.Get("Plsql.member.type", "타입"),
        _ => UiText.Get("Plsql.member.cursor", "커서"),
    };

    /// <summary>있는 곳: 「명세·본문」 / 「본문만」 / 「명세만」.</summary>
    public string WhereText => (InSpec, InBody) switch
    {
        (true, true) => UiText.Get("Plsql.member.both", "명세·본문"),
        (false, true) => UiText.Get("Plsql.member.bodyOnly", "본문만"),
        _ => UiText.Get("Plsql.member.specOnly", "명세만"),
    };
}

// PlsqlEditorViewModel 의 일부: 명세 | 본문 두 쪽(PKG1, D-067). 한 탭이 쪽마다 글·되돌리기·고침 표시·서버 기준을 따로 갖는다.
public sealed partial class PlsqlEditorViewModel
{
    private int _membersVersion;

    /// <summary>이 탭의 쪽들(패키지·타입은 둘, 그 밖은 하나).</summary>
    internal IEnumerable<PlsqlSourcePart> Parts => _secondary is null ? [_primary] : [_primary, _secondary];

    /// <summary>명세 쪽(쌍이 없는 유형은 그 한 쪽). 본문만 있는 새 문서면 null.</summary>
    public PlsqlSourcePart? SpecPart => Parts.FirstOrDefault(p => p.Side == PlsqlSide.Spec);

    /// <summary>본문 쪽. 쌍이 없는 유형이면 null.</summary>
    public PlsqlSourcePart? BodyPart => _secondary is null ? null : Parts.FirstOrDefault(p => p.Side == PlsqlSide.Body);

    /// <summary>처음 만든 쪽의 편집기(편집 화면의 첫 편집기가 묶인다).</summary>
    internal SqlEditorViewModel PrimaryEditor => _primary.Editor;

    /// <summary>다른 쪽의 편집기(쌍이 없으면 null, 편집 화면이 둘째 편집기를 만들어 묶는다).</summary>
    internal SqlEditorViewModel? SecondaryEditor => _secondary?.Editor;

    /// <summary>보이는 쪽.</summary>
    public PlsqlSide Side => _current.Side;

    /// <summary>
    /// 편집 도구줄 「명세 | 본문」 단추의 고른 칸(0 = 명세, 1 = 본문). 고르면 그 쪽으로 바꾼다(아직 읽지 않았으면 서버에서 읽는다).
    /// 바꾸지 못하면(접속 없음·읽기 실패) 고른 칸이 지금 쪽으로 돌아온다.
    /// </summary>
    public int SideIndex
    {
        get => _current.Side == PlsqlSide.Body ? 1 : 0;
        set
        {
            if (value is 0 or 1 && value != SideIndex)
            {
                _ = SwitchFromViewAsync(value == 1 ? PlsqlSide.Body : PlsqlSide.Spec);
            }
        }
    }

    private async Task SwitchFromViewAsync(PlsqlSide side)
    {
        try
        {
            await SwitchSideAsync(side).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            OnPropertyChanged(nameof(SideIndex));
        }
    }

    public bool IsSpecSide => _current.Side == PlsqlSide.Spec;

    public bool IsBodySide => _current.Side == PlsqlSide.Body;

    /// <summary>명세/본문 두 쪽을 가진 탭(패키지·타입). 접속을 기다리는 복원 탭도 참이다.</summary>
    public bool HasSides => _secondary is not null;

    /// <summary>
    /// 명세/본문을 바꿔 볼 수 있다(문맥 키 <c>isPackageDoc</c>). 접속을 기다리는 복원 탭은 다른 쪽 글을 저장해 두었을 때만(서버에서 읽을 수 없다).
    /// </summary>
    public bool IsPackageDoc => _secondary is not null && (!AwaitingSession || Parts.All(p => p.Loaded));

    /// <summary>어느 한 쪽이라도 고쳤다(미리보기 탭 고정·닫기 확인).</summary>
    public bool IsDirty => Parts.Any(p => p.Editor.IsDirty);

    /// <summary>본문 쪽인데 서버에 본문이 없고 글이 비었다: 「본문 만들기」 안내를 보인다.</summary>
    public bool ShowCreateBodyHint => _current.Side == PlsqlSide.Body && _current.Missing && string.IsNullOrWhiteSpace(_current.Editor.Text);

    /// <summary>「구성원」 목록(명세·본문에서 찾은 프로시저·함수·타입·커서). 패키지·타입이 아니면 비어 있다.</summary>
    public ObservableCollection<PlsqlMemberEntry> Members { get; } = [];

    /// <summary>「구성원」 목록을 펼쳐 보인다(접으면 머리 줄만).</summary>
    [ObservableProperty]
    public partial bool MembersExpanded { get; set; } = true;

    private PlsqlSourcePart NewPart(PlsqlSide side, SqlEditorViewModel editor)
    {
        var part = new PlsqlSourcePart(side, editor);
        editor.PropertyChanged += (_, e) =>
        {
            // 고른 글·되돌리기 상태가 바뀌면 문맥 키도 따라간다(Ctrl+C·X·Z·Y 의 활성 조건, SyncKeys). 보이는 쪽 편집기만.
            if (e.PropertyName is nameof(SqlEditorViewModel.HasSelection) or nameof(SqlEditorViewModel.CanUndo) or nameof(SqlEditorViewModel.CanRedo))
            {
                if (ReferenceEquals(editor, Editor))
                {
                    SyncKeys();
                }
            }
            else if (e.PropertyName == nameof(SqlEditorViewModel.IsDirty))
            {
                OnPropertyChanged(nameof(IsDirty));
            }
        };
        editor.Document.TextChanged += (_, _) =>
        {
            if (ReferenceEquals(part, _current))
            {
                OnPropertyChanged(nameof(ShowCreateBodyHint));
            }

            ScheduleMembers();
        };
        return part;
    }

    /// <summary>
    /// 열 대상에 맞게 쪽을 짓는다. 패키지·타입이면 처음 쪽 = 명세, 다른 쪽 = 본문으로 두고 <paramref name="target"/> 쪽을 보인다.
    /// 돌려주는 값: 먼저 읽을 쪽과 함께 읽을 다른 쪽(쌍이 없으면 null).
    /// </summary>
    private (PlsqlSourcePart Want, PlsqlSourcePart? Other) Arrange(DbObjectRef target)
    {
        if (PlsqlSourceText.SiblingType(target.ObjectType) is null)
        {
            UseSinglePart(target);
            return (_primary, null);
        }

        var specType = PlsqlSourceText.SpecTypeOf(target.ObjectType);
        _primary.Side = PlsqlSide.Spec;
        _primary.Target = target with { ObjectType = specType };
        _secondary ??= NewPart(PlsqlSide.Body, new SqlEditorViewModel { Title = Title });
        _secondary.Side = PlsqlSide.Body;
        _secondary.Target = target with { ObjectType = PlsqlSourceText.SiblingType(specType)! };
        foreach (var part in Parts)
        {
            part.Errors = [];
            part.Loaded = false;
            part.Missing = false;
        }

        var want = PlsqlSourceText.IsBodyType(target.ObjectType) ? _secondary : _primary;
        SetCurrent(want);
        RaiseSides();
        return (want, ReferenceEquals(want, _primary) ? _secondary : _primary);
    }

    /// <summary>쌍이 없는 한 쪽 탭으로 둔다(새 문서·파일·프로시저 …). 다른 쪽이 있었으면 버린다.</summary>
    private void UseSinglePart(DbObjectRef? target)
    {
        _secondary = null;
        _primary.Side = PlsqlSide.Spec;
        _primary.Target = target;
        _primary.Baseline = null;
        _primary.Line1Shift = 0;
        _primary.ServerSource = null;
        _primary.Errors = [];
        _primary.Missing = false;
        SetCurrent(_primary);
        Members.Clear();
        RaiseSides();
    }

    /// <summary>새 문서를 패키지·타입(명세 또는 본문)으로 컴파일했다: 다른 쪽을 붙인다(아직 읽지 않음 — 처음 볼 때 서버에서 읽는다).</summary>
    private void BecomePair(PlsqlSourcePart part)
    {
        if (_secondary is not null || part.Target is not { } target || part.Editor.FilePath is not null
            || PlsqlSourceText.SiblingType(target.ObjectType) is not { } sibling)
        {
            return;
        }

        part.Side = PlsqlSourceText.IsBodyType(target.ObjectType) ? PlsqlSide.Body : PlsqlSide.Spec;
        var other = NewPart(part.Side == PlsqlSide.Body ? PlsqlSide.Spec : PlsqlSide.Body, new SqlEditorViewModel { Title = Title });
        other.Target = target with { ObjectType = sibling };
        other.Editor.ConnectionProfileId = part.Editor.ConnectionProfileId;
        _secondary = other;
        part.Errors = [.. part.Errors.Select(e => e with { ObjectType = target.ObjectType })];
        RaiseSides();
        SyncKeys();
    }

    /// <summary>
    /// 다른 쪽을 서버에서 조용히 읽는다(본문을 열면 명세, 명세를 열면 본문). 서버에 없으면(<see cref="InvalidOperationException"/>) 빈 쪽으로 두고
    /// <see cref="PlsqlSourcePart.Missing"/> 을 켠다. 그 밖의 실패는 읽지 않은 채로 둔다(처음 볼 때 다시 읽는다).
    /// </summary>
    private async Task LoadCompanionAsync(IMetadataProvider meta, ISession session, PlsqlSourcePart part, int gen, CancellationToken ct)
    {
        if (part.Target is not { } target)
        {
            return;
        }

        try
        {
            string source;
            try
            {
                source = await meta.GetSourceAsync(session, target, ct).ConfigureAwait(true);
            }
            catch (InvalidOperationException)
            {
                if (gen == _generation)
                {
                    part.Editor.LoadText(string.Empty);
                    part.Editor.ConnectionProfileId = session.Profile.Id;
                    part.Baseline = null;
                    part.ServerSource = null;
                    part.Line1Shift = 0;
                    part.Errors = [];
                    part.Missing = true;
                    part.Loaded = true;
                    SetServerChanged(part, false);
                }

                return;
            }

            var ddl = await ReadLastDdlAsync(meta, session, target, ct).ConfigureAwait(true);
            if (gen != _generation)
            {
                return;
            }

            Fill(part, source, ddl, session);
            part.Errors = await ReadErrorsAsync(meta, session, part, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 읽지 못했다: 처음 볼 때 다시 읽는다.
        }
        finally
        {
            RaiseSides();
        }
    }

    /// <summary>
    /// 명세 ↔ 본문(<c>oranda.plsql.toggleSpecBody</c>, Ctrl+Shift+B). <paramref name="side"/> 를 주면 그 쪽으로. 다른 쪽을 아직 읽지 않았으면 서버에서 읽는다.
    /// 바꿨으면(이미 그 쪽이면) true. 쌍이 없거나 읽을 수 없으면 상태 줄에 말하고 false.
    /// </summary>
    public async Task<bool> SwitchSideAsync(PlsqlSide? side = null, CancellationToken ct = default)
    {
        if (_secondary is null)
        {
            return false;
        }

        var wanted = side ?? (_current.Side == PlsqlSide.Spec ? PlsqlSide.Body : PlsqlSide.Spec);
        var part = Parts.First(p => p.Side == wanted);
        if (ReferenceEquals(part, _current))
        {
            return true;
        }

        if (!part.Loaded)
        {
            if (_session is not { } session || ResolveMeta() is not { } meta)
            {
                StatusText = UiText.Get("Plsql.side.needsSession", "접속하면 다른 쪽 소스를 읽는다");
                return false;
            }

            var gen = _generation;
            IsBusy = true;
            try
            {
                await LoadCompanionAsync(meta, session, part, gen, ct).ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
            }

            if (!part.Loaded)
            {
                StatusText = UiText.Format("Plsql.side.readFailed", "{0} 소스를 읽지 못했다", part.Target?.ObjectType ?? SideName(wanted));
                return false;
            }

            PublishErrors(publish: false);
            RefreshMembers();
        }

        ShowPart(part);
        return true;
    }

    /// <summary>다른 길(정의로 이동·객체 검색·본문 참조)로 같은 객체를 열 때: 그 대상의 쪽(본문이면 본문)을 보인다.</summary>
    public Task<bool> RevealSideAsync(DbObjectRef target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _secondary is null || PlsqlSourceText.SiblingType(target.ObjectType) is null
            ? Task.FromResult(true)
            : SwitchSideAsync(PlsqlSourceText.IsBodyType(target.ObjectType) ? PlsqlSide.Body : PlsqlSide.Spec, ct);
    }

    /// <summary>읽어 둔 쪽을 보인다(서버를 부르지 않는다). 차이 보기·경고는 닫는다(다른 쪽 이야기다).</summary>
    private void ShowPart(PlsqlSourcePart part)
    {
        SetCurrent(part);
        DiffText = string.Empty;
        WarningText = string.Empty;
        if (part.Target is { } t)
        {
            StatusText = part.Missing
                ? UiText.Format("Plsql.side.missing", "{0} {1}.{2} — 서버에 아직 없다", t.ObjectType, t.Owner, t.Name)
                : $"{t.ObjectType} {t.Owner}.{t.Name}";
        }

        SyncKeys();
    }

    private void SetCurrent(PlsqlSourcePart part)
    {
        if (ReferenceEquals(_current, part))
        {
            return;
        }

        _current = part;
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(Side));
        OnPropertyChanged(nameof(SideIndex));
        OnPropertyChanged(nameof(IsSpecSide));
        OnPropertyChanged(nameof(IsBodySide));
        OnPropertyChanged(nameof(ServerSourceChanged));
        OnPropertyChanged(nameof(HasConflictBanner));
        OnPropertyChanged(nameof(ServerSource));
        OnPropertyChanged(nameof(ShowCreateBodyHint));
        OnPropertyChanged(nameof(RunTarget));
    }

    /// <summary>쪽 구성(쌍 여부·읽음·없음)이 바뀌었다: 그에 딸린 속성을 알린다.</summary>
    private void RaiseSides()
    {
        OnPropertyChanged(nameof(HasSides));
        OnPropertyChanged(nameof(IsPackageDoc));
        OnPropertyChanged(nameof(SpecPart));
        OnPropertyChanged(nameof(BodyPart));
        OnPropertyChanged(nameof(SecondaryEditor));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(RunTarget));
        OnPropertyChanged(nameof(ShowCreateBodyHint));
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>오류가 난 쪽(<see cref="CompileError.ObjectType"/>). 모르면 null(보이는 쪽).</summary>
    private PlsqlSourcePart? PartOfError(CompileError error)
        => error.ObjectType is { } type ? Parts.FirstOrDefault(p => string.Equals(p.Target?.ObjectType, type, StringComparison.Ordinal)) : null;

    /// <summary>컴파일 오류 창의 「명세」·「본문」 표시. 쌍이 없는 탭이면 null.</summary>
    public string? SideLabel(CompileError item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _secondary is not null && item.ObjectType is { } type
            ? SideName(PlsqlSourceText.IsBodyType(type) ? PlsqlSide.Body : PlsqlSide.Spec)
            : null;
    }

    internal static string SideName(PlsqlSide side) => side == PlsqlSide.Spec
        ? UiText.Get("Plsql.side.spec", "명세")
        : UiText.Get("Plsql.side.body", "본문");

    /// <summary>
    /// 「본문 만들기」: 서버에 본문이 없는 패키지·타입의 빈 본문 쪽에 뼈대를 넣는다(명세의 프로시저·함수마다 빈 구현). 되돌리기로 지울 수 있다.
    /// 본문 쪽이 아니거나 이미 글이 있으면 아무것도 안 하고 false.
    /// </summary>
    public bool CreateBody()
    {
        if (BodyPart is not { } body || body.Target is not { } target || !string.IsNullOrWhiteSpace(body.Editor.Text))
        {
            return false;
        }

        if (!ReferenceEquals(body, _current))
        {
            ShowPart(body);
        }

        var skeleton = PlsqlSourceText.BodySkeleton(target.ObjectType, target.Owner, target.Name, SpecPart?.Loaded == true ? SpecPart.Editor.Text : null);
        body.Editor.Document.Replace(0, body.Editor.Document.TextLength, skeleton);
        body.Editor.Select(0, 0);
        OnPropertyChanged(nameof(ShowCreateBodyHint));
        RefreshMembers();
        return true;
    }

    /// <summary>
    /// 「명세+본문 컴파일」: 명세를 먼저, 성공(오류가 있어도 서버가 받았으면)하면 본문을 컴파일한다. 명세가 막히면(충돌·정책·실패) 본문은 보내지 않는다.
    /// 쪽마다 같은 확인 길(충돌·다른 세션·정책 관문)을 지난다. 글이 비었거나 읽지 않은 쪽은 건너뛴다. 쌍이 없는 탭은 <see cref="CompileAsync"/> 와 같다.
    /// </summary>
    public async Task<PlsqlCompileOutcome> CompileBothAsync(bool force = false, CancellationToken ct = default)
    {
        if (_secondary is null || SpecPart is not { } spec || BodyPart is not { } body)
        {
            return await CompileAsync(force, ct).ConfigureAwait(true);
        }

        var errors = new List<CompileError>();
        var messages = new List<string>();
        var worst = PlsqlCompileStatus.NothingToCompile;
        foreach (var part in new[] { spec, body })
        {
            if (!part.Loaded || string.IsNullOrWhiteSpace(part.Editor.Text))
            {
                continue;
            }

            var outcome = await CompilePartAsync(part, force, ct).ConfigureAwait(true);
            messages.Add($"{SideName(part.Side)}: {outcome.Message}");
            errors.AddRange(outcome.Errors);
            if (outcome.Status is not (PlsqlCompileStatus.Compiled or PlsqlCompileStatus.CompiledWithErrors))
            {
                if (!ReferenceEquals(part, _current))
                {
                    ShowPart(part); // 막힌 쪽(충돌 배너·경고)을 보인다
                }

                StatusText = string.Join(" · ", messages);
                return new PlsqlCompileOutcome(outcome.Status, errors, StatusText);
            }

            worst = worst == PlsqlCompileStatus.CompiledWithErrors || outcome.Status == PlsqlCompileStatus.CompiledWithErrors
                ? PlsqlCompileStatus.CompiledWithErrors
                : PlsqlCompileStatus.Compiled;
        }

        StatusText = messages.Count == 0 ? UiText.Get("Plsql.compileBoth.nothing", "컴파일할 글이 없다") : string.Join(" · ", messages);
        return new PlsqlCompileOutcome(worst, errors, StatusText);
    }

    /// <summary>
    /// 「구성원」 목록을 다시 만든다(명세·본문 글을 훑어 같은 구성원을 묶는다). 명세에 있는 차례 다음 본문에만 있는 차례.
    /// 패키지·타입이 아니면 비운다.
    /// </summary>
    public void RefreshMembers()
    {
        _membersVersion++;
        var entries = _secondary is null ? [] : BuildMembers(SpecPart, BodyPart);
        if (Members.SequenceEqual(entries))
        {
            return;
        }

        Members.Clear();
        foreach (var e in entries)
        {
            Members.Add(e);
        }
    }

    private static List<PlsqlMemberEntry> BuildMembers(PlsqlSourcePart? spec, PlsqlSourcePart? body)
    {
        var specMembers = spec is { Loaded: true } ? PlsqlMemberScanner.Scan(spec.Editor.Text) : [];
        var bodyMembers = body is { Loaded: true } ? PlsqlMemberScanner.Scan(body.Editor.Text) : [];
        var inBody = bodyMembers.ToDictionary(m => (m.Kind, m.Name, m.Ordinal), m => m.Offset);
        var entries = new List<PlsqlMemberEntry>();
        var seen = new HashSet<(PlsqlMemberKind, string, int)>();
        foreach (var m in specMembers)
        {
            var key = (m.Kind, m.Name, m.Ordinal);
            seen.Add(key);
            entries.Add(new PlsqlMemberEntry(m.Kind, m.Name, m.Offset, inBody.TryGetValue(key, out var at) ? at : null));
        }

        foreach (var m in bodyMembers.Where(m => !seen.Contains((m.Kind, m.Name, m.Ordinal))))
        {
            entries.Add(new PlsqlMemberEntry(m.Kind, m.Name, null, m.Offset));
        }

        return entries;
    }

    /// <summary>글이 바뀌면 잠깐 뒤 「구성원」 목록을 다시 만든다(화면 스레드가 있을 때만, 이어 치는 동안은 미룬다).</summary>
    private void ScheduleMembers()
    {
        if (_secondary is null)
        {
            return;
        }

        var version = ++_membersVersion;
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                DispatcherTimer.RunOnce(
                    () =>
                    {
                        if (version == _membersVersion)
                        {
                            RefreshMembers();
                        }
                    },
                    TimeSpan.FromMilliseconds(400));
            }
        }
        catch (InvalidOperationException)
        {
            // 화면 스레드가 없다(시험): 다음 읽기·바꾸기 때 다시 만든다.
        }
    }

    /// <summary>
    /// 「구성원」 줄을 눌렀다: 보이는 쪽에 그 구성원이 있으면 거기로, 다른 쪽에만 있으면 그 쪽으로 바꿔 간다. 옮겼으면 true.
    /// </summary>
    public bool GoToMember(PlsqlMemberEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // 줄 위치가 낡지 않게 목록을 다시 만든 뒤(친 뒤 아직 다시 만들지 않았을 수 있다) 같은 종류·이름의 같은 차례를 찾는다(겹쳐 쓰기).
        var nth = Members.Where(m => m.Kind == entry.Kind && m.Name == entry.Name).ToList().IndexOf(entry);
        RefreshMembers();
        var fresh = Members.Where(m => m.Kind == entry.Kind && m.Name == entry.Name).Skip(Math.Max(0, nth)).FirstOrDefault() ?? entry;
        int? here = _current.Side == PlsqlSide.Spec ? fresh.SpecOffset : fresh.BodyOffset;
        var part = _current;
        if (here is null)
        {
            var otherSide = _current.Side == PlsqlSide.Spec ? PlsqlSide.Body : PlsqlSide.Spec;
            here = otherSide == PlsqlSide.Spec ? fresh.SpecOffset : fresh.BodyOffset;
            if (here is null || Parts.FirstOrDefault(p => p.Side == otherSide) is not { Loaded: true } other)
            {
                return false;
            }

            ShowPart(other);
            part = other;
        }

        var offset = Math.Clamp(here.Value, 0, part.Editor.Document.TextLength);
        var line = part.Editor.Document.GetLineByOffset(offset).LineNumber;
        part.Editor.Select(offset, 0);
        part.Editor.ScrollLine = line;
        part.Editor.Surface?.ScrollToLine(line);
        part.Editor.Surface?.FocusEditor();
        return true;
    }

    /// <summary>
    /// 작업 공간 저장 형식으로 옮긴다(결정 71). 글은 늘 담는다(앱을 켤 때는 접속이 없어 서버에서 바로 다시 읽지 못한다).
    /// 패키지·타입은 명세 쪽을 겉에, 본문 쪽을 <see cref="PlsqlTabFile.Body"/> 에 담는다(명세를 아직 읽지 않았으면 본문을 겉에). 보이던 쪽은 <see cref="PlsqlTabFile.Side"/>.
    /// 서버에 없고 비어 있는 본문은 담지 않는다(다음에 접속하면 다시 확인한다).
    /// </summary>
    public PlsqlTabFile Capture()
    {
        if (_secondary is null)
        {
            return CapturePart(_primary);
        }

        var spec = SpecPart!;
        var body = BodyPart!;
        var outer = spec.Loaded || !body.Loaded ? spec : body;
        var file = CapturePart(outer);
        if (ReferenceEquals(outer, spec) && body.Loaded && !(body.Missing && string.IsNullOrWhiteSpace(body.Editor.Text)))
        {
            file = file with { Body = CapturePart(body) };
        }

        return _current.Side == PlsqlSide.Body && file.Body is not null ? file with { Side = PlsqlTabFile.BodySide } : file;
    }

    private PlsqlTabFile CapturePart(PlsqlSourcePart part) => new()
    {
        Owner = part.Target?.Owner,
        Name = part.Target?.Name,
        Type = part.Target?.ObjectType,
        ConnectionProfileId = _session?.Profile.Id ?? part.Editor.ConnectionProfileId,
        LastDdlTime = part.Baseline,
        Title = Title,
        Text = part.Editor.Text,
        IsDirty = NeedsSave(part),
        FilePath = part.Editor.FilePath,
        Encoding = part.Editor.FilePath is null ? null : part.Editor.Encoding,
        Caret = part.Editor.CaretOffset,
        Line1Shift = part.Line1Shift,
    };

    /// <summary>
    /// 저장한 탭을 되살린다(결정 71). 글은 저장한 그대로 두고(앱을 켤 때는 접속이 없다), 저장한 접속의 세션이 붙으면 <see cref="AttachSessionAsync"/> 가 서버와 견준다.
    /// 고친 채 저장한 글은 다시 고친 글로 연다(닫을 때 저장을 묻는다). 서버 탭의 깨끗한 기준 글은 저장하지 않으므로 되돌려도 고친 글로 남는다.
    /// 파일에 묶인 탭도 저장한 글로 연다(앱을 끈 사이 파일이 바뀌었는지는 보지 않는다). 이전 오류·차이·경고는 비운다.
    /// 패키지·타입은 두 쪽을 짓는다: 저장하지 않은 쪽(예전 형식의 한 쪽 탭)은 접속이 붙을 때 서버에서 읽는다.
    /// </summary>
    public void Restore(PlsqlTabFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Interlocked.Increment(ref _generation); // 읽던 소스가 뒤늦게 덮지 않게
        Session = null;
        _restoredProfileId = file.ConnectionProfileId;
        DbObjectRef? target = file is { Owner: { } owner, Name: { } name, Type: { } type } ? new DbObjectRef(owner, name, type) : null;
        DiffText = string.Empty;
        WarningText = string.Empty;
        IsBusy = false;
        PlsqlSourcePart main;
        if (target is not null && file.FilePath is null && PlsqlSourceText.SiblingType(target.ObjectType) is not null)
        {
            var (want, other) = Arrange(target);
            main = want;
            RestorePart(main, file);
            if (other is not null && file.Body is { } saved && saved.Type is { } bodyType
                && PlsqlSourceText.IsBodyType(bodyType) && !PlsqlSourceText.IsBodyType(target.ObjectType)
                && string.Equals(saved.Name, target.Name, StringComparison.Ordinal) && string.Equals(saved.Owner, target.Owner, StringComparison.Ordinal))
            {
                RestorePart(other, saved);
            }
            else if (other is not null)
            {
                other.Editor.LoadText(string.Empty);
                other.Editor.ConnectionProfileId = file.ConnectionProfileId;
            }

            SetCurrent(string.Equals(file.Side, PlsqlTabFile.BodySide, StringComparison.Ordinal) && BodyPart is { Loaded: true } shownBody ? shownBody : main);
        }
        else
        {
            UseSinglePart(target);
            main = _primary;
            RestorePart(main, file);
        }

        foreach (var part in Parts)
        {
            part.Errors = [];
            part.ServerSource = null;
            SetServerChanged(part, false);
        }

        Errors.Clear();
        Title = file.Title is { } title ? StripSideSuffix(title, target) : (target is { } t ? TitleOf(t) : "새 PL/SQL");
        StatusText = target is null ? string.Empty : "접속하면 서버 소스와 견준다";
        RefreshMembers();
        RaiseSides();
        SyncKeys();
    }

    private static void RestorePart(PlsqlSourcePart part, PlsqlTabFile file)
    {
        part.Baseline = file.LastDdlTime;
        part.Line1Shift = file.Line1Shift;
        part.Editor.LoadText(file.Text, dirty: file.IsDirty);
        part.Editor.FilePath = file.FilePath;
        part.Editor.Encoding = file.FilePath is not null && file.Encoding is { Length: > 0 } enc ? enc : "utf-8";
        part.Editor.ConnectionProfileId = file.ConnectionProfileId;
        part.Editor.CaretOffset = Math.Clamp(file.Caret, 0, part.Editor.Document.TextLength);
        part.Missing = false;
        part.Loaded = true;
    }

    /// <summary>예전 탭 제목의 「 (본문)」 꼬리를 뗀다(명세와 본문이 한 탭이 됐다).</summary>
    private static string StripSideSuffix(string title, DbObjectRef? target)
        => target is not null && PlsqlSourceText.SiblingType(target.ObjectType) is not null && title.EndsWith(" (본문)", StringComparison.Ordinal)
            ? title[..^" (본문)".Length]
            : title;
}
