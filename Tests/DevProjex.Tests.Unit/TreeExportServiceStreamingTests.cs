namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceStreamingTests
{
	[Theory]
	[InlineData(TreeTextFormat.Ascii, true)]
	[InlineData(TreeTextFormat.Ascii, false)]
	[InlineData(TreeTextFormat.Markdown, true)]
	[InlineData(TreeTextFormat.Markdown, false)]
	[InlineData(TreeTextFormat.Json, true)]
	[InlineData(TreeTextFormat.Json, false)]
	[InlineData(TreeTextFormat.Xml, true)]
	[InlineData(TreeTextFormat.Xml, false)]
	public async Task WriteFullTreeAsync_PreservesExistingStringContract(
		TreeTextFormat format,
		bool includeRootPath)
	{
		var (rootPath, root) = CreateTree();
		var service = new TreeExportService();
		var expected = service.BuildFullTree(
			rootPath,
			root,
			format,
			displayRootPath: "https://example.test/repository",
			displayRootName: "display repository",
			includeRootPath);
		using var destination = new StringWriter();

		await service.WriteFullTreeAsync(
			destination,
			rootPath,
			root,
			format,
			displayRootPath: "https://example.test/repository",
			displayRootName: "display repository",
			includeRootPath,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(expected, destination.ToString());
	}

	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Markdown)]
	[InlineData(TreeTextFormat.Json)]
	[InlineData(TreeTextFormat.Xml)]
	public async Task WriteFullTreeAsync_WritesLargeTreeIncrementally(TreeTextFormat format)
	{
		const int childCount = 20_000;
		var rootPath = Path.Combine(Path.GetTempPath(), "streaming-tree");
		var children = Enumerable.Range(0, childCount)
			.Select(index => CreateFile(rootPath, $"file-{index:D6}-{new string('x', 48)}.txt"))
			.ToArray();
		var root = new TreeNodeDescriptor(
			"streaming-tree",
			rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: children);
		var destination = new CountingTextWriter();

		await new TreeExportService().WriteFullTreeAsync(
			destination,
			rootPath,
			root,
			format,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(destination.WriteCount > 1);
		Assert.True(destination.TotalCharacters > 1_000_000);
		Assert.InRange(destination.MaximumWriteCharacters, 1, 32 * 1024);
	}

	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Markdown)]
	[InlineData(TreeTextFormat.Json)]
	[InlineData(TreeTextFormat.Xml)]
	public async Task WriteFullTreeAsync_StopsWhenDestinationCancels(TreeTextFormat format)
	{
		var (rootPath, root) = CreateTree();
		using var cancellation = new CancellationTokenSource();
		var destination = new CancelAfterFirstWriteTextWriter(cancellation);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			new TreeExportService().WriteFullTreeAsync(
				destination,
				rootPath,
				root,
				format,
				cancellationToken: cancellation.Token));

		Assert.Equal(1, destination.CompletedWrites);
	}

	private static (string RootPath, TreeNodeDescriptor Root) CreateTree()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "project root");
		var sourcePath = Path.Combine(rootPath, "src");
		var source = new TreeNodeDescriptor(
			"src",
			sourcePath,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children:
			[
				CreateFile(sourcePath, "App \u044E\u043D\u0438\u043A\u043E\u0434.cs"),
				CreateFile(sourcePath, "list-item.md")
			]);
		var root = new TreeNodeDescriptor(
			"project root",
			rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children:
			[
				source,
				CreateFile(rootPath, "README.md")
			]);
		return (rootPath, root);
	}

	private static TreeNodeDescriptor CreateFile(string parentPath, string name) =>
		new(
			name,
			Path.Combine(parentPath, name),
			IsDirectory: false,
			IsAccessDenied: false,
			IconKey: "file",
			Children: []);

	private class CountingTextWriter : TextWriter
	{
		public override Encoding Encoding => Encoding.UTF8;
		public int WriteCount { get; private set; }
		public long TotalCharacters { get; private set; }
		public int MaximumWriteCharacters { get; private set; }

		public override void Write(char value) => Observe(1);
		public override void Write(string? value) => Observe(value?.Length ?? 0);
		public override void Write(char[] buffer, int index, int count) => Observe(count);
		public override void Write(ReadOnlySpan<char> buffer) => Observe(buffer.Length);

		public override Task WriteAsync(
			char[] buffer,
			int index,
			int count) =>
			ObserveAsync(count);

		public override Task WriteAsync(string? value) =>
			ObserveAsync(value?.Length ?? 0);

		public override Task WriteAsync(
			ReadOnlyMemory<char> buffer,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Observe(buffer.Length);
			return Task.CompletedTask;
		}

		protected virtual void Observe(int count)
		{
			if (count <= 0)
				return;

			WriteCount++;
			TotalCharacters += count;
			MaximumWriteCharacters = Math.Max(MaximumWriteCharacters, count);
		}

		private Task ObserveAsync(int count)
		{
			Observe(count);
			return Task.CompletedTask;
		}
	}

	private sealed class CancelAfterFirstWriteTextWriter(
		CancellationTokenSource cancellation) : CountingTextWriter
	{
		public int CompletedWrites { get; private set; }

		protected override void Observe(int count)
		{
			cancellation.Token.ThrowIfCancellationRequested();
			base.Observe(count);
			CompletedWrites++;
			cancellation.Cancel();
		}
	}
}
