#requires -Version 5.1
<#
.SYNOPSIS
    Publishes UndertaleModTool for Windows x64 into one output folder.

.DESCRIPTION
    The application itself is always x64. Debugging 32-bit games runs through
    the out-of-process bsdbg-host.exe (native/win-x86), so no x86 UndertaleModTool
    build is needed or produced.

.PARAMETER Configuration
    Build configuration (default Release).

.PARAMETER Output
    Destination folder. Relative paths are resolved from the repository root.

.PARAMETER SelfContained
    Publish the .NET runtime with the app.

.PARAMETER SingleFile
    Publish a single-file executable.

.PARAMETER DefineConstants
    Optional DefineConstants value (e.g. SHOW_COMMIT_HASH for nightly builds).
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [Parameter(Mandatory = $true)]
    [string] $Output,
    [switch] $SelfContained,
    [switch] $SingleFile,
    [string] $DefineConstants = ''
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
$buildDir = Join-Path $OutputDir '.publish-x64'

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$arguments = @(
    'publish', $Project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=$($SingleFile.IsPresent.ToString().ToLowerInvariant())",
    '-o', $buildDir
)
if (-not [string]::IsNullOrWhiteSpace($DefineConstants)) {
    $arguments += "-p:DefineConstants=$DefineConstants"
}
Write-Step "Publishing win-x64 -> $buildDir"
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for win-x64 (exit code $LASTEXITCODE)."
}

Write-Step "Merging x64 output -> $OutputDir"
Copy-Item -Path (Join-Path $buildDir '*') -Destination $OutputDir -Recurse -Force
Remove-Item -Recurse -Force $buildDir

Write-Host "Published: $(Join-Path $OutputDir 'UndertaleModTool.exe')" -ForegroundColor Green
