#requires -Version 5.1
<#
.SYNOPSIS
Stages a local application-only upgrade from bag 1.0.0 to 1.1.0, or restores its backup.
.DESCRIPTION
Run after completely quitting clients that use bag. No process is stopped and no
database, export, seal or speaker configuration is opened. Existing directories
are retained. -WhatIf performs validation without creating or moving anything.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Upgrade')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Upgrade')]
    [ValidateNotNullOrEmpty()]
    [string] $PublishPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [switch] $Rollback,

    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [ValidateNotNullOrEmpty()]
    [string] $BackupPath,

    [ValidateNotNullOrEmpty()]
    [string] $InstallPath = (Join-Path $env:LOCALAPPDATA 'Programs\bag')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedPath([string] $Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) {
        $Path = Join-Path (Get-Location).ProviderPath $Path
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full -notmatch '^[A-Za-z]:\\' -or $full.IndexOf(':', 2) -ge 0) {
        throw 'Only local Windows filesystem paths are supported.'
    }
    foreach ($segment in $full.Substring(3).Split('\')) {
        if ($segment -match '[. ]$' -or $segment -match '~[0-9]') {
            throw 'Use full directory names without trailing dots/spaces or Windows short-name aliases.'
        }
    }
    return $full
}

function Test-SamePath([string] $Left, [string] $Right) {
    return [string]::Equals($Left, $Right, [StringComparison]::OrdinalIgnoreCase)
}

