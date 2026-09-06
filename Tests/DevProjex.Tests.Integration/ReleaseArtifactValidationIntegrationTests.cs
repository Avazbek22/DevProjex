using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevProjex.Tests.Integration.Helpers;
using DevProjex.Tests.Shared.StoreListing;

namespace DevProjex.Tests.Integration;

public sealed class ReleaseArtifactValidationIntegrationTests
{
	private const string Version = "5.2";
	private const string StoreVersion = "5.2.0.0";
	private static readonly Lazy<string> RepoRoot = new(StoreListingPaths.FindRepositoryRoot);

	[Fact]
	public void ValidatorAcceptsACompletePartialRidSetAndReportsThatItIsNotReleaseReady()
	{
		using var workspace = new TemporaryDirectory();
		CreateLinuxFixture(workspace.Path);

		var result = RunValidator(workspace.Path, "github", "linux-x64");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("Channel github: VALIDATED; PARTIAL (not release-ready)", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("DevProjex.v5.2.linux-x64.tar.gz", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("SHA-256", result.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("grammar")]
	[InlineData("localization")]
	[InlineData("checksum")]
	[InlineData("partial-marker")]
	public void ValidatorFailsClosedForIncompleteGitHubArtifacts(string mutation)
	{
		using var workspace = new TemporaryDirectory();
		CreateLinuxFixture(
			workspace.Path,
			omitGrammar: mutation == "grammar",
			omitLocalization: mutation == "localization",
			invalidChecksum: mutation == "checksum",
			omitPartialMarker: mutation == "partial-marker");

		var result = RunValidator(workspace.Path, "github", "linux-x64");
		var output = result.StandardOutput + result.StandardError;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("incomplete", output, StringComparison.OrdinalIgnoreCase);
		switch (mutation)
		{
			case "grammar":
				Assert.Contains("DevProjex.v5.2.linux-x64.tar.gz", output, StringComparison.Ordinal);
				Assert.Contains("libtree-sitter-kotlin.so", output, StringComparison.Ordinal);
				break;
			case "localization":
				Assert.Contains("DevProjex.v5.2.linux-x64.tar.gz", output, StringComparison.Ordinal);
				Assert.Contains("en.json", output, StringComparison.Ordinal);
				break;
			case "checksum":
				Assert.Contains("invalid SHA-256", output, StringComparison.Ordinal);
				break;
			case "partial-marker":
				Assert.Contains("PARTIAL-BUILD.txt", output, StringComparison.Ordinal);
				break;
		}
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public void ValidatorReadsStoreUploadBundlePackagesAndManifest(bool omitGrammar, bool omitLanguage)
	{
		using var workspace = new TemporaryDirectory();
		var artifactName = CreateStoreFixture(workspace.Path, omitGrammar, omitLanguage);

		var result = RunValidator(workspace.Path, "store", "win-x64");
		var output = result.StandardOutput + result.StandardError;

		if (!omitGrammar && !omitLanguage)
		{
			Assert.Equal(0, result.ExitCode);
			Assert.Contains("Channel store: VALIDATED", result.StandardOutput, StringComparison.Ordinal);
			return;
		}

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains(artifactName, output, StringComparison.Ordinal);
		Assert.Contains(
			omitGrammar ? "tree-sitter-kotlin.dll" : "Store resource languages",
			output,
			StringComparison.Ordinal);
	}

	[Fact]
	public void StoreMutationGateProvesTheValidatorRejectsAMissingGrammar()
	{
		using var workspace = new TemporaryDirectory();
		CreateStoreFixture(workspace.Path, omitGrammar: false, omitLanguage: false);

		var result = RunPowerShell(
			Path.Combine(RepoRoot.Value, "Scripts", "Test-ReleaseArtifactGateMutation.ps1"),
			[
				"-PublishRoot", Path.Combine(workspace.Path, "publish"),
				"-Version", Version,
				"-Channels", "store"
			]);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("Mutation gate passed", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("tree-sitter-kotlin.dll", result.StandardOutput, StringComparison.Ordinal);
	}

	public static TheoryData<string[], string> InvalidReleaseInvocations => new()
	{
		{ ["-ValidateArtifactsOnly", "-Channels", "unknown", "-NonInteractive"], "Unknown release channel 'unknown'" },
		{ ["-ValidateArtifactsOnly", "-Channels", "github", "-Rids", "unknown", "-NonInteractive"], "Unknown release RID 'unknown'" },
		{ ["-WackOnly", "-Channels", "github", "-NonInteractive"], "-WackOnly supports only the store channel" },
		{ ["-WackOnly", "-Channels", "store", "-SkipWack", "-NonInteractive"], "-SkipWack cannot be combined with -WackOnly" },
		{ ["-WackOnly", "-ValidateArtifactsOnly", "-Channels", "store", "-NonInteractive"], "-ValidateArtifactsOnly cannot be combined with -WackOnly" },
		{ ["-ValidateArtifactsOnly", "-Channels", "github", "-Version", "invalid", "-NonInteractive"], "Invalid release version 'invalid'" }
	};

	[Theory]
	[MemberData(nameof(InvalidReleaseInvocations))]
	public void NonInteractiveReleaseValidationRejectsInvalidInputOnOneLine(
		string[] arguments,
		string expectedMessage)
	{
		var result = RunPowerShell(Path.Combine(RepoRoot.Value, "Scripts", "release-all.ps1"), arguments);
		var lines = (result.StandardOutput + result.StandardError)
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		Assert.Equal(1, result.ExitCode);
		Assert.Single(lines);
		Assert.Contains(expectedMessage, lines[0], StringComparison.Ordinal);
	}

	[Fact]
	public void NonInteractiveConfigValidationUsesTheRepositoryVersionWithoutPrompting()
	{
		var result = RunPowerShell(
			Path.Combine(RepoRoot.Value, "Scripts", "release-all.ps1"),
			["-ValidateConfigOnly", "-NonInteractive"]);
		var output = result.StandardOutput + result.StandardError;

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("Configuration: VALIDATED", output, StringComparison.Ordinal);
		Assert.DoesNotContain("Enter release version", output, StringComparison.Ordinal);
	}

	[Fact]
	public void ValidateArtifactsOnlyValidatesAnExistingPartialRidSetWithoutBuilding()
	{
		var version = $"278.{Environment.ProcessId}.{Random.Shared.Next(1, 1_000_000)}";
		var releaseDirectory = Path.Combine(RepoRoot.Value, "publish", "github", $"v{version}");
		try
		{
			CreateLinuxFixture(RepoRoot.Value, version: version);

			var result = RunPowerShell(
				Path.Combine(RepoRoot.Value, "Scripts", "release-all.ps1"),
				[
					"-ValidateArtifactsOnly",
					"-Channels", "github",
					"-Rids", "linux-x64",
					"-Version", version,
					"-NonInteractive"
				]);

			Assert.Equal(0, result.ExitCode);
			Assert.Contains("Channel github: validated; PARTIAL", result.StandardOutput, StringComparison.Ordinal);
		}
		finally
		{
			if (Directory.Exists(releaseDirectory))
			{
				Directory.Delete(releaseDirectory, recursive: true);
			}
		}
	}

	private static ProcessResult RunValidator(string workspaceRoot, string channel, string rids) =>
		RunPowerShell(
			Path.Combine(RepoRoot.Value, "Scripts", "Test-ReleaseArtifacts.ps1"),
			[
				"-PublishRoot", Path.Combine(workspaceRoot, "publish"),
				"-Version", Version,
				"-Channels", channel,
				"-Rids", rids
			]);

	private static void CreateLinuxFixture(
		string workspaceRoot,
		bool omitGrammar = false,
		bool omitLocalization = false,
		bool invalidChecksum = false,
		bool omitPartialMarker = false,
		string version = Version)
	{
		var manifest = LoadManifest();
		var releaseDirectory = Path.Combine(workspaceRoot, "publish", "github", $"v{version}");
		Directory.CreateDirectory(releaseDirectory);
		var artifactName = $"DevProjex.v{version}.linux-x64.tar.gz";
		var artifactPath = Path.Combine(releaseDirectory, artifactName);
		var payloadParts = new List<string> { version };
		payloadParts.AddRange(manifest.Grammars
			.Where(grammar => !omitGrammar || grammar != "tree-sitter-kotlin")
			.Select(grammar => $"lib{grammar}.so"));
		payloadParts.AddRange(manifest.Localizations
			.Where(localization => !omitLocalization || localization != "en.json"));
		var payload = Encoding.UTF8.GetBytes(string.Join('\0', payloadParts));

		using (var file = File.Create(artifactPath))
		using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
		using (var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: false))
		{
			var entry = new UstarTarEntry(TarEntryType.RegularFile, "DevProjex")
			{
				DataStream = new MemoryStream(payload, writable: false),
				Mode = (UnixFileMode)493,
				ModificationTime = DateTimeOffset.UnixEpoch
			};
			writer.WriteEntry(entry);
		}

		var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant();
		if (invalidChecksum)
		{
			hash = new string('0', 64);
		}
		File.WriteAllText(Path.Combine(releaseDirectory, "SHA256SUMS.txt"), $"{hash} *{artifactName}\n", new UTF8Encoding(false));
		if (!omitPartialMarker)
		{
			File.WriteAllText(
				Path.Combine(releaseDirectory, "PARTIAL-BUILD.txt"),
				"PARTIAL BUILD - NOT READY FOR RELEASE\nRIDs: linux-x64\n",
				new UTF8Encoding(false));
		}
	}

	private static string CreateStoreFixture(string workspaceRoot, bool omitGrammar, bool omitLanguage)
	{
		var manifest = LoadManifest();
		var x64PackageName = "DevProjex_5.2.0.0_x64.msix";
		var arm64PackageName = "DevProjex_5.2.0.0_arm64.msix";
		var x64Package = CreateStorePackage(manifest.Grammars, "x64", omitGrammar);
		var arm64Package = CreateStorePackage(manifest.Grammars, "arm64", omitGrammar: false);
		var languages = manifest.StoreLanguages.Where(language => !omitLanguage || language != "en-us");
		var bundleManifest = $$"""
			<?xml version="1.0" encoding="utf-8"?>
			<Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle">
			  <Identity Name="DevProjex" Publisher="CN=Test" Version="{{StoreVersion}}" />
			  <Packages>
			    <Package Type="application" Architecture="x64" FileName="{{x64PackageName}}" />
			    <Package Type="application" Architecture="arm64" FileName="{{arm64PackageName}}" />
			  </Packages>
			  <Resources>
			    {{string.Join(Environment.NewLine + "    ", languages.Select(language => $"<Resource Language=\"{language}\" />"))}}
			  </Resources>
			</Bundle>
			""";
		var bundle = CreateZip(new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			["AppxMetadata/AppxBundleManifest.xml"] = Encoding.UTF8.GetBytes(bundleManifest),
			[x64PackageName] = x64Package,
			[arm64PackageName] = arm64Package
		});
		var artifactName = "DevProjex.Store_5.2.0.0_x64_arm64_bundle_ReleaseStore.msixupload";
		var upload = CreateZip(new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			["DevProjex_5.2.0.0_x64_arm64.msixbundle"] = bundle
		});
		var releaseDirectory = Path.Combine(workspaceRoot, "publish", "store", $"v{Version}");
		Directory.CreateDirectory(releaseDirectory);
		var artifacts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			[artifactName] = upload,
			["DevProjex.Store_5.2.0.0_x64_arm64_ReleaseStore.msixbundle"] = bundle,
			["DevProjex.Store_5.2.0.0_x64_ReleaseStore.msix"] = x64Package
		};
		foreach (var (name, bytes) in artifacts)
		{
			File.WriteAllBytes(Path.Combine(releaseDirectory, name), bytes);
		}
		var checksumLines = artifacts
			.OrderBy(static artifact => artifact.Key, StringComparer.Ordinal)
			.Select(static artifact =>
				$"{Convert.ToHexString(SHA256.HashData(artifact.Value)).ToLowerInvariant()} *{artifact.Key}");
		File.WriteAllText(
			Path.Combine(releaseDirectory, "SHA256SUMS.txt"),
			string.Join('\n', checksumLines) + '\n',
			new UTF8Encoding(false));
		return artifactName;
	}

	private static byte[] CreateStorePackage(IEnumerable<string> grammars, string architecture, bool omitGrammar)
	{
		var packageManifest = $$"""
			<?xml version="1.0" encoding="utf-8"?>
			<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
			         xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
			         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
			  <Identity Name="DevProjex" Publisher="CN=Test" Version="{{StoreVersion}}" ProcessorArchitecture="{{architecture}}" />
			  <Applications>
			    <Application Id="App" Executable="DevProjex.Avalonia.exe" EntryPoint="Windows.FullTrustApplication">
			      <Extensions>
			        <uap3:Extension Category="windows.appExecutionAlias" Executable="DevProjex.Avalonia\DevProjex.exe" EntryPoint="Windows.FullTrustApplication">
			          <uap3:AppExecutionAlias><desktop:ExecutionAlias Alias="devprojex.exe" /></uap3:AppExecutionAlias>
			        </uap3:Extension>
			      </Extensions>
			    </Application>
			  </Applications>
			</Package>
			""";
		var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			["AppxManifest.xml"] = Encoding.UTF8.GetBytes(packageManifest),
			["DevProjex.Avalonia/DevProjex.exe"] = Encoding.ASCII.GetBytes("fixture")
		};
		foreach (var grammar in grammars.Where(grammar => !omitGrammar || grammar != "tree-sitter-kotlin"))
		{
			entries[$"DevProjex.Avalonia/grammars/{grammar}.dll"] = Encoding.ASCII.GetBytes(grammar);
		}
		return CreateZip(entries);
	}

