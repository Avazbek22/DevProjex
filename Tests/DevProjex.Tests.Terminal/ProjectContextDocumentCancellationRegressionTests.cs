namespace DevProjex.Tests.Terminal;

public sealed class ProjectContextDocumentCancellationRegressionTests
{
	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextDocumentFormat.Markdown)]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task CancellationWaitsForUnderlyingWriteToStop(
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/source.txt", new string('x', 256 * 1024));
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		await using var destination = new BlockingWriteStream();
		using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);

		var writeTask = services.ContextDocumentService.WriteCompleteAsync(
			plan,
			ProjectContextView.Content,
			format,
			destination,
			cancellationSource.Token);
		await destination.WriteStarted.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		cancellationSource.Cancel();

		var completed = await Task.WhenAny(
			writeTask,
			Task.Delay(
				TimeSpan.FromSeconds(2),
				TestContext.Current.CancellationToken));
		if (completed != writeTask)
		{
			destination.Release();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				async () => await writeTask);
			Assert.Fail(
				$"{format} cancellation did not stop the pending destination write.");
		}

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () => await writeTask);
		Assert.Equal(0, destination.ActiveWrites);
	}

	[Fact]
	public async Task LargeXmlTreeCancellationDoesNotEnterAnUninterruptibleSynchronousWrite()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 512; index++)
		{
			workspace.WriteFile(
				$"project/tree/file-{index:D4}-{new string('x', 80)}.txt",
				string.Empty);
		}
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		await using var destination = new SynchronousBlockingWriteStream();
		using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);

		var writeTask = Task.Run(
			() => services.ContextDocumentService.WriteCompleteAsync(
				plan,
				ProjectContextView.Tree,
				ProjectContextDocumentFormat.Xml,
				destination,
				cancellationSource.Token),
			TestContext.Current.CancellationToken);
		await destination.WriteStarted.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		cancellationSource.Cancel();

		var completed = await Task.WhenAny(
			writeTask,
			Task.Delay(
				TimeSpan.FromSeconds(2),
				TestContext.Current.CancellationToken));
		if (completed != writeTask)
		{
			destination.Release();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				async () => await writeTask);
			Assert.Fail(
				"XML tree cancellation did not stop a synchronous destination write.");
		}

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () => await writeTask);
		Assert.Equal(0, destination.SynchronousWriteCount);
		Assert.Equal(0, destination.ActiveWrites);
	}

	private sealed class BlockingWriteStream : Stream
	{
		private readonly TaskCompletionSource _release =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _writeStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _activeWrites;

		public Task WriteStarted => _writeStarted.Task;
		public int ActiveWrites => Volatile.Read(ref _activeWrites);
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public void Release() => _release.TrySetResult();
		public override void Flush() { }
		public override Task FlushAsync(CancellationToken cancellationToken) =>
			Task.CompletedTask;
		public override int Read(byte[] buffer, int offset, int count) =>
			throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) =>
			throw new NotSupportedException();
		public override void SetLength(long value) =>
			throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) =>
			throw new InvalidOperationException("XML streaming must use asynchronous writes.");

		public override async Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _activeWrites);
			_writeStarted.TrySetResult();
			try
			{
				await _release.Task.WaitAsync(cancellationToken);
			}
			finally
			{
				Interlocked.Decrement(ref _activeWrites);
			}
		}

		public override async ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _activeWrites);
			_writeStarted.TrySetResult();
			try
			{
				await _release.Task.WaitAsync(cancellationToken);
			}
			finally
			{
				Interlocked.Decrement(ref _activeWrites);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				Release();
			base.Dispose(disposing);
		}

		public override ValueTask DisposeAsync()
		{
			Release();
			GC.SuppressFinalize(this);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class SynchronousBlockingWriteStream : Stream
	{
		private readonly TaskCompletionSource _release =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _writeStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _activeWrites;
		private int _synchronousWriteCount;

		public Task WriteStarted => _writeStarted.Task;
		public int ActiveWrites => Volatile.Read(ref _activeWrites);
		public int SynchronousWriteCount => Volatile.Read(ref _synchronousWriteCount);
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public void Release() => _release.TrySetResult();
		public override void Flush() => BlockSynchronously();
		public override Task FlushAsync(CancellationToken cancellationToken) =>
			WaitForReleaseAsync(cancellationToken);
		public override int Read(byte[] buffer, int offset, int count) =>
			throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) =>
			throw new NotSupportedException();
		public override void SetLength(long value) =>
			throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) =>
			BlockSynchronously();
		public override void Write(ReadOnlySpan<byte> buffer) =>
			BlockSynchronously();
		public override Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken) =>
			WaitForReleaseAsync(cancellationToken);
		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default) =>
			new(WaitForReleaseAsync(cancellationToken));

		private void BlockSynchronously()
		{
			Interlocked.Increment(ref _synchronousWriteCount);
			Interlocked.Increment(ref _activeWrites);
			_writeStarted.TrySetResult();
			try
			{
				_release.Task.GetAwaiter().GetResult();
			}
			finally
			{
				Interlocked.Decrement(ref _activeWrites);
			}
		}

		private async Task WaitForReleaseAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _activeWrites);
			_writeStarted.TrySetResult();
			try
			{
				await _release.Task.WaitAsync(cancellationToken);
			}
			finally
			{
				Interlocked.Decrement(ref _activeWrites);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				Release();
			base.Dispose(disposing);
		}

		public override ValueTask DisposeAsync()
		{
			Release();
			GC.SuppressFinalize(this);
			return ValueTask.CompletedTask;
		}
	}
}
