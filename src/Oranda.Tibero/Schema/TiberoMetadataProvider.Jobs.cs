using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 작업(JOB) 상세·DDL(2026-11-21 Tibero 6 실측). Tibero <c>*_objects</c> 에는 작업이 없어 <c>meta.detail_general</c> 대신 <c>meta.job_detail</c> 한 줄로
/// 「일반」 구역을 만든다. 목록은 <c>meta.jobs</c>(본체 <see cref="FetchObjectsAsync"/>), 표식은 <c>meta.marks_jobs</c>.
/// </summary>
internal sealed partial class TiberoMetadataProvider
{
    /// <summary>
    /// 「일반」 구역: 스케줄러 잡 = 사용·상태·작업 종류·설명, DBMS_JOB = 작업 번호·고장(BROKEN). 둘 다 다음 실행·실행 간격·실행 내용·실패 수·마지막 실행.
    /// 작업을 못 찾으면 빈 목록(다른 구역과 같다).
    /// </summary>
    private async Task<IReadOnlyList<DetailSection>> ReadJobGeneralAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        if (await ReadJobAsync(s, obj, ct).ConfigureAwait(false) is not { } r)
        {
            return [];
        }

        var dbmsJob = IsDbmsJob(r);
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "유형", JobType },
            new object?[] { "종류", dbmsJob ? "DBMS_JOB" : "DBMS_SCHEDULER" },
        };
        if (dbmsJob)
        {
            rows.Add(["작업 번호", Str(r[1])]);
            rows.Add(["고장(BROKEN)", Str(r[9])]);
        }
        else
        {
            rows.Add(["사용", Str(r[2])]);
            rows.Add(["상태", Str(r[3])]);
            rows.Add(["작업 종류", Str(r[10])]);
        }

        rows.Add(["다음 실행", Str(r[4])]);
        rows.Add(["실행 간격", Str(r[5])]);
        rows.Add(["실행 내용(WHAT)", Str(r[6])]);
        rows.Add(["실패 수", Str(r[7])]);
        rows.Add(["마지막 실행", Str(r[8])]);
        if (!dbmsJob && Str(r[11]) is { Length: > 0 } comments)
        {
            rows.Add(["설명", comments]);
        }

        return [new DetailSection("일반", ["항목", "값"], rows)];
    }

    /// <summary>
    /// DBMS_JOB → <see cref="TiberoDdlAssembler.DbmsJob"/> 블록. 스케줄러 잡은 만들지 않는다(시험 계정에서 DBMS_SCHEDULER.CREATE_JOB 이 ODP-14002 로 실패해
    /// 되살리는 문장을 실측하지 못했다) — 이유를 담은 <see cref="NotSupportedException"/>. 없으면 <see cref="InvalidOperationException"/>.
    /// </summary>
    private async Task<string> GetJobDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        var r = await ReadJobAsync(s, obj, ct).ConfigureAwait(false) ?? throw DdlNotFound(obj);
        return IsDbmsJob(r)
            ? TiberoDdlAssembler.DbmsJob(Str(r[6]) ?? string.Empty, Str(r[4]), Str(r[5]), string.Equals(Str(r[9]), "Y", StringComparison.Ordinal), o.SqlTerminator)
            : throw new NotSupportedException("Tibero 스케줄러 잡(DBMS_SCHEDULER)의 DDL 은 아직 만들지 않습니다. 속성은 「일반」 구역에서 볼 수 있습니다.");
    }

    /// <summary><c>meta.job_detail</c> 첫 줄(스케줄러 잡이 먼저). 없으면 null.</summary>
    private async Task<object?[]?> ReadJobAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        var rows = await RunAsync(s, "meta.job_detail", TiberoCatalogQuery.TableBinds(obj), ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private static bool IsDbmsJob(object?[] r) => string.Equals(Str(r[0]), "DBMS_JOB", StringComparison.Ordinal);
}
