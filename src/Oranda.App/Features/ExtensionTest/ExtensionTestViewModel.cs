using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Text;

namespace Oranda.App.Features.ExtensionTest;

/// <summary>시험 패널이 선택 SQL 에 적용하는 바꾸기. AI 대신 쓰는 고정 규칙이다(D-04: AI 기능 자체는 만들지 않는다).</summary>
public enum ExtensionTransform
{
    /// <summary>모두 대문자.</summary>
    UpperCase,

    /// <summary>모두 소문자.</summary>
    LowerCase,

    /// <summary>앞에 설명 주석 한 줄.</summary>
    CommentHeader,
}

/// <summary>
/// <c>oranda.panel.extensionTest</c> — AI 확장 지점 시험 패널(TSK-10-03, PRD APP-09·9.4, TRD 4.10). 개발 빌드만.
/// <para>
/// 나중의 챗봇 플러그인이 하는 일을 확장 지점 인터페이스만으로 흉내 낸다. Core·편집기·실행 엔진을 고치지 않는다.
/// </para>
/// <list type="number">
/// <item>읽기: <see cref="IEditorContext"/> 로 활성 문서의 선택 영역(없으면 커서 위치 문장)을 읽는다.</item>
/// <item>제안: 고정 규칙(<see cref="ExtensionTransform"/>)으로 바꾼 글을 <see cref="IEditorContext.ProposeEditAsync"/> 로 넣는다 —
/// 차이 미리보기에서 사용자가 받아들일 때만 들어간다.</item>
/// <item>실행: 현재 문장을 <see cref="IExecutionService"/> 로 보낸다(<see cref="HistorySource.Extension"/>). 정책 관문(읽기 전용·운영 확인·위험 명령)을 그대로 지난다.</item>
/// <item>메타데이터: <see cref="IMetadataProvider"/> 로 테이블 목록을 읽는다.</item>
/// <item>외부 전송: <see cref="IOutboundConsent"/> 동의 없이는 보내지 않는다. 동의 서비스가 없으면 거절로 본다.</item>
/// </list>
/// 서비스는 처음 쓸 때 꺼낸다(시작 경로에서 Oracle·ANTLR 어셈블리를 올리지 않는다, D-006).
/// 생성자는 인자 없음·<see cref="IServiceProvider"/> 둘뿐이다(셸의 생성자 고르기가 다른 형식을 해석하려고 지연 어셈블리를 깨우지 않게).
/// </summary>
[OrandaToolPanel("oranda.panel.extensionTest", ToolPanelKind.Tool, DockLocation.Right, Order = 40, DevBuildOnly = true)]
public sealed partial class ExtensionTestViewModel : ToolPanelViewModelBase
{
    /// <summary>시험 전송의 받는 곳. <c>.invalid</c> 최상위 도메인이라 실제로 어디에도 닿지 않는다.</summary>
    public static readonly Uri TestDestination = new("https://extension.invalid/assist");

    private readonly IServiceProvider? _services;
    private IEditorContext? _editor;
    private IExecutionService? _execution;
    private IMetadataProvider? _metadata;
    private IOutboundConsent? _consent;
    private bool _consentResolved;

    [ObservableProperty]
    private ExtensionTransform _transform = ExtensionTransform.UpperCase;

    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    private string _proposedText = string.Empty;

    [ObservableProperty]
    private string _statusText = "편집기에서 SQL 을 고른 뒤 「읽기」를 누르세요.";

    public ExtensionTestViewModel()
    {
    }

    public ExtensionTestViewModel(IServiceProvider services) => _services = services;

    /// <summary>편집기 확장 지점. 비어 있으면 서비스에서 꺼낸다.</summary>
    public IEditorContext? Editor
    {
        get => _editor ??= Resolve<IEditorContext>();
        init => _editor = value;
    }

    /// <summary>실행 확장 지점(정책 관문 포함). 비어 있으면 서비스에서 꺼낸다.</summary>
    public IExecutionService? Execution
    {
        get => _execution ??= Resolve<IExecutionService>();
        init => _execution = value;
    }

    /// <summary>메타데이터 확장 지점. 비어 있으면 서비스에서 꺼낸다.</summary>
    public IMetadataProvider? Metadata
    {
        get => _metadata ??= Resolve<IMetadataProvider>();
        init => _metadata = value;
    }

    /// <summary>외부 전송 동의. 비어 있고 서비스에도 없으면 모든 전송을 막는다.</summary>
    public IOutboundConsent? Consent
    {
        get
        {
            if (!_consentResolved && _consent is null)
            {
                _consent = Resolve<IOutboundConsent>();
            }

            _consentResolved = true;
            return _consent;
        }

        init
        {
            _consent = value;
            _consentResolved = true;
        }
    }

    /// <summary>활성 문서의 작업 세션 찾기. 기본은 <see cref="ISessionManager"/> 의 작업 세션 중 문서 접속과 같은 것.</summary>
    public Func<EditorSnapshot?, ISession?>? SessionResolver { get; init; }

