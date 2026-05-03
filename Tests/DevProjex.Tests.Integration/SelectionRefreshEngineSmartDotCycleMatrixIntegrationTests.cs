using DevProjex.Avalonia.Coordinators;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class SelectionRefreshEngineSmartDotCycleMatrixIntegrationTests
{
    [Theory]
    [MemberData(nameof(StackCases))]
    public void ComputeFullRefreshSnapshot_AllSupportedStacks_KeepSmartIgnoreAndDotFoldersIndependent(
        string stackName,
        string markerFile,
        string artifactFolder,
        string artifactFile)
    {
        using var temp = CreateStackWorkspace(markerFile, artifactFolder, artifactFile);
        var services = CreateServices();

        var initial = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateDefaultContext(temp.Path));
        AssertSmartAndDotState(initial, smartChecked: true, dotChecked: true);
        AssertRootOptionVisible(initial, artifactFolder, expectedVisible: false, stackName);
        AssertRootOptionVisible(initial, ".idea", expectedVisible: false, stackName);
        AssertRootOptionVisible(initial, "src", expectedVisible: true, stackName);

        var smartOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateIgnoreToggleContext(temp.Path, initial, smartChecked: false, dotChecked: true));
        AssertSmartAndDotState(smartOff, smartChecked: false, dotChecked: true);
        AssertRootOptionVisible(smartOff, artifactFolder, expectedVisible: true, stackName);
        AssertRootOptionVisible(smartOff, ".idea", expectedVisible: false, stackName);

        var bothOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateIgnoreToggleContext(temp.Path, smartOff, smartChecked: false, dotChecked: false));
        AssertSmartAndDotState(bothOff, smartChecked: false, dotChecked: false);
        AssertRootOptionVisible(bothOff, artifactFolder, expectedVisible: true, stackName);
        AssertRootOptionVisible(bothOff, ".idea", expectedVisible: true, stackName);

        var smartBack = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateIgnoreToggleContext(temp.Path, bothOff, smartChecked: true, dotChecked: false));
        AssertSmartAndDotState(smartBack, smartChecked: true, dotChecked: false);
        AssertRootOptionVisible(smartBack, artifactFolder, expectedVisible: false, stackName);
        AssertRootOptionVisible(smartBack, ".idea", expectedVisible: true, stackName);

        var restored = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateIgnoreToggleContext(temp.Path, smartBack, smartChecked: true, dotChecked: true));
        AssertSmartAndDotState(restored, smartChecked: true, dotChecked: true);
        AssertEquivalentVisibleSnapshots(initial, restored);
    }

    [Theory]
    [MemberData(nameof(StackCases))]
    public void ComputeLiveRefreshSnapshot_AllSupportedStacks_KeepsDotFolderOptionAvailableWhenAllRootsAreChecked(
        string stackName,
        string markerFile,
        string artifactFolder,
        string artifactFile)
    {
        using var temp = CreateStackWorkspace(markerFile, artifactFolder, artifactFile);
        var services = CreateServices();

        var fullSnapshot = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateDefaultContext(temp.Path));
        var liveContext = CreateContextFromSnapshot(temp.Path, fullSnapshot);
        var selectedRoots = CollectCheckedRootNames(fullSnapshot);

        var liveSnapshot = services.Engine.ComputeLiveRefreshSnapshot(
            liveContext,
            selectedRoots,
            CancellationToken.None);

        AssertSmartAndDotState(liveSnapshot, smartChecked: true, dotChecked: true);
        AssertRootOptionVisible(fullSnapshot, artifactFolder, expectedVisible: false, stackName);
        AssertRootOptionVisible(fullSnapshot, ".idea", expectedVisible: false, stackName);
    }

    [Fact]
    public void ComputeFullRefreshSnapshot_SingleGitIgnoreProject_UsesGitIgnoreAsSmartControllerAndKeepsDotFoldersIndependent()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile(".gitignore", "logs/\n");
        temp.CreateFile("pyproject.toml", "[project]\nname = \"git-python\"\n");
        temp.CreateFile("src/app.py", "print('ok')\n");
        temp.CreateFile("src/__pycache__/app.pyc", "binary");
        temp.CreateFile("logs/app.log", "ignored");
        temp.CreateFile(".idea/workspace.xml", "<project />\n");

        var services = CreateServices();
        var initial = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateDefaultContext(temp.Path));

        AssertIgnoreOption(initial, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(initial, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
        AssertIgnoreOption(initial, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertRootOptionVisible(initial, ".idea", expectedVisible: false, "single-gitignore-python");
        AssertRootOptionVisible(initial, "logs", expectedVisible: false, "single-gitignore-python");

        var gitOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateGitAndDotToggleContext(temp.Path, initial, gitIgnoreChecked: false, dotChecked: true));
        AssertIgnoreOption(gitOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(gitOff, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
        AssertIgnoreOption(gitOff, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertRootOptionVisible(gitOff, ".idea", expectedVisible: false, "single-gitignore-python");
        AssertRootOptionVisible(gitOff, "logs", expectedVisible: true, "single-gitignore-python");

        var dotOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateGitAndDotToggleContext(temp.Path, gitOff, gitIgnoreChecked: false, dotChecked: false));
        AssertIgnoreOption(dotOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(dotOff, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
        AssertRootOptionVisible(dotOff, ".idea", expectedVisible: true, "single-gitignore-python");
        AssertRootOptionVisible(dotOff, "logs", expectedVisible: true, "single-gitignore-python");

        var restored = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateGitAndDotToggleContext(temp.Path, dotOff, gitIgnoreChecked: true, dotChecked: true));
        AssertIgnoreOption(restored, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(restored, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
        AssertIgnoreOption(restored, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertEquivalentVisibleSnapshots(initial, restored);
    }

    [Fact]
    public void ComputeFullRefreshSnapshot_MixedGitAndSmartWorkspace_KeepsControllersIndependent()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("api/.gitignore", "logs/\n");
        temp.CreateFile("api/App.csproj", "<Project />\n");
        temp.CreateFile("api/Program.cs", "Console.WriteLine(\"ok\");\n");
        temp.CreateFile("api/logs/runtime.log", "git ignored\n");
        temp.CreateFile("web/package.json", "{ \"name\": \"web\" }\n");
        temp.CreateFile("web/src/app.ts", "export const ok = true;\n");
        temp.CreateFile("web/node_modules/pkg/generated.noise", "smart ignored\n");
        temp.CreateFile(".idea/workspace.xml", "<project />\n");

        var services = CreateServices();
        var initial = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateDefaultContext(temp.Path));
        AssertIgnoreOption(initial, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(initial, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(initial, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertExtensionOptionVisible(initial, ".cs", expectedVisible: true);
        AssertExtensionOptionVisible(initial, ".ts", expectedVisible: true);
        AssertExtensionOptionVisible(initial, ".log", expectedVisible: false);
        AssertExtensionOptionVisible(initial, ".noise", expectedVisible: false);
        AssertExtensionOptionVisible(initial, ".xml", expectedVisible: false);

        var gitOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                initial,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.UseGitIgnore] = false,
                    [IgnoreOptionId.SmartIgnore] = true,
                    [IgnoreOptionId.DotFolders] = true
                }));
        AssertIgnoreOption(gitOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(gitOff, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(gitOff, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertExtensionOptionVisible(gitOff, ".log", expectedVisible: true);
        AssertExtensionOptionVisible(gitOff, ".noise", expectedVisible: false);
        AssertExtensionOptionVisible(gitOff, ".xml", expectedVisible: false);

        var smartOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                gitOff,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.UseGitIgnore] = false,
                    [IgnoreOptionId.SmartIgnore] = false,
                    [IgnoreOptionId.DotFolders] = true
                }));
        AssertIgnoreOption(smartOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(smartOff, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(smartOff, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertExtensionOptionVisible(smartOff, ".log", expectedVisible: true);
        AssertExtensionOptionVisible(smartOff, ".noise", expectedVisible: true);
        AssertExtensionOptionVisible(smartOff, ".xml", expectedVisible: false);

        var dotOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                smartOff,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.UseGitIgnore] = false,
                    [IgnoreOptionId.SmartIgnore] = false,
                    [IgnoreOptionId.DotFolders] = false
                }));
        AssertIgnoreOption(dotOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(dotOff, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(dotOff, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
        AssertRootOptionVisible(dotOff, ".idea", expectedVisible: true, "mixed-git-smart-workspace");
        AssertExtensionOptionVisible(dotOff, ".log", expectedVisible: true);
        AssertExtensionOptionVisible(dotOff, ".noise", expectedVisible: true);
        AssertExtensionOptionVisible(dotOff, ".xml", expectedVisible: true);
    }

    public static IEnumerable<object[]> StackCases()
    {
        yield return ["frontend", "package.json", "node_modules", "index.js"];
        yield return ["dotnet", "App.csproj", "bin", "app.dll"];
        yield return ["python", "requirements.txt", "__pycache__", "app.pyc"];
        yield return ["jvm", "settings.gradle", "build", "classes.bin"];
        yield return ["rust", "Cargo.toml", "target", "app.bin"];
        yield return ["go", "go.work", "vendor", "module.go"];
        yield return ["php", "composer.json", "vendor", "autoload.php"];
        yield return ["ruby", "Gemfile.lock", "tmp", "cache.txt"];
    }

    private static TemporaryDirectory CreateStackWorkspace(
        string markerFile,
        string artifactFolder,
        string artifactFile)
    {
        var temp = new TemporaryDirectory();
        temp.CreateFile(markerFile, string.Empty);
        temp.CreateFile(Path.Combine(artifactFolder, artifactFile), "artifact");
        temp.CreateFile(".idea/workspace.xml", "<project />\n");
        temp.CreateFile("src/app.txt", "visible\n");
        return temp;
    }

    private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
        WorkflowServices services,
        string rootPath,
        SelectionRefreshContext context)
    {
        var firstSnapshot = services.Engine.ComputeFullRefreshSnapshot(context, CancellationToken.None);
        var convergedSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            CreateContextFromSnapshot(rootPath, firstSnapshot),
            CancellationToken.None);

        AssertEquivalentSnapshots(firstSnapshot, convergedSnapshot);
        return convergedSnapshot;
    }

    private static SelectionRefreshContext CreateIgnoreToggleContext(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        bool smartChecked,
        bool dotChecked)
    {
        return CreateManualIgnoreContext(
            rootPath,
            snapshot,
            new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.SmartIgnore] = smartChecked,
                [IgnoreOptionId.DotFolders] = dotChecked
            });
    }

    private static SelectionRefreshContext CreateGitAndDotToggleContext(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        bool gitIgnoreChecked,
        bool dotChecked)
    {
        return CreateManualIgnoreContext(
            rootPath,
            snapshot,
            new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.UseGitIgnore] = gitIgnoreChecked,
                [IgnoreOptionId.DotFolders] = dotChecked
            });
    }

    private static SelectionRefreshContext CreateManualIgnoreContext(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        IReadOnlyDictionary<IgnoreOptionId, bool> forcedStates)
    {
        var selected = CollectCheckedIgnoreOptionIds(snapshot);
        var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache);

        foreach (var (optionId, isChecked) in forcedStates)
        {
            stateCache[optionId] = isChecked;
            if (isChecked)
                selected.Add(optionId);
            else
                selected.Remove(optionId);
        }

        return CreateContextFromSnapshot(rootPath, snapshot) with
        {
            IgnoreSelectionInitialized = true,
            IgnoreSelectionCache = selected,
            IgnoreOptionStateCache = stateCache,
            IgnoreAllPreference = null
        };
    }

    private static void AssertSmartAndDotState(
        SelectionRefreshSnapshot snapshot,
        bool smartChecked,
        bool dotChecked)
    {
        AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, smartChecked);
        AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, dotChecked);
    }

    private static void AssertIgnoreOption(
        SelectionRefreshSnapshot snapshot,
        IgnoreOptionId optionId,
        bool expectedVisible,
        bool? expectedChecked)
    {
        var hasOption = false;
        var option = default(ResolvedIgnoreOptionState);
        foreach (var candidate in snapshot.IgnoreOptions)
        {
            if (candidate.Id != optionId)
                continue;

            Assert.False(hasOption, $"Ignore option '{optionId}' must not be duplicated.");
            hasOption = true;
            option = candidate;
        }

        if (!expectedVisible)
        {
            Assert.False(hasOption, $"Ignore option '{optionId}' must be hidden.");
            return;
        }

        Assert.True(hasOption, $"Ignore option '{optionId}' must be visible.");
        if (expectedChecked.HasValue)
            Assert.Equal(expectedChecked.Value, option.IsChecked);
    }

    private static void AssertRootOptionVisible(
        SelectionRefreshSnapshot snapshot,
        string rootName,
        bool expectedVisible,
        string caseName)
    {
        Assert.NotNull(snapshot.RootOptions);
        var rootOptions = snapshot.RootOptions;
        var actualVisible = rootOptions.Any(option => string.Equals(option.Name, rootName, StringComparison.Ordinal));
        Assert.True(
            actualVisible == expectedVisible,
            $"Case '{caseName}' expected root option '{rootName}' visible={expectedVisible}, actual={actualVisible}.");
    }

    private static void AssertExtensionOptionVisible(
        SelectionRefreshSnapshot snapshot,
        string extensionName,
        bool expectedVisible)
    {
        var actualVisible = snapshot.ExtensionOptions.Any(
            option => string.Equals(option.Name, extensionName, StringComparison.OrdinalIgnoreCase));
        Assert.True(
            actualVisible == expectedVisible,
            $"Expected extension option '{extensionName}' visible={expectedVisible}, actual={actualVisible}.");
    }
}
