#requires -Version 5.1
<#
.SYNOPSIS
    Builds the ButterscotchDbg embedded runner (bsdbg.dll) for Windows x64.

.DESCRIPTION
    Configures Butterscotch with PLATFORM=embedded and BACKEND=sdl2 (or noop),
    builds the shared bsdbg.dll, and stages it together with SDL2.dll in
    UndertaleModRunner/native/win-x64.

.PARAMETER Configuration
    CMake build type: Release (default) or Debug.

.PARAMETER Backend
    sdl2 (default, windowed) or noop (headless, no SDL2 dependency).

.PARAMETER Clean
    Delete the build directory before configuring.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'RelWithDebInfo', 'MinSizeRel')]
    [string] $Configuration = 'Release',

    [ValidateSet('sdl2', 'noop')]
    [string] $Backend = 'sdl2',

    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$BuildDir = Join-Path $RepoRoot 'build/bsdbg'
$OutDir = Join-Path $RepoRoot 'UndertaleModRunner/native/win-x64'
$SDLVersion = '2.30.9'
$SDLZip = Join-Path $RepoRoot "extern/SDL2-devel-$SDLVersion-VC.zip"
$SDLRoot = Join-Path $RepoRoot "extern/SDL2-$SDLVersion"

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Find-VcVars {
    $candidates = @(
        'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path $vswhere) {
        $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($install) {
            $vcvars = Join-Path $install 'VC/Auxiliary/Build/vcvars64.bat'
            if (Test-Path $vcvars) { return $vcvars }
        }
    }

    throw 'Could not locate vcvars64.bat (Visual Studio 2022 C++ workload required).'
}

