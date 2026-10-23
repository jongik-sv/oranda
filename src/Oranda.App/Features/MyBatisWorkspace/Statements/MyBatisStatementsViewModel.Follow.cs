using System.ComponentModel;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

// MyBatisStatementsViewModel 의 일부: 활성 작업 공간 · 세션 따라가기(MyBatisStatementsViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class MyBatisStatementsViewModel
{
    // ---- 활성 작업 공간 · 세션 따라가기 ------------------------------------------------------

    private void OnActiveWorkspaceChanged(object? sender, EventArgs e) => FollowWorkspace();

    /// <summary>
    /// 활성 작업 공간의 세션을 따라간다. 열기 훅은 작업 공간을 활성으로 만든 <b>뒤에</b> 세션을 붙이므로
    /// 작업 공간의 <c>MyBatis</c> 속성 변경도 함께 받아야 한다.
    /// </summary>
    private void FollowWorkspace()
    {
        var ws = Host.ActiveWorkspace;
        if (!ReferenceEquals(_workspace, ws))
        {
            if (_workspace is not null)
            {
                _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            }

            _workspace = ws;
            if (ws is not null)
            {
                ws.PropertyChanged += OnWorkspacePropertyChanged;
            }
        }

        SetSession(ws?.MyBatis as MyBatisWorkspaceSession);
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.MyBatis))
        {
            SetSession(_workspace?.MyBatis as MyBatisWorkspaceSession);
        }
    }

    private void SetSession(MyBatisWorkspaceSession? session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        if (_session is not null)
        {
            _session.StateChanged -= OnSessionStateChanged;
            UnhookProviders();
        }

        _session = session;
        MutateRows(() =>
        {
            SelectedNode = null;
            _groups.Clear();
            _rows.Clear();
        });
        _shown = [];
        if (session is null)
        {
            UpdateHeader();
            HasSession = false;
            UpdateCommandStates();
            return;
        }

        session.StateChanged += OnSessionStateChanged;
        HookProviders();
        EnsureModes();
        UpdateHeader();
        Rebuild();
        HasSession = true;
        SyncSelection();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        UpdateHeader();
        if (_session.VisibleEntries.SequenceEqual(_shown))
        {
            RefreshNodes();
            ScheduleUsage();
        }
        else
        {
            Rebuild();
        }

        SyncSelection();
    }

    private void UpdateHeader()
    {
        var path = _session?.MapperPath ?? string.Empty;
        MapperPath = path;
        MapperFileName = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path);
        MapperDirectory = string.IsNullOrEmpty(directory) ? string.Empty : directory + Path.DirectorySeparatorChar;
        NamespaceText = _session is null ? string.Empty : _session.MapperNamespace ?? T("namespaceNone");
    }
}
