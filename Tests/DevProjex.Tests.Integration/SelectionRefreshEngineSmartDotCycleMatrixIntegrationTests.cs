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
    public void ComputeFullRefreshSnapshot_SingleGitIgnoreProject_KeepsGitSmartAndDotFoldersIndependent()
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
        AssertIgnoreOption(initial, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(initial, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertRootOptionVisible(initial, ".idea", expectedVisible: false, "single-gitignore-python");
        AssertRootOptionVisible(initial, "logs", expectedVisible: false, "single-gitignore-python");

        var gitOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateGitAndDotToggleContext(temp.Path, initial, gitIgnoreChecked: false, dotChecked: true));
        AssertIgnoreOption(gitOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(gitOff, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
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
        AssertIgnoreOption(restored, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(restored, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertEquivalentVisibleSnapshots(initial, restored);
    }

    [Fact]
    public void ComputeFullRefreshSnapshot_GitFullyCoversSmartCandidate_SmartAppearsOnlyAfterGitIsDisabled()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile(".gitignore", "__pycache__/\n");
        temp.CreateFile("requirements.txt", "pytest\n");
        temp.CreateFile("src/app.py", "print('ok')\n");
        temp.CreateFile("__pycache__/app.pyc", "binary");

        var services = CreateServices();
        var initial = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateDefaultContext(temp.Path));

        AssertIgnoreOption(initial, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(initial, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
        AssertRootOptionVisible(initial, "__pycache__", expectedVisible: false, "git-covers-smart");

        var gitOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                initial,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.UseGitIgnore] = false
                }));

        AssertIgnoreOption(gitOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(gitOff, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertRootOptionVisible(gitOff, "__pycache__", expectedVisible: false, "git-covers-smart");

        var allControllersOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                gitOff,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.UseGitIgnore] = false,
                    [IgnoreOptionId.SmartIgnore] = false
                }));

        AssertIgnoreOption(allControllersOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(allControllersOff, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
        AssertRootOptionVisible(allControllersOff, "__pycache__", expectedVisible: true, "git-covers-smart");
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

    [Fact]
    public void ComputeFullRefreshSnapshot_NestedPythonRoot_DoesNotLetHiddenProfileIgnoreStatesSuppressDotFolders()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("lab2/requirements.txt", "pytest\n");
        temp.CreateFile("lab2/main.py", "print('ok')\n");
        temp.CreateFile("lab2/report_lab2.txt", "report\n");
        temp.CreateFile("lab2/var06.csv", "value\n");
        temp.CreateFile("lab2/__pycache__/main.cpython-312.pyc", "binary");
        temp.CreateFile("lab2/.idea/workspace.xml", "<project />\n");
        temp.CreateFile("lab2/.idea/lab2.iml", "<module />\n");
        temp.CreateFile("lab2 Peredelanniy.rar", "archive\n");

        var services = CreateServices();
        var snapshot = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateNestedPythonProfileContextWithStaleHiddenIgnoreStates(temp.Path));

        AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        Assert.Contains(snapshot.IgnoreOptionStateCache, item => item.Key == IgnoreOptionId.EmptyFolders && item.Value);

        var smartOnly = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                snapshot,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.SmartIgnore] = true,
                    [IgnoreOptionId.DotFolders] = false,
                    [IgnoreOptionId.EmptyFolders] = false
                }));

        AssertIgnoreOption(smartOnly, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(smartOnly, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
        var tree = BuildTreeFromSnapshot(temp.Path, smartOnly);
        AssertPathVisible(tree, "lab2/.idea/workspace.xml");
        AssertPathHidden(tree, "lab2/__pycache__");
    }

    [Theory]
    [MemberData(nameof(StackCases))]
    public void ComputeFullRefreshSnapshot_NestedProject_AllOffAndSingleTogglesStayIndependentAcrossStacks(
        string stackName,
        string markerFile,
        string artifactFolder,
        string artifactFile)
    {
        Assert.False(string.IsNullOrWhiteSpace(stackName));
        using var temp = CreateNestedStackWorkspace(markerFile, artifactFolder, artifactFile);
        var services = CreateServices();

        var initial = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateNestedStackProfileContextWithStaleHiddenStates(temp.Path, markerFile));

        AssertIgnoreOption(initial, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(initial, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertNestedTreeState(
            temp.Path,
            initial,
            visiblePaths: ["project/src/app.txt"],
            hiddenPaths:
            [
                "project/.idea/workspace.xml",
                $"project/{artifactFolder}/{artifactFile}"
            ]);

        var smartOnly = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                initial,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.SmartIgnore] = true,
                    [IgnoreOptionId.DotFolders] = false,
                    [IgnoreOptionId.EmptyFolders] = false
                }));
        AssertIgnoreOption(smartOnly, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(smartOnly, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
        AssertNestedTreeState(
            temp.Path,
            smartOnly,
            visiblePaths: ["project/.idea/workspace.xml", "project/src/app.txt"],
            hiddenPaths: [$"project/{artifactFolder}/{artifactFile}"]);

        var dotOnly = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                smartOnly,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.SmartIgnore] = false,
                    [IgnoreOptionId.DotFolders] = true,
                    [IgnoreOptionId.EmptyFolders] = false
                }));
        AssertIgnoreOption(dotOnly, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(dotOnly, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertNestedTreeState(
            temp.Path,
            dotOnly,
            visiblePaths:
            [
                $"project/{artifactFolder}/{artifactFile}",
                "project/src/app.txt"
            ],
            hiddenPaths: ["project/.idea/workspace.xml"]);

        var allOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateAllIgnoreOffContext(temp.Path, dotOnly));
        Assert.DoesNotContain(allOff.IgnoreOptions, option => option.IsChecked);
        AssertNestedTreeState(
            temp.Path,
            allOff,
            visiblePaths:
            [
                "project/.idea/workspace.xml",
                $"project/{artifactFolder}/{artifactFile}",
                "project/src/app.txt"
            ],
            hiddenPaths: []);

        var converged = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateContextFromSnapshot(temp.Path, allOff));
        Assert.DoesNotContain(converged.IgnoreOptions, option => option.IsChecked);
        AssertNestedTreeState(
            temp.Path,
            converged,
            visiblePaths:
            [
                "project/.idea/workspace.xml",
                $"project/{artifactFolder}/{artifactFile}",
                "project/src/app.txt"
            ],
            hiddenPaths: []);
    }

    [Fact]
    public void ComputeFullRefreshSnapshot_NestedGitIgnoredDotFolder_DotFoldersDoesNotClaimGitIgnoredContent()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("project/.gitignore", ".idea/\n");
        temp.CreateFile("project/requirements.txt", "pytest\n");
        temp.CreateFile("project/src/app.py", "print('ok')\n");
        temp.CreateFile("project/.idea/workspace.xml", "<project />\n");
        temp.CreateFile("project/__pycache__/app.pyc", "binary");

        var services = CreateServices();
        var initial = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateNestedStackProfileContextWithStaleHiddenStates(temp.Path, "requirements.txt"));

        AssertIgnoreOption(initial, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(initial, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(initial, IgnoreOptionId.DotFolders, expectedVisible: false, expectedChecked: null);
        AssertNestedTreeState(
            temp.Path,
            initial,
            visiblePaths: ["project/src/app.py"],
            hiddenPaths:
            [
                "project/.idea/workspace.xml",
                "project/__pycache__/app.pyc"
            ]);

        var gitOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                initial,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.UseGitIgnore] = false,
                    [IgnoreOptionId.DotFolders] = false
                }));
        AssertIgnoreOption(gitOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
        AssertIgnoreOption(gitOff, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(gitOff, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
        AssertNestedTreeState(
            temp.Path,
            gitOff,
            visiblePaths:
            [
                "project/.idea/workspace.xml",
                "project/src/app.py"
            ],
            hiddenPaths: ["project/__pycache__/app.pyc"]);
    }

    [Fact]
    public void ComputeFullRefreshSnapshot_MixedNestedPolyglotWorkspace_AllOffAndSingleTogglesStayScoped()
    {
        using var temp = CreateMixedNestedPolyglotWorkspace();
        var services = CreateServices();

        var defaults = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateDefaultContext(temp.Path));

        var allOff = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateAllIgnoreOffContext(temp.Path, defaults));
        Assert.DoesNotContain(allOff.IgnoreOptions, option => option.IsChecked);
        AssertNestedTreeState(
            temp.Path,
            allOff,
            visiblePaths:
            [
                "api/bin/Debug/app.dll",
                "api/logs/runtime.log",
                "web/node_modules/pkg/index.js",
                "python/__pycache__/app.pyc",
                ".idea/workspace.xml",
                ".env",
                "README",
                "empty.txt",
                "empty-root"
            ],
            hiddenPaths: []);

        var smartOnly = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                allOff,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.SmartIgnore] = true,
                    [IgnoreOptionId.UseGitIgnore] = false,
                    [IgnoreOptionId.DotFolders] = false,
                    [IgnoreOptionId.DotFiles] = false,
                    [IgnoreOptionId.EmptyFolders] = false,
                    [IgnoreOptionId.EmptyFiles] = false,
                    [IgnoreOptionId.ExtensionlessFiles] = false
                }));
        AssertIgnoreOption(smartOnly, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
        AssertNestedTreeState(
            temp.Path,
            smartOnly,
            visiblePaths:
            [
                "api/logs/runtime.log",
                ".idea/workspace.xml",
                ".env",
                "README",
                "empty.txt",
                "empty-root"
            ],
            hiddenPaths:
            [
                "api/bin/Debug/app.dll",
                "web/node_modules/pkg/index.js",
                "python/__pycache__/app.pyc"
            ]);

        var gitOnly = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                smartOnly,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.SmartIgnore] = false,
                    [IgnoreOptionId.UseGitIgnore] = true,
                    [IgnoreOptionId.DotFolders] = false,
                    [IgnoreOptionId.DotFiles] = false,
                    [IgnoreOptionId.EmptyFolders] = false,
                    [IgnoreOptionId.EmptyFiles] = false,
                    [IgnoreOptionId.ExtensionlessFiles] = false
                }));
        AssertIgnoreOption(gitOnly, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
        AssertNestedTreeState(
            temp.Path,
            gitOnly,
            visiblePaths:
            [
                "api/bin/Debug/app.dll",
                "web/node_modules/pkg/index.js",
                "python/__pycache__/app.pyc",
                ".idea/workspace.xml",
                ".env",
                "README",
                "empty.txt",
                "empty-root"
            ],
            hiddenPaths: ["api/logs/runtime.log"]);

        var dotOnly = ComputeConvergedSnapshot(
            services,
            temp.Path,
            CreateManualIgnoreContext(
                temp.Path,
                gitOnly,
                new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.SmartIgnore] = false,
                    [IgnoreOptionId.UseGitIgnore] = false,
                    [IgnoreOptionId.DotFolders] = true,
                    [IgnoreOptionId.DotFiles] = true,
                    [IgnoreOptionId.EmptyFolders] = false,
                    [IgnoreOptionId.EmptyFiles] = false,
                    [IgnoreOptionId.ExtensionlessFiles] = false
                }));
        AssertIgnoreOption(dotOnly, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
        AssertIgnoreOption(dotOnly, IgnoreOptionId.DotFiles, expectedVisible: true, expectedChecked: true);
        AssertNestedTreeState(
            temp.Path,
            dotOnly,
            visiblePaths:
            [
                "api/bin/Debug/app.dll",
                "api/logs/runtime.log",
                "web/node_modules/pkg/index.js",
                "python/__pycache__/app.pyc",
                "README",
                "empty.txt",
                "empty-root"
            ],
            hiddenPaths:
            [
                ".idea/workspace.xml",
                ".env"
            ]);
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

    private static TemporaryDirectory CreateMixedNestedPolyglotWorkspace()
    {
        var temp = new TemporaryDirectory();
        temp.CreateFile("api/.gitignore", "logs/\n");
        temp.CreateFile("api/App.csproj", "<Project />\n");
        temp.CreateFile("api/src/Program.cs", "Console.WriteLine(\"ok\");\n");
        temp.CreateFile("api/bin/Debug/app.dll", "binary");
        temp.CreateFile("api/logs/runtime.log", "git ignored\n");
        temp.CreateFile("web/package.json", "{}\n");
        temp.CreateFile("web/src/app.ts", "export const ok = true;\n");
        temp.CreateFile("web/node_modules/pkg/index.js", "module.exports = {};\n");
        temp.CreateFile("python/requirements.txt", "pytest\n");
        temp.CreateFile("python/app.py", "print('ok')\n");
        temp.CreateFile("python/__pycache__/app.pyc", "binary");
        temp.CreateFile(".idea/workspace.xml", "<project />\n");
        temp.CreateFile(".env", "APP_ENV=test\n");
        temp.CreateFile("README", "extensionless docs\n");
        temp.CreateFile("empty.txt", string.Empty);
        temp.CreateDirectory("empty-root");
        return temp;
    }

    private static TemporaryDirectory CreateNestedStackWorkspace(
        string markerFile,
        string artifactFolder,
        string artifactFile)
    {
        var temp = new TemporaryDirectory();
        temp.CreateFile(Path.Combine("project", markerFile), MarkerContent(markerFile));
        temp.CreateFile(Path.Combine("project", artifactFolder, artifactFile), "artifact");
        temp.CreateFile("project/.idea/workspace.xml", "<project />\n");
        temp.CreateFile("project/src/app.txt", "visible\n");
        temp.CreateFile("archive.rar", "outside archive\n");
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

    private static SelectionRefreshContext CreateNestedPythonProfileContextWithStaleHiddenIgnoreStates(string rootPath) =>
        new(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Profile,
            AllRootFoldersChecked: false,
            AllExtensionsChecked: false,
            RootSelectionInitialized: true,
            RootSelectionCache: new HashSet<string>(PathComparer.Default) { "lab2" },
            ExtensionsSelectionInitialized: true,
            ExtensionsSelectionCache: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".py",
                ".txt",
                ".csv",
                ".rar"
            },
            IgnoreSelectionInitialized: true,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId> { IgnoreOptionId.SmartIgnore },
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.SmartIgnore] = true,
                [IgnoreOptionId.DotFolders] = true,
                [IgnoreOptionId.EmptyFolders] = true
            },
            IgnoreAllPreference: null,
            CurrentSnapshotState: EmptySnapshotState,
            RootOptionStateCache: new Dictionary<string, bool>(PathComparer.Default)
            {
                ["lab2"] = true
            },
            ExtensionOptionStateCache: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [".py"] = true,
                [".txt"] = true,
                [".csv"] = true,
                [".rar"] = true
            },
            IgnoreOptionStateCacheIsComplete: true);

    private static SelectionRefreshContext CreateNestedStackProfileContextWithStaleHiddenStates(
        string rootPath,
        string markerFile)
    {
        var extensionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetExtension(markerFile)] = true,
            [".txt"] = true,
            [".xml"] = true,
            [".rar"] = true
        };
        extensionStates.Remove(string.Empty);

        return new SelectionRefreshContext(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Profile,
            AllRootFoldersChecked: false,
            AllExtensionsChecked: false,
            RootSelectionInitialized: true,
            RootSelectionCache: new HashSet<string>(PathComparer.Default) { "project" },
            ExtensionsSelectionInitialized: true,
            ExtensionsSelectionCache: new HashSet<string>(extensionStates.Keys, StringComparer.OrdinalIgnoreCase),
            IgnoreSelectionInitialized: true,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId>
            {
                IgnoreOptionId.SmartIgnore,
                IgnoreOptionId.UseGitIgnore
            },
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.SmartIgnore] = true,
                [IgnoreOptionId.UseGitIgnore] = true,
                [IgnoreOptionId.DotFolders] = true,
                [IgnoreOptionId.EmptyFolders] = true,
                [IgnoreOptionId.EmptyFiles] = true
            },
            IgnoreAllPreference: null,
            CurrentSnapshotState: EmptySnapshotState,
            RootOptionStateCache: new Dictionary<string, bool>(PathComparer.Default)
            {
                ["project"] = true,
                ["archive.rar"] = false
            },
            ExtensionOptionStateCache: extensionStates,
            IgnoreOptionStateCacheIsComplete: true);
    }

    private static SelectionRefreshContext CreateAllIgnoreOffContext(
        string rootPath,
        SelectionRefreshSnapshot snapshot)
    {
        var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache);
        foreach (var option in snapshot.IgnoreOptions)
            stateCache[option.Id] = false;
        foreach (var optionId in stateCache.Keys.ToArray())
            stateCache[optionId] = false;

        return CreateContextFromSnapshot(rootPath, snapshot) with
        {
            IgnoreSelectionInitialized = true,
            IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
            IgnoreOptionStateCache = stateCache,
            IgnoreAllPreference = false
        };
    }

    private static TreeBuildResult BuildTreeFromSnapshot(string rootPath, SelectionRefreshSnapshot snapshot)
    {
        var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService()
            .Build(rootPath, CollectCheckedIgnoreOptionIds(snapshot), CollectCheckedRootNames(snapshot));

        return new TreeBuilder().Build(rootPath, new TreeFilterOptions(
            AllowedExtensions: CollectCheckedExtensionNames(snapshot),
            AllowedRootFolders: CollectCheckedRootNames(snapshot),
            IgnoreRules: rules));
    }

    private static void AssertNestedTreeState(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        IReadOnlyCollection<string> visiblePaths,
        IReadOnlyCollection<string> hiddenPaths)
    {
        var tree = BuildTreeFromSnapshot(rootPath, snapshot);
        foreach (var visiblePath in visiblePaths)
            AssertPathVisible(tree, visiblePath);
        foreach (var hiddenPath in hiddenPaths)
            AssertPathHidden(tree, hiddenPath);
    }

    private static void AssertPathVisible(TreeBuildResult tree, string relativePath)
    {
        Assert.True(ContainsPath(tree.Root, relativePath), $"Expected path '{relativePath}' to be visible.");
    }

    private static void AssertPathHidden(TreeBuildResult tree, string relativePath)
    {
        Assert.False(ContainsPath(tree.Root, relativePath), $"Expected path '{relativePath}' to be hidden.");
    }

    private static bool ContainsPath(FileSystemNode root, string relativePath)
    {
        var current = root;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Children.FirstOrDefault(
                child => string.Equals(child.Name, segment, StringComparison.Ordinal));
            if (next is null)
                return false;

            current = next;
        }

        return true;
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

    private static string MarkerContent(string markerFile)
    {
        return Path.GetExtension(markerFile).ToLowerInvariant() switch
        {
            ".json" => "{}",
            ".toml" => "[package]\nname = \"matrix\"\n",
            ".xml" => "<Project />",
            _ => string.Empty
        };
    }
}
