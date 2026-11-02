using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.Report;

/// <summary>
/// 대화 창에 쌓인 줄과 튜닝 흐름이 넘긴 재료(<see cref="AiTuningReportSource"/>)를 레포트 입력으로 모은다(UI 와 분리, 순수 함수).
/// 첫 사용자 줄은 처음 요청이라 문답에서 뺀다. 이어 말하기 문답의 답에서는 결론(마지막 구조 결과의 요약)과 저장 안내 줄을 뺀다.
/// </summary>
public static class AiTuningReportAssembler
{
    /// <summary>저장에 성공했을 때 대화 창에 남기는 안내의 앞부분(뒤에 경로). 다음 레포트에서는 문답으로 세지 않는다.</summary>
    public const string SavedPrefix = "레포트를 저장했습니다: ";

    /// <summary>저장에 실패했을 때 대화 창에 남기는 안내의 앞부분(뒤에 사유).</summary>
    public const string FailedPrefix = "레포트를 저장하지 못했습니다: ";

    public static AiTuningReportInput Build(AiTuningReportSource source, string title, IEnumerable<ChatEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(entries);
        List<AiTuningReportTool> tools = [];
        List<AiTuningReportFollowUp> followUps = [];
        List<string> answers = [];
        string? question = null;
        var seenFirstUser = false;

        void Flush()
        {
            if (question is not null)
            {
                followUps.Add(new AiTuningReportFollowUp(question, string.Join("\n\n", answers)));
            }

            question = null;
            answers.Clear();
        }

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case ChatUserEntry user:
                    Flush();
                    if (seenFirstUser)
                    {
                        question = user.Text;
                    }

                    seenFirstUser = true;
                    break;
                case ChatToolEntry tool:
                    tools.Add(new AiTuningReportTool(tool.Headline, tool.Verdict, tool.ResponseSize, tool.Elapsed));
                    break;
                case ChatAssistantEntry assistant when question is not null && !IsNotice(assistant.Text) && assistant.Text != source.Result.Summary:
                    answers.Add(assistant.Text);
                    break;
            }
        }

        Flush();
        return new AiTuningReportInput(
            title, source.CreatedAt, EnvironmentText(source.Environment), source.Model, source.LiteralsMasked,
            source.TargetSql, source.Result, tools, followUps);
    }

    /// <summary>접속 환경 구분을 사람 말로(별칭·호스트는 다루지 않는다).</summary>
    public static string EnvironmentText(ConnectionEnvironment? environment) => environment switch
    {
        ConnectionEnvironment.Development => "개발",
        ConnectionEnvironment.Test => "테스트",
        ConnectionEnvironment.Production => "운영",
        _ => "알 수 없음",
    };

    private static bool IsNotice(string text) =>
        text.StartsWith(SavedPrefix, StringComparison.Ordinal) || text.StartsWith(FailedPrefix, StringComparison.Ordinal);
}

/// <summary>저장 파일의 이름 제안과 형식 판별. 이름에는 문장 글을 넣지 않는다(SQL 이 파일 이름에 새지 않게).</summary>
public static class AiTuningReportFiles
{
    public static string Extension(AiTuningReportFormat format) => format == AiTuningReportFormat.Markdown ? ".md" : ".html";

    /// <summary>저장 창에 제안할 이름(예 「AI튜닝레포트_20261102-1403.html」).</summary>
    public static string SuggestName(DateTimeOffset createdAt, AiTuningReportFormat format) =>
        "AI튜닝레포트_" + createdAt.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + Extension(format);

    /// <summary>
    /// 고른 경로의 확장자로 형식을 정한다(.md·.markdown = Markdown, .html·.htm = HTML). 그 밖이거나 확장자가 없으면 HTML 로 보고 .html 을 붙여 돌려준다.
    /// </summary>
    public static string Resolve(string path, out AiTuningReportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            format = AiTuningReportFormat.Markdown;
            return path;
        }

        format = AiTuningReportFormat.Html;
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ".html";
    }
}
