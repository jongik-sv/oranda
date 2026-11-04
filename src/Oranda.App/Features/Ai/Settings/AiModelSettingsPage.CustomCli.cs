using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>
/// 설정 「AI 모델」 의 기타 CLI 등록: 이름과 실행 명령을 등록·수정·삭제하고, 드롭다운이나 줄의 단추로 고른다.
/// 편집 중인 값은 <see cref="CustomClis"/> 한 개 컬렉션이 들고(입력 중 화면이 다시 그려지지 않게 같은 객체를 유지한다),
/// 활성 대상(<see cref="AiModelSettingsPage.ActiveKind"/>)과 저장(<see cref="AiModelSettingsPage.ToSettings(AiSettings)"/>)은 이 편집 값을 본다.
/// 지운 줄이 선택되어 있으면 선택 값(<c>custom:&lt;id&gt;</c>)을 그대로 두어 「쓸 수 없음」 안내를 보인다(말없이 다른 대상으로 바꾸지 않는다).
/// </summary>
public sealed partial class AiModelSettingsPage
{
    private readonly Func<string?, AiCustomCliCheck> _analyze;
    private readonly TimeSpan _installCheckDelay;

    /// <summary>등록한 기타 CLI(편집 중인 값). 컬렉션 객체는 바뀌지 않는다.</summary>
    public ObservableCollection<AiCustomCliRow> CustomClis { get; } = [];

    public bool HasCustomClis => CustomClis.Count > 0;

    /// <summary>등록 목록이 비었을 때 보이는 안내를 가리는 쪽의 반대.</summary>
    public bool NoCustomClis => !HasCustomClis;

    /// <summary>기타 CLI 를 고른 상태: 이 CLI 가 하는 일과 보내지 않는 경우를 알린다.</summary>
    public bool ShowCustomCliInfo => ShowSelection && ActiveKind == AiActiveKind.CustomCli;

    /// <summary>기타 CLI 를 골랐는데 전역 값 가림이 켜져 있다: 라우터가 이 조합을 막으므로 실행하면 오류가 난다.</summary>
    public bool ShowCustomCliMaskBlock => ShowCustomCliInfo && MaskLiterals;

    public string CustomCliInfoText => _t.T(
        "이 PC 에서 등록한 명령을 실행해 글만 주고받습니다. DB 도구와 실행 토큰은 쓰지 않으며, 운영 표시 접속과 값 가림을 켠 접속에는 보내지 않습니다.",
        "Runs the command you registered on this PC and only exchanges text. No database tools or run tokens are used, and nothing is sent on connections marked production or with value masking on.");

    public string CustomCliMaskBlockText => _t.T(
        "값 가림 설정이 켜져 있어 기타 CLI 로 보내지 않습니다. 아래 「SQL 의 리터럴 값을 :vN 으로 가려서 보냄」 확인란을 끄거나 다른 모델을 고르세요.",
        "Value masking is on, so nothing is sent to a custom CLI. Turn off the \"mask SQL literal values\" checkbox below or pick another model.");

    /// <summary>항상 보이는 경고: 빈 폴더에서 실행하지만 샌드박스는 아니다.</summary>
    public string CustomCliSandboxText => _t.T(
        "명령은 빈 임시 폴더에서 실행되지만 샌드박스가 아닙니다. 현재 사용자 권한으로 실행되므로 CLI 가 다른 폴더의 파일을 읽거나 고칠 수 있습니다.",
        "The command runs in an empty temporary folder, which is not a sandbox. It runs with your user permissions, so the CLI can still read or change files in other folders.");

    /// <summary>항상 보이는 경고: 명령 문자열은 평문으로 저장된다.</summary>
    public string CustomCliPlainText => _t.T(
        "실행 명령은 설정 파일에 평문으로 저장됩니다. API 키 같은 비밀 값은 명령에 쓰지 말고 CLI 쪽 로그인을 쓰세요.",
        "The command is stored in plain text in the settings file. Do not put secrets such as API keys in it; use the CLI's own sign-in.");

    /// <summary>항상 보이는 안내: 가림 대상 접속에는 보내지 않는다.</summary>
    public string CustomCliMaskText => _t.T(
        "운영 표시 접속과 값 가림을 켠 접속에는 기타 CLI 로 보내지 않습니다.",
        "Nothing is sent to a custom CLI on connections marked production or with value masking on.");

    /// <summary>명령 틀 목록(고르면 이름과 명령을 채운 새 줄을 더한다). 확인됨이 위에 온다.</summary>
    public IReadOnlyList<AiCustomCliExampleChoice> Examples => _examples ??=
        [.. AiCustomCliExamples.All.Select(e => new AiCustomCliExampleChoice(e,
            $"{e.Name}  ·  {(e.Verification == AiCustomCliVerification.Confirmed ? _t.T("확인됨", "Verified") : _t.T("미확인(문서 기준)", "Unverified (per docs)"))}"))];

