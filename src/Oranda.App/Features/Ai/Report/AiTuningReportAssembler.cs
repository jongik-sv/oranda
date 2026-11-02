using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.Report;

/// <summary>
/// 대화 창에 쌓인 줄과 튜닝 흐름이 넘긴 재료(<see cref="AiTuningReportSource"/>)를 레포트 입력으로 모은다(UI 와 분리, 순수 함수).
/// 첫 사용자 줄은 처음 요청이라 문답에서 뺀다. 결론은 처음 튜닝 결과의 요약이고, 이어 말하기의 답은 그 차례 결과의 요약(구조 결과가 없는 차례는 AI 글, 저장 안내 줄은 뺀다)이다.
/// 재작성·인덱스 후보는 모든 차례를 모으되 같은 것은 한 번만 싣고, 어느 차례에서 나왔는지를 함께 넘긴다.
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
        var turns = source.Turns.OrderBy(t => t.Index).ToList();
        List<AiTuningReportTool> tools = [];
        List<AiTuningReportFollowUp> followUps = [];
        List<string> answers = [];
        string? question = null;
        var seenFirstUser = false;
        var turnIndex = 0; // 대화 창의 사용자 줄 순서: 0 = 처음 요청, N = N 번째 이어 말하기

        void Flush()
        {
            if (question is not null)
            {
                // 구조 결과가 온 차례의 답 = 그 결과의 요약, 없는 차례 = 그 차례의 AI 글
                var structured = turns.Find(t => t.Index == turnIndex)?.Result.Summary;
                followUps.Add(new AiTuningReportFollowUp(question, string.IsNullOrWhiteSpace(structured) ? string.Join("\n\n", answers) : structured));
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
                        turnIndex++;
                    }

                    seenFirstUser = true;
                    break;
                case ChatToolEntry tool:
                    tools.Add(new AiTuningReportTool(tool.Headline, tool.Verdict, tool.ResponseSize, tool.Elapsed));
                    break;
                case ChatAssistantEntry assistant when question is not null && !IsNotice(assistant.Text):
                    answers.Add(assistant.Text);
                    break;
            }
        }

        Flush();

        // 후보는 모든 차례를 모으되 같은 라벨·SQL(인덱스는 같은 표·DDL)은 한 번만 싣는다.
        List<TuningRewrite> rewrites = [];
        List<int> rewriteTurns = [];
        List<TuningIndexSuggestion> indexes = [];
        List<int> indexTurns = [];
        List<string> dropped = [];
        foreach (var turn in turns)
        {
            foreach (var rewrite in turn.Result.Rewrites)
            {
                if (!rewrites.Any(r => r.Label == rewrite.Label && r.Sql == rewrite.Sql))
                {
                    rewrites.Add(rewrite);
                    rewriteTurns.Add(turn.Index);
                }
            }

            foreach (var index in turn.Result.IndexSuggestions)
            {
                if (!indexes.Any(i => i.Table == index.Table && i.Ddl == index.Ddl))
                {
                    indexes.Add(index);
                    indexTurns.Add(turn.Index);
                }
            }

            dropped.AddRange(turn.DroppedIndexes.Where(d => !dropped.Contains(d)));
        }

        var first = turns.Find(t => t.Index == 0)?.Result; // 결론 = 처음 튜닝 결과의 요약
        var merged = new TuningResult(first?.Summary ?? string.Empty, rewrites, indexes, first?.Evidence ?? []);
        return new AiTuningReportInput(
            title, source.CreatedAt, EnvironmentText(source.Environment), source.Model, source.LiteralsMasked,
            source.TargetSql, merged, tools, followUps)
        {
            RewriteTurns = rewriteTurns,
            IndexTurns = indexTurns,
            DroppedIndexes = dropped,
        };
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
    /// 고른 경로의 확장자로 형식을 정한다(.md·.markdown = Markdown, .html·.htm = HTML). 그 밖이거나 확장자가 없으면 false:
    /// 확장자는 저장 창이 붙이므로(덮어쓰기 확인 포함) 여기서 이름을 바꾸지 않고, 이미 있는 파일을 말없이 덮지 않게 쓰지 않는다.
    /// </summary>
    public static bool TryResolve(string path, out AiTuningReportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path);
        format = AiTuningReportFormat.Html;
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            format = AiTuningReportFormat.Markdown;
            return true;
        }

        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }
}
