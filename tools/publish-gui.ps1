#requires -Version 5.1
<#
.SYNOPSIS
    Publishes UndertaleModTool for Windows x64 and x86 into one output folder.

.DESCRIPTION
    The x64 build keeps the normal name (UndertaleModTool.exe). The x86 build is
    named UndertaleModTool_x86.exe and uses the same managed assemblies, so both
    executables can live in the same directory.

    Self-contained non-single-file publishes cannot share a folder (both would
    contain hostfxr/coreclr with the same names), so the x86 side is published as
    a single-file bundle in that combination. Framework-dependent and single-file
    publishes merge directly.

.PARAMETER Configuration
    Build configuration (default Release).

.PARAMETER Output
    Destination folder. Relative paths are resolved from the repository root.

.PARAMETER SelfContained
    Publish the .NET runtime with the app.

.PARAMETER SingleFile
    Publish single-file executables.

.PARAMETER DefineConstants
    Optional DefineConstants value (e.g. SHOW_COMMIT_HASH for nightly builds).

.PARAMETER X86ExecutableName
    Name of the 32-bit host executable (default UndertaleModTool_x86.exe).
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [Parameter(Mandatory = $true)]
    [string] $Output,
    [switch] $SelfContained,
    [switch] $SingleFile,
    [string] $DefineConstants = '',
    [string] $X86ExecutableName = 'UndertaleModTool_x86.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'UndertaleModTool/UndertaleModTool.csproj'
if ([IO.Path]::IsPathRooted($Output)) {
    $OutputDir = [IO.Path]::GetFullPath($Output)
} else {
    $OutputDir = [IO.Path]::GetFullPath((Join-Path $RepoRoot $Output))
}
$x64Dir = Join-Path $OutputDir '.publish-x64'
$x86Dir = Join-Path $OutputDir '.publish-x86'

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Publish([string] $Runtime, [string] $Destination, [bool] $SingleFilePublish) {
    $arguments = @(
        'publish', $Project,
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(),
        "-p:PublishSingleFile=$($SingleFilePublish.ToString().ToLowerInvariant())",
        '-o', $Destination
    )
    if (-not [string]::IsNullOrWhiteSpace($DefineConstants)) {
        $arguments += "-p:DefineConstants=$DefineConstants"
    }
    Write-Step "Publishing $Runtime -> $Destination"
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Runtime (exit code $LASTEXITCODE)."
    }
}

if (Test-Path $x64Dir) { Remove-Item -Recurse -Force $x64Dir }
if (Test-Path $x86Dir) { Remove-Item -Recurse -Force $x86Dir }
New-Item -ItemType Directory -Force -Path $OutputDir, $x64Dir, $x86Dir | Out-Null

Invoke-Publish 'win-x64' $x64Dir $SingleFile.IsPresent

# A RID-specific multi-file publish tags the application assembly for that
# architecture, so x64 and x86 copies of UndertaleModTool.dll cannot share a
# folder. The x86 side is always a single-file bundle: it carries its own
# managed assembly and native content, and only the host executable lands next
# to the x64 tree.
Write-Step 'Publishing x86 as a single-file bundle so both architectures can share the folder'
Invoke-Publish 'win-x86' $x86Dir $true

Write-Step "Merging x64 output -> $OutputDir"
Copy-Item -Path (Join-Path $x64Dir '*') -Destination $OutputDir -Recurse -Force

$renamed = @{
    'UndertaleModTool.exe' = $X86ExecutableName
    'UndertaleModTool.pdb' = [IO.Path]::ChangeExtension($X86ExecutableName, '.pdb')
}
Write-Step "Merging x86 output -> $OutputDir (host renamed to $X86ExecutableName)"
Get-ChildItem -Path $x86Dir -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($x86Dir.Length).TrimStart('\', '/')
    $destinationRelative = if ($renamed.ContainsKey($relative)) { $renamed[$relative] } else { $relative }
    $destination = Join-Path $OutputDir $destinationRelative
    $destinationParent = Split-Path -Parent $destination
    if ($destinationParent) {
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
    }
    Copy-Item -Force $_.FullName $destination
}

Remove-Item -Recurse -Force $x64Dir, $x86Dir

$x64Exe = Join-Path $OutputDir 'UndertaleModTool.exe'
$x86Exe = Join-Path $OutputDir $X86ExecutableName
Write-Host "Published x64: $x64Exe" -ForegroundColor Green
Write-Host "Published x86: $x86Exe" -ForegroundColor Green
