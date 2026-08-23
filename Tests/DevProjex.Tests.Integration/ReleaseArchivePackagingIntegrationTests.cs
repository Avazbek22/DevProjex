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

	[Fact]
	public void IcnsHelper_MapsEverySlotAndRejectsMismatchedPngDimensions()
	{
		using var workspace = new TemporaryDirectory();
		var helperPath = Path.Combine(RepoRoot.Value, "Scripts", "release-archive-helpers.ps1");
		var iconSetPath = Path.Combine(RepoRoot.Value, "Assets", "AppIcon", "MacOS", "AppIconSet");
		var scriptPath = workspace.CreateFile(
			"verify-release-icns.ps1",
			BuildIcnsVerificationScript(helperPath, iconSetPath));
		var shells = OperatingSystem.IsWindows()
			? new[] { "powershell", "pwsh" }
			: new[] { "pwsh" };

		foreach (var shell in shells)
		{
			var result = RunPowerShell(shell, scriptPath, workspace.Path);
			Assert.True(
				result.ExitCode == 0,
				$"ICNS verification failed in {shell}.{Environment.NewLine}" +
				$"STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}" +
				$"STDERR:{Environment.NewLine}{result.StandardError}");
			Assert.Contains("icns-contract-ok", result.StandardOutput, StringComparison.Ordinal);
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

	private static string BuildIcnsVerificationScript(string helperPath, string iconSetPath)
	{
		var escapedHelperPath = helperPath.Replace("'", "''", StringComparison.Ordinal);
		var escapedIconSetPath = iconSetPath.Replace("'", "''", StringComparison.Ordinal);
		return $$"""
			$ErrorActionPreference = 'Stop'
			. '{{escapedHelperPath}}'
			$iconSetPath = '{{escapedIconSetPath}}'
			$outputPath = Join-Path $PSScriptRoot 'app.icns'
			New-DeterministicIcns -iconSetPath $iconSetPath -outputPath $outputPath
			$actual = [System.IO.File]::ReadAllBytes($outputPath)
			$expected = @(
			    [pscustomobject]@{ Type = 'ic07'; File = '128.png' },
			    [pscustomobject]@{ Type = 'ic08'; File = '256.png' },
			    [pscustomobject]@{ Type = 'ic09'; File = '512.png' },
			    [pscustomobject]@{ Type = 'ic10'; File = '1024.png' },
			    [pscustomobject]@{ Type = 'ic11'; File = '32.png' },
			    [pscustomobject]@{ Type = 'ic12'; File = '64.png' },
			    [pscustomobject]@{ Type = 'ic13'; File = '256.png' },
			    [pscustomobject]@{ Type = 'ic14'; File = '512.png' }
			)
			$offset = 8
			foreach ($slot in $expected) {
			    $type = [System.Text.Encoding]::ASCII.GetString($actual, $offset, 4)
			    $entryLength = [int](Read-BigEndianUInt32 -bytes $actual -offset ($offset + 4))
			    $expectedPayload = [System.IO.File]::ReadAllBytes((Join-Path $iconSetPath $slot.File))
			    if ($type -ne $slot.Type -or $entryLength -ne (8 + $expectedPayload.Length)) {
			        throw "ICNS slot mismatch for $($slot.Type)."
			    }
			    for ($index = 0; $index -lt $expectedPayload.Length; $index++) {
			        if ($actual[$offset + 8 + $index] -ne $expectedPayload[$index]) {
			            throw "ICNS payload mismatch for $($slot.Type)."
			        }
			    }
			    $offset += $entryLength
			}
			if ($offset -ne $actual.Length) { throw 'ICNS contains unexpected trailing data.' }

			$badIconSetPath = Join-Path $PSScriptRoot 'bad-icon-set'
			New-Item -ItemType Directory -Path $badIconSetPath -Force | Out-Null
			Get-ChildItem -LiteralPath $iconSetPath -Filter '*.png' | Copy-Item -Destination $badIconSetPath -Force
			Copy-Item -LiteralPath (Join-Path $iconSetPath '128.png') -Destination (Join-Path $badIconSetPath '256.png') -Force
			$rejected = $false
			try {
			    New-DeterministicIcns -iconSetPath $badIconSetPath -outputPath (Join-Path $PSScriptRoot 'bad.icns')
			}
			catch {
			    if ($_.Exception.Message -notlike '*ic08*256x256px*128x128px*') { throw }
			    $rejected = $true
			}
			if (-not $rejected) { throw 'Mismatched PNG dimensions were accepted.' }
			Write-Output 'icns-contract-ok'
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
