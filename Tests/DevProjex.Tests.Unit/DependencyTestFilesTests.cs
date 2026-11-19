using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyTestFilesTests
{
	[Theory]
	[InlineData("src/request.ts", "src/request.test.ts")]
	[InlineData("src/request.ts", "src/request.spec.ts")]
	[InlineData("src/request.ts", "src/__tests__/request.tsx")]
	[InlineData("pkg/client.py", "pkg/test_client.py")]
	[InlineData("httpx/_config.py", "tests/test_config.py")]
	[InlineData("httpx/_config.py", "tests/unit/test__config.py")]
	[InlineData("server/router.go", "server/router_test.go")]
	[InlineData("src/App/Foo.cs", "test/App.Tests/FooTests.cs")]
	[InlineData("src/main/java/a/Foo.java", "src/test/java/a/FooTest.java")]
	public void NamingConventionsFindTheTestFile(string source, string test)
	{
		var found = DependencyTestFiles.Find(source, [source, test, "src/other.ts"], index: null, maximum: 5);
		Assert.Equal([test], found);
	}

	[Theory]
	[InlineData("src/request.ts", "src/Request.test.ts")]
	[InlineData("src/request.ts", "lib/request.test.ts")]
	[InlineData("src/Foo.cs", "test/FooTests.java")]
	[InlineData("pkg/client.py", "other/test_client.py")]
	public void NearMissesAreNotTests(string source, string candidate)
	{
		Assert.Empty(DependencyTestFiles.Find(source, [source, candidate], index: null, maximum: 5));
	}

	[Theory]
	[InlineData("src/request.test.ts")]
	[InlineData("tests/helpers.py")]
	[InlineData("test/X.Tests/FooTests.cs")]
	[InlineData("src/__tests__/b.ts")]
	[InlineData("spec/models/user.rb")]
	public void ATestFileHasNoTestsOfItsOwn(string source)
	{
		Assert.True(DependencyTestFiles.IsTestPath(source));
		Assert.Empty(DependencyTestFiles.Find(source, [source, "src/request.test.test.ts"], index: null, maximum: 5));
	}

	[Fact]
	public void ResultsAreOrdinalAndCapped()
	{
		string[] files =
		[
			"src/Foo.cs",
			"e/FooTests.cs",
			"d/FooTests.cs",
			"c/FooTests.cs",
			"b/FooTests.cs",
			"a/FooTests.cs",
			"F/FooTests.cs"
		];
		Assert.Equal(
			["F/FooTests.cs", "a/FooTests.cs", "b/FooTests.cs", "c/FooTests.cs", "d/FooTests.cs"],
			DependencyTestFiles.Find("src/Foo.cs", files, index: null, maximum: 5));
	}
}
