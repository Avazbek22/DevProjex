namespace DevProjex.Tests.UI;

internal sealed class UiTestProject : IDisposable
{
    private readonly string _rootPath;
    private readonly string _appDataPath;
    private readonly bool _ownsWorkspaceRoot;

    private UiTestProject(string rootPath, string appDataPath, bool ownsWorkspaceRoot)
    {
        _rootPath = rootPath;
        _appDataPath = appDataPath;
        _ownsWorkspaceRoot = ownsWorkspaceRoot;
    }

    public string RootPath => _rootPath;
    public string AppDataPath => _appDataPath;

    public static UiTestProject CreateDefault()
    {
        return Create(static rootPath =>
        {
            SeedDefaultWorkspace(rootPath);
        });
    }

    public static UiTestProject CreateWithSecretRedactionWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(
                rootPath,
                Path.Combine("src", "Secrets.cs"),
                "const string awsAccessKey = \"AKIA" + "Z7M3Q5X2P6N4R7T5\";\n");
            WriteFile(rootPath, "README.md", "# Secret redaction UI fixture\n");
        });
    }

    public static UiTestProject CreateWithScopedExtensionlessEntries()
    {
        return Create(static rootPath =>
        {
            SeedDefaultWorkspace(rootPath);
            WriteFile(rootPath, Path.Combine("src", "Makefile"), "build:\n\tdotnet build");
        });
    }

    public static UiTestProject CreateWithDynamicIgnoreEntries()
    {
        return Create(static rootPath =>
        {
            SeedDefaultWorkspace(rootPath);
            WriteFile(rootPath, Path.Combine("src", "Makefile"), "build:\n\tdotnet build");
            WriteFile(rootPath, Path.Combine("src", "empty.txt"), string.Empty);
            Directory.CreateDirectory(Path.Combine(rootPath, "src", "empty-folder"));
        });
    }

    public static UiTestProject CreateWithDeepHorizontalSearchWorkspace()
    {
        return Create(static rootPath =>
        {
            // Keep the results vertically realized so this fixture isolates horizontal navigation.
            var segments = Enumerable.Range(1, 5)
                .Select(static level => $"d{level:00}")
                .ToArray();
            var resultDirectory = Path.Combine(segments);
            WriteFile(
                rootPath,
                Path.Combine(
                    resultDirectory,
                    "horizontal-search-target-a-initial.cs"),
                "internal sealed class InitialHorizontalSearchTarget {}\n");
            WriteFile(
                rootPath,
                Path.Combine(
                    resultDirectory,
                    "horizontal-search-target-b-with-a-deliberately-long-name-that-exceeds-the-visible-tree-width-and-remains-clipped-in-a-wide-tree-pane.cs"),
                "internal sealed class HorizontalSearchTarget {}\n");
            WriteFile(
                rootPath,
                Path.Combine(
                    resultDirectory,
                    "horizontal-search-target-c-short.cs"),
                "internal sealed class FinalHorizontalSearchTarget {}\n");
        });
    }

    public static UiTestProject CreateWithHierarchicalAppSearchWorkspace()
    {
        return Create(static rootPath =>
        {
            for (var index = 1; index <= 12; index++)
            {
                WriteFile(
                    rootPath,
                    Path.Combine(
                        "Application",
                        $"Area{index:00}",
                        "SelectionOption.cs"),
                    $"internal sealed class SelectionOption{index:00} {{}}\n");
            }

            WriteFile(
                rootPath,
                Path.Combine("Application", "Application.csproj"),
                "<Project />\n");
            WriteFile(
                rootPath,
                Path.Combine(
                    "Apps",
                    "Avalonia",
                    "Coordinators",
                    "AppearanceSettingsController.cs"),
                "internal sealed class AppearanceSettingsController {}\n");
            for (var index = 1; index <= 24; index++)
            {
                WriteFile(
                    rootPath,
                    Path.Combine(
                        "Apps",
                        $"Module{index:00}",
                        "SelectionOption.cs"),
                    $"internal sealed class SelectionOption{index:00} {{}}\n");
            }

            WriteFile(
                rootPath,
                Path.Combine("Assets", "Localization", "en.json"),
                "{}\n");
        });
    }

    public static UiTestProject CreateWithLargeFlatTree(int fileCount = 2000)
    {
        return Create(rootPath =>
        {
            for (var index = 0; index < fileCount; index++)
            {
                WriteFile(
                    rootPath,
                    Path.Combine("bulk", $"file-{index:0000}.txt"),
                    $"content {index}");
            }
        });
    }

    public static UiTestProject CreateWithExtensionSensitiveEmptyFolders()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine("src", "ExtensionSensitive", "keep.cs"), BuildCSharpFile("AppCore.ExtensionSensitive", "Keep", 12));
            WriteFile(rootPath, Path.Combine("src", "ExtensionSensitive", "mixed-parent", "docs", "readme.md"), BuildMarkdown("Extension-sensitive folder", 12));
        });
    }

    public static UiTestProject CreateWithGitIgnoredExtensionlessNoise()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, ".gitignore", "obj/\nbin/\n");
            WriteFile(rootPath, "README", "visible extensionless");
            WriteFile(rootPath, Path.Combine("src", "Program.cs"), BuildCSharpFile("UiProbe", "Program", 8));
            WriteFile(rootPath, Path.Combine("obj", "Debug", "net10.0", "apphost"), "smart ignored apphost");
            WriteFile(rootPath, Path.Combine("obj", "Debug", "net10.0", "singlefilehost"), "smart ignored host");
            WriteFile(rootPath, Path.Combine("bin", "Debug", "net10.0", "createdump"), "smart ignored dump");
        });
    }

    public static UiTestProject CreateWithDotFolderExtensionlessNoise()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, "README", "visible extensionless");
            WriteFile(rootPath, Path.Combine("src", "Program.cs"), BuildCSharpFile("UiProbe", "Program", 8));

            for (var index = 0; index < 128; index++)
            {
                WriteFile(
                    rootPath,
                    Path.Combine(".cache", "nested", $"artifact-{index:000}"),
                    $"noise {index}");
            }
        });
    }

    public static UiTestProject CreateWithPythonSmartIgnoreWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, "pyproject.toml", "[project]\nname = \"ui-python\"\n");
            WriteFile(rootPath, Path.Combine("src", "app.py"), "print('ok')\n");
            WriteFile(rootPath, Path.Combine("src", "__pycache__", "app.pyc"), "binary");
            WriteFile(rootPath, Path.Combine("src", ".venv", "bin", "python"), "binary");
        });
    }

    public static UiTestProject CreateWithCleanPythonSmartWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, "pyproject.toml", "[project]\nname = \"ui-clean-python\"\n");
            WriteFile(rootPath, Path.Combine("src", "app.py"), "print('ok')\n");
        });
    }

    public static UiTestProject CreateWithIgnoredNumericExtensions()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, "App.csproj", "<Project />\n");
            WriteFile(rootPath, Path.Combine("src", "App.cs"), "class App {}\n");
            WriteFile(rootPath, "empty-root.1770912967589", string.Empty);
            WriteFile(rootPath, Path.Combine("src", "generated", "empty-nested.1770912967590"), string.Empty);
            WriteFile(rootPath, Path.Combine("src", ".transient.1770912967591"), "dot payload\n");
            WriteFile(rootPath, Path.Combine("src", "archive.1770912967592"), "visible payload\n");
            WriteFile(rootPath, Path.Combine("src", "visible.1770912967593"), "visible payload\n");
            WriteFile(rootPath, Path.Combine("src", "empty.1770912967593"), string.Empty);
        });
    }

    public static UiTestProject CreateWithTopLevelSmartArtifactWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine("src", "App.cs"), "class App {}\n");
            WriteFile(rootPath, Path.Combine("obj", "project.assets.json"), "{}\n");
        });
    }

    public static UiTestProject CreateWithSmartIgnoreNegativeMatrixWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, "App.csproj", "<Project />\n");
            WriteFile(rootPath, Path.Combine("src", "App.cs"), "class App {}\n");
            WriteFile(rootPath, Path.Combine("obj-backup", "project.assets.json"), "{}\n");
            WriteFile(rootPath, Path.Combine("build", "README.md"), "source build folder\n");
            WriteFile(rootPath, Path.Combine("build", "docs", "CMakeCache.txt"), "source documentation\n");
            WriteFile(rootPath, Path.Combine("vendor", "src", "autoload.php"), "<?php // source\n");
            WriteFile(rootPath, Path.Combine("packages", "Alpha", "Alpha.nupkg"), "single incomplete package\n");
            Directory.CreateDirectory(Path.Combine(rootPath, "packages", "Alpha", "lib"));
            WriteFile(rootPath, Path.Combine("m2-backup", "repository", "service", "package.json"), "{}\n");
            WriteFile(rootPath, Path.Combine("cmake-build", "CMakeCache.txt"), "source fixture\n");
        });
    }

    public static UiTestProject CreateWithCleanGitAndSmartWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, ".gitignore", "bin/\nobj/\nlogs/\n*.user\n");
            WriteFile(rootPath, "App.csproj", "<Project />\n");
            WriteFile(rootPath, "Program.cs", "Console.WriteLine(\"ok\");\n");
        });
    }

    public static UiTestProject CreateWithIgnoredNestedGitRepositoryWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, ".gitignore", "ignored-container/\n");
            WriteFile(rootPath, "App.csproj", "<Project />\n");
            WriteFile(rootPath, "Program.cs", "Console.WriteLine(\"outer\");\n");
            WriteFile(
                rootPath,
                Path.Combine("ignored-container", "nested", "Nested.csproj"),
                "<Project />\n");
            WriteFile(
                rootPath,
                Path.Combine("ignored-container", "nested", "Tracked.cs"),
                "namespace Nested;\n");
        });
    }

    public static UiTestProject CreateWithGitIgnoreDotFileOnlyWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, ".gitignore", ".env\n");
            WriteFile(rootPath, "App.csproj", "<Project />\n");
            WriteFile(rootPath, "Program.cs", "Console.WriteLine(\"ok\");\n");
            WriteFile(rootPath, ".env", "SECRET=1\n");
        });
    }

    public static UiTestProject CreateWithPythonGitIgnoreWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, ".gitignore", "*.log\n");
            WriteFile(rootPath, "requirements.txt", "pytest\n");
            WriteFile(rootPath, Path.Combine("src", "app.py"), "print('ok')\n");
            WriteFile(rootPath, Path.Combine("src", "__pycache__", "app.pyc"), "binary");
            WriteFile(rootPath, Path.Combine("logs", "app.log"), "ignored by gitignore");
        });
    }

    public static UiTestProject CreateWithPythonSmartIgnoreAndIdeaWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, "pyproject.toml", "[project]\nname = \"ui-python-idea\"\n");
            WriteFile(rootPath, Path.Combine("src", "app.py"), "print('ok')\n");
            WriteFile(rootPath, Path.Combine("src", "__pycache__", "app.pyc"), "binary");
            WriteFile(rootPath, Path.Combine(".idea", "workspace.xml"), "<project />\n");
            WriteFile(rootPath, Path.Combine(".idea", ".gitignore"), "# JetBrains internal ignore file\n");
        });
    }

    public static UiTestProject CreateWithNestedPythonSmartIgnoreAndIdeaWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine("lab2", "requirements.txt"), "pytest\n");
            WriteFile(rootPath, Path.Combine("lab2", "main.py"), "print('ok')\n");
            WriteFile(rootPath, Path.Combine("lab2", "report_lab2.txt"), "report\n");
            WriteFile(rootPath, Path.Combine("lab2", "var06.csv"), "value\n");
            WriteFile(rootPath, Path.Combine("lab2", "__pycache__", "main.cpython-312.pyc"), "binary");
            WriteFile(rootPath, Path.Combine("lab2", ".idea", "workspace.xml"), "<project />\n");
            WriteFile(rootPath, Path.Combine("lab2", ".idea", "lab2.iml"), "<module />\n");
            WriteFile(rootPath, "lab2 Peredelanniy.rar", "archive\n");
        });
    }

    public static UiTestProject CreateWithNestedPolyglotIgnoreMatrixWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine("api", ".gitignore"), "logs/\n");
            WriteFile(rootPath, Path.Combine("api", "App.csproj"), "<Project />\n");
            WriteFile(rootPath, Path.Combine("api", "src", "Program.cs"), "Console.WriteLine(\"ok\");\n");
            WriteFile(rootPath, Path.Combine("api", "bin", "Debug", "app.dll"), "binary");
            WriteFile(rootPath, Path.Combine("api", "logs", "runtime.log"), "git ignored\n");
            WriteFile(rootPath, Path.Combine("web", "package.json"), "{}\n");
            WriteFile(rootPath, Path.Combine("web", "src", "app.ts"), "export const ok = true;\n");
            WriteFile(rootPath, Path.Combine("web", "node_modules", "pkg", "index.js"), "module.exports = {};\n");
            WriteFile(rootPath, Path.Combine("python", "requirements.txt"), "pytest\n");
            WriteFile(rootPath, Path.Combine("python", "app.py"), "print('ok')\n");
            WriteFile(rootPath, Path.Combine("python", "__pycache__", "app.pyc"), "binary");
            WriteFile(rootPath, Path.Combine(".idea", "workspace.xml"), "<project />\n");
            WriteFile(rootPath, ".env", "APP_ENV=test\n");
            WriteFile(rootPath, "README", "extensionless docs\n");
            WriteFile(rootPath, "empty.txt", string.Empty);
            Directory.CreateDirectory(Path.Combine(rootPath, "empty-root"));
        });
    }

    public static UiTestProject CreateWithHierarchicalGitIgnoreCombatWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine("repo", ".gitignore"), "*.rootdrop\n!keep.rootdrop\n[unterminated\n");
            WriteFile(rootPath, Path.Combine("repo", "drop.rootdrop"), "ROOT-DROP-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "keep.rootdrop"), "ROOT-KEEP-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "module", ".gitignore"), "!module-keep.rootdrop\n*.moddrop\n");
            WriteFile(rootPath, Path.Combine("repo", "module", "module-keep.rootdrop"), "MODULE-KEEP-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "module", "drop.moddrop"), "MODULE-DROP-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "module", "child", ".gitignore"), "!rescue.moddrop\n*.deepdrop\n");
            WriteFile(rootPath, Path.Combine("repo", "module", "child", "rescue.moddrop"), "CHILD-RESCUE-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "module", "child", "drop.deepdrop"), "CHILD-DROP-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "module", "child", "grand", ".gitignore"), "!visible.deepdrop\n*.lastdrop\ninvalid\\\n");
            WriteFile(rootPath, Path.Combine("repo", "module", "child", "grand", "visible.deepdrop"), "GRAND-KEEP-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "module", "child", "grand", "drop.lastdrop"), "GRAND-DROP-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "module", "child", "grand", "invalid", "visible.txt"), "MALFORMED-RULE-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "sibling", ".gitignore"), "*.siblingdrop\n");
            WriteFile(rootPath, Path.Combine("repo", "sibling", "drop.siblingdrop"), "SIBLING-DROP-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("repo", "outside", "visible.siblingdrop"), "SIBLING-ISOLATION-SENTINEL\n");
        });
    }

    public static UiTestProject CreateWithRootExtensionIgnoreStressWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine("api", ".gitignore"), "*.log\n!important.log\n.git-owned/\n");
            WriteFile(rootPath, Path.Combine("api", "App.csproj"), "<Project />\n");
            WriteFile(rootPath, Path.Combine("api", "src", "Program.cs"), "Console.WriteLine(\"api\");\n");
            WriteFile(rootPath, Path.Combine("api", "src", "runtime.log"), "ignored by gitignore\n");
            WriteFile(rootPath, Path.Combine("api", "src", "important.log"), "explicitly unignored\n");
            WriteFile(rootPath, Path.Combine("api", ".git-owned", "payload.txt"), "git-owned dot root\n");
            WriteFile(rootPath, Path.Combine("api", ".idea", "workspace.xml"), "<project />\n");
            WriteFile(rootPath, Path.Combine("api", ".visible-dot", "inside.cs"), "class InsideDot {}\n");

            WriteFile(rootPath, Path.Combine("web", "package.json"), "{}\n");
            WriteFile(rootPath, Path.Combine("web", "src", "app.ts"), "export const ok = true;\n");
            WriteFile(rootPath, Path.Combine("web", "node_modules", "pkg", "index.js"), "module.exports = {};\n");
            WriteFile(rootPath, Path.Combine("web", "node_modules", "pkg", "app.ts"), "export const hidden = true;\n");
            WriteFile(rootPath, Path.Combine("web", ".cache", "cache.json"), "{}\n");

            WriteFile(rootPath, Path.Combine("docs", "readme.md"), "# docs\n");
            WriteFile(rootPath, Path.Combine("docs", ".draft", "notes.md"), "# draft\n");
        });
    }

    public static UiTestProject CreateWithHiddenDotFolderOverlapWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine("src", "Program.cs"), BuildCSharpFile("OverlapProbe", "Program", 6));
            WriteFile(rootPath, Path.Combine(".idea", "workspace.xml"), "<project />\n");
            WriteFile(rootPath, Path.Combine(".hidden-dot", "payload.txt"), "hidden dot payload\n");
            WriteFile(rootPath, Path.Combine(".git", "config.txt"), "[core]\n");
            TryMarkHidden(Path.Combine(rootPath, ".hidden-dot"));
            TryMarkHidden(Path.Combine(rootPath, ".git"));
        });
    }

    public static UiTestProject CreateWithExternalRefreshMutationWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine("src", "App.cs"), BuildCSharpFile("RefreshMutation", "App", 4));
            WriteFile(rootPath, Path.Combine("docs", "notes.md"), BuildMarkdown("Refresh notes", 4));
            WriteFile(rootPath, "data.csv", "id,value\n1,initial\n");
            WriteFile(rootPath, "empty.txt", string.Empty);
        });
    }

    public static UiTestProject CreateWithProjectLoadWorkflowWorkspace()
    {
        return CreateForSharedWorkspace(ProjectLoadWorkflowSharedWorkspace.RootPath);
    }

    public static UiTestProject CreateWithMixedTextAndBinaryMetricsWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine("src", "main.cs"), BuildCSharpFile("BinaryAware", "Program", 8));
            WriteFile(rootPath, Path.Combine("src", "notes.md"), BuildMarkdown("Binary-aware metrics", 8));
            WriteBinaryFile(rootPath, Path.Combine("src", "assets", "image.bin"), [0, 1, 2, 3, 255, 0, 4, 5]);
            WriteBinaryFile(rootPath, Path.Combine("src", "assets", "raw", "sprite.bin"), [137, 80, 78, 71, 13, 10, 26, 10]);
            WriteBinaryFile(rootPath, Path.Combine("src", "assets", "raw", "atlas.bin"), [0, 255, 10, 0, 11, 12, 13]);
            WriteFile(rootPath, Path.Combine("docs", "guide.md"), BuildMarkdown("Guide", 6));
            Directory.CreateDirectory(Path.Combine(rootPath, "docs", "empty"));
        });
    }

    public static UiTestProject CreateWithManagedGitCloneContentWorkspace()
    {
        return Create(static rootPath =>
        {
            WriteFile(rootPath, Path.Combine(".git", "HEAD"), "ref: refs/heads/main\n");
            WriteFile(rootPath, Path.Combine(".git", "objects", "pack", "pack-test.pack"), "git metadata\n");
            TryMarkHidden(Path.Combine(rootPath, ".git"));

            WriteFile(
                rootPath,
                Path.Combine("src", "CloneContentProbe.cs"),
                "namespace CloneProbe;\npublic static class CloneContentProbe { public const string Value = \"CLONE-CONTENT-SENTINEL\"; }\n");
            WriteFile(
                rootPath,
                Path.Combine("docs", "clone-guide.md"),
                "# Clone guide\n\nCLONE-DOCUMENTATION-SENTINEL\n");
            WriteFile(rootPath, Path.Combine("src", "empty.txt"), string.Empty);
            WriteBinaryFile(
                rootPath,
                Path.Combine("assets", "clone-image.bin"),
                [0, 1, 2, 3, 0, 255, 4, 5]);
        });
    }

    public static UiTestProject CreateWithUnicodeJsonWorkspace()
    {
        return Create(
            static rootPath =>
            {
                WriteFile(rootPath, Path.Combine("Документы", "Отчёт [финал].txt"), "Содержимое отчёта\n");
                WriteFile(rootPath, Path.Combine("Документы", "Сводка.txt"), "Содержимое сводки\n");
                WriteFile(rootPath, "Корень.txt", "Содержимое корневого файла\n");
            },
            workspaceDirectoryName: "рабочая папка");
    }

    private static UiTestProject Create(Action<string> seedWorkspace) =>
        Create(seedWorkspace, workspaceDirectoryName: "workspace");

    private static UiTestProject Create(Action<string> seedWorkspace, string workspaceDirectoryName)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "DevProjex",
            "DevProjex.Tests.UI");
        var instanceId = Guid.NewGuid().ToString("N");
        var rootPath = Path.Combine(testRoot, instanceId, workspaceDirectoryName);
        var appDataPath = Path.Combine(testRoot, instanceId, "appdata");

        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(appDataPath);
        seedWorkspace(rootPath);

        return new UiTestProject(rootPath, appDataPath, ownsWorkspaceRoot: true);
    }

    private static UiTestProject CreateForSharedWorkspace(string sharedRootPath)
    {
        var appDataPath = Path.Combine(
            Path.GetTempPath(),
            "DevProjex",
            "DevProjex.Tests.UI",
            Guid.NewGuid().ToString("N"),
            "appdata");
        Directory.CreateDirectory(appDataPath);

        // The workflow workspace is intentionally shared across heavy UI tests because the
        // application never mutates opened projects. Each window still receives an isolated
        // app-data sandbox, so persisted selection/profile state cannot bleed between tests.
        return new UiTestProject(sharedRootPath, appDataPath, ownsWorkspaceRoot: false);
    }

    public void Dispose()
    {
        try
        {
            if (_ownsWorkspaceRoot)
            {
                var instanceRoot = Directory.GetParent(_rootPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(instanceRoot) && Directory.Exists(instanceRoot))
                {
                    Directory.Delete(instanceRoot, recursive: true);
                    return;
                }

                if (Directory.Exists(_rootPath))
                    Directory.Delete(_rootPath, recursive: true);
            }

            if (Directory.Exists(_appDataPath))
            {
                var appDataInstanceRoot = Directory.GetParent(_appDataPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(appDataInstanceRoot) && Directory.Exists(appDataInstanceRoot))
                    Directory.Delete(appDataInstanceRoot, recursive: true);
                else
                    Directory.Delete(_appDataPath, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures from background file handles on CI.
        }
    }

    private static void WriteFile(string rootPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(rootPath, relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteBinaryFile(string rootPath, string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(rootPath, relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        File.WriteAllBytes(fullPath, content);
    }

    private static void TryMarkHidden(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            File.SetAttributes(path, attributes | FileAttributes.Hidden);
        }
        catch
        {
            // Hidden attributes are platform/filesystem dependent; tests assert the contract
            // only on platforms where the attribute is supported by the scanner.
        }
    }

    private static void SeedDefaultWorkspace(string rootPath)
    {
        WriteFile(rootPath, "README.md", BuildMarkdown("DevProjex UI test workspace", 24));
        WriteFile(rootPath, Path.Combine("docs", "app-preview-notes.md"), BuildMarkdown("App preview notes", 32));
        WriteFile(rootPath, Path.Combine("configs", "appsettings.json"), BuildJson("Production"));
        WriteFile(rootPath, Path.Combine("configs", "appsettings.Development.json"), BuildJson("Development"));
        WriteFile(rootPath, Path.Combine("src", "AppHost", "Program.cs"), BuildCSharpFile("AppHost", "Program", 52));
        WriteFile(rootPath, Path.Combine("src", "AppHost", "AppBootstrap.cs"), BuildCSharpFile("AppHost", "AppBootstrap", 44));
        WriteFile(rootPath, Path.Combine("src", "AppCore", "Services", "AppService.cs"), BuildCSharpFile("AppCore.Services", "AppService", 68));
        WriteFile(rootPath, Path.Combine("src", "AppCore", "Services", "PreviewService.cs"), BuildCSharpFile("AppCore.Services", "PreviewService", 74));
        WriteFile(rootPath, Path.Combine("src", "AppCore", "Features", "ApplicationFeature.cs"), BuildCSharpFile("AppCore.Features", "ApplicationFeature", 58));
        WriteFile(rootPath, Path.Combine("src", "AppCore", "Features", "FilterSupport.cs"), BuildCSharpFile("AppCore.Features", "FilterSupport", 46));
        WriteFile(rootPath, Path.Combine("src", "AppCore", "ViewModels", "AppViewModel.cs"), BuildCSharpFile("AppCore.ViewModels", "AppViewModel", 64));
        WriteFile(rootPath, Path.Combine("src", "AppCore", "Widgets", "AppWidget.cs"), BuildCSharpFile("AppCore.Widgets", "AppWidget", 48));
        WriteFile(rootPath, Path.Combine("tests", "AppHost.Tests", "AppServiceTests.cs"), BuildCSharpFile("AppHost.Tests", "AppServiceTests", 36));
        WriteFile(rootPath, Path.Combine("tests", "AppHost.Tests", "PreviewFeatureTests.cs"), BuildCSharpFile("AppHost.Tests", "PreviewFeatureTests", 42));
    }

    private static string BuildMarkdown(string title, int lineCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        for (var index = 1; index <= lineCount; index++)
            builder.AppendLine($"- app note line {index}: preview workspace stays readable and stable.");

        return builder.ToString();
    }

    private static string BuildJson(string environmentName)
    {
        return $$"""
        {
          "ApplicationName": "DevProjex.Tests.UI",
          "Environment": "{{environmentName}}",
          "Features": {
            "PreviewWorkspace": true,
            "AppSearch": true,
            "AppFilter": true
          }
        }
        """;
    }

    private static string BuildCSharpFile(string @namespace, string typeName, int methodCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"namespace {@namespace};");
        builder.AppendLine();
        builder.AppendLine($"public sealed class {typeName}");
        builder.AppendLine("{");

        for (var index = 1; index <= methodCount; index++)
        {
            builder.AppendLine($"    public string BuildAppValue{index}()");
            builder.AppendLine("    {");
            builder.AppendLine($"        return \"app-value-{index}\";");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }
}