    private IReadOnlyList<AiCustomCliExampleChoice>? _examples;

    /// <summary>명령 틀을 골라 채운 새 줄을 더한다. 채운 뒤 이름·명령을 마음대로 고칠 수 있다.</summary>
    public AiCustomCliRow AddFromExample(AiCustomCliExample example)
    {
        ArgumentNullException.ThrowIfNull(example);
        var row = NewRow(example.Name, example.Command);
        AttachRow(row);
        Recheck(row, TimeSpan.Zero);
        return row;
    }

    partial void OnMaskLiteralsChanged(bool value) => OnPropertyChanged(nameof(ShowCustomCliMaskBlock));

    partial void OnDriverChanged(AiDriverKind value)
    {
        // 「선택 안 함」 으로 끄면 기타 CLI 고지·가림 경고도 함께 사라져야 한다(두 값은 Driver 에 달려 있다).
        OnPropertyChanged(nameof(ShowCustomCliInfo));
        OnPropertyChanged(nameof(ShowCustomCliMaskBlock));
        RefreshCustomCliSelection();
    }

    /// <summary>줄을 지울지 묻는 글(이름을 넣는다). 화면이 확인 창에 보인다.</summary>
    public string DeleteCustomCliConfirmText(AiCustomCliRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var name = row.DisplayName;
        return _t.T($"기타 CLI 「{name}」 을 지울까요? 지금 쓰는 모델이면 다른 모델을 고를 때까지 AI 를 실행할 수 없습니다.",
            $"Delete the custom CLI \"{name}\"? If it is the selected model, AI runs fail until you pick another one.");
    }

    private void LoadCustomClis(IReadOnlyList<AiCustomCli> source)
    {
        foreach (var cli in source)
        {
            AttachRow(new AiCustomCliRow(cli));
        }

        CustomClis.CollectionChanged += OnCustomClisChanged;
        foreach (var row in CustomClis)
        {
            Recheck(row, TimeSpan.Zero);
        }

        RefreshCustomCliSelection();
    }

    private void AttachRow(AiCustomCliRow row)
    {
        row.PropertyChanged += OnRowChanged;
        CustomClis.Add(row);
    }