	private static byte[] CreateZip(IReadOnlyDictionary<string, byte[]> entries)
	{
		using var stream = new MemoryStream();
		using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var (name, content) in entries)
			{
				var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
				using var entryStream = entry.Open();
				entryStream.Write(content);
			}
		}
		return stream.ToArray();
	}

	private static ReleaseManifest LoadManifest()
	{
		using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
			RepoRoot.Value,
			"Packaging",
			"Headless",
			"payload-manifest.json")));
		var root = document.RootElement;
		return new ReleaseManifest(
			root.GetProperty("grammars").EnumerateArray().Select(static item => item.GetString()!).ToArray(),
			root.GetProperty("release").GetProperty("localizations").EnumerateArray().Select(static item => item.GetString()!).ToArray(),
			root.GetProperty("release").GetProperty("store").GetProperty("resourceLanguages").EnumerateArray().Select(static item => item.GetString()!).ToArray());
	}

	private static ProcessResult RunPowerShell(string scriptPath, IReadOnlyList<string> arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "pwsh",
			WorkingDirectory = RepoRoot.Value,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("-NoLogo");
		startInfo.ArgumentList.Add("-NoProfile");
		startInfo.ArgumentList.Add("-File");
		startInfo.ArgumentList.Add(scriptPath);
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Could not start pwsh.");
		var standardOutput = process.StandardOutput.ReadToEnd();
		var standardError = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, standardOutput, standardError);
	}

	private sealed record ReleaseManifest(
		IReadOnlyList<string> Grammars,
		IReadOnlyList<string> Localizations,
		IReadOnlyList<string> StoreLanguages);

	private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
