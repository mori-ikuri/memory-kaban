#requires -Version 5.1
<#
.SYNOPSIS
Exercises the updater with prepared application payloads and fresh temporary installations only.
.DESCRIPTION
Version10PublishPath may be the current source built with Version=1.0.0: these
tests check application file replacement, not historical application behavior.
Every update invocation passes an explicit temporary InstallPath. No test uses
the real installation or archive, stops processes, or deletes retained artifacts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version10PublishPath,
    [Parameter(Mandatory = $true)] [string] $Version11PublishPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$updater = Join-Path $PSScriptRoot 'update-local.ps1'
$source10 = [IO.Path]::GetFullPath($Version10PublishPath)
$source11 = [IO.Path]::GetFullPath($Version11PublishPath)
$realInstall = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\bag'))
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
foreach ($source in @($source10, $source11)) {
    if ($source.StartsWith('C:\Pocket', [StringComparison]::OrdinalIgnoreCase) -or
        $source.StartsWith($realInstall, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Tests require prepared temporary payloads, never personal archives or the real installed application.'
    }
    if (-not $source.StartsWith($temporaryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test payloads must be prepared in a child directory of the current OS temporary directory.'
    }
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw 'A test payload directory does not exist.' }
    foreach ($segment in $source.Substring(3).Split('\')) {
        if ($segment -match '[. ]$' -or $segment -match '~[0-9]') { throw 'Test source aliases are not allowed.' }
    }
    for ($cursor = $source; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Test sources and their ancestors may not be reparse points.'
        }
    }
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($source)
    while ($pending.Count -gt 0) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
            $attributes = [IO.File]::GetAttributes($entry)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Test sources may not contain reparse points.' }
            $name = [IO.Path]::GetFileName($entry)
            $databaseName = $name -match '(?i)\.(db[^.]*|sqlite[^.]*)(\..*)?$' -and
                [IO.Path]::GetExtension($name) -notin @('.dll', '.pdb', '.xml')
            if ($databaseName -or $name -match '(?i)\.zip(\..*)?$' -or
                $name -match '(?i)^(bag\.config\.json|seals\.json|bag-seals-.*\.tmp|exports)$') {
                throw 'Prepared test sources may not contain archives, databases, or personal sidecars.'
            }
            if ($attributes -band [IO.FileAttributes]::Directory) { $pending.Push($entry) }
        }
    }
    foreach ($required in @('bag.exe', 'bag.dll', 'bag.deps.json', 'bag.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) {
            throw 'Tests require complete prepared application payloads.'
        }
    }
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('bag-update-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$failures = New-Object 'System.Collections.Generic.List[string]'
$passed = 0

function Assert-True([bool] $Value, [string] $Message) {
    if (-not $Value) { throw $Message }
}

function Assert-Throws([scriptblock] $Action, [string] $Message) {
    $failed = $false
    try { $null = & $Action }
    catch { $failed = $true }
    Assert-True $failed $Message
}

function Invoke-Case([string] $Name, [scriptblock] $Action) {
    try {
        & $Action
        $script:passed++
        Write-Host ('PASS ' + $Name)
    }
    catch {
        $script:failures.Add($Name + ': ' + $_.Exception.Message)
        Write-Host ('FAIL ' + $Name + ': ' + $_.Exception.Message)
    }
}

function New-Fixture([string] $Name) {
    $directory = Join-Path $testRoot $Name
    $null = New-Item -ItemType Directory -Path $directory
    $install = Join-Path $directory 'bag'
    $publish = Join-Path $directory 'publish'
    Copy-Item -LiteralPath $source10 -Destination $install -Recurse
    Copy-Item -LiteralPath $source11 -Destination $publish -Recurse
    return [pscustomobject]@{ Directory = $directory; Install = $install; Publish = $publish }
}

function Get-TreeFingerprint([string] $Root) {
    return (@(Get-ChildItem -LiteralPath $Root -Force -Recurse | Sort-Object FullName | ForEach-Object {
                $relative = $_.FullName.Substring($Root.Length + 1)
                if ($_.PSIsContainer) { 'D|' + $relative }
                else { 'F|' + $relative + '|' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
            }) -join "`n")
}

function Get-AppVersion([string] $Path) {
    return [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Path 'bag.dll')).Version.ToString()
}

Invoke-Case 'WhatIf upgrade creates no directories or files' {
    $f = New-Fixture 'whatif-upgrade'
    $before = Get-TreeFingerprint $f.Directory
    $result = & $updater -PublishPath $f.Publish -InstallPath $f.Install -WhatIf
    Assert-True (-not $result.Applied) 'WhatIf reported an applied update.'
    Assert-True ($before -ceq (Get-TreeFingerprint $f.Directory)) 'WhatIf mutated the temporary fixture.'
}

Invoke-Case 'Upgrade and rollback retain exact originals and displaced version' {
    $f = New-Fixture 'roundtrip'
    Set-Content -LiteralPath (Join-Path $f.Install 'legacy-only.txt') -Value 'SYNTHETIC_OLD_MARKER' -Encoding UTF8
    $old = Get-TreeFingerprint $f.Install
    $published = Get-TreeFingerprint $f.Publish
    $upgrade = & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false
    Assert-True $upgrade.Applied 'Upgrade was not applied.'
    Assert-True ((Get-AppVersion $f.Install) -ceq '1.1.0.0') 'Wrong installed version.'
    Assert-True ($old -ceq (Get-TreeFingerprint $upgrade.BackupPath)) 'The original backup changed.'
    Assert-True ($published -ceq (Get-TreeFingerprint $f.Install)) 'Installed files differ from the prepared payload.'
    Assert-True ($published -ceq (Get-TreeFingerprint $f.Publish)) 'Source payload changed.'
    Set-Content -LiteralPath (Join-Path $f.Install 'new-only.txt') -Value 'SYNTHETIC_NEW_MARKER' -Encoding UTF8
    $new = Get-TreeFingerprint $f.Install
    $beforeWhatIf = Get-TreeFingerprint $f.Directory
    $preview = & $updater -Rollback -BackupPath $upgrade.BackupPath -InstallPath $f.Install -WhatIf
    Assert-True (-not $preview.Applied) 'Rollback WhatIf reported applied.'
    Assert-True ($beforeWhatIf -ceq (Get-TreeFingerprint $f.Directory)) 'Rollback WhatIf mutated the fixture.'
    $restored = & $updater -Rollback -BackupPath $upgrade.BackupPath -InstallPath $f.Install -Confirm:$false
    Assert-True ($old -ceq (Get-TreeFingerprint $f.Install)) 'Rollback did not restore the exact old files.'
    Assert-True ($old -ceq (Get-TreeFingerprint $upgrade.BackupPath)) 'Rollback overwrote the backup.'
    Assert-True ($new -ceq (Get-TreeFingerprint $restored.DisplacedPath)) 'Rollback lost or altered the new version.'
}

Invoke-Case 'Rollback recovers a missing installation after interrupted activation' {
    $f = New-Fixture 'missing-install'
    $old = Get-TreeFingerprint $f.Install
    $upgrade = & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false
    $interrupted = Join-Path $f.Directory 'interrupted-new'
    [IO.Directory]::Move($f.Install, $interrupted)
    $new = Get-TreeFingerprint $interrupted
    $restored = & $updater -Rollback -BackupPath $upgrade.BackupPath -InstallPath $f.Install -Confirm:$false
    Assert-True ($null -eq $restored.DisplacedPath) 'Missing installation unexpectedly reported displaced files.'
    Assert-True ($old -ceq (Get-TreeFingerprint $f.Install)) 'Missing installation was not restored.'
    Assert-True ($old -ceq (Get-TreeFingerprint $upgrade.BackupPath)) 'Recovery altered its backup.'
    Assert-True ($new -ceq (Get-TreeFingerprint $interrupted)) 'Recovery altered the interrupted version.'
}

Invoke-Case 'Rollback preserves a damaged new application' {
    $f = New-Fixture 'damaged-current'
    $old = Get-TreeFingerprint $f.Install
    $upgrade = & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false
    Set-Content -LiteralPath (Join-Path $f.Install 'bag.runtimeconfig.json') -Value 'SYNTHETIC_BROKEN_RUNTIME' -Encoding UTF8
    $broken = Get-TreeFingerprint $f.Install
    $restored = & $updater -Rollback -BackupPath $upgrade.BackupPath -InstallPath $f.Install -Confirm:$false
    Assert-True ($old -ceq (Get-TreeFingerprint $f.Install)) 'Damaged application was not rolled back.'
    Assert-True ($broken -ceq (Get-TreeFingerprint $restored.DisplacedPath)) 'Damaged version was not preserved.'
}

Invoke-Case 'Wrong source version and repeated upgrade fail before mutation' {
    $f = New-Fixture 'wrong-version'
    $before = Get-TreeFingerprint $f.Directory
    Assert-Throws { & $updater -PublishPath $source10 -InstallPath $f.Install -Confirm:$false } 'Accepted v1.0 as the new payload.'
    Assert-True ($before -ceq (Get-TreeFingerprint $f.Directory)) 'Invalid source version mutated the fixture.'
    $null = & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false
    $after = Get-TreeFingerprint $f.Directory
    Assert-Throws { & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false } 'Accepted already upgraded installation.'
    Assert-True ($after -ceq (Get-TreeFingerprint $f.Directory)) 'Repeated upgrade mutated the fixture.'
}

Invoke-Case 'Incomplete and malformed publish payloads fail before mutation' {
    $f = New-Fixture 'bad-payload'
    $minimal = Join-Path $f.Directory 'minimal'
    $null = New-Item -ItemType Directory -Path $minimal
    Copy-Item -LiteralPath (Join-Path $source11 'bag.dll') -Destination (Join-Path $minimal 'bag.dll')
    $before = Get-TreeFingerprint $f.Directory
    Assert-Throws { & $updater -PublishPath $minimal -InstallPath $f.Install -Confirm:$false } 'Accepted incomplete payload.'
    Assert-True ($before -ceq (Get-TreeFingerprint $f.Directory)) 'Incomplete payload caused mutation.'
    Set-Content -LiteralPath (Join-Path $f.Publish 'bag.runtimeconfig.json') -Value '{}' -Encoding UTF8
    $before = Get-TreeFingerprint $f.Directory
    Assert-Throws { & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false } 'Accepted malformed runtime manifest.'
    Assert-True ($before -ceq (Get-TreeFingerprint $f.Directory)) 'Malformed manifest caused mutation.'
}

Invoke-Case 'Missing dependency assets are detected before activation' {
    $f = New-Fixture 'missing-dependency'
    [IO.File]::Move((Join-Path $f.Publish 'Microsoft.Data.Sqlite.dll'),
        (Join-Path $f.Directory 'retained-Microsoft.Data.Sqlite.dll'))
    $before = Get-TreeFingerprint $f.Directory
    Assert-Throws { & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false } 'Accepted a payload missing a referenced dependency.'
    Assert-True ($before -ceq (Get-TreeFingerprint $f.Directory)) 'Missing dependency caused mutation.'
}

Invoke-Case 'Resource locales cannot resolve dependencies outside the payload' {
    $f = New-Fixture 'unsafe-resource-locale'
    $outside = Join-Path $f.Directory 'outside'
    $null = New-Item -ItemType Directory -Path $outside
    Copy-Item -LiteralPath (Join-Path $source11 'bag.dll') -Destination (Join-Path $outside 'missing.resources.dll')
    $manifest = Join-Path $f.Publish 'bag.deps.json'
    $deps = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
    $projectName = @($deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' })[0].Name
    $project = $target.PSObject.Properties[$projectName].Value
    $project | Add-Member -MemberType NoteProperty -Name resources -Value ([pscustomobject]@{
            'missing.resources.dll' = [pscustomobject]@{ locale = '..\outside' }
        }) -Force
    $deps | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $manifest -Encoding UTF8
    $before = Get-TreeFingerprint $f.Directory
    Assert-Throws { & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false } 'Accepted a resource outside the payload.'
    Assert-True ($before -ceq (Get-TreeFingerprint $f.Directory)) 'Unsafe resource locale caused mutation.'
}

Invoke-Case 'Database and sidecar markers in source or installation are rejected' {
    foreach ($location in @('Publish', 'Install')) {
        foreach ($name in @('synthetic.db-wal', 'synthetic.sqlite3', 'bag.config.json', 'seals.json', 'synthetic.zip')) {
            $f = New-Fixture ('data-' + $location + '-' + $name.Replace('.', '-'))
            Set-Content -LiteralPath (Join-Path $f.$location $name) -Value 'SYNTHETIC_DATA_MARKER' -Encoding UTF8
            $before = Get-TreeFingerprint $f.Directory
            Assert-Throws { & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false } ('Accepted ' + $name)
            Assert-True ($before -ceq (Get-TreeFingerprint $f.Directory)) 'Data guard caused mutation.'
        }
    }
}

Invoke-Case 'Nested paths and dangerous roots are rejected before mutation' {
    $f = New-Fixture 'unsafe-paths'
    $nested = Join-Path $f.Install 'nested-publish'
    Copy-Item -LiteralPath $source11 -Destination $nested -Recurse
    $before = Get-TreeFingerprint $f.Directory
    foreach ($target in @('C:\', 'C:\Pocket\bag', $f.Publish)) {
        Assert-Throws { & $updater -PublishPath $f.Publish -InstallPath $target -WhatIf } 'Accepted unsafe target path.'
    }
    Assert-Throws { & $updater -PublishPath $nested -InstallPath $f.Install -Confirm:$false } 'Accepted nested source and destination.'
    Assert-Throws { & $updater -PublishPath $f.Directory -InstallPath $f.Install -Confirm:$false } 'Accepted ancestor source directory.'
    Assert-True ($before -ceq (Get-TreeFingerprint $f.Directory)) 'Unsafe path guard mutated the fixture.'
}

Invoke-Case 'Open application file aborts without stopping its owner or changing files' {
    $f = New-Fixture 'file-in-use'
    $before = Get-TreeFingerprint $f.Directory
    $lock = [IO.File]::Open((Join-Path $f.Install 'bag.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        Assert-Throws { & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false } 'Accepted locked application.'
        Assert-True $lock.CanRead 'Updater closed the owner file handle.'
    }
    finally { $lock.Dispose() }
    Assert-True ($before -ceq (Get-TreeFingerprint $f.Directory)) 'Locked file caused mutation.'
}

Invoke-Case 'Reparse descendants and ancestors are rejected without following them' {
    $f = New-Fixture 'reparse-paths'
    $other = Join-Path $f.Directory 'synthetic-link-target'
    $null = New-Item -ItemType Directory -Path $other
    $null = New-Item -ItemType Junction -Path (Join-Path $f.Publish 'linked') -Target $other
    $old = Get-TreeFingerprint $f.Install
    Assert-Throws { & $updater -PublishPath $f.Publish -InstallPath $f.Install -Confirm:$false } 'Accepted descendant junction.'
    $alias = Join-Path $testRoot 'reparse-parent-alias'
    $null = New-Item -ItemType Junction -Path $alias -Target $f.Directory
    Assert-Throws { & $updater -PublishPath $source11 -InstallPath (Join-Path $alias 'bag') -WhatIf } 'Accepted ancestor junction.'
    Assert-True ($old -ceq (Get-TreeFingerprint $f.Install)) 'Reparse rejection altered the installation.'
    Assert-True (@(Get-ChildItem -LiteralPath $f.Directory | Where-Object {
                $_.Name.StartsWith('bag.', [StringComparison]::OrdinalIgnoreCase)
            }).Count -eq 0) 'Reparse rejection created staging or backup.'
}

Write-Host ('PowerShell: ' + $PSVersionTable.PSVersion.ToString())
Write-Host ('Temporary test artifacts retained at: ' + $testRoot)
if ($failures.Count -gt 0) { throw ($failures.Count.ToString() + ' updater test case(s) failed: ' + ($failures -join '; ')) }
[pscustomobject]@{ Passed = $passed; Failed = 0; PowerShell = $PSVersionTable.PSVersion.ToString(); TestRoot = $testRoot }
