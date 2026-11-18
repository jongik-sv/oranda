using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Oranda.Core.Text;

namespace Oranda.App.Features.PlsqlEditor.Ai;

/// <summary>
/// PL/SQL 편집기의 AI 명령(설계 §3.3, TSK-15-07): 「오류 고치기」 = 편집기 아래 제안 막대(<see cref="AiSuggestionBar"/>, 바로 처리),
/// 「코드 설명」 = 채팅창 새 대화. 둘 다 가벼운 일 한 번짜리(도구 없음)이고 소스가 크면 대상을 고를 때 주 모델로 올린다(<see cref="AiKitRoute.For"/>).
/// 컴파일·실행은 사람이 한다. 받아들이기는 기존 차이 미리보기에서 적용한 때만 편집기 글을 바꾼다.
/// </summary>
public static class PlsqlAiActions
{
    /// <summary>코드 설명 대화 제목 앞부분(SQL 탭의 「SQL 설명」 에 해당).</summary>
    public const string ExplainTitle = "코드 설명";

    /// <summary>감사 기록의 기능 이름.</summary>
    public const string FixFeature = "plsql_fix";

    public const string ExplainFeature = "plsql_explain";

    /// <summary>편집기에 소스가 없을 때.</summary>
    public const string EmptyText = "설명할 소스가 없습니다.";

    /// <summary>접속이 없을 때.</summary>
    public const string NoSessionText = "이 PL/SQL 편집기에 접속이 없어 보내지 않았습니다. 접속한 뒤 다시 시도하세요.";

    /// <summary>답을 받는 사이 소스가 바뀌었을 때.</summary>
    public const string ChangedText = "답을 받는 사이 소스가 바뀌어 넣지 않았습니다. 다시 컴파일한 뒤 시도하세요.";

    /// <summary>차이 미리보기에서 거절했을 때.</summary>
    public const string RejectedText = "적용하지 않았습니다.";

    /// <summary>
    /// 보이는 쪽(명세·본문)의 오류 고치기 입력. 오류 줄은 서버 줄 번호라 편집기 줄로 옮기고(앞에 붙인 <c>CREATE OR REPLACE</c>·머리 주석만큼), 다른 쪽(명세/본문) 오류는 뺀다.
    /// 본문이면 명세 전체를 참고로 싣는다.
    /// </summary>
    public static PlsqlFixInput FixInput(PlsqlEditorViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return FixInput(vm.Editor.Text, vm.Target, vm.Errors, vm.IsBodySide ? vm.SpecPart?.Editor.Text : null);
    }

