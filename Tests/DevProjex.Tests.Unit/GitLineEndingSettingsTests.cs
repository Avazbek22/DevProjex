using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Unit;

public sealed class GitLineEndingSettingsTests
{
	[Fact]
	public void WorkingChangesPassLineEndingSettingsAsConfigurationBeforeTheDiff()
	{
		var repositoryRoot = Path.GetFullPath("repository");
		var plain = GitScopePathProvider.CreateStartInfo(
			repositoryRoot,
			GitProcessOperation.ReadWorkingChanges()).ArgumentList.ToArray();
		var converted = GitScopePathProvider.CreateStartInfo(
			repositoryRoot,
			GitProcessOperation.ReadWorkingChanges(["core.autocrlf=true", "core.eol=lf"])).ArgumentList.ToArray();

		Assert.DoesNotContain(plain, static argument => argument.StartsWith("core.autocrlf", StringComparison.Ordinal));
		var autoCrlf = Array.IndexOf(converted, "core.autocrlf=true");
		var eol = Array.IndexOf(converted, "core.eol=lf");
		Assert.True(autoCrlf > 0);
		Assert.Equal("-c", converted[autoCrlf - 1]);
		Assert.Equal("-c", converted[eol - 1]);
		Assert.True(Array.IndexOf(converted, "diff") > eol);
		Assert.Equal(plain.Length + 4, converted.Length);
	}

	[Theory]
	[InlineData("core.fsmonitor=true")]
	[InlineData("core.autocrlf=maybe")]
	[InlineData("core.autocrlf=true -c core.fsmonitor=true")]
	[InlineData("core.eol=")]
	public void WorkingChangesRejectAnythingButALineEndingSetting(string setting) =>
		Assert.Throws<ArgumentException>(() => GitProcessOperation.ReadWorkingChanges([setting]));

	[Fact]
	public void LineEndingReadsUseFixedQueriesAndAnAbsoluteFile()
	{
		var repositoryRoot = Path.GetFullPath("repository");
		var file = Path.GetFullPath("gitconfig");
		string[] Arguments(GitProcessOperation operation) =>
			GitScopePathProvider.CreateStartInfo(repositoryRoot, operation).ArgumentList.ToArray()[^4..];

		Assert.Equal(
			["config", "--get-regexp", "^core\\.(autocrlf|eol)$"],
			Arguments(GitProcessOperation.ReadConfigValue(GitConfigReadKind.RepositoryLineEndings))[1..]);
		Assert.Equal(
			["config", "--system", "--get-regexp", "^core\\.(autocrlf|eol)$"],
			Arguments(GitProcessOperation.ReadConfigValue(GitConfigReadKind.SystemLineEndings)));
		Assert.Equal(
			["--file", file, "--get-regexp", "^core\\.(autocrlf|eol)$"],
			Arguments(GitProcessOperation.ReadLineEndingsFromFile(file)));
		Assert.Throws<ArgumentException>(() => GitProcessOperation.ReadConfigValue(GitConfigReadKind.FileLineEndings));
		Assert.Throws<ArgumentException>(() => GitProcessOperation.ReadLineEndingsFromFile("relative/gitconfig"));
	}

	[Theory]
	[InlineData("true", "core.autocrlf=true")]
	[InlineData("Yes", "core.autocrlf=true")]
	[InlineData("1", "core.autocrlf=true")]
	[InlineData("off", "core.autocrlf=false")]
	[InlineData("", "core.autocrlf=false")]
	[InlineData("INPUT", "core.autocrlf=input")]
	public void AutoCrlfAcceptsEveryBooleanSpellingGitAccepts(string value, string expected) =>
		Assert.Equal([expected], GitLineEndingSettings.Normalize(new Dictionary<string, string> { ["core.autocrlf"] = value }));

	[Fact]
	public void UnknownValuesAreDroppedInsteadOfGuessed()
	{
		var settings = GitLineEndingSettings.Normalize(new Dictionary<string, string>
		{
			["core.autocrlf"] = "sometimes",
			["core.eol"] = "CRLF"
		});

		Assert.Equal(["core.eol=crlf"], settings);
	}
}
