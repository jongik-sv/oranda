using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>
/// AI 파티션 추천의 Oracle 근거 공급자(설계 2026-11-18 5장): 조회 조건(V$SQL_PLAN·V$SQL)·날짜형 판정 표본·월별 건수·상위 값.
/// 문장은 카탈로그 <c>advisor.*</c> 에만 있고, 열 이름 판정 같은 해석은 Core 의 <see cref="EvidenceProviderRules"/> 가 한다.
/// V$ 를 볼 권한이 없으면 <see cref="OracleException"/>(ORA-00942·ORA-01031)을 그대로 던진다 — 모으기가 「권한 없음」으로 적고 계속한다.
/// 11g 에서도 같은 문장이 돈다(분석 함수·ROWNUM·SAMPLE 만 씀).
/// </summary>
[OrandaService(typeof(IPartitionEvidenceProvider), Dbms = Dbms.Oracle)]
internal sealed class OraclePartitionEvidenceProvider : CatalogPartitionEvidenceProvider
{
    protected override async Task<IReadOnlyList<object?[]>> QueryAsync(
        ISession s, string id, IReadOnlyDictionary<string, string> replacements, IReadOnlyDictionary<string, object?> binds, CancellationToken ct)
    {
        var table = await SchemaQuery.RunAsync(s, id, replacements, c =>
        {
            foreach (var (name, value) in binds)
            {
                c.Parameters.Add(new OracleParameter(name, OracleDbType.Varchar2) { Value = value });
            }
        }, ct).ConfigureAwait(false);
        return table.Rows;
    }
}
