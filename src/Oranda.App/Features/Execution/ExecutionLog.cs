using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Events;

namespace Oranda.App.Features.Execution;

/// <summary>메시지 창 한 줄의 종류.</summary>
public enum MessageKind
{
    Info,
    Warning,
    Error,
}

/// <summary>메시지 창(실행 로그·스크립트 오류) 한 줄.</summary>
public sealed record MessageEntry(DateTimeOffset Time, MessageKind Kind, string Text)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string Line => $"{TimeText}  {Text}";

    /// <summary>앞 아이콘 키(정보 = 파란 i, 경고 = 노란 삼각형, 오류 = 빨간 원).</summary>
    public string IconKey => Kind switch
    {
        MessageKind.Error => "error",
        MessageKind.Warning => "warn",
        _ => "info",
    };

    public bool IsWarning => Kind == MessageKind.Warning;

    public bool IsError => Kind == MessageKind.Error;
}

/// <summary>
/// DBMS 출력 창과 메시지 창이 함께 보는 기록. 도구 창 ViewModel 은 처음 열릴 때 만들어지므로(지연 생성)
/// 기록은 이벤트 버스마다 따로 둔다(<see cref="For"/>). <c>DbmsOutputReceived</c> 는 여기서 직접 받는다.
/// </summary>
public sealed class ExecutionLog : ObservableObject
{
    /// <summary>한도를 넘으면 오래된 것부터 버린다.</summary>
    public const int MaxOutputChars = 4_000_000;

    public const int MaxMessages = 5000;

    private static readonly ConditionalWeakTable<IEventBus, ExecutionLog> PerBus = [];
    private static readonly Lock Gate = new();
    private static ExecutionLog? s_fallback;

    private readonly StringBuilder _output = new();
    private readonly ExecutionState _state;

    public ExecutionLog(IEventBus? bus = null, ExecutionState? state = null)
    {
        _state = state ?? ExecutionState.For(bus);
        bus?.Subscribe<DbmsOutputReceived>(this, e => Post(() => AppendOutput(e.Lines)));
    }

    public static ExecutionLog For(IEventBus? bus)
    {
        lock (Gate)
        {
            if (bus is null)
            {
                return s_fallback ??= new ExecutionLog();
            }

            if (!PerBus.TryGetValue(bus, out var log))
            {
                log = new ExecutionLog(bus);
                PerBus.Add(bus, log);
            }

            return log;
        }
    }

    /// <summary>DBMS_OUTPUT 으로 모은 글(줄바꿈으로 이은 것).</summary>
    public string OutputText => _output.ToString();

    public int OutputLength => _output.Length;

    public ObservableCollection<MessageEntry> Messages { get; } = [];

    public event EventHandler? OutputChanged;

    /// <summary>DBMS_OUTPUT 줄을 붙인다. 수집을 끈 상태(<see cref="ExecutionState.DbmsOutputEnabled"/>)면 버린다.</summary>
    public void AppendOutput(IEnumerable<string> lines)
    {
        if (!_state.DbmsOutputEnabled)
        {
            return;
        }

        bool any = false;
        foreach (var line in lines)
        {
            _output.Append(line).Append('\n');
            any = true;
        }

        if (!any)
        {
            return;
        }

        if (_output.Length > MaxOutputChars)
        {
            _output.Remove(0, _output.Length - (MaxOutputChars * 3 / 4));
        }

        OnPropertyChanged(nameof(OutputText));
        OutputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearOutput()
    {
        _output.Clear();
        OnPropertyChanged(nameof(OutputText));
        OutputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddMessage(MessageKind kind, string text)
    {
        Messages.Add(new MessageEntry(DateTimeOffset.Now, kind, text));
        while (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(0);
        }
    }

    public void ClearMessages() => Messages.Clear();

    private static void Post(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
