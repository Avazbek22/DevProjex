namespace DevProjex.Tests.Integration;

public sealed class TreeBuilderTests
{
	// Verifies root-folder filtering applies to directories while root files still match extensions.
	[Fact]
	public void Build_FiltersByRootFoldersAndExtensions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class A {}");
		temp.CreateFile("src/readme.md", "hello");
		temp.CreateFile("docs/info.txt", "doc");
		temp.CreateFile("root.txt", "root");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "docs" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var children = result.Root.Children.Select(c => c.Name).ToList();
		Assert.Contains("docs", children);
		Assert.DoesNotContain("src", children);
		Assert.Contains("root.txt", children);

		var docs = result.Root.Children.First(c => c.Name == "docs");
		Assert.Single(docs.Children);
		Assert.Equal("info.txt", docs.Children[0].Name);
	}

	// Verifies directories are ordered before files in the root listing.
	[Fact]
	public void Build_OrdersDirectoriesBeforeFiles()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("a.txt", "a");
		temp.CreateFile("folder/b.txt", "b");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "folder" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Equal("folder", result.Root.Children.First().Name);
		Assert.False(result.Root.Children.Last().IsDirectory);
	}

	// Verifies no files are included when no extensions are allowed.
	[Fact]
	public void Build_SkipsFilesWhenAllowedExtensionsEmpty()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("root.txt", "root");
		temp.CreateFile("src/app.cs", "class A {}");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(),
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => !child.IsDirectory);
	}

	// Verifies dot folders are excluded when the ignore rule is set.
	[Fact]
	public void Build_RespectsDotFolderIgnoreRule()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".cache/hidden.txt", "hidden");
		temp.CreateFile("visible.txt", "visible");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreRules: new IgnoreRules(false, false, true, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == ".cache");
	}

	// Verifies smart-ignored folders are excluded.
	[Fact]
	public void Build_RespectsSmartIgnoredFolders()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("ignored/skip.txt", "skip");
		temp.CreateFile("keep/ok.txt", "ok");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ignored", "keep" },
			IgnoreRules: new IgnoreRules(false, false, false, false,
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ignored" },
				new HashSet<string>())
			{
				UseSmartIgnore = true
			});

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == "ignored");
		Assert.Contains(result.Root.Children, child => child.Name == "keep");
	}

	// Verifies allowed extensions matching is case-insensitive.
	[Fact]
	public void Build_RespectsAllowedExtensionsCaseInsensitive()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("note.txt", "note");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".TXT" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, child => child.Name == "note.txt");
	}

	// Verifies name filter keeps matching root files and matching descendants only.
	[Fact]
	public void Build_NameFilter_FiltersFilesAndDirectoriesBySubstring()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("order.cs", "class Order {}");
		temp.CreateFile("other.txt", "other");
		temp.CreateFile("src/order.handler.cs", "class Handler {}");
		temp.CreateFile("src/note.md", "note");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".md" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var rootNames = result.Root.Children.Select(c => c.Name).ToList();
		Assert.Contains("order.cs", rootNames);
		Assert.Contains("src", rootNames);
		Assert.DoesNotContain("other.txt", rootNames);

		var src = result.Root.Children.First(c => c.Name == "src");
		Assert.Single(src.Children);
		Assert.Equal("order.handler.cs", src.Children[0].Name);
	}

	// Verifies name filter keeps directories that contain matching children.
	[Fact]
	public void Build_NameFilter_IncludesDirectoryWhenChildMatches()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("domain/invoice.txt", "invoice");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "domain" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "invoice");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var domain = result.Root.Children.Single(c => c.Name == "domain");
		Assert.Single(domain.Children);
		Assert.Equal("invoice.txt", domain.Children[0].Name);
	}

	// Verifies name filter drops directories without matching descendants.
	[Fact]
	public void Build_NameFilter_ExcludesDirectoryWithoutMatches()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("services/service.txt", "service");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "services" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == "services");
	}

	// Verifies name filter keeps a matching directory even if it is empty.
	[Fact]
	public void Build_NameFilter_IncludesEmptyDirectoryWhenNameMatches()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateDirectory("orders");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "orders");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var orders = result.Root.Children.Single(c => c.Name == "orders");
		Assert.Empty(orders.Children);
	}

	// Verifies name filter respects allowed extensions for matching files.
	[Fact]
	public void Build_NameFilter_RespectsAllowedExtensions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/order.bin", "bin");
		temp.CreateFile("src/order.txt", "text");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var src = result.Root.Children.Single(c => c.Name == "src");
		Assert.Single(src.Children);
		Assert.Equal("order.txt", src.Children[0].Name);
	}

	// Verifies root folder filtering still applies when a name filter matches.
	[Fact]
	public void Build_NameFilter_DoesNotOverrideRootFolderFiltering()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("orders/order.txt", "order");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == "orders");
	}

	// Verifies name filter does not include root files without a match.
	[Fact]
	public void Build_NameFilter_ExcludesRootFilesWithoutMatch()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("root.txt", "root");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == "root.txt");
	}

	[Fact]
	public void Build_WithGitIgnoreNegation_KeepsExplicitlyUnignoredFile()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("build/keep.txt", "keep");
		temp.CreateFile("build/drop.txt", "drop");

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[] { "build/", "!build/keep.txt" });
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "build" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var build = result.Root.Children.Single(child => child.Name == "build");
		Assert.Contains(build.Children, child => child.Name == "keep.txt");
		Assert.DoesNotContain(build.Children, child => child.Name == "drop.txt");
	}

	#region GitIgnore Integration Tests

	[Fact]
	public void Build_GitIgnore_IgnoresBinObjDirectoriesWithCharacterClass()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/MyProject/Program.cs", "class Program {}");
		temp.CreateFile("src/MyProject/bin/Debug/app.dll", "dll");
		temp.CreateFile("src/MyProject/obj/Debug/cache.txt", "cache");
		temp.CreateFile("src/MyProject/Bin/Release/app.exe", "exe"); // Capital B
		temp.CreateFile("src/MyProject/Obj/Release/data.txt", "data"); // Capital O

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[]
		{
			"[Bb]in/",
			"[Oo]bj/"
		});
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".dll", ".exe", ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var myProject = result.Root.Children
			.Single(c => c.Name == "src").Children
			.Single(c => c.Name == "MyProject");

		Assert.DoesNotContain(myProject.Children, c => c.Name == "bin");
		Assert.DoesNotContain(myProject.Children, c => c.Name == "Bin");
		Assert.DoesNotContain(myProject.Children, c => c.Name == "obj");
		Assert.DoesNotContain(myProject.Children, c => c.Name == "Obj");
		Assert.Contains(myProject.Children, c => c.Name == "Program.cs");
	}

	[Fact]
	public void Build_GitIgnore_IgnoresNestedBinObjWithDoubleAsterisk()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("root.cs", "root");
		temp.CreateFile("Project1/src.cs", "src1");
		temp.CreateFile("Project1/bin/Debug/app.dll", "dll");
		temp.CreateFile("Project2/src.cs", "src2");
		temp.CreateFile("Project2/obj/cache.txt", "cache");
		temp.CreateFile("deep/nested/Project3/bin/out.dll", "out");

		// Use directory patterns to ignore bin/obj folders themselves
		var matcher = GitIgnoreMatcher.Build(temp.Path, new[]
		{
			"[Bb]in/",
			"[Oo]bj/"
		});
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".dll", ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Project1", "Project2", "deep" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		// Project1 should have src.cs but bin should be excluded
		var project1 = result.Root.Children.Single(c => c.Name == "Project1");
		Assert.Contains(project1.Children, c => c.Name == "src.cs");
		Assert.DoesNotContain(project1.Children, c => c.Name == "bin");

		// Project2 should have src.cs but obj should be excluded
		var project2 = result.Root.Children.Single(c => c.Name == "Project2");
		Assert.Contains(project2.Children, c => c.Name == "src.cs");
		Assert.DoesNotContain(project2.Children, c => c.Name == "obj");
	}

	[Fact]
	public void Build_GitIgnore_NodeModulesIgnored()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("package.json", "{}");
		temp.CreateFile("src/index.js", "code");
		temp.CreateFile("node_modules/lodash/index.js", "lib");
		temp.CreateFile("packages/app/node_modules/react/index.js", "react");

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[] { "node_modules/" });
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".js", ".json" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "node_modules", "packages" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, c => c.Name == "node_modules");
		Assert.Contains(result.Root.Children, c => c.Name == "src");

		if (result.Root.Children.Any(c => c.Name == "packages"))
		{
			var packages = result.Root.Children.Single(c => c.Name == "packages");
			var app = packages.Children.SingleOrDefault(c => c.Name == "app");
			if (app != null)
				Assert.DoesNotContain(app.Children, c => c.Name == "node_modules");
		}
	}

	[Fact]
	public void Build_GitIgnore_PythonPycacheIgnored()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("main.py", "code");
		temp.CreateFile("__pycache__/main.cpython-311.pyc", "cache");
		temp.CreateFile("src/app.py", "app");
		temp.CreateFile("src/__pycache__/app.cpython-311.pyc", "cache");
		temp.CreateFile("module.pyc", "compiled");

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[]
		{
			"__pycache__/",
			"*.py[cod]"
		});
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".pyc" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "__pycache__" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, c => c.Name == "main.py");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "__pycache__");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "module.pyc");

		var src = result.Root.Children.Single(c => c.Name == "src");
		Assert.Contains(src.Children, c => c.Name == "app.py");
		Assert.DoesNotContain(src.Children, c => c.Name == "__pycache__");
	}

	[Fact]
	public void Build_GitIgnore_JavaTargetIgnored()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pom.xml", "<project/>");
		temp.CreateFile("src/main/java/App.java", "class App {}");
		temp.CreateFile("target/classes/App.class", "compiled");
		temp.CreateFile("module/target/output.jar", "jar");

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[]
		{
			"target/",
			"*.class"
		});
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".java", ".xml", ".class", ".jar" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "target", "module" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, c => c.Name == "target");
		Assert.Contains(result.Root.Children, c => c.Name == "src");
	}

	[Fact]
	public void Build_GitIgnore_GoVendorIgnored()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("main.go", "package main");
		temp.CreateFile("go.mod", "module example");
		temp.CreateFile("vendor/github.com/pkg/lib.go", "lib");
		temp.CreateFile("app.exe", "binary");

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[]
		{
			"vendor/",
			"*.exe"
		});
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".go", ".mod", ".exe" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vendor" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, c => c.Name == "main.go");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "vendor");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "app.exe");
	}

	[Fact]
	public void Build_GitIgnore_RubyBundleIgnored()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Gemfile", "source 'https://rubygems.org'");
		temp.CreateFile("app/main.rb", "code");
		temp.CreateFile("vendor/bundle/ruby/gems/rack/lib.rb", "lib");
		temp.CreateFile("log/development.log", "log");

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[]
		{
			"vendor/bundle/",
			"log/"
		});
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".rb", ".log", "" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app", "vendor", "log" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, c => c.Name == "app");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "log");
	}

	[Fact]
	public void Build_GitIgnore_RustTargetIgnored()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Cargo.toml", "[package]");
		temp.CreateFile("src/main.rs", "fn main() {}");
		temp.CreateFile("target/debug/app", "binary");
		temp.CreateFile("src/lib.rs.bk", "backup");

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[]
		{
			"/target/",
			"**/*.rs.bk"
		});
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".rs", ".toml", ".bk", "" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "target" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, c => c.Name == "src");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "target");

		var src = result.Root.Children.Single(c => c.Name == "src");
		Assert.Contains(src.Children, c => c.Name == "main.rs");
		Assert.DoesNotContain(src.Children, c => c.Name == "lib.rs.bk");
	}

	#endregion

	#region Ignore Rules Priority Tests

	[Fact]
	public void Build_GitIgnoreTakesPriorityOverDotFolders()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".git/config", "git config");
		temp.CreateFile(".github/workflows/ci.yml", "workflow"); // Should be visible if gitignore allows
		temp.CreateFile(".hidden/secret.txt", "secret");
		temp.CreateFile("src/app.cs", "code");

		// GitIgnore only ignores .git, not .github or .hidden
		var matcher = GitIgnoreMatcher.Build(temp.Path, new[] { ".git/" });
		var rules = new IgnoreRules(false, false, false, false, // DotFolders is false
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".yml", ".txt", "" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", ".git", ".github", ".hidden" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		// .git is ignored by gitignore
		Assert.DoesNotContain(result.Root.Children, c => c.Name == ".git");
		// .github and .hidden are visible (DotFolders is false, gitignore doesn't ignore them)
		Assert.Contains(result.Root.Children, c => c.Name == ".github");
		Assert.Contains(result.Root.Children, c => c.Name == ".hidden");
	}

	[Fact]
	public void Build_DotFoldersIgnoredWhenGitIgnoreDisabled()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".config/settings.json", "{}");
		temp.CreateFile(".vscode/launch.json", "{}");
		temp.CreateFile("src/app.cs", "code");

		var rules = new IgnoreRules(false, false, true, false, // IgnoreDotFolders = true
			new HashSet<string>(), new HashSet<string>());
		// Note: UseGitIgnore defaults to false

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".json" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", ".config", ".vscode" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, c => c.Name == ".config");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == ".vscode");
		Assert.Contains(result.Root.Children, c => c.Name == "src");
	}

	[Fact]
	public void Build_SmartIgnoreTakesPriorityAfterGitIgnore()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "code");
		temp.CreateFile("ignored/data.txt", "data"); // Smart-ignored folder
		temp.CreateFile("build/out.dll", "dll"); // GitIgnore-ignored

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[] { "build/" });
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ignored" },
			new HashSet<string>())
		{
			UseGitIgnore = true,
			UseSmartIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".dll" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "ignored", "build" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, c => c.Name == "src");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "ignored"); // Smart-ignored
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "build"); // GitIgnore-ignored
	}

	[Fact]
	public void Build_CombinedIgnoreRules_AllApplied()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "code");
		temp.CreateFile(".hidden/secret.txt", "secret"); // Dot folder
		temp.CreateFile("node_modules/lib.js", "lib"); // GitIgnore
		temp.CreateFile("smart_ignored/data.txt", "data"); // Smart ignore
		temp.CreateFile(".env", "secret"); // Dot file

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[] { "node_modules/" });
		var rules = new IgnoreRules(false, true, true, true, // DotFolders, DotFiles
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "smart_ignored" },
			new HashSet<string>())
		{
			UseGitIgnore = true,
			UseSmartIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".js", "" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", ".hidden", "node_modules", "smart_ignored" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, c => c.Name == "src");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == ".hidden");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "node_modules");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "smart_ignored");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == ".env");
	}

	#endregion

	#region Cross-Platform Path Tests

	[Fact]
	public void Build_HandlesLongFilePaths()
	{
		using var temp = new TemporaryDirectory();
		var deepPath = string.Join("/", Enumerable.Repeat("a", 20));
		temp.CreateFile($"{deepPath}/file.txt", "content");

		var rules = new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>());

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.NotEmpty(result.Root.Children);
	}

	[Fact]
	public void Build_HandlesUnicodeFileNames()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("документы/файл.txt", "content");
		temp.CreateFile("文档/文件.txt", "content");

		var rules = new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>());

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "документы", "文档" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, c => c.Name == "документы");
		Assert.Contains(result.Root.Children, c => c.Name == "文档");
	}

	[Fact]
	public void Build_HandlesSpacesInPaths()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("my folder/my file.txt", "content");
		temp.CreateFile("another folder/nested folder/file.txt", "content");

		var rules = new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>());

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "my folder", "another folder" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, c => c.Name == "my folder");
		Assert.Contains(result.Root.Children, c => c.Name == "another folder");
	}

	#endregion

	#region Dot Files Ignore Tests

	[Fact]
	public void Build_IgnoresDotFilesWhenEnabled()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "*.log");
		temp.CreateFile(".env", "SECRET=123");
		temp.CreateFile(".eslintrc", "{}");
		temp.CreateFile("src/app.js", "code");
		temp.CreateFile("readme.md", "readme");

		var rules = new IgnoreRules(false, true, false, false, // IgnoreDotFiles = true
			new HashSet<string>(), new HashSet<string>());

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".js", ".md", "" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, c => c.Name == ".gitignore");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == ".env");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == ".eslintrc");
		Assert.Contains(result.Root.Children, c => c.Name == "readme.md");
	}

	[Fact]
	public void Build_ShowsDotFilesWhenDisabled()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "*.log");
		temp.CreateFile(".env", "SECRET=123");
		temp.CreateFile("src/app.js", "code");

		var rules = new IgnoreRules(false, false, false, false, // IgnoreDotFiles = false
			new HashSet<string>(), new HashSet<string>());

		// Note: .gitignore has extension ".gitignore", .env has extension ".env"
		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".js", ".gitignore", ".env" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, c => c.Name == ".gitignore");
		Assert.Contains(result.Root.Children, c => c.Name == ".env");
	}

	#endregion

	#region Smart Ignore Files Tests

	[Fact]
	public void Build_IgnoresSmartIgnoredFiles()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Thumbs.db", "cache");
		temp.CreateFile(".DS_Store", "cache");
		temp.CreateFile("desktop.ini", "config");
		temp.CreateFile("src/app.cs", "code");

		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db", ".DS_Store", "desktop.ini" })
		{
			UseSmartIgnore = true
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".db", ".ini", "" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, c => c.Name == "Thumbs.db");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == ".DS_Store");
		Assert.DoesNotContain(result.Root.Children, c => c.Name == "desktop.ini");
		Assert.Contains(result.Root.Children, c => c.Name == "src");
	}

	#endregion

	#region Edge Cases

	[Fact]
	public void Build_EmptyDirectory_ReturnsEmptyChildren()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateDirectory("empty");

		var rules = new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>());

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "empty" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var empty = result.Root.Children.Single(c => c.Name == "empty");
		Assert.Empty(empty.Children);
	}

	[Fact]
	public void Build_DeeplyNestedStructure_WorksCorrectly()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("a/b/c/d/e/f/g/h/i/j/file.txt", "deep");

		var rules = new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>());

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var current = result.Root.Children.Single(c => c.Name == "a");
		foreach (var name in new[] { "b", "c", "d", "e", "f", "g", "h", "i", "j" })
		{
			current = current.Children.Single(c => c.Name == name);
		}
		Assert.Single(current.Children);
		Assert.Equal("file.txt", current.Children[0].Name);
	}

	[Fact]
	public void Build_MixedFileTypes_OnlyAllowedExtensionsIncluded()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "code");
		temp.CreateFile("src/app.js", "js");
		temp.CreateFile("src/app.py", "py");
		temp.CreateFile("src/app.rb", "rb");
		temp.CreateFile("src/app.go", "go");

		var rules = new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>());

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".js" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var src = result.Root.Children.Single(c => c.Name == "src");
		Assert.Equal(2, src.Children.Count);
		Assert.Contains(src.Children, c => c.Name == "app.cs");
		Assert.Contains(src.Children, c => c.Name == "app.js");
		Assert.DoesNotContain(src.Children, c => c.Name == "app.py");
		Assert.DoesNotContain(src.Children, c => c.Name == "app.rb");
		Assert.DoesNotContain(src.Children, c => c.Name == "app.go");
	}

	#endregion
}

