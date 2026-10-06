param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
# Compile the full production store. The only save replacement is the atomic
# writer, to capture persisted content and inject failures without touching cfgs.
$source = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'InviteTravelCooldownStore.cs'))
$source = $source.Replace('namespace PortalRules;', 'namespace PortalRules {') + "`n}`n"
$fixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'InviteCollapseChecks.cs'))
$fixture = [regex]::Replace($fixture, '(?m)^using [^;]+;\r?\n', '')
$yamlDll = Join-Path $ProjectRoot 'bin\Debug\YamlDotNet.dll'
Add-Type -Path $yamlDll
$frameworkRefs = [string[]](Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
Add-Type -TypeDefinition ("#nullable enable annotations`n#pragma warning disable CS0649`nusing System.Reflection;`nusing System.Collections;`n" + $source + $fixture) -ReferencedAssemblies ($frameworkRefs + $yamlDll)
[PortalRules.InviteCollapseChecks]::Run()
