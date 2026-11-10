namespace Oranda.App.Features.DataCopy;

/// <summary>데이터 복사 창이 상태 줄·알림에 쓰는 문구 모음.</summary>
internal static class DataCopyText
{
    /// <summary>영어 화면이면 문구 표(literals-en.json)의 영어로, 아니면(또는 표에 없으면) 그대로. 바인딩으로 보이는 고정 문구에 쓴다.</summary>
    public static string L(string korean) =>
        Localization.LiteralTranslator.IsEnglish && Localization.LiteralTranslator.Table.TryGetValue(korean, out var english) ? english : korean;

    /// <summary>
    /// 멈춤 표시 글(결정 70). 한국어는 실행기와 같은 글(<see cref="Oranda.Core.Transfer.Copy.CopyLogText.Wait"/>: 「배치 n 쓰는 중 · m분째 대기」·「원본 읽는 중 · m분째」),
    /// 영어 화면이면 같은 뜻의 영어.
    /// </summary>
    public static string WaitText(Oranda.Core.Transfer.Copy.CopyWait wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        if (!Localization.LiteralTranslator.IsEnglish)
        {
            return Oranda.Core.Transfer.Copy.CopyLogText.Wait(wait);
        }

        var minute = wait.Minute.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // 대기 원인(WH1)은 실행기가 만든 글 그대로 덧붙인다(DB 이벤트 이름·세션 정보가 대부분이다).
        return Oranda.Core.Transfer.Copy.CopyLogText.WithCause(wait.Step switch
        {
            Oranda.Core.Transfer.Copy.CopyWaitStep.Delete => $"Deleting target rows (DELETE) · waiting, minute {minute}",
            Oranda.Core.Transfer.Copy.CopyWaitStep.Commit => $"Committing table · waiting, minute {minute}",
            _ => wait.Side == Oranda.Core.Transfer.Copy.CopyWaitSide.Source
                ? $"Reading source · minute {minute}"
                : $"Writing batch {(wait.Batch ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)} · waiting, minute {minute}",
        }, wait);
    }

    /// <summary>
    /// 실행기가 돌아오지 않는 명령 때문에 세션을 버린 뒤(리뷰 PF1-3) 앱이 세션 쌍을 닫았을 때. 다시 실행하려면 다시 점검해 새 세션을 연다.
    /// </summary>
    public const string SessionsAbandoned = "취소한 명령이 돌아오지 않아 복사 세션을 닫았습니다. 다시 실행하려면 사전 점검을 다시 하세요(새 세션을 엽니다).";

    /// <summary>동시 복사 단계 글(PF2). 한국어는 실행기와 같은 글(<see cref="Oranda.Core.Transfer.Copy.CopyLogText.Stage"/>: 「동시 2개 · 단계 1/3」), 영어 화면이면 영어.</summary>
    public static string StageText(Oranda.Core.Transfer.Copy.CopyStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (!Localization.LiteralTranslator.IsEnglish)
        {
            return Oranda.Core.Transfer.Copy.CopyLogText.Stage(stage);
        }

        var c = System.Globalization.CultureInfo.InvariantCulture;
        return $"Concurrent {stage.Lanes.ToString(c)} · stage {stage.Index.ToString(c)}/{stage.Count.ToString(c)}";
    }

    /// <summary>시간 나눔 글(결정 74). 한국어는 실행기와 같은 글(<see cref="Oranda.Core.Transfer.Copy.CopyLogText.Times"/>), 영어 화면이면 영어.</summary>
    public static string TimesText(Oranda.Core.Transfer.Copy.CopyTableTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (!Localization.LiteralTranslator.IsEnglish)
        {
            return Oranda.Core.Transfer.Copy.CopyLogText.Times(times);
        }

        static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "s";
        return (times.Load is { } load ? $"load span {Seconds(load)} · " : string.Empty)
               + $"read {Seconds(times.Read)}" + (times.FirstRow is { } first ? $" (first row {Seconds(first)})" : string.Empty)
               + $" · convert {Seconds(times.Convert)} · write {Seconds(times.Write)} · "
               + times.RowsPerSecond.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " rows/s"
               + (times.Total is { } total ? $" · table total {Seconds(total)}" : string.Empty)
               + (times.Other is { } other ? $" · other {Seconds(other)}" : string.Empty);
    }

    public const string StoreMissing = "복사 작업 저장소가 아직 준비되지 않았습니다. 앱을 다시 시작하거나 최신 버전으로 업데이트하세요.";
    public const string PrecheckMissing = "사전 점검 서비스가 아직 준비되지 않았습니다.";
    public const string RunnerMissing = "복사 실행기가 아직 준비되지 않았습니다.";
    public const string RunLogMissing = "실행 기록 서비스가 아직 준비되지 않았습니다.";
    public const string ExecutionMissing = "SQL 실행 서비스를 찾을 수 없습니다.";
    public const string MetadataMissing = "메타데이터 서비스를 찾을 수 없어 테이블 정보를 읽지 못했습니다.";
    public const string SessionsNotOpened = "원본·대상 세션을 열지 않았습니다(비밀번호 입력을 취소했거나 접속하지 못했습니다).";
    public const string SourceMissing = "원본 접속을 고르세요.";
    public const string TargetMissing = "대상 접속을 고르세요.";
    public const string NoTables = "복사할 테이블을 하나 이상 추가하세요.";
    public const string ProductionTarget = "운영 접속은 복사 대상으로 쓸 수 없습니다.";
    public const string ReadOnlyTarget = "대상 접속이 읽기 전용이라 복사할 수 없습니다. 쓰기가 가능한 접속을 고르세요.";
    /// <summary>원본·대상 DBMS 쌍을 복사할 수 없을 때(같은 DBMS 끼리와 Oracle↔Tibero 만 복사한다). 두 DBMS 이름을 붙인다.</summary>
    public static string UnsupportedPair(string source, string target) =>
        $"원본({source})에서 대상({target})으로는 복사할 수 없습니다. 같은 DBMS 끼리, 또는 Oracle 과 Tibero 사이만 복사할 수 있으니 다른 접속을 고르세요.";

