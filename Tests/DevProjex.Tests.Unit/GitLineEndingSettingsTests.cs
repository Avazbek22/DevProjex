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
			["config", "-z", "--get-regexp", "^core\\.(autocrlf|eol)$"],
			Arguments(GitProcessOperation.ReadConfigValue(GitConfigReadKind.RepositoryLineEndings)));
		Assert.Equal(
			["--system", "-z", "--get-regexp", "^core\\.(autocrlf|eol)$"],
			Arguments(GitProcessOperation.ReadConfigValue(GitConfigReadKind.SystemLineEndings)));
		Assert.Equal(
			[file, "-z", "--get-regexp", "^core\\.(autocrlf|eol)$"],
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

	[Theory]
	[InlineData("true\n")]
	[InlineData("true\r\n")]
	[InlineData("true\0")]
	[InlineData("input -c core.fsmonitor=hostile")]
	[InlineData("true\ncore.eol crlf")]
	public void NormalizationNeverAcceptsControlCharactersOrAnotherConfigurationEntry(string value)
	{
		Assert.Empty(GitLineEndingSettings.Normalize(new Dictionary<string, string>
		{
			["core.autocrlf"] = value,
			["core.eol"] = value
		}));
	}

	[Fact]
	public void WorkingChangesCaptureValidatedSettingsWithoutRetainingTheCallersMutableList()
	{
		var settings = new List<string> { "core.autocrlf=input", "core.eol=native" };
		var operation = GitProcessOperation.ReadWorkingChanges(settings);
		settings[0] = "core.fsmonitor=hostile";
		settings.Add("core.askPass=hostile");
		Assert.Equal(["core.autocrlf=input", "core.eol=native"], operation.LineEndingSettings);
		var start = GitScopePathProvider.CreateStartInfo(Path.GetFullPath("repository"), operation);
		Assert.DoesNotContain("core.fsmonitor=hostile", start.ArgumentList);
		Assert.DoesNotContain("core.askPass=hostile", start.ArgumentList);
		Assert.Equal("1", start.Environment["GIT_NO_LAZY_FETCH"]);
	}

	[Theory]
	[InlineData("2", "true")]
	[InlineData("-1", "true")]
	[InlineData("+2", "true")]
	[InlineData("0x1", "true")]
	[InlineData("017", "true")]
	[InlineData("1K", "true")]
	[InlineData("-2m", "true")]
	[InlineData("1g", "true")]
	[InlineData("2147483647", "true")]
	[InlineData("-2147483647", "true")]
	[InlineData("0x7fffffff", "true")]
	[InlineData("017777777777", "true")]
	[InlineData("2097151k", "true")]
	[InlineData("0x0", "false")]
	[InlineData("-0k", "false")]
	[InlineData("000", "false")]
	public void NumericAutoCrlfIsCanonicalizedBeforeItCanReachGit(string value, string expected) =>
		Assert.Equal(["core.autocrlf=" + expected], GitLineEndingSettings.Normalize(
			new Dictionary<string, string> { ["core.autocrlf"] = value }));

	[Theory]
	[InlineData("2147483648")]
	[InlineData("-2147483648")]
	[InlineData("0x80000000")]
	[InlineData("020000000000")]
	[InlineData("2097152k")]
	[InlineData("2g")]
	[InlineData("08")]
	[InlineData("0b1")]
	[InlineData("1kb")]
	[InlineData("k")]
	[InlineData("0x")]
	[InlineData("+")]
	[InlineData("--1")]
	[InlineData("2\ncore.fsmonitor=hostile")]
	[InlineData("٢")]
	public void InvalidNumericAutoCrlfIsNeverForwarded(string value) =>
		Assert.Empty(GitLineEndingSettings.Normalize(new Dictionary<string, string> { ["core.autocrlf"] = value }));

	[Theory]
	[InlineData(null, true)]
	[InlineData("", true)]
	[InlineData("false", true)]
	[InlineData("0", true)]
	[InlineData("OFF", true)]
	[InlineData("0x0", true)]
	[InlineData("true", false)]
	[InlineData("yes", false)]
	[InlineData("ON", false)]
	[InlineData("1", false)]
	[InlineData("2", false)]
	[InlineData("-1", false)]
	[InlineData("0x1", false)]
	public void SystemConfigurationOptOutUsesGitBooleanSemantics(string? noSystem, bool expected) =>
		Assert.Equal(expected, GitLineEndingSettings.ReadsSystemConfiguration(noSystem));
}
