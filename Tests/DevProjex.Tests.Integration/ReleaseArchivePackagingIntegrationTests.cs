using System.ComponentModel;
using System.Diagnostics;
using DevProjex.Tests.Integration.Helpers;
using DevProjex.Tests.Shared.StoreListing;

namespace DevProjex.Tests.Integration;

public sealed class ReleaseArchivePackagingIntegrationTests
{
	private static readonly Lazy<string> RepoRoot = new(StoreListingPaths.FindRepositoryRoot);

	[Fact]
	public void UstarHelper_RoundTripsModesAndProducesDeterministicTarGzip()
	{
		using var workspace = new TemporaryDirectory();
		var helperPath = Path.Combine(RepoRoot.Value, "Scripts", "release-archive-helpers.ps1");
		var scriptPath = workspace.CreateFile("verify-release-archive.ps1", BuildVerificationScript(helperPath));
		var shells = OperatingSystem.IsWindows()
			? new[] { "powershell", "pwsh" }
			: new[] { "pwsh" };

		foreach (var shell in shells)
		{
			var result = RunPowerShell(shell, scriptPath, workspace.Path);
			Assert.True(
				result.ExitCode == 0,
				$"Archive verification failed in {shell}.{Environment.NewLine}" +
				$"STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}" +
				$"STDERR:{Environment.NewLine}{result.StandardError}");
			Assert.Contains("archive-contract-ok", result.StandardOutput, StringComparison.Ordinal);
		}
	}

	private static string BuildVerificationScript(string helperPath)
	{
		var escapedHelperPath = helperPath.Replace("'", "''", StringComparison.Ordinal);
		return $$"""
			$ErrorActionPreference = 'Stop'
			. '{{escapedHelperPath}}'
			$payloadPath = Join-Path $PSScriptRoot 'DevProjex'
			[System.IO.File]::WriteAllText($payloadPath, 'release payload')
			$entries = @(
			    [pscustomobject]@{ Name = 'bundle/'; Mode = 493; IsDirectory = $true; SourcePath = ''; Bytes = $null },
			    [pscustomobject]@{ Name = 'bundle/DevProjex'; Mode = 493; IsDirectory = $false; SourcePath = $payloadPath; Bytes = $null },
			    [pscustomobject]@{ Name = 'bundle/Info.plist'; Mode = 420; IsDirectory = $false; SourcePath = ''; Bytes = [Text.Encoding]::UTF8.GetBytes('plist') }
			)
			$firstArchive = Join-Path $PSScriptRoot 'first.tar.gz'
			$secondArchive = Join-Path $PSScriptRoot 'second.tar.gz'
			New-UstarGzipArchive -archivePath $firstArchive -entries $entries
			New-UstarGzipArchive -archivePath $secondArchive -entries $entries
			$firstHash = Get-FileSha256Hex -path $firstArchive
			$secondHash = Get-FileSha256Hex -path $secondArchive
			if ($firstHash -ne $secondHash) { throw 'Archive output is not deterministic.' }
			$actual = @(Read-UstarGzipArchive -archivePath $firstArchive -captureEntryNames @('bundle/Info.plist'))
			if ($actual.Count -ne 3) { throw "Unexpected entry count: $($actual.Count)." }
			if ($actual[0].Name -ne 'bundle/' -or -not $actual[0].IsDirectory -or $actual[0].Mode -ne 493) { throw 'Directory entry mismatch.' }
			if ($actual[1].Name -ne 'bundle/DevProjex' -or $actual[1].Mode -ne 493 -or $actual[1].Size -le 0) { throw 'Executable entry mismatch.' }
			if ($actual[2].Name -ne 'bundle/Info.plist' -or $actual[2].Mode -ne 420 -or $actual[2].Size -le 0) { throw 'Metadata entry mismatch.' }
			& tar -tvf $firstArchive
			if ($LASTEXITCODE -ne 0) { throw "tar -tvf failed with exit code $LASTEXITCODE." }
			Write-Output 'archive-contract-ok'
			""";
	}

	private static (int ExitCode, string StandardOutput, string StandardError) RunPowerShell(
		string executable,
		string scriptPath,
		string workingDirectory)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("-NoLogo");
		startInfo.ArgumentList.Add("-NoProfile");
		if (OperatingSystem.IsWindows() && executable.Equals("powershell", StringComparison.Ordinal))
		{
			startInfo.ArgumentList.Add("-ExecutionPolicy");
			startInfo.ArgumentList.Add("Bypass");
		}
		startInfo.ArgumentList.Add("-File");
		startInfo.ArgumentList.Add(scriptPath);

		try
		{
			using var process = Process.Start(startInfo)
				?? throw new InvalidOperationException($"Could not start {executable}.");
			var standardOutput = process.StandardOutput.ReadToEnd();
			var standardError = process.StandardError.ReadToEnd();
			process.WaitForExit();
			return (process.ExitCode, standardOutput, standardError);
		}
		catch (Win32Exception exception)
		{
			throw new InvalidOperationException(
				$"Required PowerShell executable '{executable}' was not found.",
				exception);
		}
	}
}
