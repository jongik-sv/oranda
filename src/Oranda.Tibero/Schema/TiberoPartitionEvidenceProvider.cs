using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// AI 파티션 추천의 Tibero 근거 공급자(설계 2026-11-18 5장): 조회 조건(V$SQL_PLAN·V$SQL)·날짜형 판정 표본·월별 건수·상위 값.
/// Oracle 공급자와 같은 뼈대(<see cref="CatalogPartitionEvidenceProvider"/>)에 Tibero 카탈로그 <c>advisor.*</c> 를 꽂는다.
/// V$ 를 볼 권한이 없으면 서버 오류를 그대로 던진다 — 모으기가 「권한 없음」으로 적고 계속한다.
/// </summary>
[OrandaService(typeof(IPartitionEvidenceProvider), Dbms = Dbms.Tibero)]
internal sealed class TiberoPartitionEvidenceProvider : CatalogPartitionEvidenceProvider
{
    protected override async Task<IReadOnlyList<object?[]>> QueryAsync(
        ISession s, string id, IReadOnlyDictionary<string, string> replacements, IReadOnlyDictionary<string, object?> binds, CancellationToken ct)
    {
        var table = await TiberoCatalogQuery.RunAsync(s, id, c =>
        {
            foreach (var (name, value) in binds)
            {
                TiberoSessionQuery.Bind(c, name, value);
            }
        }, replacements, ct).ConfigureAwait(false);
        return table.Rows;
    }
}
