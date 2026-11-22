using Oranda.App.Features.AiSetup.AskAdmin;
using Oranda.App.Shell;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Tests.Features.AiSetup.AskAdmin;

/// <summary>B4 「담당자에게 묻기」(갈래 C 마지막): 메신저 글·복사·끝남.</summary>
public sealed class AskAdminStepTests
{
    private sealed class Copier
    {
        public List<string> Copied { get; } = [];

        public bool Fail { get; set; }

        public Task<bool> CopyAsync(string text)
        {
            if (Fail)
            {
                return Task.FromResult(false);
            }

            Copied.Add(text);
            return Task.FromResult(true);
        }
    }

    private static AiSetupDiagnosticInfo Info(bool claude = false) =>
        new("0.1.5", "Linux 6.8", AiSetupPath.NotSure, claude, claude ? "2.1.330" : null, null, DateTimeOffset.Now);

    private static AskAdminViewModel Create(Func<string, Task<bool>> copy, string language = "ko", bool claude = false) =>
        new(Info(claude), copy, new ShellText(language: language));

    [Fact]
    public void Message_HasVersionAndClaudeState_AndAsksForAccountOrFile()
    {
        var vm = Create(_ => Task.FromResult(true));
        var missing = vm.Message;
        vm.UpdateClaude(true, "2.1.330");

        Assert.Contains("0.1.5", vm.Message);
        Assert.Contains("2.1.330", vm.Message);
        Assert.Contains(".oranda-ai", vm.Message);
        Assert.Contains("회사 Claude 계정", vm.Message);
        Assert.NotEqual(missing, vm.Message);
    }

    [Fact]
    public async Task Copy_PutsMessageOnClipboard_AndCompletesStep()
    {
        var copier = new Copier();
        var vm = Create(copier.CopyAsync);
        Assert.False(vm.IsComplete);

        await vm.CopyAsync();

        Assert.Equal([vm.Message], copier.Copied);
        Assert.True(vm.IsComplete);
        Assert.Contains("복사", vm.StatusText);
    }

    [Fact]
    public async Task CopyFailure_ShowsHint_AndStepStaysOpen()
    {
        var copier = new Copier { Fail = true };
        var vm = Create(copier.CopyAsync, claude: true);

        await vm.CopyAsync();

        Assert.False(vm.IsComplete);
        Assert.Contains("직접", vm.StatusText);
    }

    [Fact]
    public void LanguageSwitch_RebuildsMessage()
    {
        var text = new ShellText(language: "ko");
        var vm = new AskAdminViewModel(Info(), _ => Task.FromResult(true), text);
        Assert.Contains("안녕하세요", vm.Message);

        text.SetLanguage("en");

        Assert.Contains("Hello", vm.Message);
    }

    [Fact]
    public void Message_HasNoSecretsOrAddresses()
    {
        foreach (var language in new[] { "ko", "en" })
        {
            var message = Create(_ => Task.FromResult(true), language, claude: true).Message;
            Assert.DoesNotContain("http", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@", message);
            Assert.DoesNotContain(Environment.UserName, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CopyThrowing_IsTreatedAsFailure()
    {
        var vm = Create(_ => throw new InvalidOperationException("clipboard busy (test)"), "en", claude: true);

        await vm.CopyAsync();

        Assert.False(vm.IsComplete);
        Assert.Contains("yourself", vm.StatusText);
    }
}
