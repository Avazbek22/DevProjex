using System;
using System.Collections.Generic;
using DevProjex.Application.Services;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class SmartIgnoreServiceTests
{
	// Verifies smart-ignore rules are merged and de-duplicated.
	[Fact]
	public void Build_MergesRuleResults()
	{
		var rules = new[]
		{
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file.tmp" })),
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "obj" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file.tmp" }))
		};

		var service = new SmartIgnoreService(rules);

		var result = service.Build("/root");

		Assert.Contains("bin", result.FolderNames);
		Assert.Contains("obj", result.FolderNames);
		Assert.Single(result.FileNames);
	}

	// Verifies no rules yield empty ignore sets.
	[Fact]
	public void Build_ReturnsEmptyWhenNoRules()
	{
		var service = new SmartIgnoreService(Array.Empty<ISmartIgnoreRule>());

		var result = service.Build("/root");

		Assert.Empty(result.FolderNames);
		Assert.Empty(result.FileNames);
	}

	// Verifies case-insensitive de-duplication across rules.
	[Fact]
	public void Build_DeduplicatesCaseInsensitive()
	{
		var rules = new[]
		{
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BIN" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.DB" })),
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "thumbs.db" }))
		};

		var service = new SmartIgnoreService(rules);

		var result = service.Build("/root");

		Assert.Single(result.FolderNames);
		Assert.Single(result.FileNames);
	}

	[Fact]
	public void Build_IgnoresFaultyRule_AndContinuesWithHealthyRules()
	{
		var rules = new ISmartIgnoreRule[]
		{
			new ThrowingSmartIgnoreRule(),
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db" }))
		};

		var service = new SmartIgnoreService(rules);
		var result = service.Build("/root");

		Assert.Contains("bin", result.FolderNames);
		Assert.Contains("Thumbs.db", result.FileNames);
	}

	private sealed class ThrowingSmartIgnoreRule : ISmartIgnoreRule
	{
		public SmartIgnoreResult Evaluate(string rootPath)
		{
			throw new UnauthorizedAccessException("Access denied.");
		}
	}
}
