using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Platform.Ai.Skills;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>스킬 설치 창의 대상 한 줄(폴더 하나).</summary>
public sealed partial class AgentSkillRow(AgentSkillTarget target, string clients, string path) : ObservableObject
{
    public AgentSkillTarget Target { get; } = target;

    public string Clients { get; } = clients;

    public string FilePath { get; } = path;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private AgentSkillState _state;

    /// <summary>지난 설치가 남의 파일 때문에 건너뛰었다(「덮어쓰기」 단추를 보인다).</summary>
    [ObservableProperty]
    private bool _skippedForeign;

    public string StateText => State switch
    {
        AgentSkillState.NotInstalled => ExternalAgentText.T("설치 안 됨", "Not installed"),
        AgentSkillState.Current => ExternalAgentText.T("설치됨", "Installed"),
        AgentSkillState.Outdated => ExternalAgentText.T("옛 경로 — 다시 설치 필요", "Old path — reinstall"),
        AgentSkillState.Foreign => ExternalAgentText.T("다른 oranda 스킬이 있음(덮지 않음)", "Another oranda skill is there (kept)"),
        _ => State.ToString(),
    };

    partial void OnStateChanged(AgentSkillState value) => OnPropertyChanged(nameof(StateText));
}

/// <summary>
/// 「스킬 설치…」 창(설계서 §7.1): 대상 폴더 셋을 체크 칸으로 보이고 「설치」·「지우기」를 한다. 기본 체크 = 그 CLI 의 설정 폴더가 있거나 이미 Oranda 스킬이 있는 곳.
/// 표식 없는 남의 파일은 덮지 않고, 그 줄에 「덮어쓰기」를 보여 사람이 한 번 더 누를 때만 덮는다.
/// </summary>
public sealed partial class AgentSkillInstallViewModel : ObservableObject
{
    private readonly IAgentSkillInstaller _installer;

    [ObservableProperty]
    private string _message = string.Empty;

    public AgentSkillInstallViewModel(IAgentSkillInstaller installer)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        foreach (var status in installer.Inspect())
        {
            Rows.Add(new AgentSkillRow(status.Target, AgentSkillTargets.ClientNames(status.Target), status.FilePath)
            {
                State = status.State,
                IsChecked = status.State is AgentSkillState.Current or AgentSkillState.Outdated || installer.IsClientPresent(status.Target),
            });
        }
    }

    public ObservableCollection<AgentSkillRow> Rows { get; } = [];

    /// <summary>스킬 본문이 부를 orandactl 경로.</summary>
    public string OrandactlPath => _installer.OrandactlPath;

    public Task InstallAsync() => RunAsync(Rows.Where(r => r.IsChecked).ToList(), rows => _installer.Install(rows.Select(r => r.Target)));

    public Task UninstallAsync() => RunAsync(Rows.Where(r => r.IsChecked).ToList(), rows => _installer.Uninstall(rows.Select(r => r.Target)));

    /// <summary>「덮어쓰기」: 남의 파일이 있던 한 줄만 덮어쓴다.</summary>
    public Task OverwriteAsync(AgentSkillRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return RunAsync([row], rows => _installer.Install(rows.Select(r => r.Target), overwriteForeign: true));
    }

    private async Task RunAsync(List<AgentSkillRow> rows, Func<List<AgentSkillRow>, IReadOnlyList<AgentSkillInstallResult>> work)
    {
        if (rows.Count == 0)
        {
            Message = ExternalAgentText.T("고른 곳이 없습니다.", "Nothing is selected.");
            return;
        }

        var results = await Task.Run(() => work(rows)).ConfigureAwait(true);
        var states = await Task.Run(_installer.Inspect).ConfigureAwait(true);
        foreach (var row in Rows)
        {
            row.State = states.First(s => s.Target == row.Target).State;
            row.SkippedForeign = results.Any(r => r.Target == row.Target && r.Outcome == AgentSkillInstallOutcome.SkippedForeign && row.State == AgentSkillState.Foreign);
        }

        Message = string.Join("\n", results.Select(Describe));
    }

    private string Describe(AgentSkillInstallResult result)
    {
        var row = Rows.First(r => r.Target == result.Target);
        var what = result.Outcome switch
        {
            AgentSkillInstallOutcome.Installed => ExternalAgentText.T("설치함", "installed"),
            AgentSkillInstallOutcome.Updated => ExternalAgentText.T("새 경로로 다시 씀", "rewritten with the new path"),
            AgentSkillInstallOutcome.Removed => ExternalAgentText.T("지움", "removed"),
            AgentSkillInstallOutcome.NotInstalled => ExternalAgentText.T("설치돼 있지 않음", "not installed"),
            AgentSkillInstallOutcome.SkippedForeign => ExternalAgentText.T("Oranda 가 쓴 파일이 아니라 건너뜀", "skipped: not written by Oranda"),
            AgentSkillInstallOutcome.Failed => ExternalAgentText.T("실패: ", "failed: ") + result.Message,
            _ => result.Outcome.ToString(),
        };
        return row.Clients + " — " + what;
    }
}