    /// <summary>
    /// 동의를 받은 뒤 실제로 보내는 함수. 1.0 은 AI 기능이 없으므로 기본은 없음(보내지 않고 기록만 한다).
    /// 시험·나중의 플러그인이 넣는다.
    /// </summary>
    public Func<OutboundRequest, CancellationToken, Task>? Transport { get; init; }

    /// <summary>고를 수 있는 바꾸기 목록(View 콤보 상자).</summary>
    public IReadOnlyList<ExtensionTransform> Transforms { get; } = Enum.GetValues<ExtensionTransform>();

    /// <summary>읽은 범위. 제안 편집이 이 범위를 바꾼다. 아직 읽지 않았으면 null.</summary>
    public TextRange? SourceRange { get; private set; }

    /// <summary>마지막 실행 결과(시험·화면 표시).</summary>
    public ExecutionResult? LastResult { get; private set; }

    /// <summary>메타데이터에서 읽은 객체 이름.</summary>
    public ObservableCollection<string> Objects { get; } = [];

    /// <summary>패널 기록(최근이 아래).</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>선택 영역(없으면 커서 위치 문장)을 읽어 <see cref="SourceText"/> 에 넣고 제안을 만든다. 읽을 것이 없으면 false.</summary>
    public bool ReadEditor()
    {
        var editor = Editor;
        var snapshot = editor?.GetActiveDocument();
        if (editor is null || snapshot is null)
        {
            SourceRange = null;
            return Report(false, "활성 SQL 편집기가 없습니다.");
        }

        TextRange range;
        if (snapshot.Selection.Length > 0)
        {
            range = snapshot.Selection;
        }
        else if (editor.GetCurrentStatement() is { } stmt)
        {
            range = stmt.Range;
        }
        else
        {
            SourceRange = null;
            return Report(false, "선택 영역도 커서 위치 문장도 없습니다.");
        }

        if (range.Start < 0 || range.End > snapshot.Text.Length)
        {
            SourceRange = null;
            return Report(false, "읽은 범위가 문서 밖입니다.");
        }

        SourceRange = range;
        SourceText = snapshot.Text.Substring(range.Start, range.Length);
        ProposedText = Apply(Transform, SourceText);
        return Report(true, $"읽음: '{snapshot.Title ?? "문서"}' {range.Start}..{range.End} ({range.Length}자)");
    }

    partial void OnTransformChanged(ExtensionTransform value)
    {
        if (SourceRange is not null)
        {
            ProposedText = Apply(value, SourceText);
        }
    }

    /// <summary>제안을 편집기에 넣는다. 차이 미리보기에서 사용자가 받아들였을 때만 true.</summary>
    public async Task<bool> ProposeAsync(CancellationToken ct)
    {
        if (Editor is not { } editor || SourceRange is not { } range)
        {
            return Report(false, "먼저 「읽기」로 바꿀 범위를 고르세요.");
        }

        var edit = new TextEdit(range, ProposedText) { Description = "확장 시험: " + Describe(Transform) };
        var accepted = await editor.ProposeEditAsync(edit, ct).ConfigureAwait(true);
        if (!accepted)
        {
            return Report(false, "제안 편집: 적용 안 됨(거절했거나 그 사이 문서가 바뀜).");
        }

        SourceRange = new TextRange(range.Start, ProposedText.Length);
        SourceText = ProposedText;
        return Report(true, "제안 편집: 사용자가 받아들여 적용됨.");
    }

