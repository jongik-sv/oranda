using System.Text.Json;
using System.Text.Json.Serialization;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;
using Oranda.Core.MyBatis;
using Oranda.Core.Storage;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

/// <summary>
/// MyBatis 작업 공간 복구(design.md 5.6, MB-03): 열린 매퍼·기준 원문·요소마다의 고침(원문·id 바꾸기·새 문장·지움)·문장 탭 ↔ 문장·고르던 문장을
/// 작업 공간 저장본 곁 파일 <c>{id}.mybatis-statements.json</c> 에 둔다. 문장 탭의 글 자체는 호스트가 작업 공간 저장본(탭 Content)에 남긴다.
/// 되살릴 때 디스크 매퍼가 기준과 같으면 디스크 문서를 기준으로 쓰고, 다르면 저장해 둔 기준 원문으로 세션을 세운다
/// (그 뒤 밖에서 바뀜 병합은 03-03 감시가 맡는다, design.md 5.3).
/// </summary>
[OrandaService(typeof(IMyBatisRecoveryParticipant))]
public sealed class RecoveryStatementsParticipant : IMyBatisRecoveryParticipant
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _savedDir;
    private readonly string _autosaveDir;

    public RecoveryStatementsParticipant(IAppDataPaths paths)
        : this(paths?.WorkspacesDirectory ?? throw new ArgumentNullException(nameof(paths)), paths.AutosaveDirectory)
    {
    }

    /// <summary>시험용: 곁 파일 폴더를 직접 준다.</summary>
    public RecoveryStatementsParticipant(string savedDirectory, string autosaveDirectory)
    {
        _savedDir = savedDirectory;
        _autosaveDir = autosaveDirectory;
    }

    public string Key => "statements";

    public string? Fingerprint(SqlWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.MyBatis is not MyBatisWorkspaceSession session)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder().Append(session.BaselineHash).Append('|').Append(session.SelectedStatementId);
        foreach (var e in session.Entries)
        {
            sb.Append('|').Append(e.Id).Append(e.IsDeleted ? "-" : string.Empty).Append(e.IsNew ? "+" : string.Empty)
                .Append(e.Tab?.TabId.ToString("N")[..8]);
            if (e.IsModified && !e.IsDeleted)
            {
                sb.Append(':').Append(e.Text.Length).Append(':').Append(string.GetHashCode(e.Text, StringComparison.Ordinal));
            }
        }

        return sb.ToString();
    }

    public async Task SaveAsync(SqlWorkspaceViewModel workspace, MyBatisRecoveryTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.MyBatis is not MyBatisWorkspaceSession session)
        {
            return;
        }

        var state = Capture(session);
        var path = PathOf(workspace.Id, target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".oranda-tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, Json), ct).ConfigureAwait(true);
        File.Move(temp, path, overwrite: true);
    }

    public void Restore(SqlWorkspaceViewModel workspace, MyBatisRecoveryTarget target)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var path = PathOf(workspace.Id, target);
        var existing = workspace.MyBatis as MyBatisWorkspaceSession;
        if (!File.Exists(path))
        {
            // 세션이 붙은 작업 공간을 같은 ID 로 다시 복원했다(호스트가 탭만 바꿔 끼움): 새 탭을 요소에 다시 잇는다.
            existing?.AdoptOrphanTabs();
            return;
        }

        if (workspace.MyBatis is not null && existing is null)
        {
            return; // 다른 구현의 세션은 건드리지 않는다
        }

        StatementsState? state;
        try
        {
            state = JsonSerializer.Deserialize<StatementsState>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            existing?.AdoptOrphanTabs();
            return; // 곁 파일이 깨졌으면 세션 없이 둔다(문장 탭 글은 작업 공간 저장본에 남아 있다)
        }

        if (state is not { Version: 1 })
        {
            existing?.AdoptOrphanTabs();
            return;
        }

        if (!Apply(workspace, state))
        {
            existing?.AdoptOrphanTabs();
        }
    }

    public Task DeleteAsync(string workspaceId, MyBatisRecoveryTarget target, CancellationToken ct)
    {
        var path = PathOf(workspaceId, target);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task RenameAsync(string oldWorkspaceId, string newWorkspaceId, CancellationToken ct)
    {
        var from = PathOf(oldWorkspaceId, MyBatisRecoveryTarget.Saved);
        if (File.Exists(from))
        {
            File.Move(from, PathOf(newWorkspaceId, MyBatisRecoveryTarget.Saved), overwrite: true);
        }

        return Task.CompletedTask;
    }

    internal string PathOf(string workspaceId, MyBatisRecoveryTarget target) =>
        Path.Combine(target == MyBatisRecoveryTarget.Autosave ? _autosaveDir : _savedDir, $"{workspaceId}.mybatis-{Key}.json");

    private static StatementsState Capture(MyBatisWorkspaceSession session)
    {
        var elements = session.Document.Elements;
        var entries = new List<EntryState>(session.Entries.Count);
        foreach (var e in session.Entries)
        {
            entries.Add(new EntryState
            {
                BaselineIndex = e.Baseline is { } b ? IndexOf(elements, b) : null,
                Id = e.Id,
                Kind = e.Kind.ToString(),
                Text = e.IsModified && !e.IsDeleted ? e.Text : null,
                IsDeleted = e.IsDeleted ? true : null,
                TabId = e.Tab?.TabId,
            });
        }

        return new StatementsState
        {
            Version = 1,
            MapperPath = session.MapperPath,
            BaselineHash = session.BaselineHash,
            BaselineText = session.Document.Text,
            HasBom = session.Document.Format.HasBom ? true : null,
            SelectedIndex = session.SelectedEntry is { } picked ? session.Entries.ToList().IndexOf(picked) : null,
            Entries = entries,
        };
    }

    private static int IndexOf(IReadOnlyList<MyBatisElement> elements, MyBatisElement element)
    {
        for (int i = 0; i < elements.Count; i++)
        {
            if (ReferenceEquals(elements[i], element))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>곁 파일로 세션을 세워 붙인다. 기준 문서를 세우지 못하면 false(붙어 있던 세션은 그대로).</summary>
    private static bool Apply(SqlWorkspaceViewModel workspace, StatementsState state)
    {
        if ((state.MapperPath ?? workspace.SourcePath) is not { } mapperPath)
        {
            return false;
        }

        IMyBatisMapperDocument? baseline = null;
        try
        {
            // 디스크가 기준과 같으면 디스크 문서(원래 인코딩·BOM 그대로). 다르거나 읽지 못하면(쓰는 중·깨짐) 저장해 둔 기준 원문으로 세운다.
            var disk = new MapperParser().Parse(File.ReadAllBytes(mapperPath));
            baseline = string.Equals(disk.Hash, state.BaselineHash, StringComparison.Ordinal) ? disk : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
        }

        try
        {
            baseline ??= state.BaselineText is { } text
                ? MyBatisWorkspaceSession.ParseLike(text, MapperFileFormat.Default with { HasBom = state.HasBom == true })
                : null;
        }
        catch (FormatException)
        {
            baseline = null;
        }

        if (baseline is null)
        {
            return false;
        }

        var restored = new List<MyBatisStatementEntry>();
        var tabs = new List<(MyBatisStatementEntry Entry, Guid TabId)>();
        foreach (var s in state.Entries ?? [])
        {
            if (s is null)
            {
                continue;
            }

            var kind = Enum.TryParse<MyBatisStatementKind>(s.Kind, out var k) ? k : MyBatisStatementKind.Other;
            MyBatisElement? el = s.BaselineIndex is int i && i >= 0 && i < baseline.Elements.Count ? baseline.Elements[i] : null;
            if ((s.BaselineIndex is not null && el is null) || (el is null && !MyBatisStatementText.IsValidId(s.Id)))
            {
                continue; // 기준과 맞지 않는 요소·id 없는 새 문장은 버린다
            }

            var entry = MyBatisWorkspaceSession.NewEntry(baseline, el, el is null ? kind : MyBatisStatementEntry.KindOf(el.Kind), s.Id ?? el?.Id, s.Text ?? el?.Text ?? string.Empty);
            entry.IsDeleted = s.IsDeleted == true && entry.IsEditable;
            restored.Add(entry);
            if (s.TabId is { } tabId)
            {
                tabs.Add((entry, tabId));
            }
        }

        // 기준 요소 중 곁 파일에 없는 것(곁 파일보다 기준이 길다)도 목록에 남긴다.
        for (int i = 0; i < baseline.Elements.Count; i++)
        {
            var el = baseline.Elements[i];
            if (!restored.Exists(e => ReferenceEquals(e.Baseline, el)))
            {
                restored.Insert(Math.Min(i, restored.Count), MyBatisWorkspaceSession.NewEntry(baseline, el, MyBatisStatementEntry.KindOf(el.Kind), el.Id, el.Text));
            }
        }

        workspace.MyBatis = null; // 같은 ID 다시 복원이면 옛 세션을 떼고(탭 구독을 푼다) 곁 파일로 새로 세운다
        var session = new MyBatisWorkspaceSession(workspace, mapperPath, baseline);
        session.RestoreEntries(restored, state.SelectedIndex);
        foreach (var (entry, tabId) in tabs)
        {
            if (workspace.Tabs.FirstOrDefault(t => t.TabId == tabId) is { } tab)
            {
                session.AdoptTab(entry, tab);
            }
        }

        session.AdoptOrphanTabs();
        workspace.MyBatis = session;
        return true;
    }

    /// <summary>곁 파일 모양(판 1).</summary>
    internal sealed class StatementsState
    {
        public int Version { get; set; }

        public string? MapperPath { get; set; }

        public string? BaselineHash { get; set; }

        public string? BaselineText { get; set; }

        public bool? HasBom { get; set; }

        /// <summary>고르던 요소의 순번(<see cref="Entries"/> 안). 같은 id 요소가 여럿이어도 가리킬 수 있게 순번으로 둔다.</summary>
        public int? SelectedIndex { get; set; }

        public List<EntryState>? Entries { get; set; }
    }

    /// <summary>요소 하나. 기준 요소는 기준 문서 안 순번(id 가 없거나 겹쳐도 가리킬 수 있게), 새 문장은 null.</summary>
    internal sealed class EntryState
    {
        public int? BaselineIndex { get; set; }

        public string? Id { get; set; }

        public string? Kind { get; set; }

        /// <summary>고친 원문(기준과 같으면 null).</summary>
        public string? Text { get; set; }

        public bool? IsDeleted { get; set; }

        public Guid? TabId { get; set; }
    }
}
