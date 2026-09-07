param(
	[string]$ReferencePack = (Join-Path $env:ProgramFiles 'dotnet\packs\Microsoft.NETCore.App.Ref'),
	[string]$OutputPath = (Join-Path $PSScriptRoot 'dotnet-net10.0.json')
)

$version = Get-ChildItem -LiteralPath $ReferencePack -Directory |
	Where-Object Name -Like '10.*' |
	Sort-Object { [version]$_.Name } -Descending |
	Select-Object -First 1
if ($null -eq $version) { throw 'The net10.0 reference pack is not installed.' }
$referenceDirectory = Join-Path $version.FullName 'ref\net10.0'
$assemblyPaths = Get-ChildItem -LiteralPath $referenceDirectory -Filter '*.dll' | ForEach-Object FullName
if (-not ('DevProjexCatalogGenerator' -as [type])) {
	Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

public static class DevProjexCatalogGenerator
{
    public static string[] Read(string[] paths)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var definition = metadata.GetTypeDefinition(handle);
                if ((definition.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public ||
                    !definition.GetDeclaringType().IsNil) continue;
                var name = metadata.GetString(definition.Name);
                if (name.Length == 0 || name[0] == '<') continue;
                var tick = name.IndexOf('`');
                if (tick >= 0) name = name.Substring(0, tick);
                var ns = metadata.GetString(definition.Namespace);
                result.Add(ns.Length == 0 ? name : ns + "." + name);
            }
        }
        var values = new string[result.Count];
        result.CopyTo(values);
        return values;
    }
}
'@
}
[DevProjexCatalogGenerator]::Read([string[]]$assemblyPaths) |
	ConvertTo-Json |
	Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