function Test-Within([string] $Path, [string] $Parent) {
    return (Test-SamePath $Path $Parent) -or $Path.StartsWith($Parent + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SeparateTrees([string] $Left, [string] $Right) {
    if ((Test-Within $Left $Right) -or (Test-Within $Right $Left)) {
        throw 'Source and installation directories must be separate, non-nested trees.'
    }
}

function Assert-SafePath([string] $Path, [bool] $AllowMissing = $false) {
    $driveRoot = [IO.Path]::GetPathRoot($Path).TrimEnd('\', '/')
    if ((Test-SamePath $Path $driveRoot) -or (Test-Within $Path 'C:\Pocket')) {
        throw 'A drive root or personal archive location cannot be used for an application update.'
    }
    foreach ($protected in @($env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA,
            $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:SystemRoot, $env:TEMP)) {
        if ($protected -and (Test-SamePath $Path ([IO.Path]::GetFullPath($protected).TrimEnd('\', '/')))) {
            throw 'A profile, system, program, or temporary-directory root cannot be an update target.'
        }
    }
    $cursor = $Path
    $first = $true
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Update paths and their ancestors must be ordinary directories, without reparse points.'
            }
        }
        elseif (-not ($first -and $AllowMissing)) {
            throw 'An update directory or its parent does not exist.'
        }
        $first = $false
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Get-FileSha256([string] $Path) {
    $stream = $null
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($Path)
        return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        $hash.Dispose()
    }
}

function Get-SafeInventory([string] $Root) {
    Assert-SafePath $Root
    $entries = New-Object 'System.Collections.Generic.List[object]'
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($path)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Application trees may not contain symbolic links, junctions, or other reparse points.'
            }
            $name = [IO.Path]::GetFileName($path)
            $isDirectory = [bool]($attributes -band [IO.FileAttributes]::Directory)
            # Microsoft.Data.Sqlite.dll is an application library, not a SQLite archive.
            $databaseName = $name -match '(?i)\.(db[^.]*|sqlite[^.]*)(\..*)?$' -and
                [IO.Path]::GetExtension($name) -notin @('.dll', '.pdb', '.xml')
            if ($name -match '(?i)^(bag\.config\.json|seals\.json|bag-seals-.*\.tmp|exports)$' -or
                $databaseName -or $name -match '(?i)\.zip(\..*)?$') {
                throw 'Application trees must not contain databases, export ZIPs, exports directories, seals, or speaker configuration.'
            }
            $relative = $path.Substring($Root.Length + 1)
            if ($isDirectory) {
                $entries.Add([pscustomobject]@{ RelativePath = $relative; Directory = $true; Hash = ''; Length = 0L })
                $pending.Push($path)
            }
            else {
                $info = New-Object IO.FileInfo($path)
                $entries.Add([pscustomobject]@{
                    RelativePath = $relative; Directory = $false
                    Hash = (Get-FileSha256 $path); Length = $info.Length
                })
            }
        }
    }
    return @($entries | Sort-Object -Property RelativePath)
}

function Get-PropertyValue($Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Assert-ApplicationPayload([string] $Root, [string] $ExpectedVersion) {
    $inventory = @(Get-SafeInventory $Root)
    foreach ($name in @('bag.exe', 'bag.dll', 'bag.deps.json', 'bag.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $name) -PathType Leaf)) {
            throw "Application payload is missing required file: $name"
        }
    }
    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Root 'bag.dll'))
    if ($assembly.Name -cne 'bag' -or $assembly.Version.ToString() -cne ($ExpectedVersion + '.0')) {
        throw "Expected bag $ExpectedVersion assembly metadata. No application files were replaced."
    }
    $runtime = Get-Content -LiteralPath (Join-Path $Root 'bag.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $options = Get-PropertyValue $runtime 'runtimeOptions'
    if ((Get-PropertyValue $options 'tfm') -cne 'net10.0') {
        throw 'The prepared application must target net10.0.'
    }
    $frameworks = @()
    foreach ($key in @('framework', 'frameworks', 'includedFrameworks')) {
        $value = Get-PropertyValue $options $key
        if ($null -ne $value) { $frameworks += @($value) }
    }
    if (-not @($frameworks | Where-Object {
                (Get-PropertyValue $_ 'name') -ceq 'Microsoft.NETCore.App' -and
                (Get-PropertyValue $_ 'version') -match '^10\.'
            }).Count) {
        throw 'The application runtime manifest must identify Microsoft.NETCore.App 10.'
    }
    $deps = Get-Content -LiteralPath (Join-Path $Root 'bag.deps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $libraries = Get-PropertyValue $deps 'libraries'
    $projectLibraries = @($libraries.PSObject.Properties | Where-Object {
            $_.Name.EndsWith('/' + $ExpectedVersion, [StringComparison]::Ordinal) -and
            (Get-PropertyValue $_.Value 'type') -ceq 'project'
        })
    if ($projectLibraries.Count -ne 1) {
        throw 'The dependency manifest does not identify exactly one project with the expected version.'
    }
    $targetName = Get-PropertyValue (Get-PropertyValue $deps 'runtimeTarget') 'name'
    $target = Get-PropertyValue (Get-PropertyValue $deps 'targets') $targetName
    if ($null -eq $target -or $null -eq (Get-PropertyValue $target $projectLibraries[0].Name)) {
        throw 'The dependency manifest has no matching runtime target.'
    }
    foreach ($library in $target.PSObject.Properties) {
        foreach ($group in @('runtime', 'native', 'resources', 'runtimeTargets')) {
            $assets = Get-PropertyValue $library.Value $group
            if ($null -eq $assets) { continue }
            foreach ($asset in $assets.PSObject.Properties) {
                $assetPath = $asset.Name.Replace('/', '\')
                if ([string]::IsNullOrWhiteSpace($assetPath) -or [IO.Path]::IsPathRooted($assetPath) -or
                    $assetPath.Contains(':') -or
                    @($assetPath.Split('\') | Where-Object { $_ -eq '..' -or $_ -match '[. ]$' -or $_ -match '~[0-9]' }).Count) {
                    throw 'The dependency manifest contains an unsafe asset path.'
                }
                $exact = Join-Path $Root $assetPath
                $flat = Join-Path $Root ([IO.Path]::GetFileName($assetPath))
                $locale = Get-PropertyValue $asset.Value 'locale'
                if ($null -ne $locale -and ($locale -isnot [string] -or $locale -notmatch '^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$')) {
                    throw 'The dependency manifest contains an unsafe resource locale.'
                }
                $localized = if ($locale) { Join-Path (Join-Path $Root $locale) ([IO.Path]::GetFileName($assetPath)) } else { $flat }
                if (-not ((Test-Path -LiteralPath $exact -PathType Leaf) -or
                        (Test-Path -LiteralPath $flat -PathType Leaf) -or
                        (Test-Path -LiteralPath $localized -PathType Leaf))) {
                    throw "Application payload is missing a dependency asset: $($asset.Name)"
                }
            }
        }
    }
    return $inventory
}

function Assert-SameInventory($Expected, $Actual) {
    $left = @($Expected)
    $right = @($Actual)
    if ($left.Count -ne $right.Count) { throw 'Application files changed during update preparation.' }
    for ($index = 0; $index -lt $left.Count; $index++) {
        foreach ($property in @('RelativePath', 'Directory', 'Hash', 'Length')) {
            if ($left[$index].$property -cne $right[$index].$property) {
                throw 'Application files changed during update preparation.'
            }
        }
    }
}

function Assert-NotInUse([string] $Root, $Inventory) {
    # CIM module initialization may otherwise print internal Set-Alias WhatIf messages.
    # This function performs read-only inspection; the caller still owns ShouldProcess.
    $WhatIfPreference = $false
    $exe = Join-Path $Root 'bag.exe'
    $dll = Join-Path $Root 'bag.dll'
    foreach ($process in @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'bag.exe' OR Name = 'dotnet.exe'")) {
        $executable = [string]$process.ExecutablePath
        $commandLine = ([string]$process.CommandLine).Replace('/', '\')
        if (($executable -and (Test-SamePath $executable $exe)) -or
            ($commandLine.IndexOf($dll, [StringComparison]::OrdinalIgnoreCase) -ge 0) -or
            ($process.Name -ieq 'dotnet.exe' -and $commandLine -match '(?i)(^|\s|["''])(?:[^"'':\s]*\\)?bag\.dll($|\s|["''])') -or
            ($process.Name -ieq 'bag.exe' -and -not $executable)) {
            throw 'bag may still be running. Completely quit its clients and close bag processes, then try again.'
        }
    }
    foreach ($entry in @($Inventory | Where-Object { -not $_.Directory })) {
        $handle = $null
        try {
            $handle = [IO.File]::Open((Join-Path $Root $entry.RelativePath), [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::None)
        }
        catch { throw 'Application files are in use or inaccessible. No running process will be stopped.' }
        finally { if ($null -ne $handle) { $handle.Dispose() } }
    }
}

function Copy-ApplicationTree([string] $Source, [string] $Destination, $Inventory) {
    if (Test-Path -LiteralPath $Destination) { throw 'The staging directory already exists; nothing will be overwritten.' }
    $null = New-Item -ItemType Directory -Path $Destination
    foreach ($entry in @($Inventory | Where-Object { $_.Directory } | Sort-Object { $_.RelativePath.Length })) {
        $null = New-Item -ItemType Directory -Path (Join-Path $Destination $entry.RelativePath)
    }
    foreach ($entry in @($Inventory | Where-Object { -not $_.Directory })) {
        Copy-Item -LiteralPath (Join-Path $Source $entry.RelativePath) -Destination (Join-Path $Destination $entry.RelativePath)
    }
}

$install = Get-NormalizedPath $InstallPath
Assert-SafePath $install ([bool]$Rollback)
$parent = [IO.Path]::GetDirectoryName($install)
$leaf = [IO.Path]::GetFileName($install)
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$stage = Join-Path $parent ($leaf + '.staging-' + $stamp)
$displaced = $null
$backup = $null
$activeInventory = @()

if ($Rollback) {
    $source = Get-NormalizedPath $BackupPath
    Assert-SeparateTrees $source $install
    Assert-SafePath $source
    if (-not (Test-SamePath ([IO.Path]::GetDirectoryName($source)) $parent) -or
        -not ([IO.Path]::GetFileName($source).StartsWith($leaf + '.backup-v1.0.0-', [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Rollback requires a v1.0.0 backup created for this installation in the same parent directory.'
    }
    $backup = $source
    $expected = '1.0.0'
    $sourceInventory = @(Assert-ApplicationPayload $source $expected)
    if (Test-Path -LiteralPath $install) {
        # Rollback also accepts a damaged v1.1 payload; it is retained, never overwritten.
        $activeInventory = @(Get-SafeInventory $install)
        $displaced = Join-Path $parent ($leaf + '.displaced-' + $stamp)
    }
    $action = 'Restore bag 1.0.0 from the retained backup, preserving any current installation'
}
else {
    $source = Get-NormalizedPath $PublishPath
    Assert-SeparateTrees $source $install
    Assert-SafePath $source
    $expected = '1.1.0'
    $sourceInventory = @(Assert-ApplicationPayload $source $expected)
    $activeInventory = @(Assert-ApplicationPayload $install '1.0.0')
    $backup = Join-Path $parent ($leaf + '.backup-v1.0.0-' + $stamp)
    $displaced = $backup
    $action = 'Back up bag 1.0.0 and activate the prepared bag 1.1.0 application'
}

if (Test-Path -LiteralPath $install) { Assert-NotInUse $install $activeInventory }
if (-not $PSCmdlet.ShouldProcess($install, $action)) {
    return [pscustomobject]@{
        Action = $(if ($Rollback) { 'Rollback' } else { 'Upgrade' }); Applied = $false
        InstallPath = $install; BackupPath = $backup; DisplacedPath = $displaced; StagePath = $stage; Version = $expected
    }
}

$movedCurrent = $false
try {
    # All writes begin here, after validation and ShouldProcess (including -WhatIf).
    Copy-ApplicationTree $source $stage $sourceInventory
    Assert-SameInventory $sourceInventory @(Assert-ApplicationPayload $stage $expected)
    Assert-SameInventory $sourceInventory @(Get-SafeInventory $source)
    Assert-SafePath $install ([bool]$Rollback)
    if (Test-Path -LiteralPath $install) {
        Assert-SameInventory $activeInventory @(Get-SafeInventory $install)
        Assert-NotInUse $install $activeInventory
        if (Test-Path -LiteralPath $displaced) { throw 'The retained directory already exists; nothing will be overwritten.' }
        [IO.Directory]::Move($install, $displaced)
        $movedCurrent = $true
    }
    [IO.Directory]::Move($stage, $install)
}
catch {
    $detail = if ($movedCurrent -and -not (Test-Path -LiteralPath $install)) {
        'The application path is currently absent. Restore the retained backup using -Rollback -BackupPath and -InstallPath.'
    } else { 'Existing folders have been retained. Inspect the reported paths before trying again.' }
    throw "Update did not complete. $detail BackupPath: $backup DisplacedPath: $displaced StagePath: $stage Cause: $($_.Exception.Message)"
}

[pscustomobject]@{
    Action = $(if ($Rollback) { 'Rollback' } else { 'Upgrade' }); Applied = $true
    InstallPath = $install; BackupPath = $backup; DisplacedPath = $displaced; StagePath = $null; Version = $expected
}
