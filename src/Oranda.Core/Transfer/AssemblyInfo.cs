using System.Runtime.CompilerServices;

// 내보내기 쓰기기(TransferWriters)·정의서 쓰기기는 internal 이다 — DB 없는 단위 시험과 Oracle 통합 시험이 내용을 직접 본다.
[assembly: InternalsVisibleTo("Oranda.Core.Tests")]
[assembly: InternalsVisibleTo("Oranda.Oracle.IntegrationTests")]
