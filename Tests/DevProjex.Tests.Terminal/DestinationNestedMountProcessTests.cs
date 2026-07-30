using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class DestinationNestedMountProcessTests
{
	private const string RequireRootlessMountTestsVariable =
		"DEVPROJEX_REQUIRE_ROOTLESS_MOUNT_TESTS";

	[Fact]
	public async Task AnalyzeRejectsAliasToFileSystemMountedInsideSource()
	{
		if (!OperatingSystem.IsLinux())
			Assert.Skip("Linux mount namespaces and bind mounts are required.");

		var probe = await RunUnshareAsync(
			"""
			set -e
			base=$(mktemp -d /tmp/dpx-mount-probe.XXXXXX)
			trap 'umount "$base" 2>/dev/null || true; rmdir "$base" 2>/dev/null || true' EXIT
			mount -t tmpfs tmpfs "$base"
			""",
			environment: null);
		if (probe.ExitCode != 0)
		{
			SkipOrFailUnavailableRootlessMount(
				$"Rootless mount namespaces are unavailable: {probe.StandardError.Trim()}");
		}

		var applicationAssembly = FindApplicationAssembly();
		Assert.True(
			File.Exists(applicationAssembly),
			$"The built unified application was not found: {applicationAssembly}");
		var result = await RunUnshareAsync(
			"""
			set -euo pipefail
			mount --make-rprivate /
			base=$(mktemp -d /tmp/dpx-nested-mount.XXXXXX)
			cleanup()
			{
				umount "$base/safe-alias" 2>/dev/null || true
				umount "$base/alias" 2>/dev/null || true
				umount "$base/source/nested" 2>/dev/null || true
				umount "$base/backing" 2>/dev/null || true
				rm -rf "$base"
			}
			trap cleanup EXIT
			mkdir -p "$base/backing" "$base/source/nested" \
				"$base/alias" "$base/safe-alias" "$base/xdg"
			mount -t tmpfs tmpfs "$base/backing"
			mkdir -p "$base/backing/protected" "$base/backing/safe"
			mount --bind "$base/backing/protected" "$base/source/nested"
			mount --bind "$base/backing/protected" "$base/alias"
			mount --bind "$base/backing/safe" "$base/safe-alias"
			printf 'class App {}\n' > "$base/source/app.cs"
			set +e
			XDG_CONFIG_HOME="$base/xdg/config" \
			XDG_DATA_HOME="$base/xdg/data" \
			XDG_STATE_HOME="$base/xdg/state" \
			XDG_CACHE_HOME="$base/xdg/cache" \
			dotnet "$DPX_APPLICATION" analyze "$base/source" \
				--format text --plain --progress never \
				--git-mode none --exclude none \
				-o "$base/alias/report.txt" \
				>"$base/analyze.stdout" 2>"$base/analyze.stderr"
			analyze_rc=$?
			XDG_CONFIG_HOME="$base/xdg/config" \
			XDG_DATA_HOME="$base/xdg/data" \
			XDG_STATE_HOME="$base/xdg/state" \
			XDG_CACHE_HOME="$base/xdg/cache" \
			dotnet "$DPX_APPLICATION" export context "$base/source" \
				--view content --format text --plain --progress never \
				--git-mode none --exclude none \
				-o "$base/alias/context.txt" \
				>"$base/context.stdout" 2>"$base/context.stderr"
			context_rc=$?
			XDG_CONFIG_HOME="$base/xdg/config" \
			XDG_DATA_HOME="$base/xdg/data" \
			XDG_STATE_HOME="$base/xdg/state" \
			XDG_CACHE_HOME="$base/xdg/cache" \
			dotnet "$DPX_APPLICATION" export project "$base/source" \
				--as folder --plain --progress never \
				--git-mode none --exclude none \
				-o "$base/alias/project-folder" \
				>"$base/folder.stdout" 2>"$base/folder.stderr"
			folder_rc=$?
			XDG_CONFIG_HOME="$base/xdg/config" \
			XDG_DATA_HOME="$base/xdg/data" \
			XDG_STATE_HOME="$base/xdg/state" \
			XDG_CACHE_HOME="$base/xdg/cache" \
			dotnet "$DPX_APPLICATION" export project "$base/source" \
				--as zip --plain --progress never \
				--git-mode none --exclude none \
				-o "$base/alias/project.zip" \
				>"$base/zip.stdout" 2>"$base/zip.stderr"
			zip_rc=$?
			XDG_CONFIG_HOME="$base/xdg/config" \
			XDG_DATA_HOME="$base/xdg/data" \
			XDG_STATE_HOME="$base/xdg/state" \
			XDG_CACHE_HOME="$base/xdg/cache" \
			dotnet "$DPX_APPLICATION" profile export "$base/source" \
				--profile standard \
				-o "$base/alias/profile.json" \
				>"$base/profile.stdout" 2>"$base/profile.stderr"
			profile_rc=$?
			XDG_CONFIG_HOME="$base/xdg/config" \
			XDG_DATA_HOME="$base/xdg/data" \
			XDG_STATE_HOME="$base/xdg/state" \
			XDG_CACHE_HOME="$base/xdg/cache" \
			dotnet "$DPX_APPLICATION" analyze "$base/source" \
				--format text --plain --progress never \
				--git-mode none --exclude none \
				-o "$base/safe-alias/report.txt" \
				>"$base/safe.stdout" 2>"$base/safe.stderr"
			safe_rc=$?
			set -e
			source_visible=no
			alias_visible=no
			safe_visible=no
			test -n "$(find "$base/source/nested" -mindepth 1 -print -quit)" && source_visible=yes
			test -n "$(find "$base/alias" -mindepth 1 -print -quit)" && alias_visible=yes
			test -e "$base/safe-alias/report.txt" && safe_visible=yes
			printf 'analyze_rc=%s context_rc=%s folder_rc=%s zip_rc=%s profile_rc=%s safe_rc=%s source_visible=%s alias_visible=%s safe_visible=%s\n' \
				"$analyze_rc" "$context_rc" "$folder_rc" "$zip_rc" "$profile_rc" \
				"$safe_rc" "$source_visible" "$alias_visible" "$safe_visible"
			test "$analyze_rc" -eq 3
			test "$context_rc" -eq 3
			test "$folder_rc" -eq 3
			test "$zip_rc" -eq 3
			test "$profile_rc" -eq 3
			test "$safe_rc" -eq 0
			test "$source_visible" = no
			test "$alias_visible" = no
			test "$safe_visible" = yes
			for command in analyze context folder zip profile
			do
				test ! -s "$base/$command.stdout"
				grep -q 'DPX-EXPORT-UNSAFE-DESTINATION' "$base/$command.stderr"
			done
			test ! -s "$base/safe.stderr"
			grep -q "$base/safe-alias/report.txt" "$base/safe.stdout"
			test "$(cat "$base/source/app.cs")" = 'class App {}'
			""",
			new Dictionary<string, string?>
			{
				["DPX_APPLICATION"] = applicationAssembly
			});

		Assert.True(
			result.ExitCode == 0,
			$"Mount-namespace regression exited {result.ExitCode}." +
			$"{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}" +
			$"{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");
		Assert.Contains(
			"analyze_rc=3 context_rc=3 folder_rc=3 zip_rc=3 profile_rc=3 safe_rc=0 " +
			"source_visible=no alias_visible=no safe_visible=yes",
			result.StandardOutput,
			StringComparison.Ordinal);
		Assert.Empty(result.StandardError);
	}

	[Fact]
	public async Task ContextForceRejectsFileBindAliasToMountedSourceFile()
	{
		if (!OperatingSystem.IsLinux())
			Assert.Skip("Linux mount namespaces and bind mounts are required.");

		var probe = await RunUnshareAsync(
			"""
			set -e
			base=$(mktemp -d /tmp/dpx-file-mount-probe.XXXXXX)
			trap 'umount "$base" 2>/dev/null || true; rmdir "$base" 2>/dev/null || true' EXIT
			mount -t tmpfs tmpfs "$base"
			""",
			environment: null);
		if (probe.ExitCode != 0)
		{
			SkipOrFailUnavailableRootlessMount(
				$"Rootless mount namespaces are unavailable: {probe.StandardError.Trim()}");
		}

		var applicationAssembly = FindApplicationAssembly();
		Assert.True(
			File.Exists(applicationAssembly),
			$"The built unified application was not found: {applicationAssembly}");
		var result = await RunUnshareAsync(
			"""
			set -euo pipefail
			mount --make-rprivate /
			base=$(mktemp -d /tmp/dpx-file-mount.XXXXXX)
			cleanup()
			{
				umount "$base/alias.txt" 2>/dev/null || true
				umount "$base/source/protected.txt" 2>/dev/null || true
				umount "$base/backing" 2>/dev/null || true
				rm -rf "$base"
			}
			trap cleanup EXIT
			mkdir -p "$base/backing" "$base/source" "$base/xdg"
			mount -t tmpfs tmpfs "$base/backing"
			printf 'PROTECTED\n' > "$base/backing/value.txt"
			touch "$base/source/protected.txt" "$base/alias.txt"
			mount --bind "$base/backing/value.txt" "$base/source/protected.txt"
			mount --bind "$base/backing/value.txt" "$base/alias.txt"
			printf 'class App {}\n' > "$base/source/app.cs"
			set +e
			XDG_CONFIG_HOME="$base/xdg/config" \
			XDG_DATA_HOME="$base/xdg/data" \
			XDG_STATE_HOME="$base/xdg/state" \
			XDG_CACHE_HOME="$base/xdg/cache" \
			dotnet "$DPX_APPLICATION" export context "$base/source" \
				--view content --format text --plain --progress never \
				--git-mode none --exclude none --force \
				-o "$base/alias.txt" \
				>"$base/stdout" 2>"$base/stderr"
			cli_rc=$?
			set -e
			source_value=$(cat "$base/source/protected.txt")
			alias_value=$(cat "$base/alias.txt")
			temp_count=$(find "$base/source" "$base" \
				-maxdepth 1 -type f -name '.*.tmp' -print | wc -l)
			diagnostic_count=$(
				grep -c 'DPX-EXPORT-UNSAFE-DESTINATION' "$base/stderr" || true)
			printf 'cli_rc=%s source=%s alias=%s temp_count=%s diagnostic_count=%s\n' \
				"$cli_rc" "$source_value" "$alias_value" "$temp_count" \
				"$diagnostic_count"
			test "$cli_rc" -eq 3
			test "$source_value" = PROTECTED
			test "$alias_value" = PROTECTED
			test "$temp_count" -eq 0
			test "$diagnostic_count" -eq 1
			test ! -s "$base/stdout"
			test "$(cat "$base/source/app.cs")" = 'class App {}'
			""",
			new Dictionary<string, string?>
			{
				["DPX_APPLICATION"] = applicationAssembly
			});

		Assert.True(
			result.ExitCode == 0,
			$"File-mount regression exited {result.ExitCode}." +
			$"{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}" +
			$"{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");
		Assert.Contains(
			"cli_rc=3 source=PROTECTED alias=PROTECTED temp_count=0 diagnostic_count=1",
			result.StandardOutput,
			StringComparison.Ordinal);
		Assert.Empty(result.StandardError);
	}

	[Fact]
	public async Task AnalyzeRejectsNestedMountWhenSourceUsesCaseOnlyAlias()
	{
		if (!OperatingSystem.IsLinux())
			Assert.Skip("Linux mount namespaces and a case-insensitive volume are required.");

		var caseInsensitiveRoot = ResolveCaseInsensitiveRootOrSkip();
		var applicationAssembly = FindApplicationAssembly();
		var result = await RunUnshareAsync(
			"""
			set -euo pipefail
			mount --make-rprivate /
			base=$(mktemp -d "$DPX_CASE_ROOT/DevProjex Case Ж.XXXXXX")
			actual_source="$base/ProjectCaseNested"
			alias_source="$base/pROJECTcASEnESTED"
			cleanup()
			{
				umount "$base/alias" 2>/dev/null || true
				umount "$actual_source/nested" 2>/dev/null || true
				umount "$base/backing" 2>/dev/null || true
				rm -rf "$base"
			}
			trap cleanup EXIT
			mkdir -p "$base/backing" "$actual_source/nested" "$base/alias" "$base/xdg"
			mount -t tmpfs tmpfs "$base/backing"
			mkdir -p "$base/backing/protected"
			mount --bind "$base/backing/protected" "$actual_source/nested"
			mount --bind "$base/backing/protected" "$base/alias"
			printf 'class App {}\n' > "$actual_source/app.cs"
			test -d "$alias_source"
			set +e
			XDG_CONFIG_HOME="$base/xdg/config" \
			XDG_DATA_HOME="$base/xdg/data" \
			XDG_STATE_HOME="$base/xdg/state" \
			XDG_CACHE_HOME="$base/xdg/cache" \
			dotnet "$DPX_APPLICATION" analyze "$alias_source" \
				--format text --plain --progress never \
				--git-mode none --exclude none \
				-o "$base/alias/report.txt" \
				>"$base/stdout" 2>"$base/stderr"
			cli_rc=$?
			set -e
			source_visible=no
			test -e "$actual_source/nested/report.txt" && source_visible=yes
			printf 'cli_rc=%s source_visible=%s\n' "$cli_rc" "$source_visible"
			test "$cli_rc" -eq 3
			test "$source_visible" = no
			test ! -s "$base/stdout"
			grep -q 'DPX-EXPORT-UNSAFE-DESTINATION' "$base/stderr"
			test "$(cat "$actual_source/app.cs")" = 'class App {}'
			""",
			new Dictionary<string, string?>
			{
				["DPX_APPLICATION"] = applicationAssembly,
				["DPX_CASE_ROOT"] = caseInsensitiveRoot
			});

		Assert.True(
			result.ExitCode == 0,
			$"Case-alias mount regression exited {result.ExitCode}." +
			$"{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}" +
			$"{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");
		Assert.Contains(
			"cli_rc=3 source_visible=no",
			result.StandardOutput,
			StringComparison.Ordinal);
		Assert.Empty(result.StandardError);
	}

	private static async Task<ProcessResult> RunUnshareAsync(
		string script,
		IReadOnlyDictionary<string, string?>? environment)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo("unshare")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		foreach (var argument in new[] { "-Urnm", "bash", "-c", script })
			process.StartInfo.ArgumentList.Add(argument);
		if (environment is not null)
		{
			foreach (var pair in environment)
				process.StartInfo.Environment[pair.Key] = pair.Value;
		}

		try
		{
			Assert.True(process.Start());
		}
		catch (Exception exception) when (exception is
			       InvalidOperationException or
			       System.ComponentModel.Win32Exception)
		{
			SkipOrFailUnavailableRootlessMount(
				$"The unshare command is unavailable: {exception.GetType().Name}.");
		}

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(45));
		var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
		var standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
		try
		{
			await process.WaitForExitAsync(timeout.Token);
			return new ProcessResult(
				process.ExitCode,
				await standardOutputTask,
				await standardErrorTask);
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync(CancellationToken.None);
			}
		}
	}

	private static string FindApplicationAssembly()
	{
		var configuration = new DirectoryInfo(
				AppContext.BaseDirectory.TrimEnd(
					Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar))
			.Parent?
			.Name ?? "Debug";
		return Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"bin",
			configuration,
			"net10.0",
			"DevProjex.dll");
	}

	private static string ResolveCaseInsensitiveRootOrSkip()
	{
		var root = Environment.GetEnvironmentVariable(
			"DEVPROJEX_CASE_INSENSITIVE_TEST_ROOT");
		if (string.IsNullOrWhiteSpace(root) &&
		    Directory.Exists("/mnt/c"))
		{
			root = "/mnt/c";
		}
		if (string.IsNullOrWhiteSpace(root) ||
		    !Directory.Exists(root))
		{
			Assert.Skip(
				"Set DEVPROJEX_CASE_INSENSITIVE_TEST_ROOT to a writable " +
				"case-insensitive Linux-mounted volume.");
		}

		var probe = Path.Combine(
			root,
			$"DevProjexCaseProbe{Guid.NewGuid():N}");
		var alias = Path.Combine(
			root,
			Path.GetFileName(probe).ToUpperInvariant());
		try
		{
			Directory.CreateDirectory(probe);
			if (!Directory.Exists(alias))
			{
				Assert.Skip(
					$"The configured test root is case-sensitive: {root}");
			}
		}
		finally
		{
			if (Directory.Exists(probe))
				Directory.Delete(probe);
		}

		return root;
	}

	private static void SkipOrFailUnavailableRootlessMount(string message)
	{
		if (string.Equals(
			    Environment.GetEnvironmentVariable(RequireRootlessMountTestsVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Fail(
				$"{message} The release gate requires executable rootless mount tests.");
		}

		Assert.Skip(message);
	}

	private sealed record ProcessResult(
		int ExitCode,
		string StandardOutput,
		string StandardError);
}