    /// <summary>
    /// 편집기의 현재 문장을 실행 엔진으로 보낸다(<see cref="HistorySource.Extension"/>). 정책 관문이 거부·취소하면 그 판단을 기록한다.
    /// 세션·문장·실행 서비스가 없으면 null.
    /// </summary>
    public async Task<ExecutionResult?> RunCurrentStatementAsync(CancellationToken ct)
    {
        var stmt = Editor?.GetCurrentStatement();
        if (stmt is null)
        {
            Report(false, "실행할 문장이 없습니다.");
            return null;
        }

        var session = ResolveSession();
        if (session is null || Execution is not { } exec)
        {
            Report(false, "열린 작업 세션 또는 실행 서비스가 없습니다.");
            return null;
        }

        var result = await exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.Extension }, ct).ConfigureAwait(true);
        LastResult = result;

        // 결과 집합은 이 패널이 보여 주지 않는다. 커서를 바로 닫는다.
        if (result.ResultSet is { } rs)
        {
            await rs.DisposeAsync().ConfigureAwait(true);
        }

        Report(result.Status == ExecutionStatus.Succeeded, DescribeResult(result));
        return result;
    }

    /// <summary>메타데이터 확장 지점으로 현재 사용자의 테이블 이름을 읽는다(최대 50개). 읽은 개수, 못 읽으면 -1.</summary>
    public async Task<int> LoadTablesAsync(CancellationToken ct)
    {
        var session = ResolveSession();
        if (session is null || Metadata is not { } meta)
        {
            Report(false, "열린 작업 세션 또는 메타데이터 서비스가 없습니다.");
            return -1;
        }

        var rows = await meta.ListObjectsAsync(session, new ObjectQuery { ObjectTypes = ["TABLE"], Limit = 50 }, ct).ConfigureAwait(true);
        Objects.Clear();
        foreach (var o in rows)
        {
            Objects.Add(o.Owner + "." + o.Name);
        }

        Report(true, $"메타데이터: 테이블 {rows.Count}개");
        return rows.Count;
    }

    /// <summary>
    /// 제안 글을 외부로 보내는 흉내. <see cref="IOutboundConsent"/> 가 허락할 때만 <see cref="Transport"/> 를 부른다.
    /// 보냈으면(또는 전송 함수가 없어 보낼 수 있는 상태면) true, 막혔으면 false.
    /// </summary>
    public async Task<bool> SendOutboundAsync(CancellationToken ct)
    {
        var payload = string.IsNullOrEmpty(ProposedText) ? SourceText : ProposedText;
        var request = new OutboundRequest(OutboundPurpose.Extension, TestDestination, "확장 시험 패널: 선택 SQL 전송", payload);
        if (Consent is not { } consent)
        {
            return Report(false, "외부 전송: 동의 서비스가 없어 막음.");
        }

        var decision = await consent.RequestAsync(request, ct).ConfigureAwait(true);
        if (decision == ConsentDecision.Denied)
        {
            return Report(false, "외부 전송: 사용자가 거절해 막음.");
        }

        if (Transport is null)
        {
            return Report(true, "외부 전송: 동의됨(1.0 은 실제로 보내지 않음).");
        }

        await Transport(request, ct).ConfigureAwait(true);
        return Report(true, "외부 전송: 동의 후 보냄.");
    }

    // ---- 화면 버튼(값을 돌려주는 위 메서드를 감싼다) ----

    [RelayCommand]
    private void Read() => ReadEditor();

    [RelayCommand]
    private async Task Propose(CancellationToken ct) => await ProposeAsync(ct).ConfigureAwait(true);

    [RelayCommand]
    private async Task Run(CancellationToken ct) => await RunCurrentStatementAsync(ct).ConfigureAwait(true);

    [RelayCommand]
    private async Task LoadTables(CancellationToken ct) => await LoadTablesAsync(ct).ConfigureAwait(true);

    [RelayCommand]
    private async Task Send(CancellationToken ct) => await SendOutboundAsync(ct).ConfigureAwait(true);

    /// <summary>바꾸기 규칙. 순수 함수(시험 대상).</summary>
    public static string Apply(ExtensionTransform transform, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return transform switch
        {
            ExtensionTransform.UpperCase => text.ToUpperInvariant(),
            ExtensionTransform.LowerCase => text.ToLowerInvariant(),
            ExtensionTransform.CommentHeader => "-- extension test\n" + text,
            _ => text,
        };
    }

    private static string Describe(ExtensionTransform t) => t switch
    {
        ExtensionTransform.UpperCase => "대문자로",
        ExtensionTransform.LowerCase => "소문자로",
        _ => "주석 머리 달기",
    };

    private static string DescribeResult(ExecutionResult r)
    {
        var text = r.Status switch
        {
            ExecutionStatus.Denied => "실행: 정책 관문이 거부",
            ExecutionStatus.Cancelled when r.Policy is not null => "실행: 확인 대화상자에서 취소",
            ExecutionStatus.Cancelled => "실행: 취소",
            ExecutionStatus.Failed => "실행: 실패",
            _ => "실행: 성공",
        };
        if (r.Policy is { Rule: not PolicyRule.None } p)
        {
            text += $" [{p.Rule}]";
        }

        if (r.RowsAffected is { } n)
        {
            text += " " + n.ToString(CultureInfo.InvariantCulture) + "행";
        }

        if (r.Error is { } e)
        {
            text += " — " + e.Message;
        }
        else if (r.Status == ExecutionStatus.Denied && r.Policy?.Message is { } m)
        {
            text += " — " + m;
        }

        return text;
    }

    private ISession? ResolveSession()
    {
        var snapshot = Editor?.GetActiveDocument();
        if (SessionResolver is { } resolver)
        {
            return resolver(snapshot);
        }

        var manager = Resolve<ISessionManager>();
        if (snapshot?.SessionId is { } sid
            && manager?.Sessions.FirstOrDefault(s => s.Purpose == SessionPurpose.Work && s.Id == sid) is { } bySession)
        {
            return bySession;
        }

        // 문서의 접속이 열려 있지 않으면 null(다른 접속으로 대신하지 않는다). 접속이 없는 문서는 지금 접속.
        return SessionRouter.ForProfile(_services, snapshot?.ConnectionProfileId);
    }

    private T? Resolve<T>()
        where T : class => _services?.GetService(typeof(T)) as T;

    private bool Report(bool ok, string message)
    {
        StatusText = message;
        Log.Add(message);
        return ok;
    }
}
