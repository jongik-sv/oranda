using System;
using System.Collections.Generic;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.Report;

/// <summary>저장할 레포트의 형식. 파일 확장자로 정한다.</summary>
public enum AiTuningReportFormat
{
    Markdown,
    Html,
}

/// <summary>
/// 튜닝 흐름이 결과를 반영할 때 대화 창에 넘기는 레포트 재료. 대화 창에 이미 쌓인 줄(도구 카드·이어 말하기)은 저장할 때 대화 창에서 읽는다.
/// 가린 대화면 <see cref="TargetSql"/> 과 <see cref="Result"/> 는 자리표시(<c>:vN</c>)가 든 글 그대로다: 원래 값으로 되돌리지 않는다.
/// </summary>
/// <param name="CreatedAt">튜닝을 시작한 시각.</param>
/// <param name="Environment">접속 환경 구분(개발·테스트·운영). 접속 별칭·호스트는 싣지 않는다.</param>
/// <param name="Model">고른 모델 이름(기본 모델이면 null).</param>
/// <param name="LiteralsMasked">리터럴 값을 가려서 보낸 대화다.</param>
/// <param name="TargetSql">대화에 보낸 대상 문장(가린 대화면 가린 글).</param>
/// <param name="Result">마지막 구조 결과.</param>
public sealed record AiTuningReportSource(
    DateTimeOffset CreatedAt,
    ConnectionEnvironment? Environment,
    string? Model,
    bool LiteralsMasked,
    string TargetSql,
    TuningResult Result);

/// <summary>레포트 생성기의 입력(UI 와 분리한 값). <see cref="AiTuningReportBuilder"/> 가 글로 바꾼다.</summary>
public sealed record AiTuningReportInput(
    string Title,
    DateTimeOffset CreatedAt,
    string EnvironmentText,
    string? Model,
    bool LiteralsMasked,
    string TargetSql,
    TuningResult Result,
    IReadOnlyList<AiTuningReportTool> Tools,
    IReadOnlyList<AiTuningReportFollowUp> FollowUps);

/// <summary>사용한 도구 한 줄(대화 창의 도구 카드와 같은 사실).</summary>
public sealed record AiTuningReportTool(string Name, string Verdict, string Size, string Elapsed);

/// <summary>이어 말하기 문답 하나.</summary>
public sealed record AiTuningReportFollowUp(string Question, string Answer);