    /// <summary><see cref="FixInput(PlsqlEditorViewModel)"/> 의 계산(편집기 없이).</summary>
    internal static PlsqlFixInput FixInput(string source, DbObjectRef? target, IEnumerable<CompileError> errors, string? specSource)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(errors);
        var (lineOffset, _) = PlsqlSourceText.SourceStart(source);
        var type = target?.ObjectType;
        var shown = errors
            .Where(e => e.ObjectType is null || type is null || string.Equals(e.ObjectType, type, StringComparison.OrdinalIgnoreCase))
            .Select(e => e with { Line = e.Line + lineOffset })
            .ToList();
        return new PlsqlFixInput(ObjectName(target), type ?? "PL/SQL", source, shown) { SpecSource = specSource };
    }

    /// <summary>코드 설명 요청 글과 꾸러미(보이는 쪽 소스, 본문이면 명세 참고). 가림 대상이면 소스 리터럴을 가린다.</summary>
    public static (string Prompt, IAiContextBundle Bundle)? ExplainRequest(PlsqlFixInput input, AiKitRoute route, ISqlLiteralMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(route);
        var bundle = route.Bundle(ExplainTitle, masker)
            .Add("source", "소스", input.Source, AiAccessLevel.Metadata, AiContextKind.Sql)
            .Add("package_spec", "패키지 명세(참고)", input.SpecSource, AiAccessLevel.Metadata, AiContextKind.Sql)
            .Build();
        if (bundle.Items.All(i => i.Key != "source"))
        {
            return null;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Explain this Oracle PL/SQL {OneLine(input.ObjectType)} {OneLine(input.ObjectName)} to the user in plain Korean. ");
        text.Append("You have no tools and cannot read the database or run anything; explain only what the source shows and say briefly when a meaning is a guess. ");
        text.Append("Response style (applies to every answer, including follow-ups): write in Korean and be brief. ");
        text.Append("1) One bold line saying what the unit does. 2) ## 하는 일: 3 to 6 numbered steps in order. 3) ## 읽고 쓰는 곳: tables and other units it reads or changes. ");
        text.Append("4) ## 주의: only real pitfalls seen in the code (exceptions swallowed, commits inside, implicit conversion); skip if none. ");
        text.Append("Do not rewrite the code. Never write tool or function names. ");
        if (input.SpecSource is { Length: > 0 })
        {
            text.Append("package_spec is for reference only. ");
        }

        return (text.ToString().TrimEnd() + "\n\n" + bundle.ToPrompt(), bundle);
    }

    /// <summary>
    /// 「받아들이기」: <see cref="PlsqlFixPrompt.Read"/> 로 고친 소스 전체를 만들고, 요청 때와 같은 소스인지 대조한 뒤 차이 미리보기로 편집기에 넣는다.
    /// 넣었으면 null, 아니면 까닭.
    /// </summary>
    /// <param name="accept">띠가 넘긴 요청·결과.</param>
    /// <param name="input">요청 때의 입력(소스 원문).</param>
    /// <param name="editor">고칠 편집기(보낸 쪽).</param>
    /// <param name="version">요청 때 편집기 판.</param>
    /// <param name="preview">차이 미리보기(제목, 원래 글, 제안 글 → 적용할 글, 거절 = null).</param>
    /// <param name="masker">가린 값 되돌리기.</param>
    public static async Task<string?> AcceptAsync(
        AiStripAccept accept, PlsqlFixInput input, SqlEditorViewModel editor, long version,
        Func<string, string, string, CancellationToken, Task<string?>> preview, ISqlLiteralMasker? masker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(preview);
        var result = PlsqlFixPrompt.Read(accept.Result.Text, input, accept.Request, masker);
        if (result.EditorText is not { } fixedText)
        {
            return result.EditorNote ?? PlsqlFixPrompt.NoFixText;
        }

        if (editor.Version != version || !string.Equals(editor.Text, input.Source, StringComparison.Ordinal))
        {
            return ChangedText;
        }

        var title = string.IsNullOrWhiteSpace(result.Reason) ? "AI 오류 고치기" : "AI 오류 고치기 — " + result.Reason;
        var applied = await preview(title, input.Source, fixedText, ct).ConfigureAwait(true);
        if (applied is null)
        {
            return RejectedText;
        }

        if (editor.Version != version)
        {
            return ChangedText; // 미리보기를 보는 사이 바뀌었다
        }

        var length = editor.Document.TextLength;
        return editor.Apply(new TextOpResult(0, length, applied, 0, applied.Length)) ? null : ChangedText;
    }

    /// <summary>앱 연결: 보이는 쪽 편집기 아래 제안 막대를 띄우고 요청한다.</summary>
    public static async Task FixErrorAsync(IServiceProvider services, PlsqlEditorViewModel? vm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (vm is null)
        {
            return;
        }

        var editor = vm.Editor;
        if (editor.AiBar is { IsRunning: true })
        {
            return;
        }

        var masker = services.GetService(typeof(ISqlLiteralMasker)) as ISqlLiteralMasker;
        PlsqlFixInput? sent = null;
        long version = 0;
        var bar = AiSuggestionBar.Create(
            PlsqlFixPrompt.Title,
            async (tier, token) =>
            {
                var input = FixInput(vm);
                if (await RunForAsync(services, vm, tier ?? PlsqlFixPrompt.Tier, FixFeature, AiKitRoute.EstimateFor(input.Source, input.SpecSource), token).ConfigureAwait(true) is not { } run)
                {
                    return AiStripPreparation.Refuse(NoSessionText);
                }

                sent = input;
                version = editor.Version;
                return PlsqlFixPrompt.Prepare(input, run, masker);
            },
            (accept, token) => sent is null
                ? Task.FromResult<string?>(PlsqlFixPrompt.NoFixText)
                : AcceptAsync(accept, sent, editor, version, (title, before, after, t) => SqlEditorHost.Current.Dialogs.PreviewEditTextAsync(title, before, after, t), masker, token),
            AiChatHost.For(services));
        editor.AiBar = bar;
        await bar.RunAsync(ct).ConfigureAwait(true);
    }

    /// <summary>앱 연결: 코드 설명을 채팅창 새 대화로(진행 중인 다른 대화가 있으면 멈출지 묻는다).</summary>
    public static async Task ExplainAsync(IServiceProvider services, PlsqlEditorViewModel? vm, CancellationToken ct) =>
        await ExplainAsync(services, vm, AiChatHost.For(services), ct).ConfigureAwait(true);

    internal static async Task<AiCarriedConversation?> ExplainAsync(IServiceProvider services, PlsqlEditorViewModel? vm, AiChatHost host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(host);
        if (vm is null)
        {
            return null;
        }

        var input = FixInput(vm);
        if (string.IsNullOrWhiteSpace(input.Source))
        {
            await NoticeAsync(host, EmptyText).ConfigureAwait(true);
            return null;
        }

        if (await RunForAsync(services, vm, AiCommandTiers.ExplainSql, ExplainFeature, AiKitRoute.EstimateFor(input.Source, input.SpecSource), ct).ConfigureAwait(true) is not { } run)
        {
            await NoticeAsync(host, NoSessionText).ConfigureAwait(true);
            return null;
        }

        if (run.Route.Context.AccessLevel == AiAccessLevel.Off)
        {
            await NoticeAsync(host, AiTuningFlow.CommandAccessOffText).ConfigureAwait(true);
            return null;
        }

        var masker = services.GetService(typeof(ISqlLiteralMasker)) as ISqlLiteralMasker;
        if (ExplainRequest(input, run.Route, masker) is not { } request)
        {
            await NoticeAsync(host, PlsqlFixPrompt.MaskFailedText).ConfigureAwait(true);
            return null;
        }

        var title = ExplainTitle + " — " + input.ObjectName;
        var conversation = await AiChatOpener.OpenAsync(host, chat =>
        {
            var created = AiCarriedConversation.ForRequest(chat, title, run, request.Prompt, request.Bundle);
            created.ConfirmText = host.ConfirmText;
            return created;
        }).ConfigureAwait(true);
        if (conversation is null)
        {
            return null;
        }

        var line = ExplainTitle + " · " + input.ObjectName + (request.Bundle.Masked ? " · 리터럴 값과 주석은 가려서 보냈습니다" : string.Empty);
        await conversation.StartAsync(line, ct).ConfigureAwait(true);
        return conversation;
    }

    /// <summary>편집기의 접속으로 요청 경로를 만든다(접속이 없으면 null).</summary>
    private static async Task<AiOneShotRun?> RunForAsync(IServiceProvider services, PlsqlEditorViewModel vm, AiTier tier, string feature, int tokens, CancellationToken ct)
    {
        if (vm.Session?.Profile is not { } known || services.GetService(typeof(IAiDriver)) is not IAiDriver driver)
        {
            return null;
        }

        var (profile, settings) = await FixErrorInlineLauncher.ReadStartAsync(services, known, ct).ConfigureAwait(true);
        return FixErrorInlineLauncher.NewRun(services, driver, AiKitRoute.For(profile, settings, tier, tokens), feature);
    }

    private static async Task NoticeAsync(AiChatHost host, string text)
    {
        if (await host.ShowChat().ConfigureAwait(true) is { } chat)
        {
            chat.AddAssistantText(text);
        }
    }

    private static string ObjectName(DbObjectRef? target) => target is null ? "(새 소스)" : target.Owner + "." + target.Name;

    private static string OneLine(string? text) => string.IsNullOrWhiteSpace(text) ? "-" : text.ReplaceLineEndings(" ").Trim();
}
