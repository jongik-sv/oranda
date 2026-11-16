using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>기간 종류 고르기 목록의 한 줄.</summary>
public sealed record PeriodKindOption(PeriodKind Kind, string Display);
