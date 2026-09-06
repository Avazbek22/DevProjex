using System.CommandLine;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevProjex.Application.Presentation;
using DevProjex.Terminal.Execution;

namespace DevProjex.Tests.Terminal;

public sealed class DocumentationAndPackagingContractTests
{
	private static readonly string[] RequiredDocumentation =
	[
		"CLI-V1-Contract.md",
		"CommandLine.md",
		"TerminalWorkspace.md",
		"CLI-Output-Contract.md",
		"CLI-Migration.md",
		"CLI-Architecture.md",
		"CLI-Profiles.md",
		"Desktop-Control.md",
		"Git-Safety.md",
		"Security.md",
		"SmartIgnore.md",
		"HideSecrets.md",
		"Benchmarks.md",
		"Comparison.md",
		"Release-Channels.md",
		"Release-Process.md"
	];

	[Fact]
	public void SecurityAndBenchmarkDocumentsKeepTheirEvidenceBoundaries()
	{
		var rootPath = FindRepositoryRoot();
		var security = File.ReadAllText(Path.Combine(rootPath, "Docs", "Security.md"));
		var benchmarks = File.ReadAllText(Path.Combine(rootPath, "Docs", "Benchmarks.md"));
		var comparison = File.ReadAllText(Path.Combine(rootPath, "Docs", "Comparison.md"));

		Assert.Contains("Prompt injection", security, StringComparison.Ordinal);
		Assert.Contains("Secret detection is not proof", security, StringComparison.Ordinal);
		Assert.Contains("ProxyCommand", security, StringComparison.Ordinal);
		Assert.Contains("same operating-system user", security, StringComparison.Ordinal);
		Assert.Contains("d318b683471101618febed18996405ad26462110", benchmarks, StringComparison.Ordinal);
		Assert.Contains("85e3969b010c72b905203812d1a3f5beb84a2102", benchmarks, StringComparison.Ordinal);
		Assert.Contains("three", benchmarks, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("operating-system page cache", benchmarks, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Benchmarks.md", comparison, StringComparison.Ordinal);
		Assert.Contains("different", comparison, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void GitSafetyProfilesAndFilterRefusalRemainDocumented()
	{
		var rootPath = FindRepositoryRoot();
		var safety = File.ReadAllText(Path.Combine(rootPath, "Docs", "Git-Safety.md"));
		var smartIgnore = File.ReadAllText(Path.Combine(rootPath, "Docs", "SmartIgnore.md"));
		var commandLine = File.ReadAllText(Path.Combine(rootPath, "Docs", "CommandLine.md"));

		Assert.Contains("LocalRead", safety, StringComparison.Ordinal);
		Assert.Contains("ManagedCheckout", safety, StringComparison.Ordinal);
		Assert.Contains("ExplicitNetwork", safety, StringComparison.Ordinal);
		Assert.Contains("GIT_CONFIG_NOSYSTEM=1", safety, StringComparison.Ordinal);
		Assert.Contains("GIT_NO_LAZY_FETCH=1", safety, StringComparison.Ordinal);
		Assert.Contains("DPX-GIT-UNSAFE-FILTER", safety, StringComparison.Ordinal);
		Assert.Contains("SSH configuration", safety, StringComparison.Ordinal);
		Assert.Contains("Git-Safety.md", smartIgnore, StringComparison.Ordinal);
		Assert.Contains("Git-Safety.md", commandLine, StringComparison.Ordinal);
	}

	[Fact]
	public void McpSecretDocumentationSeparatesControlFromDetectionGuarantees()
	{
		var rootPath = FindRepositoryRoot();
		var documents = new[]
		{
			File.ReadAllText(Path.Combine(rootPath, "README.md")),
			File.ReadAllText(Path.Combine(rootPath, "Docs", "McpServer.md")),
			File.ReadAllText(Path.Combine(rootPath, "Docs", "HideSecrets.md"))
		};

		Assert.All(documents, static document =>
		{
			Assert.Contains("guarantee", document, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("heuristic", document, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("review each pack", document, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("publishing", document, StringComparison.OrdinalIgnoreCase);
		});

		foreach (var document in documents.Skip(1))
		{
			Assert.Contains("placeholder", document, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("EXAMPLE", document, StringComparison.Ordinal);
			Assert.Contains("allowlist", document, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("Gitleaks", document, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("scope-aware", document, StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public void McpAgentErgonomicsAreSpecifiedInServerAndVersionContracts()
	{
		var rootPath = FindRepositoryRoot();
		var server = File.ReadAllText(Path.Combine(rootPath, "Docs", "McpServer.md"));
		var version = File.ReadAllText(Path.Combine(rootPath, "Docs", "CLI-V1-Contract.md"));
		var normalizedServer = Regex.Replace(server, @"\s+", " ");
		var normalizedVersion = Regex.Replace(version, @"\s+", " ");

		Assert.Contains("end_line B exceeded the file", server, StringComparison.Ordinal);
		Assert.Contains("start_line=B+1", server, StringComparison.Ordinal);
		Assert.Contains("Tree limited to depth D of N", server, StringComparison.Ordinal);
		Assert.Contains("An explicit `max_depth` is the caller's choice", normalizedServer, StringComparison.Ordinal);
		Assert.Contains("JSON and XML never return partial syntax", server, StringComparison.Ordinal);
		Assert.Contains("analyze.topFiles[].uninspected", server, StringComparison.Ordinal);
		Assert.Contains("initialize` result", server, StringComparison.Ordinal);

		Assert.Contains("MCP agent ergonomics changes four v5.2 behaviors", normalizedVersion, StringComparison.Ordinal);
		Assert.Contains("there is no strict-range switch", normalizedVersion, StringComparison.Ordinal);
		Assert.Contains("Passing an explicit `max_depth` restores", normalizedVersion, StringComparison.Ordinal);
		Assert.Contains("Cached MCP", normalizedVersion, StringComparison.Ordinal);
	}

	[Fact]
	public void McpReadmeNetworkBoundaryMatchesRemoteOptIn()
	{
		var rootPath = FindRepositoryRoot();
		var readme = File.ReadAllText(Path.Combine(rootPath, "README.md"));

		Assert.Contains(
			"network access is disabled unless `--allow-remote`",
			readme,
			StringComparison.Ordinal);
		Assert.Contains("cannot modify project files", readme, StringComparison.Ordinal);
		Assert.Contains(
			"remote Git URL checkouts are pinned on first use",
			readme,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"tools cannot modify files, run project code, or touch the network",
			readme,
			StringComparison.Ordinal);
	}

	[Fact]
	public void SecretRuleAttributionShipsWithTheEmbeddedConfiguration()
	{
		var rootPath = FindRepositoryRoot();
		var infrastructureProject = XDocument.Load(
			Path.Combine(rootPath, "Infrastructure", "Infrastructure.csproj"));
		var embeddedResources = infrastructureProject
			.Descendants("EmbeddedResource")
			.Select(element => element.Attribute("Include")?.Value)
			.ToArray();

		Assert.Contains("Secrets\\Rules\\gitleaks-v8.30.1.toml", embeddedResources);
		Assert.Contains("..\\THIRD-PARTY-NOTICES.md", embeddedResources);
		var notices = File.ReadAllText(Path.Combine(rootPath, "THIRD-PARTY-NOTICES.md"));
		Assert.Contains("Copyright (c) 2019 Zachary Rice", notices, StringComparison.Ordinal);
		Assert.Contains("Copyright (c) 2019-2026, Alexandre Mutel", notices, StringComparison.Ordinal);
	}

	[Fact]
	public void EveryCuratedGrammarIsNamedInThirdPartyNotices()
	{
		var rootPath = FindRepositoryRoot();
		var infrastructureProject = XDocument.Load(
			Path.Combine(rootPath, "Infrastructure", "Infrastructure.csproj"));
		var grammarNames = infrastructureProject
			.Descendants()
			.Where(static element =>
				element.Name.LocalName is "DevProjexGrammar" or "DevProjexVendoredGrammar")
			.Select(element => element.Attribute("Include")?.Value)
			.Where(static value => value is not null)
			.Cast<string>()
			.ToArray();
		var notices = File.ReadAllText(Path.Combine(rootPath, "THIRD-PARTY-NOTICES.md"));

		Assert.NotEmpty(grammarNames);
		Assert.Equal(
			grammarNames.Length,
			grammarNames.Distinct(StringComparer.Ordinal).Count());
		foreach (var grammarName in grammarNames)
		{
			Assert.Matches(
				$@"(?<![a-z0-9-]){Regex.Escape(grammarName)}(?![a-z0-9-])",
				notices);
		}
	}

	[Fact]
	public void CrossPublishRidFlowsToTheAssemblyThatEmbedsGrammars()
	{
		var rootPath = FindRepositoryRoot();
		var desktopProject = XDocument.Load(Path.Combine(
			rootPath,
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia.csproj"));
		var terminalProject = XDocument.Load(Path.Combine(
			rootPath,
			"Apps",
			"Terminal",
			"DevProjex.Terminal.csproj"));
		var mcpProject = XDocument.Load(Path.Combine(
			rootPath,
			"Apps",
			"Mcp",
			"DevProjex.Mcp.csproj"));
		var probeProject = XDocument.Load(Path.Combine(
			rootPath,
			"Packaging",
			"GrammarDeliveryProbe",
			"GrammarDeliveryProbe.csproj"));
		var infrastructureProject = XDocument.Load(Path.Combine(
			rootPath,
			"Infrastructure",
			"Infrastructure.csproj"));

		AssertProjectReferenceProperty(
			desktopProject,
			"Infrastructure.csproj",
			"DevProjexGrammarRuntimeIdentifier=$(RuntimeIdentifier)");
		AssertProjectReferenceProperty(
			desktopProject,
			"DevProjex.Mcp.csproj",
			"DevProjexGrammarRuntimeIdentifier=$(RuntimeIdentifier)");
		AssertProjectReferenceProperty(
			desktopProject,
			"DevProjex.Terminal.csproj",
			"DevProjexGrammarRuntimeIdentifier=$(RuntimeIdentifier)");
		AssertProjectReferenceProperty(
			terminalProject,
			"Infrastructure.csproj",
			"DevProjexGrammarRuntimeIdentifier=$(DevProjexGrammarRuntimeIdentifier)");
		AssertProjectReferenceProperty(
			terminalProject,
			"DevProjex.Mcp.csproj",
			"DevProjexGrammarRuntimeIdentifier=$(DevProjexGrammarRuntimeIdentifier)");
		AssertProjectReferenceProperty(
			mcpProject,
			"Infrastructure.csproj",
			"DevProjexGrammarRuntimeIdentifier=$(DevProjexGrammarRuntimeIdentifier)");
		AssertProjectReferenceProperty(
			probeProject,
			"Infrastructure.csproj",
			"DevProjexGrammarRuntimeIdentifier=$(RuntimeIdentifier)");
		AssertRidPropertyDoesNotFlowPastInfrastructure(
			terminalProject,
			["Infrastructure.csproj", "DevProjex.Mcp.csproj"]);
		AssertRidPropertyDoesNotFlowPastInfrastructure(mcpProject, ["Infrastructure.csproj"]);
		AssertRidPropertyDoesNotFlowPastInfrastructure(infrastructureProject, excludedProjects: null);
	}

	[Fact]
	public void HeadlessPackagesStayRidSpecificAndFailClosed()
	{
		var rootPath = FindRepositoryRoot();
		var hostPath = Path.Combine(rootPath, "Apps", "TerminalHost", "DevProjex.TerminalHost.csproj");
		var host = XDocument.Load(hostPath);
		string Property(string name) => host.Descendants(name).Single().Value;

		Assert.Equal("Exe", Property("OutputType"));
		Assert.Equal("devprojex", Property("AssemblyName"));
		Assert.Equal("devprojex", Property("PackageId"));
		Assert.Equal("devprojex", Property("ToolCommandName"));
		Assert.Equal("true", Property("PackAsTool"));
		Assert.Equal("true", Property("SelfContained"));
		Assert.Equal("false", Property("PublishAot"));
		Assert.Equal("false", Property("PublishTrimmed"));
		Assert.DoesNotContain(
			host.Descendants("ProjectReference"),
			element => (element.Attribute("Include")?.Value ?? string.Empty)
				.Contains("Avalonia", StringComparison.OrdinalIgnoreCase));
		var rids = Property("RuntimeIdentifiers").Split(';');
		Assert.Equal(
			["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"],
			rids);
		Assert.DoesNotContain("any", rids);

		using var manifestDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
			rootPath,
			"Packaging",
			"Headless",
			"payload-manifest.json")));
		var manifestGrammars = manifestDocument.RootElement.GetProperty("grammars")
			.EnumerateArray()
			.Select(static element => element.GetString())
			.Order(StringComparer.Ordinal)
			.ToArray();
		var infrastructure = XDocument.Load(Path.Combine(rootPath, "Infrastructure", "Infrastructure.csproj"));
		var projectGrammars = infrastructure.Descendants()
			.Where(static element => element.Name.LocalName is "DevProjexGrammar" or "DevProjexVendoredGrammar")
			.Select(static element => element.Attribute("Include")?.Value)
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(projectGrammars, manifestGrammars);
		Assert.Contains("tree-sitter-c-sharp", manifestGrammars);
		Assert.Contains("tree-sitter-kotlin", manifestGrammars);

		var launcherTemplate = File.ReadAllText(Path.Combine(
			rootPath, "Packaging", "Npm", "devprojex", "package.json.template"));
		Assert.DoesNotContain("\"dependencies\"", launcherTemplate, StringComparison.Ordinal);
		Assert.DoesNotContain("\"scripts\"", launcherTemplate, StringComparison.Ordinal);
		Assert.Contains("\"optionalDependencies\"", launcherTemplate, StringComparison.Ordinal);
		Assert.Contains("\"node\": \">=20\"", launcherTemplate, StringComparison.Ordinal);

		var workflow = File.ReadAllText(Path.Combine(
			rootPath, ".github", "workflows", "publish-packages.yml"));
		Assert.Contains("Test-HeadlessPackages.ps1", workflow, StringComparison.Ordinal);
		Assert.Contains("Test-HeadlessPackageGateMutation.ps1", workflow, StringComparison.Ordinal);
		Assert.Contains("windows-latest", workflow, StringComparison.Ordinal);
		Assert.Contains("ubuntu-latest", workflow, StringComparison.Ordinal);
		Assert.Contains("macos-latest", workflow, StringComparison.Ordinal);
		Assert.Contains("NuGet/login@v1", workflow, StringComparison.Ordinal);
		Assert.Contains("inputs.dry_run == false", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("--skip-duplicate", workflow, StringComparison.Ordinal);
	}

	[Fact]
	public void ReleasePayloadContractsTrackGrammarsStoreResourcesAndSdkPublishItems()
	{
		var rootPath = FindRepositoryRoot();
		using var manifestDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
			rootPath,
			"Packaging",
			"Headless",
			"payload-manifest.json")));
		var manifest = manifestDocument.RootElement;
		var manifestGrammars = manifest.GetProperty("grammars")
			.EnumerateArray()
			.Select(static element => element.GetString())
			.Order(StringComparer.Ordinal)
			.ToArray();
		var infrastructure = XDocument.Load(Path.Combine(rootPath, "Infrastructure", "Infrastructure.csproj"));
		var projectGrammars = infrastructure.Descendants()
			.Where(static element => element.Name.LocalName is "DevProjexGrammar" or "DevProjexVendoredGrammar")
			.Select(static element => element.Attribute("Include")?.Value)
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(projectGrammars, manifestGrammars);

		var release = manifest.GetProperty("release");
		Assert.False(release.TryGetProperty("localizations", out _));
		var headless = release.GetProperty("headless");
		Assert.Equal("DevProjex-headless", headless.GetProperty("archivePrefix").GetString());
		Assert.Equal("SHA256SUMS.headless.txt", headless.GetProperty("checksumFile").GetString());

		var store = release.GetProperty("store");
		var manifestLanguages = store.GetProperty("resourceLanguages")
			.EnumerateArray()
			.Select(static element => element.GetString()!.ToLowerInvariant())
			.Order(StringComparer.Ordinal)
			.ToArray();
		var storeManifest = XDocument.Load(Path.Combine(
			rootPath,
			"Packaging",
			"Windows",
			"DevProjex.Store",
			"Package.appxmanifest"));
		var storeLanguages = storeManifest.Descendants()
			.Where(static element => element.Name.LocalName == "Resource")
			.Select(static element => element.Attribute("Language")!.Value.ToLowerInvariant())
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(storeLanguages, manifestLanguages);
		Assert.Equal(["arm64", "x64"], store.GetProperty("platforms")
			.EnumerateArray()
			.Select(static element => element.GetString())
			.Order(StringComparer.Ordinal));

		var releaseScript = File.ReadAllText(Path.Combine(rootPath, "Scripts", "release-all.ps1"));
		Assert.Contains("/p:EnableCompressionInSingleFile=false", releaseScript, StringComparison.Ordinal);
		Assert.Contains("Test-ReleaseArtifacts.ps1", releaseScript, StringComparison.Ordinal);
		var buildTargets = File.ReadAllText(Path.Combine(rootPath, "Directory.Build.targets"));
		Assert.Contains("@(FilesToBundle)", buildTargets, StringComparison.Ordinal);
		Assert.Contains("@(ResolvedFileToPublish)", buildTargets, StringComparison.Ordinal);
		Assert.Contains("Write-PublishPayloadReceipt.ps1", buildTargets, StringComparison.Ordinal);
		Assert.Contains("DevProjexGenerateFolderPayloadReceipt", buildTargets, StringComparison.Ordinal);
		Assert.Contains("'$(Configuration)'=='ReleaseStore'", buildTargets, StringComparison.Ordinal);
	}

	[Fact]
	public void HeadlessReleaseAndContainerChannelsRemainDistinctAndFailClosed()
	{
		var rootPath = FindRepositoryRoot();
		var buildScript = File.ReadAllText(Path.Combine(rootPath, "Scripts", "build-headless-packages.ps1"));
		var validator = File.ReadAllText(Path.Combine(rootPath, "Scripts", "Test-ReleaseArtifacts.ps1"));
		var archiveWorkflow = File.ReadAllText(Path.Combine(rootPath, ".github", "workflows", "package-headless.yml"));
		var containerWorkflow = File.ReadAllText(Path.Combine(rootPath, ".github", "workflows", "publish-container.yml"));
		var containerSmoke = File.ReadAllText(Path.Combine(rootPath, "Scripts", "Test-HeadlessContainerSmoke.ps1"));
		var dockerfile = File.ReadAllText(Path.Combine(rootPath, "Dockerfile"));
		var installation = File.ReadAllText(Path.Combine(rootPath, "Docs", "Installation.md"));
		var releaseProcess = File.ReadAllText(Path.Combine(rootPath, "Docs", "Release-Process.md"));

		Assert.Contains("$($manifest.release.headless.archivePrefix).v$Version.$($Rid.rid).$extension", buildScript, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex.v$Version.$($Rid.rid)", buildScript, StringComparison.Ordinal);
		Assert.Contains("DevProjexGenerateReleasePayloadReceipt=true", buildScript, StringComparison.Ordinal);
		Assert.Contains("'headless'", validator, StringComparison.Ordinal);
		Assert.Contains("[string]$Rid.binary", validator, StringComparison.Ordinal);
		Assert.Contains("release.headless.checksumFile", validator, StringComparison.Ordinal);

		Assert.Contains("types: [published]", archiveWorkflow, StringComparison.Ordinal);
		Assert.Contains("actions/checkout@v7", archiveWorkflow, StringComparison.Ordinal);
		Assert.Contains("actions/setup-dotnet@v6", archiveWorkflow, StringComparison.Ordinal);
		Assert.Contains("actions/upload-artifact@v7", archiveWorkflow, StringComparison.Ordinal);
		Assert.Contains("actions/download-artifact@v8", archiveWorkflow, StringComparison.Ordinal);
		Assert.Contains("gh release upload", archiveWorkflow, StringComparison.Ordinal);
		Assert.Contains("if: ${{ steps.metadata.outputs.release_tag != '' }}", archiveWorkflow, StringComparison.Ordinal);

		Assert.Contains("FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0", dockerfile, StringComparison.Ordinal);
		Assert.Contains("runtime-deps:10.0-noble-chiseled-extra", dockerfile, StringComparison.Ordinal);
		Assert.Contains("-p:PublishSingleFile=false", dockerfile, StringComparison.Ordinal);
		Assert.Contains("-p:DevProjexGrammarDelivery=Content", dockerfile, StringComparison.Ordinal);
		Assert.Contains("USER app", dockerfile, StringComparison.Ordinal);
		Assert.Contains("ENTRYPOINT [\"devprojex\"]", dockerfile, StringComparison.Ordinal);
		Assert.DoesNotContain("alpine", dockerfile, StringComparison.OrdinalIgnoreCase);

		using var glama = JsonDocument.Parse(File.ReadAllText(Path.Combine(rootPath, "glama.json")));
		Assert.Equal("https://glama.ai/mcp/schemas/server.json", glama.RootElement.GetProperty("$schema").GetString());
		Assert.Equal(["Avazbek22"], glama.RootElement.GetProperty("maintainers").EnumerateArray()
			.Select(static item => item.GetString()!).ToArray());

		Assert.Contains("ubuntu-24.04-arm", containerWorkflow, StringComparison.Ordinal);
		Assert.Contains("linux/amd64,linux/arm64", containerWorkflow, StringComparison.Ordinal);
		Assert.Contains("docker/setup-buildx-action@v4", containerWorkflow, StringComparison.Ordinal);
		Assert.Contains("docker/login-action@v4", containerWorkflow, StringComparison.Ordinal);
		Assert.Contains("docker/build-push-action@v7", containerWorkflow, StringComparison.Ordinal);
		Assert.Contains("actions/attest-build-provenance@v4", containerWorkflow, StringComparison.Ordinal);
		Assert.Contains("packages: write", containerWorkflow, StringComparison.Ordinal);
		Assert.Contains("id-token: write", containerWorkflow, StringComparison.Ordinal);
		Assert.Contains("attestations: write", containerWorkflow, StringComparison.Ordinal);
		Assert.Contains("--read-only", containerSmoke, StringComparison.Ordinal);
		Assert.Contains("--tmpfs /tmp", containerSmoke, StringComparison.Ordinal);
		Assert.DoesNotContain("setup-qemu", containerWorkflow, StringComparison.OrdinalIgnoreCase);

		Assert.Contains("DevProjex-headless.v<version>.<rid>", installation, StringComparison.Ordinal);
		Assert.Contains("ghcr.io/avazbek22/devprojex", installation, StringComparison.Ordinal);
		Assert.Contains("Two independent producers cannot atomically update one checksum manifest", releaseProcess, StringComparison.Ordinal);
	}

	[Fact]
	public void ReleaseWorkflowsShareVersionAndAppImageReceiptGates()
	{
		var rootPath = FindRepositoryRoot();
		var workflowNames = new[]
		{
			"package-appimage.yml",
			"package-headless.yml",
			"publish-container.yml",
			"publish-packages.yml"
		};
		foreach (var workflowName in workflowNames)
		{
			var workflow = File.ReadAllText(Path.Combine(rootPath, ".github", "workflows", workflowName));
			Assert.Contains("types: [published]", workflow, StringComparison.Ordinal);
			Assert.Contains("Scripts/ci/Test-ReleaseVersion.ps1", workflow, StringComparison.Ordinal);
			Assert.Contains("github.event.release.tag_name", workflow, StringComparison.Ordinal);
		}
		var packagesWorkflow = File.ReadAllText(Path.Combine(
			rootPath, ".github", "workflows", "publish-packages.yml"));
		Assert.Contains(
			"SelectSingleNode('/Project/PropertyGroup/DevProjexVersion')",
			packagesWorkflow,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			".Project.PropertyGroup.DevProjexVersion",
			packagesWorkflow,
			StringComparison.Ordinal);

		var appImageWorkflow = File.ReadAllText(Path.Combine(rootPath, ".github", "workflows", "package-appimage.yml"));
		Assert.Contains("DevProjexGenerateReleasePayloadReceipt=true", appImageWorkflow, StringComparison.Ordinal);
		Assert.Contains("Test-ReleaseArtifacts.ps1", appImageWorkflow, StringComparison.Ordinal);
		Assert.Contains("Test-ReleaseArtifactGateMutation.ps1", appImageWorkflow, StringComparison.Ordinal);
		Assert.Contains("-Channels appimage", appImageWorkflow, StringComparison.Ordinal);

		var releaseProcess = File.ReadAllText(Path.Combine(rootPath, "Docs", "Release-Process.md"));
		Assert.Contains("must match", releaseProcess, StringComparison.Ordinal);
		Assert.Contains("workflow_dispatch", releaseProcess, StringComparison.Ordinal);
		Assert.Contains("AppImage workflow enables the same SDK-generated", releaseProcess, StringComparison.Ordinal);
	}

	[Fact]
	public void ReadmeCommandExamplesParseAgainstTheProductionCommandTree()
	{
		var rootPath = FindRepositoryRoot();
		var readme = File.ReadAllText(Path.Combine(rootPath, "README.md"));
		var examples = readme
			.Split('\n')
			.Select(static line => line.Trim())
			.Where(static line => line.StartsWith("devprojex", StringComparison.Ordinal))
			.ToArray();
		var commandTree = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		Assert.NotEmpty(examples);
		foreach (var example in examples)
		{
			var arguments = example
				.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Skip(1)
				.ToArray();
			if (arguments.Length == 0)
			{
				var environment = new TestTerminalEnvironment
				{
					HasAttachedConsole = true,
					IsInputInteractive = true,
					IsOutputInteractive = true
				};
				Assert.Equal(
					ProcessInvocationMode.Terminal,
					ProcessInvocationRouter.Resolve(
						arguments,
						environment,
						hasPendingDesktopRequest: false,
						isFrameworkDependentLaunch: false));
				continue;
			}
			var parseResult = commandTree.Parse(arguments);

			Assert.True(
				parseResult.Errors.Count == 0,
				$"README example does not parse: {example}{Environment.NewLine}" +
				string.Join(Environment.NewLine, parseResult.Errors.Select(static error => error.Message)));
		}
	}

	[Fact]
	public void ReadmeReportsTheShippedLocalizationCount()
	{
		var rootPath = FindRepositoryRoot();
		var readme = File.ReadAllText(Path.Combine(rootPath, "README.md"));
		var advertised = Regex.Match(
			readme,
			"Localization in (?<count>\\d+) languages",
			RegexOptions.CultureInvariant);
		var shippedCount = Directory
			.EnumerateFiles(Path.Combine(rootPath, "Assets", "Localization"), "*.json")
			.Count();

		Assert.True(advertised.Success, "README localization count is missing.");
		Assert.Equal(shippedCount, int.Parse(advertised.Groups["count"].Value));
	}

	[Fact]
	public void CliDocumentationCoversEveryPublicCommandAndRequiredContractDocument()
	{
		var rootPath = FindRepositoryRoot();
		var docsPath = Path.Combine(rootPath, "Docs");
		foreach (var fileName in RequiredDocumentation)
			Assert.True(File.Exists(Path.Combine(docsPath, fileName)), $"Missing CLI document: {fileName}");

		var commandLine = File.ReadAllText(Path.Combine(docsPath, "CommandLine.md"));
		var commandTree = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		foreach (var path in EnumeratePublicCommandPaths(commandTree))
		{
			var syntax = $"devprojex {string.Join(' ', path)}".TrimEnd();
			Assert.Contains(syntax, commandLine, StringComparison.Ordinal);
		}

		Assert.Contains("Exclusions", commandLine, StringComparison.Ordinal);
		Assert.Contains("--git-mode <MODE>", commandLine, StringComparison.Ordinal);
		Assert.Contains("`diff:<REF>..<REF>`", commandLine, StringComparison.Ordinal);
		Assert.Contains("stdout", commandLine, StringComparison.Ordinal);
		Assert.Contains("stderr", commandLine, StringComparison.Ordinal);
	}

	[Fact]
	public void DesktopControlDocumentationListsEveryPublishedDesktopGitMode()
	{
		var rootPath = FindRepositoryRoot();
		var documentation = File.ReadAllText(Path.Combine(rootPath, "Docs", "Desktop-Control.md"));
		var gitModeRow = Regex.Match(
			documentation,
			@"(?m)^\| `gitMode` \|[^\r\n]+\r?$",
			RegexOptions.CultureInvariant);

		Assert.True(gitModeRow.Success, "Desktop Control gitMode state contract is missing.");
		foreach (var descriptor in ProjectPresentationCatalog.GitFiltering.OrderBy(static item => item.Order))
			Assert.Contains($"`{descriptor.Token}`", gitModeRow.Value, StringComparison.Ordinal);
		Assert.DoesNotContain("`diff:", gitModeRow.Value, StringComparison.Ordinal);

		var readinessRow = Regex.Match(
			documentation,
			@"(?m)^\| `trackedGitReady` \|[^\r\n]+\r?$",
			RegexOptions.CultureInvariant);
		Assert.True(readinessRow.Success, "Desktop Control Git-readiness state contract is missing.");
		foreach (var descriptor in ProjectPresentationCatalog.GitFiltering.Where(static item =>
			         DesktopOpenReadiness.RequiresGitReadiness(item.Id)))
		{
			Assert.Contains($"`{descriptor.Token}`", readinessRow.Value, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void SmartIgnoreDocumentationUsesTheCurrentGitAxis()
	{
		var rootPath = FindRepositoryRoot();
		var documentation = File.ReadAllText(Path.Combine(rootPath, "Docs", "SmartIgnore.md"));

		foreach (var descriptor in ProjectPresentationCatalog.GitFiltering)
		{
			Assert.Matches(
				$@"(?m)^\| `{Regex.Escape(descriptor.Token)}` \|[^\r\n]+\r?$",
				documentation);
		}
		Assert.Matches(@"(?m)^\| `diff:<REF>\.\.<REF>` \|[^\r\n]+\r?$", documentation);
		Assert.DoesNotContain("Git checkbox", documentation, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("two Git modes as checkboxes", documentation, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			"chooses one mode: no Git filtering, `.gitignore`, or tracked files only",
			documentation,
			StringComparison.OrdinalIgnoreCase);

		var readme = File.ReadAllText(Path.Combine(rootPath, "README.md"));
		Assert.Contains("staged files", readme, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("all current changes", readme, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("ref-to-ref diff", readme, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void CliContractListsEverySupportedLanguageCode()
	{
		var rootPath = FindRepositoryRoot();
		var contract = File.ReadAllText(Path.Combine(rootPath, "Docs", "CLI-V1-Contract.md"));
		var tokenBlock = Regex.Match(
			contract,
			"supported canonical tokens are:\\s*```text\\s*(?<tokens>[^`]+)```",
			RegexOptions.CultureInvariant);
		var documentedCodes = tokenBlock.Groups["tokens"].Value
			.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.Order(StringComparer.Ordinal)
			.ToArray();
		var supportedCodes = Enum.GetValues<DevProjex.Kernel.Models.AppLanguage>()
			.Select(DevProjex.Kernel.Models.AppLanguageUtility.ToCode)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.True(tokenBlock.Success, "The CLI contract language-token block is missing.");
		Assert.Equal(supportedCodes, documentedCodes);
	}

	[Fact]
	public void UserDocumentationDoesNotAdvertiseTheRemovedFlatCli()
	{
		var rootPath = FindRepositoryRoot();
		var files = new[]
			{
				Path.Combine(rootPath, "README.md"),
				Path.Combine(rootPath, "Docs", "CommandLine.md")
			}
			.Concat(Directory.EnumerateFiles(Path.Combine(rootPath, "Assets", "HelpContent"), "help.*.txt"))
			.ToArray();
		string[] removedSyntax =
		[
			"--no-ui",
			"--silent",
			"--report ",
			"--copy ",
			"--benchmark-ui",
			"--session-metrics",
			"--export tree",
			"--export content",
			"--export tree-content"
		];

		foreach (var file in files)
		{
			var content = File.ReadAllText(file);
			foreach (var token in removedSyntax)
				Assert.DoesNotContain(token, content, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void PackagingKeepsTerminalAsALibraryInsideTheSingleDesktopExecutable()
	{
		var rootPath = FindRepositoryRoot();
		var terminalProjectPath = Path.Combine(
			rootPath,
			"Apps",
			"Terminal",
			"DevProjex.Terminal.csproj");
		var desktopProjectPath = Path.Combine(
			rootPath,
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia.csproj");
		var terminalProject = XDocument.Load(terminalProjectPath);
		var desktopProject = XDocument.Load(desktopProjectPath);

		Assert.DoesNotContain(
			terminalProject.Descendants("OutputType"),
			static element => element.Value.Equals("Exe", StringComparison.OrdinalIgnoreCase));
		var referencedProjects = desktopProject
			.Descendants("ProjectReference")
			.Select(element => Path.GetFullPath(
				Path.Combine(
					Path.GetDirectoryName(desktopProjectPath)!,
					(element.Attribute("Include")?.Value ?? string.Empty)
					.Replace('\\', Path.DirectorySeparatorChar)
					.Replace('/', Path.DirectorySeparatorChar))))
			.ToArray();
		Assert.Contains(
			Path.GetFullPath(terminalProjectPath),
			referencedProjects,
			StringComparer.OrdinalIgnoreCase);

		var workflow = File
			.ReadAllText(Path.Combine(rootPath, ".github", "workflows", "release-validate.yml"))
			.ReplaceLineEndings("\n");
		foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
			Assert.Contains(rid, workflow, StringComparison.Ordinal);
		Assert.Contains(
			"runner = 'macos-15'; rid = 'osx-arm64'",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_TERMINAL_HOST=1", workflow, StringComparison.Ordinal);
		Assert.Contains("Validate Single-File Output", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"Get-ChildItem -LiteralPath $publishDirectory -File -Recurse",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("$files.Count -ne 1", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"[System.IO.Path]::GetRelativePath($publishDirectory, $_.FullName)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("${{ matrix.binary }}", workflow, StringComparison.Ordinal);
		Assert.Contains("Desktop IPC and Redirected EOF Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("retained redirected CLI handles", workflow, StringComparison.Ordinal);
		Assert.Contains("[int] $TimeoutMilliseconds = 30000", workflow, StringComparison.Ordinal);
		Assert.Contains("-TimeoutMilliseconds 150000", workflow, StringComparison.Ordinal);
		Assert.Contains("Get-DesktopTimeoutDiagnostics", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"[IO.Path]::GetFullPath([string]$_.projectPath)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"$statusResponse = $status.Stdout | ConvertFrom-Json",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("$statusResponse.ok", workflow, StringComparison.Ordinal);
		Assert.Contains("$state = $statusResponse.state", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"$state = $status.Stdout | ConvertFrom-Json",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("env -u CI \"$2\"", workflow, StringComparison.Ordinal);
		Assert.Contains("Portable Launcher ConPTY TUI Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("${{ startsWith(matrix.rid, 'win-') }}", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("${{ matrix.rid == 'win-x64' }}", workflow, StringComparison.Ordinal);
		Assert.Contains("Published Native PTY TUI Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("Published Single-File Extraction Contract", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"PublishedSingleFileExtractionProcessTests",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"McpServerProcessTests.PublishedSingleFileCompletesHandshakeListsToolsCallsToolAndExitsOnEof",
			workflow,
			StringComparison.Ordinal);
		var publishStepIndex = workflow.IndexOf(
			"\n      - name: Publish\n",
			StringComparison.Ordinal);
		var completionStepName =
			"\n      - name: Published Completion Native Shell Integration\n";
		var completionStepIndex = workflow.IndexOf(
			completionStepName,
			StringComparison.Ordinal);
		Assert.True(
			publishStepIndex >= 0,
			"The release workflow must publish the application before validating it.");
		Assert.True(
			completionStepIndex > publishStepIndex,
			"The native-shell completion gate must run after the application is published.");
		var completionStepEndIndex = workflow.IndexOf(
			"\n      - name:",
			completionStepIndex + completionStepName.Length,
			StringComparison.Ordinal);
		var completionStep = workflow[
			completionStepIndex..
			(completionStepEndIndex >= 0 ? completionStepEndIndex : workflow.Length)];
		Assert.Contains(
			"artifacts/publish/${{ matrix.rid }}/${{ matrix.binary }}",
			completionStep,
			StringComparison.Ordinal);
		Assert.Contains(
			"FullyQualifiedName~GeneratedCompletionNativeShellIntegrationTests",
			completionStep,
			StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_COMPLETION_SHELLS",
			completionStep,
			StringComparison.Ordinal);
		Assert.Contains(
			"rid = 'win-x64'; binary = 'DevProjex.exe'; " +
			"completion_shells = 'bash,powershell'",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"rid = 'win-arm64'; binary = 'DevProjex.exe'; " +
			"completion_shells = 'powershell'",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"completion_shells = 'bash,zsh,fish,powershell'",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"completion_shells = 'zsh,powershell'",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"${{ matrix.completion_shells }}",
			completionStep,
			StringComparison.Ordinal);
		Assert.Contains(
			"Completion shell matrix is missing for ${{ matrix.rid }}.",
			completionStep,
			StringComparison.Ordinal);
		Assert.Contains(
			"apt-get install -y xvfb zsh fish",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"Nested-Mount Destination Safety Gate (Linux x64)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"Enable Rootless Mount Namespace Gate (Linux x64)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"apparmor_restrict_unprivileged_userns",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"unshare -Urnm true",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"Restore Rootless Mount Namespace Policy (Linux x64)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"always() && matrix.rid == 'linux-x64'",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_REQUIRE_ROOTLESS_MOUNT_TESTS",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DestinationNestedMountProcessTests.AnalyzeRejectsAliasToFileSystemMountedInsideSource",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DestinationNestedMountProcessTests.ContextForceRejectsFileBindAliasToMountedSourceFile",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("project folder export", workflow, StringComparison.Ordinal);
		Assert.Contains("project ZIP export", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"[System.IO.Compression.ZipFile]::OpenRead",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("Published Broken Pipe Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("Startup Smoke (macOS)", workflow, StringComparison.Ordinal);
		Assert.Contains("branches: [ \"master\", \"v5.1\", \"v5.2\" ]", workflow, StringComparison.Ordinal);
		Assert.Contains("Smart Secrets context contract", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"Password=DEVPROJEX_REDACTED[connection-password#1]",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("mixed-case analysis JSON", workflow, StringComparison.Ordinal);
		Assert.Contains("NO_COLOR analysis", workflow, StringComparison.Ordinal);
		Assert.Contains("context dry-run", workflow, StringComparison.Ordinal);
		var dryRunSectionStart = workflow.IndexOf(
			"$dryRunDirectory =",
			StringComparison.Ordinal);
		Assert.True(
			dryRunSectionStart >= 0,
			"The release workflow must define a context dry-run destination.");
		var dryRunSectionEnd = workflow.IndexOf(
			"$conflictDestination =",
			dryRunSectionStart,
			StringComparison.Ordinal);
		Assert.True(
			dryRunSectionEnd > dryRunSectionStart,
			"The release workflow must contain a bounded context dry-run smoke.");
		var dryRunSection = workflow[dryRunSectionStart..dryRunSectionEnd];
		var dryRunParentCreationIndex = dryRunSection.IndexOf(
			"New-Item -ItemType Directory -Path $dryRunDirectory -Force",
			StringComparison.Ordinal);
		var dryRunInvocationIndex = dryRunSection.IndexOf(
			"Invoke-NativeCommand -Name \"context dry-run\"",
			StringComparison.Ordinal);
		Assert.True(
			dryRunParentCreationIndex >= 0 &&
			dryRunInvocationIndex > dryRunParentCreationIndex,
			"The dry-run destination parent must exist before preflight.");
		Assert.Contains(
			"Test-Path -LiteralPath $dryRunDirectory -PathType Container",
			dryRunSection,
			StringComparison.Ordinal);
		Assert.Contains(
			"Test-Path -LiteralPath $dryRunDestination",
			dryRunSection,
			StringComparison.Ordinal);
		Assert.Contains(
			"Get-ChildItem -LiteralPath $dryRunDirectory -Force",
			dryRunSection,
			StringComparison.Ordinal);
		Assert.Contains("existing destination conflict", workflow, StringComparison.Ordinal);
		Assert.Contains("real CLI usage-error contract", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"InstallOrRepair_ReleaseGateEnvironment_InstallsOfficialWindowsLauncherThroughPublicApi",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_RELEASE_WINDOWS_LAUNCHER_TARGET",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_RELEASE_WINDOWS_LAUNCHER_LOCAL_APP_DATA",
			workflow,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Join-Path $env:RUNNER_TEMP \"devprojex.cmd\"",
			workflow,
			StringComparison.Ordinal);
		Assert.DoesNotContain("rem target-base64:", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Set-Content -LiteralPath $launcherPath",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"TerminalRecentRepositoriesPtyTests.PopulatedCachedRepositoryOpensOfflineWithCleanIdentity",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"TerminalClonePtyTests.LocalRepositoryCloneThroughApplicationBinaryOpensWorkspaceAndPreservesSource",
			workflow,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"TerminalClonePtyTests.CloneProgressCancellationCleansCacheAndRetryOpensWorkspace",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"TerminalLargePreviewPtyTests.FileBackedPreviewReachesFirstMiddleAndFinalSectionsWithDistinctScrollbars",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"TerminalPtyLifecycleTests.SupportedTerminalSizeMatrixRemainsKeyboardUsableAndWithinViewport",
			workflow,
			StringComparison.Ordinal);
		Assert.True(
			Regex.Matches(workflow, "DEVPROJEX_SKIP_TUI_PTY_TESTS: 0").Count >= 2,
			"Curated published-binary PTY steps must override the global skip switch.");
		Assert.Contains("DEVPROJEX_TUI_TEST_BINARY", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex.Cli", workflow, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("\"--path\"", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("\"--copy\"", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("\"--report\"", workflow, StringComparison.Ordinal);

		var desktopEntry = File.ReadAllText(
			Path.Combine(
				rootPath,
				"Packaging",
				"Linux",
				"io.github.Avazbek22.DevProjex.desktop"));
		Assert.Contains("Exec=devprojex open %f", desktopEntry, StringComparison.Ordinal);
		Assert.DoesNotContain("Exec=devprojex %F", desktopEntry, StringComparison.Ordinal);

		var macPackaging = File.ReadAllText(
			Path.Combine(rootPath, "Packaging", "MacOS", "README.md"));
		Assert.Contains("DEVPROJEX_TERMINAL_HOST=1", macPackaging, StringComparison.Ordinal);

		var symbolTarget = desktopProject
			.Descendants("Target")
			.Single(element =>
				element.Attribute("Name")?.Value == "RemovePackageSymbolsFromSingleFilePublish");
		Assert.Contains("PublishSingleFile", symbolTarget.Attribute("Condition")?.Value, StringComparison.Ordinal);
		Assert.Contains("DebugSymbols", symbolTarget.Attribute("Condition")?.Value, StringComparison.Ordinal);
		Assert.Contains(
			symbolTarget.Descendants("ResolvedFileToPublish"),
			element =>
				element.Attribute("Remove")?.Value == "@(ResolvedFileToPublish)" &&
				element.Attribute("Condition")?.Value.Contains(".pdb", StringComparison.Ordinal) == true);
	}

	[Fact]
	public void ReleaseValidationInlineScriptsStayBelowGitHubExpressionLimit()
	{
		const int inlineScriptSafetyLimit = 18_000;
		var rootPath = FindRepositoryRoot();
		var workflowPath = Path.Combine(rootPath, ".github", "workflows", "release-validate.yml");
		var lines = File.ReadAllLines(workflowPath);

		for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
		{
			var runBlockMatch = Regex.Match(lines[lineIndex], "^(\\s*)run:\\s*[|>]\\s*$");
			if (!runBlockMatch.Success)
				continue;

			var runIndent = runBlockMatch.Groups[1].Value.Length;
			var bodyLength = 0;
			for (var bodyLineIndex = lineIndex + 1; bodyLineIndex < lines.Length; bodyLineIndex++)
			{
				var bodyLine = lines[bodyLineIndex];
				if (bodyLine.Length > 0)
				{
					var bodyIndent = bodyLine.Length - bodyLine.TrimStart().Length;
					if (bodyIndent <= runIndent)
						break;
				}

				bodyLength += bodyLine.Length + 1;
			}

			Assert.True(
				bodyLength < inlineScriptSafetyLimit,
				$"Inline run block at {Path.GetFileName(workflowPath)}:{lineIndex + 1} is " +
				$"{bodyLength} characters. Split it before GitHub's 21,000-character expression limit.");
		}
	}

	[Fact]
	public void NativePtyAutomationDependencyIsPinnedAndTestOnly()
	{
		var rootPath = FindRepositoryRoot();
		var packageVersions = XDocument.Load(Path.Combine(rootPath, "Directory.Packages.props"));
		var hex1bVersion = packageVersions
			.Descendants("PackageVersion")
			.Single(element => element.Attribute("Include")?.Value == "Hex1b")
			.Attribute("Version")?.Value;
		Assert.Equal("0.165.0", hex1bVersion);
		Assert.DoesNotContain(
			packageVersions.Descendants("PackageVersion"),
			element => element.Attribute("Include")?.Value == "Porta.Pty");

		var terminalTestProject = XDocument.Load(
			Path.Combine(
				rootPath,
				"Tests",
				"DevProjex.Tests.Terminal",
				"DevProjex.Tests.Terminal.csproj"));
		Assert.Contains(
			terminalTestProject.Descendants("PackageReference"),
			element => element.Attribute("Include")?.Value == "Hex1b");

		var productProjectPaths = new[]
		{
			Path.Combine(
				rootPath,
				"Apps",
				"Terminal",
				"DevProjex.Terminal.csproj"),
			Path.Combine(
				rootPath,
				"Apps",
				"Avalonia",
				"DevProjex.Avalonia.csproj")
		};
		foreach (var productProjectPath in productProjectPaths)
		{
			var productProject = XDocument.Load(productProjectPath);
			Assert.DoesNotContain(
				productProject.Descendants("PackageReference"),
				element => element.Attribute("Include")?.Value is "Hex1b" or "Porta.Pty");
		}
	}

	[Fact]
	public void ProductSourcesContainNoEnvironmentDrivenProgressCheckpointBackdoor()
	{
		var rootPath = FindRepositoryRoot();
		var productRoots = new[]
		{
			Path.Combine(rootPath, "Kernel"),
			Path.Combine(rootPath, "Application"),
			Path.Combine(rootPath, "Infrastructure"),
			Path.Combine(rootPath, "Apps")
		};

		foreach (var file in productRoots.SelectMany(path =>
			         Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)))
		{
			var source = File.ReadAllText(file);
			Assert.DoesNotContain(
				"DEVPROJEX_INTERNAL_TUI_PROGRESS_",
				source,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				"TerminalProgressTestCheckpoint",
				source,
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public void TestSourcesDoNotReferencePreFlattenedApplicationDirectories()
	{
		var rootPath = FindRepositoryRoot();
		var testsRoot = Path.Combine(rootPath, "Tests");
		var obsoletePathPatterns = new[]
		{
			new Regex(
				"\"Apps\"\\s*,\\s*\"Avalonia\"\\s*,\\s*\"DevProjex\\.Avalonia\"",
				RegexOptions.CultureInvariant),
			new Regex(
				"\"Apps\"\\s*,\\s*\"Terminal\"\\s*,\\s*\"DevProjex\\.Terminal\"",
				RegexOptions.CultureInvariant)
		};

		foreach (var sourcePath in Directory.EnumerateFiles(
			         testsRoot,
			         "*.cs",
			         SearchOption.AllDirectories))
		{
			var source = File.ReadAllText(sourcePath);
			foreach (var obsoletePathPattern in obsoletePathPatterns)
			{
				Assert.False(
					obsoletePathPattern.IsMatch(source),
					$"Obsolete pre-flattening path in {Path.GetRelativePath(rootPath, sourcePath)}.");
			}
		}
	}

	private static void AssertProjectReferenceProperty(
		XDocument project,
		string projectFileName,
		string expectedProperty)
	{
		var reference = project
			.Descendants("ProjectReference")
			.Single(element =>
				(element.Attribute("Include")?.Value ?? string.Empty)
				.EndsWith(projectFileName, StringComparison.OrdinalIgnoreCase));
		var property = Assert.Single(reference.Elements("AdditionalProperties"));
		Assert.Equal(expectedProperty, property.Value);
		Assert.Contains(
			"!=''",
			property.Attribute("Condition")?.Value ?? string.Empty,
			StringComparison.Ordinal);
	}

	private static void AssertRidPropertyDoesNotFlowPastInfrastructure(
		XDocument project,
		IReadOnlyList<string>? excludedProjects)
	{
		var references = project
			.Descendants("ProjectReference")
			.Where(element => excludedProjects is null ||
				excludedProjects.All(excludedProject =>
					!(element.Attribute("Include")?.Value ?? string.Empty)
					.EndsWith(excludedProject, StringComparison.OrdinalIgnoreCase)))
			.ToArray();
		Assert.NotEmpty(references);
		Assert.All(references, reference => Assert.Contains(
			"DevProjexGrammarRuntimeIdentifier",
			reference.Attribute("GlobalPropertiesToRemove")?.Value ?? string.Empty,
			StringComparison.Ordinal));
	}

	private static IEnumerable<IReadOnlyList<string>> EnumeratePublicCommandPaths(RootCommand root)
	{
		var stack = new Stack<(Command Command, string[] Path)>();
		foreach (var command in root.Subcommands.Reverse())
		{
			if (!command.Hidden)
				stack.Push((command, [command.Name]));
		}

		while (stack.Count > 0)
		{
			var (command, path) = stack.Pop();
			yield return path;
			foreach (var child in command.Subcommands.Reverse())
			{
				if (!child.Hidden)
					stack.Push((child, [.. path, child.Name]));
			}
		}
	}

	[Fact]
	public void AppImageWorkflowAndDocumentationKeepTheReleaseContractFailClosed()
	{
		var rootPath = FindRepositoryRoot();
		var workflow = File.ReadAllText(Path.Combine(
			rootPath,
			".github",
			"workflows",
			"package-appimage.yml"));
		var installation = File.ReadAllText(Path.Combine(
			rootPath,
			"Docs",
			"Installation.md"));
		var linuxPackaging = File.ReadAllText(Path.Combine(
			rootPath,
			"Packaging",
			"Linux",
			"README.md"));

		Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
		Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
		Assert.Contains("- Packaging/Linux/**", workflow, StringComparison.Ordinal);
		Assert.Contains("- Kernel/ProcessEntryPointResolver.cs", workflow, StringComparison.Ordinal);
		Assert.Contains("types: [published]", workflow, StringComparison.Ordinal);
		Assert.Contains("runner: ubuntu-22.04", workflow, StringComparison.Ordinal);
		Assert.Contains("runner: ubuntu-22.04-arm", workflow, StringComparison.Ordinal);
		Assert.Contains("/p:PublishSingleFile=true", workflow, StringComparison.Ordinal);
		Assert.Contains("/p:IncludeNativeLibrariesForSelfExtract=true", workflow, StringComparison.Ordinal);
		Assert.Contains("/p:PublishReadyToRun=true", workflow, StringComparison.Ordinal);
		Assert.Contains("/p:PublishTrimmed=false", workflow, StringComparison.Ordinal);
		Assert.Contains("appstreamcli validate --strict --explain", workflow, StringComparison.Ordinal);
		Assert.Contains("APPSTREAM_VERSION: 0.16.4", workflow, StringComparison.Ordinal);
		Assert.Contains("APPSTREAM_SHA256:", workflow, StringComparison.Ordinal);
		Assert.Contains("LIBXMLB_SHA256:", workflow, StringComparison.Ordinal);
		Assert.Contains("MESON_SHA256:", workflow, StringComparison.Ordinal);
		Assert.Contains("desktop-file-validate", workflow, StringComparison.Ordinal);
		Assert.Contains("appdir-lint.sh", workflow, StringComparison.Ordinal);
		Assert.Contains("Verify packaged binary identity", workflow, StringComparison.Ordinal);
		Assert.Contains("needs: [prepare, package]", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"if: ${{ needs.prepare.outputs.upload_release == 'true' }}",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("gh release upload", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("--updateinformation", workflow, StringComparison.Ordinal);
		Assert.Contains("output_path}.zsync", workflow, StringComparison.Ordinal);
		Assert.Contains("DevProjex-<version>-x86_64.AppImage", installation, StringComparison.Ordinal);
		Assert.Contains("--appimage-extract-and-run", installation, StringComparison.Ordinal);
		Assert.Contains("Open Anyway", installation, StringComparison.Ordinal);
		Assert.Contains("data/DevProjex", linuxPackaging, StringComparison.Ordinal);
		Assert.Contains("https://github.com/Avazbek22/DevProjex", linuxPackaging, StringComparison.Ordinal);
	}

	private static string FindRepositoryRoot()
		=> PublishedApplicationLocator.FindRepositoryRoot();
}