    public const string RunStopped = "사용자가 복사를 중지했습니다.";
    public const string UnfinishedHeader = "지난 실행이 끝나지 않았습니다. 대상 트리거·FK 상태를 확인하세요.";

    /// <summary>작업 파일의 schemaVersion 이 이 앱보다 새것이라 읽기 전용으로 연 작업. 모르는 필드(표마다 계정 등)를 버리고 실행하면 다른 표를 지울 수 있어 점검·실행을 막는다.</summary>
    public const string NewerVersionJob = "새 버전 Oranda 가 만든 작업이라 사전 점검·실행을 할 수 없습니다. Oranda 를 업데이트하세요.";

    public const string SameSourceAndTarget = "원본과 대상이 같은 접속이고 지울 대상 테이블이 읽을 원본 테이블과 같습니다. 원본 행이 지워질 수 있어 시작하지 않습니다.";

    /// <summary>같은 접속에서 지울 대상 표가 읽을 원본 표와 겹칠 때. 겹치는 표(<c>계정.표</c>)를 붙인다.</summary>
    public static string SameSourceTables(IEnumerable<string> tables) =>
        $"{SameSourceAndTarget} ({string.Join(", ", tables)}) 대상 계정을 바꾸세요.";

    /// <summary>상세 칸: 규칙의 대상 계정이 대상 접속 계정이 아닐 때.</summary>
    public const string OtherTargetOwner = "접속 계정이 아닌 계정입니다. 그 계정 테이블의 INSERT·DELETE 권한이 필요합니다.";

    /// <summary>원본 세션 없이 시노님 노드를 더하려 할 때.</summary>
    public const string SynonymNeedsSource = "시노님은 원본 접속을 열어야 실제 테이블로 풀 수 있습니다. 원본 접속을 고른 뒤 다시 추가하세요.";

    /// <summary>실행 확인이 필요한 작업인데 확인 창을 띄울 수 없을 때(묻지 못했으면 실행하지 않는다).</summary>
    public const string RunConfirmMissing = "실행 확인 창을 띄울 수 없어 복사를 시작하지 않았습니다.";

    /// <summary>원본 세션은 열렸지만 메타데이터 서비스가 없어 시노님을 풀지 못하고 적은 이름 그대로 더했을 때.</summary>
    public static string SynonymNotResolved(string table) =>
        $"{table} 규칙을 추가했지만 메타데이터 서비스를 찾을 수 없어 시노님인지 확인하지 못했습니다. 시노님이면 실제 계정.테이블로 다시 추가하세요.";

    public static string InvalidCondition(string table, string problem) => $"테이블 {table} 의 조건을 쓸 수 없습니다. {problem}";

    public static string RecreateNotAllowed(string table) =>
        $"테이블 {table} 의 「테이블 다시 만들기」는 대상이 개발 접속일 때만 쓸 수 있습니다. 대상 처리를 바꾸세요.";

    public static string TargetBlocked(string name) =>
        $"작업에 저장된 대상 접속 「{name}」 이(가) 이 PC 에서는 운영 접속이라 대상을 비웠습니다. 개발·테스트 접속을 다시 고르세요.";

    public static string SourceNotFound(string name) => $"원본 접속 「{name}」 을(를) 이 PC 에서 찾지 못했습니다. 접속을 다시 고르세요.";

    public static string TargetNotFound(string name) => $"대상 접속 「{name}」 을(를) 이 PC 에서 찾지 못했습니다. 접속을 다시 고르세요.";
}

/// <summary>
/// 테이블 줄의 「단계」 칸 글(적재가 끝난 뒤). 실행기의 단계 이름(<see cref="Oranda.Core.Transfer.Copy.CopyRunner"/> 의 Phase*)을 마무리 단계 글로 옮긴다.
/// 영어 화면은 문구 표(literals-en.json)에 같은 글로 등록돼 있다.
/// </summary>
internal static class RunPhases
{
    /// <summary>적재는 끝났고 시퀀스·다시 켜기·건수 비교 같은 마무리가 이 테이블에 닿기를 기다린다.</summary>
    public const string LoadEnded = "적재 끝 · 마무리 대기";

    public const string FinishingCount = "마무리 중 · 건수 비교";
    public const string FinishingSequence = "마무리 중 · 시퀀스 맞추기";
    public const string FinishingEnable = "마무리 중 · 다시 켜기";
    public const string FinishingRestore = "마무리 중 · 되살리기";

    /// <summary>적재를 마친 테이블에 온 보고의 단계 이름 → 마무리 중 글. 마무리 단계가 아니면 null(글을 바꾸지 않는다).</summary>
    public static string? Finishing(string phase) => phase switch
    {
        Oranda.Core.Transfer.Copy.CopyRunner.PhaseCount => FinishingCount,
        Oranda.Core.Transfer.Copy.CopyRunner.PhaseSequence => FinishingSequence,
        Oranda.Core.Transfer.Copy.CopyRunner.PhaseEnable => FinishingEnable,
        Oranda.Core.Transfer.Copy.CopyRunner.PhaseRestore => FinishingRestore,
        _ => null,
    };
}
