using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Unit;

public sealed class GitFileVersionOperationTests
{
	private const string Commit = "0123456789abcdef0123456789abcdef01234567";

	[Fact]
	public void TreeEntriesArePinnedToLiteralPathsAfterTheSeparator()
	{
		var arguments = Arguments(GitProcessOperation.ReadTreeEntries(Commit, ["src/a.cs", "-dash.txt"]));

		Assert.Equal(
			["--literal-pathspecs", "ls-tree", "-r", "-z", "--full-tree", Commit, "--", "src/a.cs", "-dash.txt"],
			arguments[^9..]);
		Assert.True(Array.IndexOf(arguments, "-dash.txt") > Array.IndexOf(arguments, "--"));
	}

	[Fact]
	public void IndexEntriesArePinnedToLiteralPathsAfterTheSeparator()
	{
		var arguments = Arguments(GitProcessOperation.ReadIndexEntries(["a b.txt", "-x"]));

		Assert.Equal(["--literal-pathspecs", "ls-files", "--stage", "-z", "--", "a b.txt", "-x"], arguments[^7..]);
	}

	[Fact]
	public void ObjectReadsTakeNoOperandAndNeverConvertContent()
	{
		var headers = Arguments(GitProcessOperation.ReadObjectHeaders());
		var contents = Arguments(GitProcessOperation.ReadObjectContents());

		Assert.Equal(["cat-file", "--batch-check"], headers[^2..]);
		Assert.Equal(["cat-file", "--batch"], contents[^2..]);
		foreach (var arguments in new[] { headers, contents })
		{
			Assert.DoesNotContain("--textconv", arguments);
			Assert.DoesNotContain("--filters", arguments);
		}
	}

	[Fact]
	public void FileVersionReadsUseTheLocalReadProfile()
	{
		foreach (var operation in new[]
				 {
					 GitProcessOperation.ReadTreeEntries(Commit, ["a.txt"]),
					 GitProcessOperation.ReadIndexEntries(["a.txt"]),
					 GitProcessOperation.ReadObjectHeaders(),
					 GitProcessOperation.ReadObjectContents()
				 })
		{
			Assert.Equal(GitProcessProfile.LocalRead, operation.Profile);
			var startInfo = GitProcessStartInfoFactory.Create(Path.GetFullPath("repository"), operation);
			Assert.Equal("--no-pager", startInfo.ArgumentList[0]);
			Assert.Contains("core.fsmonitor=false", startInfo.ArgumentList);
			Assert.Contains("protocol.allow=never", startInfo.ArgumentList);
			Assert.Equal("1", startInfo.Environment["GIT_NO_LAZY_FETCH"]);
			Assert.Equal("", startInfo.Environment["GIT_ALLOW_PROTOCOL"]);
			Assert.True(startInfo.RedirectStandardInput);
		}
	}

	[Theory]
	[InlineData("HEAD")]
	[InlineData("-0123456789abcdef0123456789abcdef0123456")]
	[InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
	[InlineData("0123456789abcdef")]
	[InlineData("0123456789abcdef0123456789abcdef0123456g")]
	[InlineData("0123456789abcdef0123456789abcdef01234567\n")]
	[InlineData("")]
	public void ObjectIdsMustBeFullLowercaseHex(string objectId)
	{
		Assert.False(GitProcessOperation.IsObjectId(objectId));
		Assert.Throws<ArgumentException>(() => GitProcessOperation.ValidateObjectId(objectId));
		Assert.Throws<ArgumentException>(() => GitProcessOperation.ReadTreeEntries(objectId, ["a.txt"]));
	}

	[Fact]
	public void Sha256ObjectIdsAreAccepted()
	{
		var objectId = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));

		Assert.Equal(objectId, GitProcessOperation.ValidateObjectId(objectId));
	}

	[Theory]
	[InlineData("line\nbreak.txt")]
	[InlineData("carriage\rreturn.txt")]
	[InlineData("nul\0.txt")]
	[InlineData("../outside.txt")]
	[InlineData("src/../outside.txt")]
	[InlineData("./a.txt")]
	[InlineData("/absolute.txt")]
	[InlineData("trailing/")]
	[InlineData("double//slash.txt")]
	[InlineData("back\\slash.txt")]
	[InlineData("")]
	public void UnsafeRepositoryPathsAreRefused(string path)
	{
		Assert.False(GitProcessOperation.IsSafeRepositoryPath(path));
		Assert.Throws<ArgumentException>(() => GitProcessOperation.ReadTreeEntries(Commit, [path]));
		Assert.Throws<ArgumentException>(() => GitProcessOperation.ReadIndexEntries([path]));
	}

	[Fact]
	public void PathListsMustBeNonEmptyAndBounded()
	{
		Assert.Throws<ArgumentException>(() => GitProcessOperation.ReadIndexEntries([]));
		var paths = Enumerable.Range(0, GitProcessOperation.MaximumPathArgumentCharacters / 10 + 1)
			.Select(index => $"f{index:D8}")
			.ToArray();
		Assert.Throws<ArgumentException>(() => GitProcessOperation.ReadIndexEntries(paths));
	}

	private static string[] Arguments(GitProcessOperation operation) =>
		GitProcessStartInfoFactory.Create(Path.GetFullPath("repository"), operation).ArgumentList.ToArray();
}