    private void OnCustomClisChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasCustomClis));
        OnPropertyChanged(nameof(NoCustomClis));
        NotifyCustomCliChanged();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AiCustomCliRow row)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(AiCustomCliRow.Command):
                Recheck(row, _installCheckDelay);
                NotifyCustomCliChanged();
                break;
            case nameof(AiCustomCliRow.Name):
                NotifyCustomCliChanged();
                break;
        }
    }

    // 편집 값이 바뀌어 달라질 수 있는 파생 값만 알린다(전체 알림은 입력 중 다른 칸까지 다시 그린다).
    private void NotifyCustomCliChanged()
    {
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(ActiveKind));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(ShowProblem));
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(ShowCustomCliInfo));
        OnPropertyChanged(nameof(ShowCustomCliMaskBlock));
        RefreshCustomCliSelection();
    }

    /// <summary>줄의 「지금 쓰는 대상」 표시를 설정이 가리키는 값에 맞춘다.</summary>
    private void RefreshCustomCliSelection()
    {
        var selectedId = Driver == AiDriverKind.ClaudeCode && ActiveProfileId is null && AiCustomCliTarget.TryGetId(SystemCliId, out var id) ? id : null;
        foreach (var row in CustomClis)
        {
            row.IsSelected = selectedId is not null && string.Equals(row.Id, selectedId, StringComparison.Ordinal);
        }
    }

    /// <summary>편집 중인 목록을 설정 값으로 바꾼다. 방금 추가해 아직 아무것도 쓰지 않은 줄은 저장하지 않는다.</summary>
    private IReadOnlyList<AiCustomCli> CurrentCustomClis() =>
        [.. CustomClis.Where(r => !(r.Draft && string.IsNullOrWhiteSpace(r.Name) && string.IsNullOrWhiteSpace(r.Command))).Select(r => r.ToModel())];

    /// <summary>선택 값(<c>custom:&lt;id&gt;</c>)이 가리키는 줄. 선택 값이 기타 CLI 가 아니거나 목록에 없으면 null.</summary>
    private AiCustomCliRow? FindCustomCliRow(string? selection) =>
        AiCustomCliTarget.TryGetId(selection, out var id) && id.Length > 0
            ? CustomClis.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))
            : null;

    private static string CustomCliTitle(AiCustomCli cli) => string.IsNullOrWhiteSpace(cli.Name) ? cli.Id : cli.Name;

    // 드롭다운의 「기타 CLI」 묶음: 등록한 줄이 있거나 기타 CLI 를 가리키는 선택이 있을 때만 만든다.
    private void AddCustomCliChoices(List<AiProfileChoice> rows, AiActiveProfile active)
    {
        var pointsAtCustom = active.Kind is AiActiveKind.CustomCli or AiActiveKind.CustomCliUnavailable;
        if (CustomClis.Count == 0 && !pointsAtCustom)
        {
            return;
        }

        rows.Add(new AiProfileChoice(AiChoiceKind.Header, _t.T("기타 CLI", "Custom CLI")));
        var selectedId = pointsAtCustom && AiCustomCliTarget.TryGetId(active.SystemCliId, out var id) ? id : null;
        foreach (var row in CustomClis)
        {
            var runnable = AiCustomCliTarget.IsRunnable(row.ToModel());
            rows.Add(new AiProfileChoice(AiChoiceKind.CustomCli, row.DisplayName, CliId: row.Id,
                IsCurrent: selectedId is not null && string.Equals(selectedId, row.Id, StringComparison.Ordinal),
                Reason: runnable ? null : _t.T("명령 없음", "No command")));
        }

        if (pointsAtCustom && FindCustomCliRow(active.SystemCliId) is null)
        {
            rows.Add(new AiProfileChoice(AiChoiceKind.MissingCustomCli, _t.T("사용할 수 없는 기타 CLI", "Custom CLI unavailable"), IsCurrent: true,
                Reason: _t.T("찾을 수 없음", "Not found"), CliId: selectedId));
        }
    }

    private void UseCustomCli(string id)
    {
        Notice = string.Empty;
        Driver = AiDriverKind.ClaudeCode;
        ActiveProfileId = null;
        SystemCliId = AiCustomCliTarget.SelectionOf(id);
    }

    /// <summary>줄의 「사용」 단추: 이 기타 CLI 를 고른다. 명령이 비어 있으면 고를 수 없다.</summary>
    [RelayCommand]
    private void UseCustomCliRow(AiCustomCliRow? row)
    {
        if (row is not null && AiCustomCliTarget.IsRunnable(row.ToModel()))
        {
            UseCustomCli(row.Id);
        }
    }

    /// <summary>새 줄을 더한다(이름·명령이 빈 초안). 아무것도 쓰지 않으면 저장하지 않는다.</summary>
    [RelayCommand]
    private void AddCustomCli()
    {
        var row = NewRow("", "");
        AttachRow(row);
        Recheck(row, TimeSpan.Zero);
    }

    // 새 줄: 겹치지 않는 id 를 만든다. 틀에서 채운 줄은 저장 대상이고, 아무것도 안 채운 줄(Draft)은 비어 있으면 저장하지 않는다.
    private AiCustomCliRow NewRow(string name, string command)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N")[..8];
        }
        while (CustomClis.Any(r => string.Equals(r.Id, id, StringComparison.Ordinal)));

        return new AiCustomCliRow(new AiCustomCli { Id = id, Name = name, Command = command }) { Draft = true };
    }

    /// <summary>줄을 지운다(확인은 화면이 먼저 받는다). 지운 줄이 선택되어 있으면 선택 값은 그대로 두어 「쓸 수 없음」 안내가 보인다.</summary>
    public void RemoveCustomCli(AiCustomCliRow? row)
    {
        if (row is null || !CustomClis.Contains(row))
        {
            return;
        }

        row.Removed = true;
        row.PropertyChanged -= OnRowChanged;
        CustomClis.Remove(row);
    }

    // 명령을 분해해 경고를 갱신하고, 실행 파일 이름이 나오면 UI 스레드 밖에서 PATH 를 찾는다. 오래된 결과는 버린다.
    private void Recheck(AiCustomCliRow row, TimeSpan delay)
    {
        var version = ++row.CheckVersion;
        var check = _analyze(row.Command);
        row.Check = check;
        row.Warnings = BuildWarnings(check);
        row.TemplateNote = TemplateNoteFor(check);
        var probe = Services?.CustomCliInstalled;
        if (probe is null || check.Executable is null)
        {
            row.InstallState = AiCustomCliInstallState.Unknown;
            row.PendingCheck = Task.CompletedTask;
            return;
        }

        row.InstallState = AiCustomCliInstallState.Checking;
        row.PendingCheck = FindExecutableAsync(row, check.Executable, probe, version, delay);
    }

    private static async Task FindExecutableAsync(AiCustomCliRow row, string executable, Func<string, bool> probe, int version, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay).ConfigureAwait(true);
        }

        if (row.Removed || row.CheckVersion != version)
        {
            return;
        }

        AiCustomCliInstallState result;
        try
        {
            result = await Task.Run(() => probe(executable)).ConfigureAwait(true) ? AiCustomCliInstallState.Found : AiCustomCliInstallState.NotFound;
        }
        catch (Exception)
        {
            result = AiCustomCliInstallState.Unknown; // 찾는 중 오류는 「확인 못 함」이다. 없다고 단정하지 않는다.
        }

        if (!row.Removed && row.CheckVersion == version)
        {
            row.InstallState = result;
        }
    }

    // 명령 틀 표가 같은 실행 파일에 붙인 안내. 다시 열어도 같은 줄에 보이게 저장한 값이 아니라 명령에서 다시 정한다.
    private string? TemplateNoteFor(AiCustomCliCheck check)
    {
        if (check.Executable is null)
        {
            return null;
        }

        foreach (var example in AiCustomCliExamples.All)
        {
            if (example.NoteKo is null || AiCustomCliCommand.Parse(example.Command).Command is not { } template
                || !string.Equals(template.Executable, check.Executable, StringComparison.Ordinal))
            {
                continue;
            }

            if (example.NoteUnlessOption is { } option && check.Arguments.Any(a => string.Equals(a, option, StringComparison.Ordinal)))
            {
                return null;
            }

            return _t.IsEnglish ? example.NoteEn : example.NoteKo;
        }

        return null;
    }

    private List<string> BuildWarnings(AiCustomCliCheck check)
    {
        var lines = new List<string>();
        if (check.ParseError is { } error)
        {
            // MessageOf 는 고정된 한국어 글이고 영어는 공용 번역 표에 있다(값을 끼워 넣지 않으므로 그대로 찾는다).
            lines.Add(_t.IsEnglish && Localization.LiteralTranslator.Table.TryGetValue(error, out var english) ? english : error);
        }

        if (check.AutoApproveOptions.Count > 0)
        {
            var options = string.Join(", ", check.AutoApproveOptions);
            lines.Add(_t.T($"자동 승인 옵션이 들어 있습니다({options}). 이 CLI 는 확인 없이 파일을 고치거나 명령을 실행할 수 있습니다.",
                $"The command contains auto-approve options ({options}). The CLI may change files or run commands without asking."));
        }

        if (check.SecretEnvironmentNames.Count > 0)
        {
            var names = string.Join(", ", check.SecretEnvironmentNames);
            lines.Add(_t.T($"환경 변수 {names} 의 값이 설정 파일에 평문으로 남습니다. 비밀 값은 명령에 쓰지 마세요.",
                $"The values of the environment variables {names} are kept in plain text in the settings file. Do not put secrets in the command."));
        }

        if (check.RiskyEnvironmentNames.Count > 0)
        {
            var names = string.Join(", ", check.RiskyEnvironmentNames);
            lines.Add(_t.T($"환경 변수 {names} 는 프로그램을 불러오는 방식이나 실행 환경을 바꿉니다. 꼭 필요한 경우에만 쓰세요.",
                $"The environment variables {names} change how the program is loaded or run. Use them only when needed."));
        }

        if (check.PromptInArguments)
        {
            lines.Add(_t.T("{prompt} 를 쓰면 글(SQL 포함)이 명령줄 인자로 실려 이 PC 의 다른 프로세스에도 보입니다.",
                "With {prompt}, the text (including SQL) is passed as a command-line argument and is visible to other processes on this PC."));
        }

        return lines;
    }
}

/// <summary>
/// 기타 CLI 명령 문자열을 경고 재료로 분해하는 한 곳. 분해 규칙은 Core 의 <c>AiCustomCliCommand</c> 가 정하므로 이 쪽에는 복제하지 않는다.
/// </summary>
internal static class AiCustomCliAnalyzer
{
    public static AiCustomCliCheck Analyze(string? command)
    {
        // 빈 명령은 오류가 아니라 아직 안 쓴 칸이다(새 줄에 「명령이 비었다」 경고가 뜨지 않게 한다). 실행을 막는 일은 라우터의 몫이다.
        if (string.IsNullOrWhiteSpace(command))
        {
            return AiCustomCliCheck.None;
        }

        var parsed = AiCustomCliCommand.Parse(command);
        return parsed.Command is { } c
            ? new AiCustomCliCheck(c.Executable, c.Arguments, null, c.AutoApproveOptions, c.SecretEnvironmentNames, c.UsesPromptArgument, c.RiskyEnvironmentNames)
            : new AiCustomCliCheck(null, [], AiCustomCliCommand.MessageOf(parsed.Error!.Value), [], [], false);
    }
}
