using DevProjex.Application.Presentation;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Builds the expected ignore-panel order from the catalog instead of naming every row by hand.
///
/// Content transformations sit between the primary exclusions and the Git modes. Spelling them out
/// in each test made adding one a sweep of roughly forty assertions, which is how a second
/// transformation ended up inheriting the first one's label unnoticed.
/// </summary>
internal static class IgnoreOptionOrder
{
	public static IReadOnlyList<IgnoreOptionId> Expected(
		IReadOnlyList<IgnoreOptionId> primary,
		IReadOnlyList<IgnoreOptionId> rest) =>
		[.. primary, .. ContentTransformations, .. rest];

	public static IReadOnlyList<IgnoreOptionId> ContentTransformations { get; } =
		ProjectPresentationCatalog.ContentTransformations
			.OrderBy(static descriptor => descriptor.Order)
			.Select(static descriptor => descriptor.LegacyOptionId)
			.ToArray();

	public static int Count => ContentTransformations.Count;
}
