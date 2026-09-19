using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyGoNavigationIntegrationTests
{
	[Fact]
	public async Task PackageVariablesAndConstantsProducePreciseNavigationDeclarations()
	{
		using var fixture = new TemporaryDirectory();
		const string text = """
			package sample
			var Single = 1
			var First, Second = 2, 3
			var (
				Grouped = 4
				Multiline = map[string]int{
					"answer": 42,
				}
			)
			const Constant = 5
			const (
				Zero = iota
				One
			)
			func shadow() {
				Single := 6
				const Constant = 7
				_, _ = Single, Constant
			}
			""";
		var source = fixture.CreateFile("sample.go", text);
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);
		var facts = Assert.Single(index.Files);
		var fields = facts.NavigationDeclarations
			.Where(static declaration => declaration.Kind == NavigationSymbolKind.Field)
			.ToArray();

		Assert.Equal(new[] { "Single", "First", "Second", "Grouped", "Multiline", "Constant", "Zero", "One" },
			fields.Select(static declaration => declaration.Name));
		Assert.Equal((3, 3), RangeOf(fields, "First"));
		Assert.Equal((3, 3), RangeOf(fields, "Second"));
		Assert.Equal((6, 8), RangeOf(fields, "Multiline"));
		Assert.DoesNotContain(fields, static declaration => declaration.StartLine >= 15);

		using var extractor = new TreeSitterDependencyFactExtractor();
		Assert.Equal(
			facts.NavigationDeclarations,
			extractor.ExtractNavigation("sample.go", text, facts.ContentFingerprint, TestContext.Current.CancellationToken));
	}

	private static (int StartLine, int EndLine) RangeOf(
		IReadOnlyList<NavigationDeclaration> declarations,
		string name)
	{
		var declaration = Assert.Single(declarations, item => item.Name == name);
		return (declaration.StartLine, declaration.EndLine);
	}

	private static DependencyFactsEngine CreateEngine() =>
		new(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());
}
