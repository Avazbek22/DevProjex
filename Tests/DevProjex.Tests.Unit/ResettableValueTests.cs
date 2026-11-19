using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class ResettableValueTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ConcurrentReadersRetryAFailedFirstInitializationWithoutDuplicateCompilations(bool exhausted)
	{
		using var ready = new CountdownEvent(8);
		var expected = new object();
		Exception failure = exhausted ? new OutOfMemoryException() : new InvalidOperationException();
		var calls = 0;
		var holder = new ResettableValue<object>(() =>
		{
			if (Interlocked.Increment(ref calls) == 1)
			{
				ready.Wait(TestContext.Current.CancellationToken);
				throw failure;
			}
			return expected;
		});
		var values = new System.Collections.Concurrent.ConcurrentBag<object>();
		var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
		var readers = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
		{
			ready.Signal();
			try { values.Add(holder.Value); }
			catch (Exception exception) { failures.Add(exception); }
		})).ToArray();
		foreach (var reader in readers)
			reader.Start();
		foreach (var reader in readers)
			reader.Join();
		Assert.Same(failure, Assert.Single(failures));
		Assert.Equal(7, values.Count);
		Assert.All(values, value => Assert.Same(expected, value));
		Assert.Equal(2, calls);
	}

	[Fact]
	public void RepeatedFailuresAndResetsNeverPublishAPartialValue()
	{
		var calls = 0;
		var failure = new OutOfMemoryException("controlled failure");
		var expected = new object();
		var holder = new ResettableValue<object>(() => ++calls <= 3 ? throw failure : expected);
		for (var attempt = 0; attempt < 3; attempt++)
		{
			Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => holder.Value));
			holder.Reset();
		}
		Assert.Same(expected, holder.Value);
		Assert.Same(expected, holder.Value);
		Assert.Equal(4, calls);
		holder.Reset();
		Assert.Same(expected, holder.Value);
		Assert.Equal(5, calls);
	}

	[Fact]
	public async Task ResetDuringInitializationCannotRepublishTheDiscardedGeneration()
	{
		using var release = new ManualResetEventSlim();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var resetRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		var holder = new ResettableValue<object>(() =>
		{
			if (Interlocked.Increment(ref calls) == 1)
			{
				entered.SetResult();
				release.Wait(TestContext.Current.CancellationToken);
			}
			return new();
		});
		var initial = Task.Run(() => holder.Value, TestContext.Current.CancellationToken);
		await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
		var reset = Task.Run(() =>
		{
			resetRequested.SetResult();
			holder.Reset();
		}, TestContext.Current.CancellationToken);
		try
		{
			await resetRequested.Task.WaitAsync(TestContext.Current.CancellationToken);
		}
		finally
		{
			release.Set();
		}
		var previous = await initial;
		await reset;
		Assert.NotSame(previous, holder.Value);
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task ConcurrentAccessPublishesOneValue()
	{
		var calls = 0;
		using var ready = new CountdownEvent(8);
		using var release = new ManualResetEventSlim();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var holder = new ResettableValue<object>(() =>
		{
			Interlocked.Increment(ref calls);
			entered.TrySetResult();
			ready.Wait(TestContext.Current.CancellationToken);
			release.Wait(TestContext.Current.CancellationToken);
			return new();
		});
		var values = new object[8];
		var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
		var threads = Enumerable.Range(0, 8).Select(index => new Thread(() =>
		{
			ready.Signal();
			try { values[index] = holder.Value; }
			catch (Exception exception) { failures.Enqueue(exception); }
		})).ToArray();
		foreach (var thread in threads) thread.Start();
		await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
		release.Set();
		foreach (var thread in threads) thread.Join();
		Assert.Empty(failures);
		Assert.Equal(1, calls);
		Assert.All(values, value => Assert.Same(values[0], value));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FailedInitializationCanBeRetried(bool exhausted)
	{
		var calls = 0;
		var expected = new object();
		var holder = new ResettableValue<object>(() =>
		{
			if (++calls == 1) throw exhausted ? new OutOfMemoryException() : new InvalidOperationException();
			return expected;
		});
		Assert.ThrowsAny<Exception>(() => holder.Value);
		Assert.Same(expected, holder.Value);
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task ResetPreservesAnOperationThatAlreadyAcquiredItsValue()
	{
		var holder = new ResettableValue<object>(() => new());
		var acquired = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
		var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var operation = Task.Run(async () =>
		{
			var value = holder.Value;
			acquired.SetResult(value);
			await finish.Task;
			return value;
		});
		var previous = await acquired.Task;
		holder.Reset();
		Assert.NotSame(previous, holder.Value);
		finish.SetResult();
		Assert.Same(previous, await operation);
	}

	[Fact]
	public void DetectorResetPreservesPinnedRulesAndIdentity()
	{
		var detector = new GitleaksSecretDetector();
		var count = detector.RuleCount;
		var identity = detector.RulesIdentity;
		detector.Reset();
		Assert.Equal(222, count);
		Assert.Equal(count, detector.RuleCount);
		Assert.Equal(identity, detector.RulesIdentity);
	}

	[Fact]
	public void DetectorRetriesItsLoaderAfterAControlledAllocationFailure()
	{
		var calls = 0;
		var assembly = typeof(GitleaksSecretDetector).Assembly;
		var name = assembly.GetManifestResourceNames().Single(name => name.EndsWith("gitleaks-v8.30.1.toml", StringComparison.Ordinal));
		using var stream = assembly.GetManifestResourceStream(name)!;
		using var reader = new StreamReader(stream);
		var configuration = reader.ReadToEnd();
		var detector = new GitleaksSecretDetector(() =>
		{
			if (++calls == 1) throw new OutOfMemoryException();
			return configuration;
		});
		Assert.Throws<OutOfMemoryException>(() => detector.RuleCount);
		Assert.Equal(222, detector.RuleCount);
		Assert.Equal(2, calls);
	}
	[Fact]
	public void ResettingCompiledRulesKeepsManualProtection()
	{
		using var directory = new TemporaryDirectory();
		const string value = "manually-marked-value-42";
		var path = directory.CreateFile("config.env", "VALUE=" + value);
		Assert.True(DevProjex.Application.Secrets.MarkedSecretValueNormalizer.TryCreate(value, out var normalized, out _));
		var detector = new GitleaksSecretDetector();
		using var session = new DevProjex.Application.Secrets.SecretRedactionSession(detector);
		session.ReplaceMarkedSecrets([new MarkedSecretProfileEntry(normalized.Hash, null, normalized.Length)]);
		for (var index = 0; index < 2; index++)
		{
			var scope = session.BeginOutput(directory.Path, [path]);
			var result = scope.Redact(path, File.ReadAllText(path), TestContext.Current.CancellationToken);
			Assert.DoesNotContain(value, result.Text, StringComparison.Ordinal);
			scope.Complete();
			detector.Reset();
			File.AppendAllText(path, "\nNEXT=ordinary");
		}
	}
}
