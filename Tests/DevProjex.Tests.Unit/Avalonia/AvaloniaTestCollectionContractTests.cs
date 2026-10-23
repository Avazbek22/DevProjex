namespace DevProjex.Tests.Unit.Avalonia;

/// <summary>
/// The headless session resets the UI-thread dispatcher after every isolated test, and the next thread that
/// creates any Avalonia object becomes the UI thread. A parallel test doing that while the session sets up the
/// next test makes that setup fail with "The calling thread cannot access this object". Classes whose tests run
/// in the session therefore belong to the AvaloniaUI collection, which never overlaps parallel collections.
/// </summary>
public sealed class AvaloniaTestCollectionContractTests
{
	private const BindingFlags DeclaredMethods =
		BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	[Fact]
	public void TheAvaloniaCollectionNeverRunsInParallelWithOtherTests()
	{
		var definition = typeof(AvaloniaUiCollection).GetCustomAttributesData()
			.Single(static data => data.AttributeType == typeof(CollectionDefinitionAttribute));

		Assert.Equal("AvaloniaUI", definition.ConstructorArguments.Single().Value);
		Assert.Contains(
			definition.NamedArguments,
			static argument => argument.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization) &&
				Equals(argument.TypedValue.Value, true));
	}

	[Fact]
	public void EveryClassWithHeadlessTestsRunsInTheAvaloniaCollection()
	{
		var headlessClasses = typeof(AvaloniaUiCollection).Assembly.GetTypes()
			.Where(static type => type.GetMethods(DeclaredMethods).Any(IsHeadlessTest))
			.ToArray();

		Assert.NotEmpty(headlessClasses);
		Assert.Empty(headlessClasses
			.Where(static type => CollectionName(type) != "AvaloniaUI")
			.Select(static type => type.FullName)
			.Order(StringComparer.Ordinal));
	}

	private static bool IsHeadlessTest(MethodInfo method) =>
		method.IsDefined(typeof(AvaloniaFactAttribute), inherit: false) ||
		method.IsDefined(typeof(AvaloniaTheoryAttribute), inherit: false);

	private static string? CollectionName(Type type) => type.GetCustomAttributesData()
		.Where(static data => data.AttributeType == typeof(CollectionAttribute))
		.Select(static data => data.ConstructorArguments.FirstOrDefault().Value as string)
		.SingleOrDefault();
}
