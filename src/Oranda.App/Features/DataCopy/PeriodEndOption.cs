
namespace Oranda.App.Features.DataCopy;

/// <summary>직접 지정 기간의 끝 날짜 셈. <paramref name="Inclusive"/> 면 끝 날짜까지 들어간다.</summary>
public sealed record PeriodEndOption(bool Inclusive, string Display);
