using System.Text;
using System.Text.RegularExpressions;
using DevProjex.Application.Dependencies;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpLargeStructureMembersTests
{
	[Theory]
	[InlineData(59, false)]
	[InlineData(60, true)]
	public void CompactStructuresExpandOnlyParentsAtTheLineThreshold(int length, bool expanded)
	{
		var declarations = Parents([length, 40, 40], 15);
		var map = Build(declarations);
		Assert.Equal(expanded, map.Contains("  2-2 M0 (method)", StringComparison.Ordinal));
		Assert.Equal(3, Lines(map).Count(static line => !line.StartsWith(' ')));
		Assert.Contains($"1-{length} Root0 (type, 15 members)\n", map, StringComparison.Ordinal);
		Assert.DoesNotContain("  " + declarations[17].StartLine + "-", map, StringComparison.Ordinal);
	}

	[Fact]
	public void EntryBudgetGoesToTheLongestParentBeforeEarlierShorterParents()
	{
		var declarations = Parents([100, 200, 100], 80);
		var map = Build(declarations);
		Assert.Contains("Root0 (type, 80 members)\n101-300 Root1", map, StringComparison.Ordinal);
		Assert.DoesNotContain("  2-2 M0", map, StringComparison.Ordinal);
		Assert.Contains("  102-102 M0 (method)\n", map, StringComparison.Ordinal);
		Assert.Contains("  (+3 more)\n301-400 Root2", map, StringComparison.Ordinal);
		Assert.Equal(80, Regex.Matches(map, @"(?m)^ *\d+-\d+ ").Count);
		Assert.InRange(map.Length, 1, 2_500);
		Assert.Equal(map, Build(declarations));
	}

	[Fact]
	public void EqualSpansPreferTheEarlierParentAndKeepAllTopLevelLines()
	{
		var declarations = Parents([100, 100, 100], 80);
		var map = Build(declarations);
		Assert.Contains("  2-2 M0 (method)", map, StringComparison.Ordinal);
		Assert.DoesNotContain("  102-102 M0", map, StringComparison.Ordinal);
		Assert.Equal(new[] { "1-100 Root0 (type, 80 members)", "101-200 Root1 (type, 80 members)", "201-300 Root2 (type, 80 members)" },
			Lines(map).Where(static line => !line.StartsWith(' ')));
	}

	[Fact]
	public void CharacterBudgetKeepsAPartialMemberListAndItsExactRemainingCount()
	{
		var declarations = Parents([100, 100, 100], 20)
			.Select(declaration => declaration.Kind == NavigationSymbolKind.Method
				? declaration with { Name = declaration.Name + new string('x', 150) } : declaration).ToArray();
		var map = Build(declarations);
		var printed = Lines(map).Count(static line => line.StartsWith("  ", StringComparison.Ordinal) && !line.Contains("more)", StringComparison.Ordinal));
		Assert.InRange(printed, 1, 19);
		Assert.Contains($"  (+{20 - printed} more)\n", map, StringComparison.Ordinal);
		Assert.Equal(3, Lines(map).Count(static line => !line.StartsWith(' ')));
		Assert.InRange(map.Length, 1, 2_500);
	}

	[Fact]
	public void AParentWhoseFirstMemberCannotFitKeepsOnlyItsCount()
	{
		var declarations = Parents([200, 100, 80], 15)
			.Select(declaration => declaration.Owner == "Root1"
				? declaration with { Name = declaration.Name + new string('x', 2_400) } : declaration).ToArray();
		var map = Build(declarations);
		var expected = new StringBuilder("1-200 Root0 (type, 15 members)\n");
		for (var member = 0; member < 15; member++)
			expected.Append($"  {member + 2}-{member + 2} M{member} (method)\n");
		expected.Append("201-300 Root1 (type, 15 members)\n301-380 Root2 (type, 15 members)\n");
		Assert.Equal(expected.ToString(), map);
		Assert.DoesNotContain("more)", map, StringComparison.Ordinal);
	}

	[Fact]
	public void ATruncatedTopLevelNeverGetsMembers()
	{
		var map = Build(Parents(Enumerable.Repeat(60, 90).ToArray(), 1));
		Assert.Contains("more)\n", map, StringComparison.Ordinal);
		Assert.DoesNotContain("\n  ", map, StringComparison.Ordinal);
		Assert.InRange(map.Length, 1, 2_500);
		Assert.InRange(Regex.Matches(map, @"(?m)^\d+-\d+ ").Count, 1, 80);
	}

	[Fact]
	public void SmallStructuresKeepTheirExactOriginalMap()
	{
		Assert.Equal("1-60 Root0 (type)\n  2-2 M0 (method)\n61-120 Root1 (type)\n  62-62 M0 (method)\n121-180 Root2 (type)\n  122-122 M0 (method)\n",
			Build(Parents([60, 60, 60], 1)));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public void OneOrTwoParentsKeepTheirExactOriginalCompactMap(int count)
	{
		var expected = new StringBuilder();
		for (var parent = 0; parent < count; parent++)
		{
			var start = parent * 100 + 1;
			expected.Append($"{start}-{start + 99} Root{parent} (type, 41 members)\n");
			for (var member = 0; member < 41; member++)
				expected.Append($"  {start + member + 1}-{start + member + 1} M{member} (method)\n");
		}
		var actual = Build(Parents(Enumerable.Repeat(100, count).ToArray(), 41));
		if (count == 1) Assert.Equal(expected.ToString(), actual);
		else
		{
			var expectedLines = Lines(expected.ToString()).Take(80);
			Assert.Equal(string.Join('\n', expectedLines) + "\n(+4 more)\n", actual);
		}
	}

	[Fact]
	public void MarkdownAndTestBlocksUseTheSameMemberExpansion()
	{
		var markdown = new StringBuilder();
		var tests = new StringBuilder();
		for (var parent = 0; parent < 3; parent++)
		{
			markdown.Append($"# Part{parent}\n");
			tests.Append($"describe('Part{parent}', () => {{\n");
			for (var member = 0; member < 15; member++)
			{
				markdown.Append($"## Section{member}\ntext\ntext\ntext\n");
				tests.Append($"it('Case{member}', () => {{\nlet value = 1;\nvalue++;\n}});\n");
			}
			tests.Append("});\n");
		}
		Assert.Contains(" Section0\n", Build([], "book.md", markdown.ToString()), StringComparison.Ordinal);
		var map = Build([], "cases.ts", tests.ToString());
		Assert.Contains("\"Part0\" (describe, 15 members)", map, StringComparison.Ordinal);
		Assert.Contains("  2-5 \"Case0\" (test)", map, StringComparison.Ordinal);
	}

	private static IReadOnlyList<string> Lines(string map) => map.Split('\n', StringSplitOptions.RemoveEmptyEntries);

	private static string Build(IReadOnlyList<NavigationDeclaration> declarations, string path = "sample.cs", string? content = null) =>
		Assert.IsType<McpLargeFileOutline>(McpLargeFileOutlineBuilder.Build(path, content ?? new string('\n', 6_000),
			() => declarations, TestContext.Current.CancellationToken, requireLargeFile: false)).Map;

	private static NavigationDeclaration[] Parents(int[] lengths, int members)
	{
		var declarations = new List<NavigationDeclaration>();
		var start = 1;
		for (var index = 0; index < lengths.Length; index++)
		{
			declarations.Add(new($"Root{index}", NavigationSymbolKind.Type, null, start, start + lengths[index] - 1, "fixture"));
			for (var member = 0; member < members; member++)
				declarations.Add(new($"Root{index}.M{member}", NavigationSymbolKind.Method, $"Root{index}", start + member + 1, start + member + 1, "fixture"));
			start += lengths[index];
		}
		return declarations.ToArray();
	}
}
