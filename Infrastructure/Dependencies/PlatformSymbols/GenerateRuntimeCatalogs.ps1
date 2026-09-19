param(
	[Parameter(Mandatory)]
	[string]$Python312,
	[Parameter(Mandatory)]
	[string]$Python313,
	[string]$Node = 'node',
	[string]$PythonOutputPath = (Join-Path $PSScriptRoot 'python-3.12-3.13.json'),
	[string]$NodeOutputPath = (Join-Path $PSScriptRoot 'node-24.json')
)

function Read-PythonStandardLibrary([string]$Executable, [string]$ExpectedVersion) {
	$payload = & $Executable -c 'import json,sys; print(sys.version_info[:2]); print(json.dumps(sorted(sys.stdlib_module_names)))'
	if ($LASTEXITCODE -ne 0 -or $payload.Count -ne 2 -or $payload[0] -ne "($ExpectedVersion)") {
		throw "Expected Python $ExpectedVersion at '$Executable'."
	}
	return @($payload[1] | ConvertFrom-Json)
}

function Read-NodeBuiltIns([string]$Executable) {
	$payload = & $Executable -e "const m=[...new Set(require('module').builtinModules.filter(x=>!x.startsWith('_')).map(x=>x.replace(/^node:/,'').split('/')[0]))].sort(); console.log(process.versions.node); console.log(JSON.stringify(m))"
	if ($LASTEXITCODE -ne 0 -or $payload.Count -ne 2 -or -not $payload[0].StartsWith('24.')) {
		throw "Expected Node 24 at '$Executable'."
	}
	return @($payload[1] | ConvertFrom-Json)
}

[ordered]@{
	'3.12' = Read-PythonStandardLibrary $Python312 '3, 12'
	'3.13' = Read-PythonStandardLibrary $Python313 '3, 13'
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $PythonOutputPath -Encoding utf8NoBOM

Read-NodeBuiltIns $Node |
	ConvertTo-Json |
	Set-Content -LiteralPath $NodeOutputPath -Encoding utf8NoBOM
