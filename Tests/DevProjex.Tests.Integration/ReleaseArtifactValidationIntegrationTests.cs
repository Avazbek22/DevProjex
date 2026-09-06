using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevProjex.ReleaseValidation;
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

		Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
		Assert.Contains("Channel github: VALIDATED; PARTIAL (not release-ready)", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("publish-payload.linux-x64.json", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void ValidatorReusesPayloadInspectorLoadedFromAnotherPath()
	{
		using var workspace = new TemporaryDirectory();
		CreateLinuxFixture(workspace.Path);
		var alternateDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "alternate")).FullName;
		var alternateInspector = Path.Combine(alternateDirectory, "ReleasePayloadInspection.cs");
		File.Copy(Path.Combine(RepoRoot.Value, "Scripts", "ReleasePayloadInspection.cs"), alternateInspector);
		var wrapperPath = Path.Combine(workspace.Path, "invoke-validator.ps1");
		File.WriteAllText(wrapperPath, """
			param($InspectorPath, $ValidatorPath, $PublishRoot)
			Add-Type -Path $InspectorPath
			& $ValidatorPath -PublishRoot $PublishRoot -Version 5.2 -Channels github -Rids linux-x64
			exit $LASTEXITCODE
			""", new UTF8Encoding(false));

		var result = RunPowerShell(wrapperPath,
			["-InspectorPath", alternateInspector,
				"-ValidatorPath", Path.Combine(RepoRoot.Value, "Scripts", "Test-ReleaseArtifacts.ps1"),
				"-PublishRoot", Path.Combine(workspace.Path, "publish")]);

		Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
	}

	[Theory]
	[InlineData("missing-file")]
	[InlineData("missing-resource")]
	[InlineData("extra-file")]
	[InlineData("payload-hash")]
	[InlineData("checksum")]
	[InlineData("partial-marker")]
	public void ValidatorFailsClosedForIncompleteGitHubArtifacts(string mutation)
	{
		using var workspace = new TemporaryDirectory();
		CreateLinuxFixture(workspace.Path, mutation);

		var result = RunValidator(workspace.Path, "github", "linux-x64");
		var output = result.StandardOutput + result.StandardError;

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("incomplete", output, StringComparison.OrdinalIgnoreCase);
		var expected = mutation switch
		{
			"missing-file" => "receipt-only.dat",
			"missing-resource" => "Fixture.Missing.Resource",
			"extra-file" => "unexpected.dat",
			"payload-hash" => "libSkiaSharp.dll",
			"checksum" => "SHA-256",
			"partial-marker" => "PARTIAL-BUILD.txt",
			_ => throw new ArgumentOutOfRangeException(nameof(mutation))
		};
		Assert.Contains(expected, output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public void ValidatorReadsStoreUploadBundlePackagesAndReceipts(bool omitPayloadFile, bool omitLanguage)
	{
		using var workspace = new TemporaryDirectory();
		var artifactName = CreateStoreFixture(workspace.Path, omitPayloadFile, omitLanguage);

		var result = RunValidator(workspace.Path, "store", "win-x64");
		var output = result.StandardOutput + result.StandardError;

		if (!omitPayloadFile && !omitLanguage)
		{
			Assert.Equal(0, result.ExitCode);
			Assert.Contains("Channel store: VALIDATED", result.StandardOutput, StringComparison.Ordinal);
			return;
		}

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains(artifactName, output, StringComparison.Ordinal);
		Assert.Contains(omitPayloadFile ? "libSkiaSharp.dll" : "Store resource languages", output, StringComparison.Ordinal);
	}

	[Fact]
	public void StoreMutationGateUsesTheSameDiffForFilesAndEmbeddedResources()
	{
		using var workspace = new TemporaryDirectory();
		CreateStoreFixture(workspace.Path, omitPayloadFile: false, omitLanguage: false);

		var result = RunPowerShell(
			Path.Combine(RepoRoot.Value, "Scripts", "Test-ReleaseArtifactGateMutation.ps1"),
			["-PublishRoot", Path.Combine(workspace.Path, "publish"), "-Version", Version, "-Channels", "store"]);

		Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
		Assert.Contains("grammars/tree-sitter-kotlin.dll", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("DevProjex.Assets.Localization.en.json", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("libSkiaSharp.dll", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void SdkReceiptMatchesBundleFolderPublishAndManagedResourceTables()
	{
		using var workspace = new TemporaryDirectory();
		var fixture = CreateReceiptBuildFixture(workspace.Path);

		var singleResult = RunDotNet(fixture.Root,
			["publish", fixture.AppProject, "-c", "Release", "-r", "win-x64", "--self-contained", "false",
				"-nodeReuse:false",
				"/p:PublishSingleFile=true", "/p:IncludeNativeLibrariesForSelfExtract=true",
				"/p:EnableCompressionInSingleFile=false", "/p:DebugType=None", "/p:DebugSymbols=false",
				"/p:DevProjexGenerateReleasePayloadReceipt=true",
				$"/p:DevProjexPayloadReceiptDirectory={fixture.SingleReceipt}", "-o", fixture.SinglePublish]);
		Assert.True(singleResult.ExitCode == 0, singleResult.StandardOutput + singleResult.StandardError);

		var folderResult = RunDotNet(fixture.Root,
			["publish", fixture.AppProject, "-c", "ReleaseStore", "-r", "win-x64", "--self-contained", "false",
				"-nodeReuse:false",
				"/p:PublishSingleFile=false", "/p:DebugType=None", "/p:DebugSymbols=false",
				"/p:DevProjexGenerateReleasePayloadReceipt=true",
				$"/p:DevProjexPayloadReceiptDirectory={fixture.FolderReceipt}", "-o", fixture.FolderPublish]);
		Assert.True(folderResult.ExitCode == 0, folderResult.StandardOutput + folderResult.StandardError);

		var singleReceipt = ReadReceipt(Path.Combine(fixture.SingleReceipt, "publish-payload.win-x64.json"));
		var folderReceipt = ReadReceipt(Path.Combine(fixture.FolderReceipt, "publish-payload.win-x64.json"));
		var bundle = ReleasePayloadInspector.InspectBundle(Path.Combine(fixture.SinglePublish, "FixtureApp.exe"));
		Assert.Equal(
			singleReceipt.Files.Select(static file => file.Path).Order(StringComparer.Ordinal),
			bundle.Files.Select(static file => file.Path).Order(StringComparer.Ordinal));

		var folderFiles = Directory.EnumerateFiles(fixture.FolderPublish, "*", SearchOption.AllDirectories)
			.Select(path => Path.GetRelativePath(fixture.FolderPublish, path).Replace('\\', '/'))
			.Where(static path => !path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
			.Order(StringComparer.Ordinal);
		Assert.Equal(folderFiles, folderReceipt.Files.Select(static file => file.Path).Order(StringComparer.Ordinal));
		var unbundledPayloadFiles = folderReceipt.Files
			.Where(static file => file.Path != "FixtureApp.exe")
			.Select(static file => file.Path)
			.Order(StringComparer.Ordinal);
		var bundledPayloadFiles = singleReceipt.Files.Select(static file => file.Path).Order(StringComparer.Ordinal);
		Assert.True(unbundledPayloadFiles.SequenceEqual(bundledPayloadFiles, StringComparer.Ordinal),
			$"Folder only: {string.Join(", ", unbundledPayloadFiles.Except(bundledPayloadFiles, StringComparer.Ordinal))}; " +
			$"bundle only: {string.Join(", ", bundledPayloadFiles.Except(unbundledPayloadFiles, StringComparer.Ordinal))}");

		foreach (var assemblyName in new[] { "Assets.dll", "Infrastructure.dll" })
		{
			var beforeBundle = ReleasePayloadInspector.TryReadManagedResources(Path.Combine(fixture.FolderPublish, assemblyName));
			var fromBundle = bundle.Files.Single(file => file.Path == assemblyName).ManagedResources;
			var fromReceipt = singleReceipt.Files.Single(file => file.Path == assemblyName).ManagedResources;
			Assert.Equal(beforeBundle, fromBundle);
			Assert.Equal(beforeBundle, fromReceipt);
		}
		Assert.Contains("Fixture.Assets.NewContent.txt",
			singleReceipt.Files.Single(file => file.Path == "Assets.dll").ManagedResources!);
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
	public void NonInteractiveReleaseValidationRejectsInvalidInputOnOneLine(string[] arguments, string expectedMessage)
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
		var result = RunPowerShell(Path.Combine(RepoRoot.Value, "Scripts", "release-all.ps1"),
			["-ValidateConfigOnly", "-NonInteractive"]);
		Assert.Equal(0, result.ExitCode);
		Assert.Contains("Configuration: VALIDATED", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("Enter release version", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void ValidateArtifactsOnlyValidatesAnExistingPartialRidSetWithoutBuilding()
	{
		var version = $"278.{Environment.ProcessId}.{Random.Shared.Next(1, 1_000_000)}";
		var releaseDirectory = Path.Combine(RepoRoot.Value, "publish", "github", $"v{version}");
		try
		{
			CreateLinuxFixture(RepoRoot.Value, version: version);
			var result = RunPowerShell(Path.Combine(RepoRoot.Value, "Scripts", "release-all.ps1"),
				["-ValidateArtifactsOnly", "-Channels", "github", "-Rids", "linux-x64",
					"-Version", version, "-NonInteractive"]);
			Assert.Equal(0, result.ExitCode);
			Assert.Contains("Channel github: validated; PARTIAL", result.StandardOutput, StringComparison.Ordinal);
		}
		finally
		{
			if (Directory.Exists(releaseDirectory)) Directory.Delete(releaseDirectory, recursive: true);
		}
	}

	private static ProcessResult RunValidator(string workspaceRoot, string channel, string rids) =>
		RunPowerShell(Path.Combine(RepoRoot.Value, "Scripts", "Test-ReleaseArtifacts.ps1"),
			["-PublishRoot", Path.Combine(workspaceRoot, "publish"), "-Version", Version,
				"-Channels", channel, "-Rids", rids]);

	private static void CreateLinuxFixture(string workspaceRoot, string? mutation = null, string version = Version)
	{
		var releaseDirectory = Path.Combine(workspaceRoot, "publish", "github", $"v{version}");
		Directory.CreateDirectory(releaseDirectory);
		var payload = CreateFixturePayload(version);
		var bundleFiles = payload.ToList();
		var receiptFiles = payload.Select(ToReceiptFile).ToList();
		if (mutation == "missing-file") receiptFiles.Add(new ReceiptFile("receipt-only.dat", 1, new string('0', 64), null));
		if (mutation == "missing-resource")
		{
			var index = receiptFiles.FindIndex(static file => file.Path == "Assets.dll");
			receiptFiles[index] = receiptFiles[index] with
			{
				ManagedResources = [.. receiptFiles[index].ManagedResources!, "Fixture.Missing.Resource"]
			};
		}
		if (mutation == "extra-file") bundleFiles.Add(new PayloadFile("unexpected.dat", Encoding.ASCII.GetBytes("extra"), 0));
		if (mutation == "payload-hash")
		{
			var index = receiptFiles.FindIndex(static file => file.Path == "libSkiaSharp.dll");
			receiptFiles[index] = receiptFiles[index] with { Sha256 = new string('0', 64) };
		}

		var artifactName = $"DevProjex.v{version}.linux-x64.tar.gz";
		var artifactPath = Path.Combine(releaseDirectory, artifactName);
		var bundle = CreateBundle(bundleFiles);
		using (var file = File.Create(artifactPath))
		using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
		using (var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: false))
		{
			writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "DevProjex")
			{
				DataStream = new MemoryStream(bundle, writable: false),
				Mode = (UnixFileMode)493,
				ModificationTime = DateTimeOffset.UnixEpoch
			});
		}

		var receiptName = "publish-payload.linux-x64.json";
		WriteReceipt(Path.Combine(releaseDirectory, receiptName), "linux-x64", receiptFiles);
		WriteChecksums(releaseDirectory, [artifactName, receiptName], mutation == "checksum" ? artifactName : null);
		if (mutation != "partial-marker")
			File.WriteAllText(Path.Combine(releaseDirectory, "PARTIAL-BUILD.txt"),
				"PARTIAL BUILD - NOT READY FOR RELEASE\nRIDs: linux-x64\n", new UTF8Encoding(false));
	}

	private static IReadOnlyList<PayloadFile> CreateFixturePayload(string version = Version) =>
	[
		new("DevProjex.exe", Encoding.ASCII.GetBytes($"fixture-{version}"), 2),
		new("Assets.dll", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets.dll")), 1),
		new("Infrastructure.dll", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Infrastructure.dll")), 1),
		new("grammars/tree-sitter-kotlin.dll", Encoding.ASCII.GetBytes($"grammar-{version}"), 2),
		new("libSkiaSharp.dll", Encoding.ASCII.GetBytes($"native-{version}"), 2),
		new("DevProjex.runtimeconfig.json", Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\"}}"), 4)
	];

	private static byte[] CreateBundle(IReadOnlyList<PayloadFile> files)
	{
		var signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
		writer.Write(0L);
		writer.Write(signature);
		var entries = new List<(PayloadFile File, long Offset)>();
		foreach (var file in files)
		{
			entries.Add((file, stream.Position));
			writer.Write(file.Bytes);
		}
		var headerOffset = stream.Position;
		writer.Write(6u);
		writer.Write(0u);
		writer.Write(entries.Count);
		writer.Write("FixtureId001");
		for (var index = 0; index < 5; index++) writer.Write(0L);
		foreach (var entry in entries)
		{
			writer.Write(entry.Offset);
			writer.Write((long)entry.File.Bytes.Length);
			writer.Write(0L);
			writer.Write(entry.File.FileType);
			writer.Write(entry.File.Path);
		}
		stream.Position = 0;
		writer.Write(headerOffset);
		return stream.ToArray();
	}

	private static ReceiptFile ToReceiptFile(PayloadFile file)
	{
		string[]? resources = null;
		if (file.FileType == 1)
		{
			var path = Path.Combine(Path.GetTempPath(), $"dpx-receipt-{Guid.NewGuid():N}.dll");
			try
			{
				File.WriteAllBytes(path, file.Bytes);
				resources = ReleasePayloadInspector.TryReadManagedResources(path);
			}
			finally
			{
				File.Delete(path);
			}
		}
		return new ReceiptFile(file.Path, file.Bytes.LongLength,
			Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant(), resources);
	}

	private static string CreateStoreFixture(string workspaceRoot, bool omitPayloadFile, bool omitLanguage)
	{
		var payload = CreateFixturePayload();
		var receiptFiles = payload.Select(ToReceiptFile).ToArray();
		var x64PackageName = "DevProjex_5.2.0.0_x64.msix";
		var arm64PackageName = "DevProjex_5.2.0.0_arm64.msix";
		var x64Package = CreateStorePackage(payload, "x64", omitPayloadFile);
		var arm64Package = CreateStorePackage(payload, "arm64", omitPayloadFile: false);
		var languages = LoadStoreLanguages().Where(language => !omitLanguage || language != "en-us");
		var bundleManifest = $$"""
			<?xml version="1.0" encoding="utf-8"?>
			<Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle">
			  <Identity Name="DevProjex" Publisher="CN=Test" Version="{{StoreVersion}}" />
			  <Packages>
			    <Package Type="application" Architecture="x64" FileName="{{x64PackageName}}" />
			    <Package Type="application" Architecture="arm64" FileName="{{arm64PackageName}}" />
			  </Packages>
			  <Resources>{{string.Concat(languages.Select(language => $"<Resource Language=\"{language}\" />"))}}</Resources>
			</Bundle>
			""";
		var bundle = CreateZip(new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			["AppxMetadata/AppxBundleManifest.xml"] = Encoding.UTF8.GetBytes(bundleManifest),
			[x64PackageName] = x64Package,
			[arm64PackageName] = arm64Package
		});
		var artifactName = "DevProjex.Store_5.2.0.0_x64_arm64_bundle_ReleaseStore.msixupload";
		var artifacts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			[artifactName] = CreateZip(new Dictionary<string, byte[]> { ["DevProjex_5.2.0.0_x64_arm64.msixbundle"] = bundle }),
			["DevProjex.Store_5.2.0.0_x64_arm64_ReleaseStore.msixbundle"] = bundle,
			["DevProjex.Store_5.2.0.0_x64_ReleaseStore.msix"] = x64Package
		};
		var releaseDirectory = Path.Combine(workspaceRoot, "publish", "store", $"v{Version}");
		Directory.CreateDirectory(releaseDirectory);
		foreach (var (name, bytes) in artifacts) File.WriteAllBytes(Path.Combine(releaseDirectory, name), bytes);
		foreach (var rid in new[] { "win-x64", "win-arm64" })
			WriteReceipt(Path.Combine(releaseDirectory, $"publish-payload.{rid}.json"), rid, receiptFiles);
		WriteChecksums(releaseDirectory, [.. artifacts.Keys, "publish-payload.win-x64.json", "publish-payload.win-arm64.json"]);
		return artifactName;
	}

	private static byte[] CreateStorePackage(IReadOnlyList<PayloadFile> payload, string architecture, bool omitPayloadFile)
	{
		var manifest = $$"""
			<?xml version="1.0" encoding="utf-8"?>
			<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3" xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
			  <Identity Name="DevProjex" Publisher="CN=Test" Version="{{StoreVersion}}" ProcessorArchitecture="{{architecture}}" />
			  <Applications><Application Id="App" Executable="DevProjex.Avalonia.exe" EntryPoint="Windows.FullTrustApplication"><Extensions>
			    <uap3:Extension Category="windows.appExecutionAlias" Executable="DevProjex.Avalonia\DevProjex.exe" EntryPoint="Windows.FullTrustApplication"><uap3:AppExecutionAlias><desktop:ExecutionAlias Alias="devprojex.exe" /></uap3:AppExecutionAlias></uap3:Extension>
			  </Extensions></Application></Applications>
			</Package>
			""";
		var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["AppxManifest.xml"] = Encoding.UTF8.GetBytes(manifest) };
		foreach (var file in payload.Where(file => !omitPayloadFile || file.Path != "libSkiaSharp.dll"))
			entries[$"DevProjex.Avalonia/{file.Path}"] = file.Bytes;
		return CreateZip(entries);
	}

	private static byte[] CreateZip(IReadOnlyDictionary<string, byte[]> entries)
	{
		using var stream = new MemoryStream();
		using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
		foreach (var (name, content) in entries)
		{
			var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
			using var entryStream = entry.Open();
			entryStream.Write(content);
		}
		return stream.ToArray();
	}

	private static string[] LoadStoreLanguages()
	{
		using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Value,
			"Packaging", "Headless", "payload-manifest.json")));
		return document.RootElement.GetProperty("release").GetProperty("store").GetProperty("resourceLanguages")
			.EnumerateArray().Select(static item => item.GetString()!).ToArray();
	}

	private static void WriteReceipt(string path, string rid, IReadOnlyList<ReceiptFile> files)
	{
		var payload = new { schemaVersion = 1, rid, files = files.Select(file => file.ManagedResources is null
			? (object)new { path = file.Path, size = file.Size, sha256 = file.Sha256 }
			: new { path = file.Path, size = file.Size, sha256 = file.Sha256, managedResources = file.ManagedResources }) };
		File.WriteAllText(path, JsonSerializer.Serialize(payload), new UTF8Encoding(false));
	}

	private static Receipt ReadReceipt(string path) =>
		JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

	private static void WriteChecksums(string directory, IEnumerable<string> names, string? invalidName = null)
	{
		var lines = names.Order(StringComparer.Ordinal).Select(name =>
		{
			var hash = name == invalidName ? new string('0', 64) :
				Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))).ToLowerInvariant();
			return $"{hash} *{name}";
		});
		File.WriteAllText(Path.Combine(directory, "SHA256SUMS.txt"), string.Join('\n', lines) + '\n', new UTF8Encoding(false));
	}

	private static ReceiptBuildFixture CreateReceiptBuildFixture(string root)
	{
		var scripts = Directory.CreateDirectory(Path.Combine(root, "Scripts")).FullName;
		File.Copy(Path.Combine(RepoRoot.Value, "Directory.Build.targets"), Path.Combine(root, "Directory.Build.targets"));
		File.Copy(Path.Combine(RepoRoot.Value, "Scripts", "Write-PublishPayloadReceipt.ps1"), Path.Combine(scripts, "Write-PublishPayloadReceipt.ps1"));
		File.Copy(Path.Combine(RepoRoot.Value, "Scripts", "ReleasePayloadInspection.cs"), Path.Combine(scripts, "ReleasePayloadInspection.cs"));
		CreateFixtureProject(root, "Assets", "Fixture.Assets.NewContent.txt");
		CreateFixtureProject(root, "Infrastructure", "Fixture.Infrastructure.Rule.json");
		var app = Path.Combine(root, "FixtureApp");
		Directory.CreateDirectory(app);
		File.WriteAllText(Path.Combine(app, "FixtureApp.csproj"), """
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>FixtureApp</AssemblyName></PropertyGroup>
			  <ItemGroup><ProjectReference Include="..\Assets\Assets.csproj" /><ProjectReference Include="..\Infrastructure\Infrastructure.csproj" /></ItemGroup>
			</Project>
			""");
		File.WriteAllText(Path.Combine(app, "Program.cs"), "System.Console.WriteLine(typeof(Assets.Marker).FullName + typeof(Infrastructure.Marker).FullName);");
		return new ReceiptBuildFixture(root, Path.Combine(app, "FixtureApp.csproj"),
			Path.Combine(root, "single"), Path.Combine(root, "single-receipt"),
			Path.Combine(root, "folder"), Path.Combine(root, "folder-receipt"));
	}

	private static void CreateFixtureProject(string root, string name, string logicalName)
	{
		var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
		File.WriteAllText(Path.Combine(directory, $"{name}.csproj"), $$"""
			<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems></PropertyGroup>
			<ItemGroup><EmbeddedResource Include="content.txt" LogicalName="{{logicalName}}" /></ItemGroup></Project>
			""");
		File.WriteAllText(Path.Combine(directory, "Marker.cs"), $"namespace {name}; public sealed class Marker {{ }}");
		File.WriteAllText(Path.Combine(directory, "content.txt"), name);
	}

	private static ProcessResult RunPowerShell(string scriptPath, IReadOnlyList<string> arguments) =>
		RunProcess("pwsh", RepoRoot.Value, ["-NoLogo", "-NoProfile", "-File", scriptPath, .. arguments]);

	private static ProcessResult RunDotNet(string workingDirectory, IReadOnlyList<string> arguments) =>
		RunProcess("dotnet", workingDirectory, arguments);

	private static ProcessResult RunProcess(string fileName, string workingDirectory, IReadOnlyList<string> arguments)
	{
		var startInfo = new ProcessStartInfo { FileName = fileName, WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, output, error);
	}

	private sealed record PayloadFile(string Path, byte[] Bytes, byte FileType);
	private sealed record ReceiptFile(string Path, long Size, string Sha256, string[]? ManagedResources);
	private sealed record Receipt(string Rid, ReceiptFile[] Files);
	private sealed record ReceiptBuildFixture(string Root, string AppProject, string SinglePublish,
		string SingleReceipt, string FolderPublish, string FolderReceipt);
	private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
