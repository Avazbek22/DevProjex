using DevProjex.Application.Context;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpMissingFileIdentityTests
{
	[Theory]
	[InlineData("missing.md")]
	[InlineData("existing/missing.md")]
	[InlineData("existing/new/sub/missing.md")]
	public void MissingSelectorsUseTheirPhysicalExistingParent(string relative)
	{
		using var fixture = new IdentityFixture();
		var expected = Path.Combine(fixture.PhysicalRoot, relative.Replace('/', Path.DirectorySeparatorChar));
		Assert.Equal(expected, fixture.Identity(relative));
		Assert.Equal(expected, fixture.Identity(Path.Combine(fixture.ConfiguredRoot, relative.Replace('/', Path.DirectorySeparatorChar))));
		Assert.Equal(expected, fixture.Identity(expected));
		Assert.False(File.Exists(expected));
	}

	[Fact]
	public void MissingSelectorsResolveAnInternalDirectoryAlias()
	{
		using var fixture = new IdentityFixture();
		var expected = Path.Combine(fixture.PhysicalRoot, "existing", "missing.md");
		Assert.Equal(expected, fixture.Identity("inside/missing.md"));
		Assert.Equal(expected, fixture.Identity(Path.Combine(fixture.ConfiguredRoot, "inside", "missing.md")));
		Assert.Equal(expected, fixture.Identity(expected));
	}

	[Fact]
	public void ExistingFilesDoNotNeedSelectionMembershipForIdentity()
	{
		using var fixture = new IdentityFixture();
		var expected = Path.Combine(fixture.PhysicalRoot, "existing", "hidden.md");
		File.WriteAllText(expected, "hidden-content-marker");
		Assert.Equal(expected, fixture.Identity("existing/hidden.md"));
		Assert.Equal(expected, fixture.Identity(Path.Combine(fixture.ConfiguredRoot, "existing", "hidden.md")));
	}

	[Fact]
	public void APathOutsideEveryRegisteredSpellingIsRefusedBeforePhysicalLookup()
	{
		using var fixture = new IdentityFixture();
		var calls = fixture.PhysicalLookups;
		var failure = Assert.Throws<McpToolException>(() => fixture.Identity(Path.Combine(fixture.OutsideRoot, "missing.md")));
		Assert.Equal(McpErrorCodes.RootViolation, failure.Code);
		Assert.All(fixture.PhysicalLookupPaths.Skip(calls), path => Assert.Equal(fixture.PhysicalRoot, path));
	}

	[Fact]
	public void APhysicalParentOutsideTheRootCannotSupplyAMissingIdentity()
	{
		using var fixture = new IdentityFixture();
		var failure = Assert.Throws<McpToolException>(() => fixture.Identity(Path.Combine(fixture.ConfiguredRoot, "escape", "missing.md")));
		Assert.Equal(McpErrorCodes.RootViolation, failure.Code);
	}

	[Fact]
	public void MissingNamesRetainTheirCanonicalCaseIdentity()
	{
		using var fixture = new IdentityFixture();
		var upper = fixture.Identity(Path.Combine(fixture.ConfiguredRoot, "Missing.md"));
		var lower = fixture.Identity(Path.Combine(fixture.ConfiguredRoot, "missing.md"));
		Assert.Equal(Path.Combine(fixture.PhysicalRoot, "Missing.md"), upper);
		Assert.Equal(Path.Combine(fixture.PhysicalRoot, "missing.md"), lower);
		Assert.NotEqual(upper, lower);
	}

	private sealed class IdentityFixture : IDisposable
	{
		private readonly TemporaryDirectory _workspace = new();
		private readonly McpProjectService _service;
		private readonly ProjectContextPlan _plan;

		public IdentityFixture()
		{
			ConfiguredRoot = _workspace.CreateFolder("configured");
			PhysicalRoot = McpRootRegistry.ResolvePhysicalExistingPath(_workspace.CreateFolder("physical"), requireDirectory: true);
			OutsideRoot = McpRootRegistry.ResolvePhysicalExistingPath(_workspace.CreateFolder("outside"), requireDirectory: true);
			Directory.CreateDirectory(Path.Combine(PhysicalRoot, "existing"));
			var registry = new McpRootRegistry([ConfiguredRoot], ResolvePhysical);
			_service = new McpProjectService(null!, new McpProjectRootJail(registry), null!, false, null);
			_plan = new ProjectContextPlan(PhysicalRoot, new ProjectSelectionSpec(), [], [], [], [], null!, null!,
				new HashSet<string>(StringComparer.Ordinal), [], [], null!, [], null!, "identity");
		}

		public string ConfiguredRoot { get; }
		public string PhysicalRoot { get; }
		public string OutsideRoot { get; }
		public int PhysicalLookups => PhysicalLookupPaths.Count;
		public List<string> PhysicalLookupPaths { get; } = [];

		public string Identity(string path) => _service.ResolveFileRequestIdentity(_plan, path);

		private string ResolvePhysical(string path, bool requireDirectory)
		{
			var absolute = Path.GetFullPath(path);
			PhysicalLookupPaths.Add(absolute);
			var relative = IsWithin(ConfiguredRoot, absolute) ? Path.GetRelativePath(ConfiguredRoot, absolute) :
				IsWithin(PhysicalRoot, absolute) ? Path.GetRelativePath(PhysicalRoot, absolute) : null;
			var mapped = relative is null ? absolute : Path.GetFullPath(relative, PhysicalRoot);
			if (relative is not null)
			{
				var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (segments[0] is "inside" or "escape")
				{
					mapped = segments[0] == "inside" ? Path.Combine(PhysicalRoot, "existing") : OutsideRoot;
					foreach (var segment in segments.Skip(1)) mapped = Path.Combine(mapped, segment);
				}
			}
			if (requireDirectory)
			{
				if (!Directory.Exists(mapped)) throw new DirectoryNotFoundException(mapped);
			}
			else if (!Directory.Exists(mapped) && !File.Exists(mapped)) throw new FileNotFoundException("Missing path.", mapped);
			return mapped;
		}

		private static bool IsWithin(string root, string path) => StringComparer.Ordinal.Equals(root, path) ||
			path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

		public void Dispose()
		{
			_service.Dispose();
			_workspace.Dispose();
		}
	}
}
