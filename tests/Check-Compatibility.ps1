param(
    [Parameter(Mandatory = $true)][string]$GameManaged,
    [Parameter(Mandatory = $true)][string]$BepInExCore,
    [Parameter(Mandatory = $true)][string]$CecilDll,
    [string]$ModDll = (Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\Debug\PortalRules.dll')
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CecilDll
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
foreach ($directory in @($GameManaged, $BepInExCore, (Split-Path $ModDll),
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319")) {
    $resolver.AddSearchDirectory($directory)
}
$parameters = [Mono.Cecil.ReaderParameters]::new()
$parameters.AssemblyResolver = $resolver
$module = [Mono.Cecil.ModuleDefinition]::ReadModule($ModDll, $parameters)
function Get-Types($types) {
    foreach ($type in $types) { $type; Get-Types $type.NestedTypes }
}
function Base-TypeName($type) {
    if ($type -is [Mono.Cecil.ByReferenceType]) { return $type.ElementType.FullName }
    return $type.FullName
}
$failures = [Collections.Generic.List[string]]::new()
$references = 0
$patches = 0
$injections = 0
$dynamic = [Collections.Generic.List[string]]::new()
try {
    if (@($module.AssemblyReferences | Where-Object Name -eq 'Jotunn').Count) {
        $failures.Add('Final DLL still references Jotunn')
    }
    foreach ($type in (Get-Types $module.Types)) {
        foreach ($method in $type.Methods) {
            if (!$method.HasBody) { continue }
            foreach ($instruction in $method.Body.Instructions) {
                $operand = $instruction.Operand
                if ($instruction.OpCode.OperandType -eq [Mono.Cecil.Cil.OperandType]::ShortInlineBrTarget) {
                    $distance = $operand.Offset - ($instruction.Offset + $instruction.GetSize())
                    if ($distance -lt -128 -or $distance -gt 127) {
                        $failures.Add("Invalid short branch: $($method.FullName)")
                    }
                }
                if ($operand -isnot [Mono.Cecil.MethodReference] -and $operand -isnot [Mono.Cecil.FieldReference]) { continue }
                $scope = $operand.DeclaringType.Scope.Name
                if ($scope -notin @('assembly_valheim', 'assembly_utils', 'assembly_guiutils', 'Splatform')) { continue }
                try {
                    $resolved = $operand.Resolve()
                    if (!$resolved) { throw 'Member not found' }
                    if ($operand -is [Mono.Cecil.FieldReference] -and $resolved.IsLiteral -and
                        $instruction.OpCode.Code -in @([Mono.Cecil.Cil.Code]::Ldsfld, [Mono.Cecil.Cil.Code]::Ldsflda)) {
                        throw 'Literal field used as runtime storage'
                    }
                    $references++
                }
                catch { $failures.Add("$($method.FullName): $operand -- $_") }
            }
        }
        if (!$type.FullName.StartsWith('PortalRules.')) { continue }
        $attributes = @($type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' })
        if (!$attributes.Count) { continue }
        $targetType = $null
        $targetName = $null
        $argumentTypes = $null
        foreach ($attribute in $attributes) {
            foreach ($argument in $attribute.ConstructorArguments) {
                switch ($argument.Type.FullName) {
                    'System.Type' { $targetType = $argument.Value.Resolve() }
                    'System.String' { $targetName = [string]$argument.Value }
                    'System.Type[]' { $argumentTypes = @($argument.Value | ForEach-Object { $_.Value.FullName }) }
                }
            }
        }
        if (!$targetType -or !$targetName) {
            if (@($type.Methods | Where-Object Name -in @('TargetMethod', 'TargetMethods')).Count) {
                $dynamic.Add($type.FullName)
            }
            continue
        }
        $targets = @($targetType.Methods | Where-Object Name -eq $targetName)
        if ($null -ne $argumentTypes) {
            $targets = @($targets | Where-Object {
                (@($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -eq ($argumentTypes -join '|')
            })
        }
        if ($targets.Count -ne 1) {
            $failures.Add("Harmony target ambiguous/missing: $($type.FullName) -> $($targetType.FullName).$targetName ($($targets.Count))")
            continue
        }
        $target = $targets[0]
        $patches++
        foreach ($patch in ($type.Methods | Where-Object Name -in @('Prefix', 'Postfix', 'Finalizer'))) {
            foreach ($parameter in $patch.Parameters) {
                if ($parameter.Name.StartsWith('___')) {
                    $fieldName = $parameter.Name.Substring(3)
                    $field = $targetType.Fields | Where-Object Name -eq $fieldName
                    if (!$field -or $field.FieldType.FullName -ne (Base-TypeName $parameter.ParameterType)) {
                        $failures.Add("Harmony field injection mismatch: $($patch.FullName) / $fieldName")
                    }
                    $injections++
                }
                elseif (!$parameter.Name.StartsWith('__')) {
                    $originalParameter = $target.Parameters | Where-Object Name -eq $parameter.Name
                    if (!$originalParameter -or (Base-TypeName $originalParameter.ParameterType) -ne (Base-TypeName $parameter.ParameterType)) {
                        $failures.Add("Harmony argument mismatch: $($patch.FullName) / $($parameter.Name)")
                    }
                }
            }
        }
    }
    $gameReference = $module.AssemblyReferences | Where-Object Name -eq 'assembly_valheim'
    $game = $resolver.Resolve($gameReference).MainModule
    # Original private members accessed through cached FieldRefs or exact Harmony targets.
    foreach ($contract in @(
        @('ZNetScene', 'm_namedPrefabs', 'System.Collections.Generic.Dictionary`2<System.Int32,UnityEngine.GameObject>'),
        @('ObjectDB', 'm_buildPieces', 'System.Collections.Generic.List`1<Piece>'),
        @('PieceTable', 'm_availablePiecesByCategory', 'System.Collections.Generic.List`1<System.Collections.Generic.List`1<Piece>>'),
        @('Minimap', 'm_visibleIconTypes', 'System.Boolean[]'),
        @('Minimap', 'm_largeZoom', 'System.Single')
    )) {
        $owner = $game.Types | Where-Object Name -eq $contract[0]
        $field = $owner.Fields | Where-Object Name -eq $contract[1]
        if (!$field -or $field.FieldType.FullName -ne $contract[2]) {
            $failures.Add("Cached private field contract changed: $($contract[0]).$($contract[1])")
        }
    }
    $objectDb = $game.Types | Where-Object Name -eq 'ObjectDB'
    foreach ($name in @('Awake', 'CopyOtherDB')) {
        if (@($objectDb.Methods | Where-Object Name -eq $name).Count -ne 1) {
            $failures.Add("Dynamic Hammer registration target changed: ObjectDB.$name")
        }
    }
    $minimap = $game.Types | Where-Object Name -eq 'Minimap'
    $updateMap = $minimap.Methods | Where-Object Name -eq 'UpdateMap'
    $instructions = $updateMap.Body.Instructions
    $wheelMatches = 0
    for ($i = 0; $i -lt $instructions.Count; $i++) {
        $operand = $instructions[$i].Operand
        if ($operand -isnot [Mono.Cecil.MethodReference] -or $operand.DeclaringType.Name -ne 'ZInput' -or
            $operand.Name -ne 'GetMouseScrollWheel') { continue }
        $window = @($instructions | Select-Object -Skip ($i + 1) -First 12)
        $hasMin = @($window | Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and $_.Operand -eq [single]-0.05 }).Count
        $hasMax = @($window | Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and $_.Operand -eq [single]0.05 }).Count
        $hasClamp = @($window | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.FullName -eq 'System.Single UnityEngine.Mathf::Clamp(System.Single,System.Single,System.Single)' }).Count
        if ($hasMin -and $hasMax -and $hasClamp) { $wheelMatches++ }
    }
    if ($wheelMatches -ne 1) { $failures.Add("Unexpected wheel transpiler anchor count: $wheelMatches") }
    $portalConfigType = $module.Types |
        Where-Object FullName -eq 'PortalRules.PublicPortalConfig'
    function Method-Calls($method, [string]$declaringType, [string]$name) {
        @($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq $declaringType -and
            $_.Operand.Name -eq $name
        }).Count
    }
    $fileChanged = $portalConfigType.Methods | Where-Object Name -eq 'ReadConfigValues'
    $reloadConfig = $portalConfigType.Methods | Where-Object Name -eq 'ReloadConfigIfChanged'
    $saveConfig = $portalConfigType.Methods | Where-Object Name -eq 'SaveConfigNow'
    $settingChanged = $portalConfigType.Methods | Where-Object Name -eq 'OnConfigSettingChanged'
    if (!$fileChanged -or !$reloadConfig -or !$saveConfig -or !$settingChanged) {
        $failures.Add('Config persistence methods are missing')
    }
    else {
        if ((Method-Calls $fileChanged 'BepInEx.Configuration.ConfigFile' 'Save') -ne 0 -or
            (Method-Calls $fileChanged 'BepInEx.Configuration.ConfigFile' 'Reload') -ne 0) {
            $failures.Add('Config file watcher performs immediate config I/O')
        }
        if ((Method-Calls $reloadConfig 'BepInEx.Configuration.ConfigFile' 'Reload') -ne 1 -or
            (Method-Calls $reloadConfig 'BepInEx.Configuration.ConfigFile' 'Save') -ne 0) {
            $failures.Add('Config reload must read once without rewriting the file')
        }
        if ((Method-Calls $saveConfig 'BepInEx.Configuration.ConfigFile' 'Save') -ne 1 -or
            (Method-Calls $saveConfig 'BepInEx.Configuration.ConfigFile' 'Reload') -ne 0) {
            $failures.Add('Debounced config save must write exactly once')
        }
        $checksServerSyncUpdate = @($settingChanged.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.FieldReference] -and
            $_.Operand.DeclaringType.FullName -eq 'ServerSync.ConfigSync' -and
            $_.Operand.Name -eq 'ProcessingServerUpdate'
        }).Count
        if ($checksServerSyncUpdate -ne 1) {
            $failures.Add('Config save debounce does not distinguish ServerSync updates')
        }
    }
    $subscribeSettings = $portalConfigType.Methods |
        Where-Object Name -eq 'SubscribeToSettingChanges'
    $unsubscribeSettings = $portalConfigType.Methods |
        Where-Object Name -eq 'UnsubscribeFromSettingChanges'
    $settingAdds = @($subscribeSettings.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'add_SettingChanged'
    }).Count
    $settingRemoves = @($unsubscribeSettings.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'remove_SettingChanged'
    }).Count
    if ($settingAdds -ne 9 -or $settingRemoves -ne 9) {
        $failures.Add(
            "Live config subscriptions are not symmetric: add=$settingAdds remove=$settingRemoves")
    }
    if ($failures.Count) { throw ($failures -join "`n") }
    [pscustomobject]@{
        GameMemberInstructionsResolved = $references
        ExplicitHarmonyTargetsChecked = $patches
        InjectedFieldsChecked = $injections
        DynamicTargetsRequiringSeparateCheck = @($dynamic)
        JotunnAssemblyReference = $false
        DynamicHammerTargetsChecked = 2
        PrivateFieldContractsChecked = 5
        WheelTranspilerAnchors = $wheelMatches
        ConfigPersistenceContractsChecked = 4
        LiveConfigSubscriptionsChecked = $settingAdds
        Note = 'Static metadata/IL checks; does not install Harmony patches or execute Unity.'
    } | ConvertTo-Json -Depth 4
}
finally { $module.Dispose(); $resolver.Dispose() }
