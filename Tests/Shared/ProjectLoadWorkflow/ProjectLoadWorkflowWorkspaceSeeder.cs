namespace DevProjex.Tests.Shared.ProjectLoadWorkflow;

public static class ProjectLoadWorkflowWorkspaceSeeder
{
    public static void Seed(string rootPath)
    {
        // This workspace intentionally mixes root-level files, dynamic ignore candidates,
        // gitignored content and smart-ignore folders. The goal is to exercise the same
        // cross-section interactions that previously looked correct in isolated tests
        // but diverged during real project loading and Apply workflows.
        WriteFile(rootPath, ".gitignore", "generated/\nlogs/\n");

        WriteFile(rootPath, "README.md", BuildMarkdown("Workflow root", 6));
        WriteFile(rootPath, "GLOBAL", "root extensionless payload\nstill visible when extensionless ignore is off\n");

        WriteFile(rootPath, Path.Combine("docs", "guide.md"), BuildMarkdown("Guide", 8));
        WriteFile(rootPath, Path.Combine("docs", "notes.txt"), "docs notes\nsecond line\n");
        WriteFile(rootPath, Path.Combine("docs", ".draft.txt"), "draft note\n");
        WriteFile(rootPath, Path.Combine("docs", "empty.txt"), string.Empty);
        Directory.CreateDirectory(Path.Combine(rootPath, "docs", "empty-dir"));
        WriteFile(rootPath, Path.Combine("docs", "archive", "README"), "docs archive extensionless\n");

        WriteFile(rootPath, Path.Combine("src", "App", "Program.cs"), BuildCSharpFile("Workflow.App", "Program", 12));
        WriteFile(rootPath, Path.Combine("src", "App", "appsettings.json"), "{ \"env\": \"test\" }\n");
        WriteFile(rootPath, Path.Combine("src", "App", "README"), "src extensionless note\n");
        WriteFile(rootPath, Path.Combine("src", "App", "empty.txt"), string.Empty);
        WriteFile(rootPath, Path.Combine("src", "App", "Features", "FeatureA.cs"), BuildCSharpFile("Workflow.App.Features", "FeatureA", 10));
        WriteFile(rootPath, Path.Combine("src", "Lib", "Library.cs"), BuildCSharpFile("Workflow.Lib", "Library", 9));

        WriteFile(rootPath, Path.Combine("samples", "demo", "Sample.cs"), BuildCSharpFile("Workflow.Samples", "Sample", 7));
        WriteFile(rootPath, Path.Combine("samples", "demo", "Walkthrough.md"), BuildMarkdown("Walkthrough", 5));

        WriteFile(rootPath, Path.Combine("generated", "out.txt"), "ignored generated output\n");
        WriteFile(rootPath, Path.Combine("logs", "app.log"), "ignored log output\n");
        WriteFile(rootPath, Path.Combine("node_modules", "pkg", "index.js"), "console.log('smart ignored');\n");

        WriteFile(rootPath, Path.Combine(".cache", "artifact-a"), "dot folder noise\n");
        WriteFile(rootPath, Path.Combine(".cache", "nested", "artifact-b"), "more dot folder noise\n");

        // Hidden entries are required to cover the real runtime contract of the
        // advanced ignore section. Older tests mostly exercised dot entries and
        // completely missed the native Hidden attribute path on Windows.
        var hiddenFolderPath = Path.Combine(rootPath, "stealth-root");
        Directory.CreateDirectory(Path.Combine(hiddenFolderPath, "nested"));
        WriteFile(rootPath, Path.Combine("stealth-root", "nested", "secret.cs"), BuildCSharpFile("Workflow.Hidden", "Secret", 4));
        TryMarkHidden(hiddenFolderPath);

        var hiddenFilePath = WriteFile(rootPath, Path.Combine("docs", "secret.txt"), "hidden docs payload\n");
        TryMarkHidden(hiddenFilePath);
    }

    private static string WriteFile(string rootPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(rootPath, relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    private static string BuildMarkdown(string title, int bulletCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        for (var index = 1; index <= bulletCount; index++)
            builder.AppendLine($"- workflow note {index}");

        return builder.ToString();
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
            builder.AppendLine($"    public string GetValue{index}()");
            builder.AppendLine("    {");
            builder.AppendLine($"        return \"value-{index}\";");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void TryMarkHidden(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.Hidden);
    }
}