function Invoke-VcCommand([string] $VcVars, [string] $Command) {
    $line = "call `"$VcVars`" >nul && $Command"
    & cmd.exe /d /s /c $line
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command"
    }
}

if ($Backend -eq 'sdl2') {
    if (-not (Test-Path $SDLRoot)) {
        if (-not (Test-Path $SDLZip)) {
            Write-Step "Downloading SDL2 $SDLVersion development package"
            New-Item -ItemType Directory -Force -Path (Split-Path $SDLZip) | Out-Null
            if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
                & curl.exe -L --fail --output $SDLZip "https://www.libsdl.org/release/SDL2-devel-$SDLVersion-VC.zip"
            } else {
                Invoke-WebRequest -UseBasicParsing `
                    -Uri "https://www.libsdl.org/release/SDL2-devel-$SDLVersion-VC.zip" `
                    -OutFile $SDLZip
            }
        }
        Write-Step "Extracting $SDLZip"
        Expand-Archive -Path $SDLZip -DestinationPath (Join-Path $RepoRoot 'extern') -Force
    }

    if (-not (Test-Path (Join-Path $SDLRoot 'lib/x64/SDL2.lib'))) {
        throw "SDL2 x64 import library not found under $SDLRoot"
    }
}

# Ensure the Butterscotch submodule is present and carries the BSDBG hooks.
$ButterscotchDir = Join-Path $RepoRoot 'Butterscotch'
if (-not (Test-Path (Join-Path $ButterscotchDir 'CMakeLists.txt'))) {
    Write-Step 'Initializing Butterscotch submodule'
    & git -C $RepoRoot submodule update --init Butterscotch
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to initialize the Butterscotch submodule.'
    }
}

$BsdbgPatch = Join-Path $RepoRoot 'ButterscotchDbg/patches/butterscotch-bsdbg.patch'
if (Test-Path $BsdbgPatch) {
    $ErrorActionPreference = 'Continue'
    & git -C $ButterscotchDir apply --reverse --check $BsdbgPatch 2>&1 | Out-Null
    $patchAlreadyApplied = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'

    if (-not $patchAlreadyApplied) {
        # The hooks may already be committed in the submodule (or the tree may
        # contain additional local changes), so the patch no longer applies in
        # either direction. Detect the hook markers instead of relying on the
        # patch's exact context before trying a forward apply.
        $logHook = Select-String -Path (Join-Path $ButterscotchDir 'src/log.c') -Pattern 'BsdbgHook_Log' -Quiet
        $vmHook = Select-String -Path (Join-Path $ButterscotchDir 'src/vm.c') -Pattern 'BsdbgHook_VmInstruction' -Quiet
        $embeddedTarget = Select-String -Path (Join-Path $ButterscotchDir 'CMakeLists.txt') -Pattern 'PLATFORM STREQUAL "embedded"' -Quiet
        if ($logHook -and $vmHook -and $embeddedTarget) {
            $patchAlreadyApplied = $true
            Write-Step 'Butterscotch BSDBG hooks already present; skipping patch'
        }
    }

    if (-not $patchAlreadyApplied) {
        Write-Step 'Applying Butterscotch BSDBG hooks patch'
        & git -C $ButterscotchDir apply --whitespace=nowarn $BsdbgPatch
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to apply ButterscotchDbg/patches/butterscotch-bsdbg.patch.'
        }
    }
}

# A cached configure from the other backend carries FORCE'd renderer options
# (noop disables GL), so start from scratch when the backend changed.
$cacheFile = Join-Path $BuildDir 'CMakeCache.txt'
if ((Test-Path $cacheFile) -and -not $Clean) {
    $cachedPlatform = Select-String -Path $cacheFile -Pattern '^PLATFORM:' | Select-Object -First 1
    $cachedBackend = Select-String -Path $cacheFile -Pattern '^BACKEND:' | Select-Object -First 1
    $platformOk = $cachedPlatform -and $cachedPlatform.Line -match '=embedded$'
    $backendOk = $cachedBackend -and $cachedBackend.Line -match "=$([regex]::Escape($Backend))$"
    if (-not ($platformOk -and $backendOk)) {
        Write-Step "Build directory configured for a different platform/backend; removing $BuildDir"
        Remove-Item -Recurse -Force $BuildDir
    }
}

if ($Clean -and (Test-Path $BuildDir)) {
    Write-Step "Removing $BuildDir"
    Remove-Item -Recurse -Force $BuildDir
}

$VcVars = Find-VcVars
Write-Step "Using $VcVars"

$cmakeArgs = @(
    '-S', (Join-Path $RepoRoot 'Butterscotch'),
    '-B', $BuildDir,
    '-G', 'Ninja',
    "-DPLATFORM=embedded",
    "-DBACKEND=$Backend",
    "-DCMAKE_BUILD_TYPE=$Configuration",
    '-DVIDEO_BACKEND=none',
    '-DENABLE_FMOD=OFF'
)
if ($Backend -eq 'sdl2') {
    $cmakeArgs += "-DSDL2_ROOT=$($SDLRoot -replace '\\', '/')"
}

Write-Step 'Configuring'
Invoke-VcCommand $VcVars ("cmake " + ($cmakeArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' ')

Write-Step 'Building'
Invoke-VcCommand $VcVars "cmake --build `"$BuildDir`" --config $Configuration --parallel 2"

$dll = Get-ChildItem -Path $BuildDir -Recurse -Filter 'bsdbg.dll' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $dll) {
    throw "bsdbg.dll was not produced under $BuildDir"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stagedDll = Join-Path $OutDir 'bsdbg.dll'
Copy-Item -Force $dll.FullName $stagedDll
$stagedAt = Get-Date
(Get-Item $stagedDll).LastWriteTime = $stagedAt
Write-Step "Staged $($dll.FullName) -> $OutDir"

if ($Backend -eq 'sdl2') {
    $stagedSdl = Join-Path $OutDir 'SDL2.dll'
    Copy-Item -Force (Join-Path $SDLRoot 'lib/x64/SDL2.dll') $stagedSdl
    # Copy-Item preserves the package's old timestamp; touch the staged copy so
    # MSBuild's incremental Butterscotch target sees it as up to date.
    (Get-Item $stagedSdl).LastWriteTime = $stagedAt
    Write-Step "Staged SDL2.dll -> $OutDir"
}

Write-Host "Build succeeded: $(Join-Path $OutDir 'bsdbg.dll')" -ForegroundColor Green
