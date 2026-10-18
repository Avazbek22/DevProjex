using System.Text.RegularExpressions;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakPlatformContractTests
{
	[Theory]
	[InlineData("Windows")]
	[InlineData("macOS")]
	public void NonLinuxPlatformsNeverConsultEitherMarker(string platform)
	{
		Assert.Contains(platform, new[] { "Windows", "macOS" });
		var result = FlatpakEnvironment.Detect(false, _ => throw new InvalidOperationException("Environment must not be read"),
			_ => throw new InvalidOperationException("Filesystem must not be read"));
		Assert.Equal(new FlatpakEnvironmentSnapshot(false, null), result);
		Assert.Null(result.ExternalLaunchCommand);
	}

	[Theory]
	[InlineData("FLATPAK_ID", "/.flatpak-info", true)]
	[InlineData("flatpak_id", "/.flatpak-info", false)]
	[InlineData("FLATPAK_ID", "/.FLATPAK-INFO", false)]
	[InlineData("Flatpak_Id", "/.flatpak-info", false)]
	public void MarkerNamesAreExactAndAppIdCaseIsPreserved(string variable, string marker, bool expected)
	{
		const string id = "io.github.Avazbek22.DevProjex";
		var snapshot = FlatpakEnvironment.Detect(true, name => name == variable ? id : null, path => path == marker);
		Assert.Equal(expected, snapshot.IsActive);
		Assert.Equal(expected ? id : null, snapshot.AppId);
	}

	[Fact]
	public void TestOverrideIsInternalNestedAndExecutionContextLocal()
	{
		Assert.Null(typeof(FlatpakEnvironment).GetMethod("UseForTesting", BindingFlags.Public | BindingFlags.Static));
		using var first = FlatpakEnvironment.UseForTesting(new(false, null));
		using (FlatpakEnvironment.UseForTesting(new(true, "test.app"))) Assert.True(FlatpakEnvironment.Current.IsActive);
		Assert.False(FlatpakEnvironment.Current.IsActive);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void EveryStorageKindHonorsOnlyInjectedXdgOrThePrivateFallback(int value)
	{
		using var directory = new TemporaryDirectory();
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		var kind = (UserDataDirectoryKind)value;
		var variable = new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME" }[value];
		var suffix = new[] { "config", "data", "state", "cache" }[value];
		var xdg = Path.Combine(directory.Path, "selected");
		Assert.Equal(xdg, UserDataPathResolver.Resolve(kind, false, (_, _) => throw new InvalidOperationException(),
			name => name == variable ? xdg : name == "HOME" ? directory.Path : null));
		foreach (var invalid in new string?[] { null, "", " ", "relative/path" })
			Assert.Equal(Path.Combine(directory.Path, ".var", "app", "test.app", suffix),
				UserDataPathResolver.Resolve(kind, false, (_, _) => throw new InvalidOperationException(),
					name => name == variable ? invalid : name == "HOME" ? directory.Path : null));
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		var platform = Path.Combine(directory.Path, "platform");
		Assert.Equal(platform, UserDataPathResolver.Resolve(kind, true, (_, _) => platform, _ => throw new InvalidOperationException()));
		var ordinaryRoot = value <= 1 ? platform : value == 2 ? Path.Combine(directory.Path, ".local", "state") : Path.Combine(directory.Path, ".cache");
		Assert.Equal(ordinaryRoot,
			UserDataPathResolver.Resolve(kind, false, (folder, _) => folder == Environment.SpecialFolder.UserProfile ? directory.Path : platform, _ => null));
	}

	[Fact]
	public async Task ConcurrentOverridesDoNotLeakIntoTheirParentOrPeers()
	{
		using var parent = FlatpakEnvironment.UseForTesting(new(false, null));
		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var entered = 0;
		await Task.WhenAll(Enumerable.Range(0, 32).Select(async index =>
		{
			var id = "test.app." + index.ToString(CultureInfo.InvariantCulture);
			using var child = FlatpakEnvironment.UseForTesting(new(true, id));
			if (Interlocked.Increment(ref entered) == 32) ready.SetResult();
			await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
			Assert.Equal(id, FlatpakEnvironment.Current.AppId);
		}));
		Assert.False(FlatpakEnvironment.Current.IsActive);
	}

	[Fact]
	public void EveryFlatpakTranslationHasExactPlaceholdersAndACommandLeadIn()
	{
		var catalog = new JsonLocalizationCatalog();
		var english = catalog.Get(AppLanguage.En);
		var keys = english.Keys.Where(key => key.StartsWith("Flatpak.", StringComparison.Ordinal)).Order().ToArray();
		Assert.NotEmpty(keys);
		Assert.Equal(20, Enum.GetValues<AppLanguage>().Length);
		foreach (var language in Enum.GetValues<AppLanguage>())
		{
			var translated = catalog.Get(language);
			Assert.Equal(keys, translated.Keys.Where(key => key.StartsWith("Flatpak.", StringComparison.Ordinal)).Order());
			foreach (var key in keys)
			{
				Assert.False(string.IsNullOrWhiteSpace(translated[key]), $"{language}: {key}");
				if (language != AppLanguage.En) Assert.NotEqual(english[key], translated[key]);
				Assert.Equal(Placeholders(english[key]), Placeholders(translated[key]));
			}
			Assert.Matches(@"[:：]\s*\{0\}\s*$", translated["Flatpak.PathUnavailable"]);
		}
	}

	private static string[] Placeholders(string value) => Regex.Matches(value, @"\{(\d+)(?:[^}]*)\}")
		.Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
}
