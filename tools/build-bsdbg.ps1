#requires -Version 5.1
<#
.SYNOPSIS
    Builds the ButterscotchDbg runtime (bsdbg.dll) and the out-of-process
    debug host (bsdbg-host.exe) for Windows.

.DESCRIPTION
    Configures Butterscotch with PLATFORM=embedded and BACKEND=sdl2 (or noop),
    builds bsdbg.dll and the native debug host, and stages both together with
    SDL2.dll in UndertaleModRunner/native/win-<arch>.

.PARAMETER Architecture
    x64 (default) or all. 'all' is kept for compatibility and now builds only
    x64: the 32-bit runtime + host are produced by the same loop when passed
    explicitly (-Architecture x86), but CI and the managed build only need x64.

.PARAMETER Configuration
    CMake build type: Release (default) or Debug.

.PARAMETER Backend
    sdl2 (default, windowed) or noop (headless, no SDL2 dependency).

.PARAMETER Clean
    Delete the build directory before configuring.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'all')]
    [string] $Architecture = 'x64',

    [ValidateSet('Debug', 'Release', 'RelWithDebInfo', 'MinSizeRel')]
    [string] $Configuration = 'Release',

    [ValidateSet('sdl2', 'noop')]
    [string] $Backend = 'sdl2',

    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ButterscotchDir = Join-Path $RepoRoot 'Butterscotch'
$SDLVersion = '2.30.9'
$SDLZip = Join-Path $RepoRoot "extern/SDL2-devel-$SDLVersion-VC.zip"
$SDLRoot = Join-Path $RepoRoot "extern/SDL2-$SDLVersion"
$Architectures = if ($Architecture -eq 'all') { @('x64') } else { @($Architecture) }

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Find-VcVars {
    $vcvarsName = 'vcvars64.bat'
    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\$vcvarsName",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\$vcvarsName",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\$vcvarsName",
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\$vcvarsName",
        "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\$vcvarsName"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path $vswhere) {
        $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($install) {
            $vcvars = Join-Path $install "VC/Auxiliary/Build/$vcvarsName"
            if (Test-Path $vcvars) { return $vcvars }
        }
    }

    throw "Could not locate $vcvarsName (Visual Studio 2022 C++ workload with x64 tools required)."
}

function Invoke-VcCommand([string] $VcVars, [string] $Command) {
    $line = "call `"$VcVars`" >nul && $Command"
    & cmd.exe /d /s /c $line
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command"
    }
}

function Get-BuildDir([string] $Arch) {
    if ($Arch -eq 'x64') { return (Join-Path $RepoRoot 'build/bsdbg') }
    return (Join-Path $RepoRoot "build/bsdbg-$Arch")
}

function Get-OutDir([string] $Arch) {
    return (Join-Path $RepoRoot "UndertaleModRunner/native/win-$Arch")
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

    foreach ($arch in $Architectures) {
        if (-not (Test-Path (Join-Path $SDLRoot "lib/$arch/SDL2.lib"))) {
            throw "SDL2 $arch import library not found under $SDLRoot"
        }
    }
}

# Ensure the Butterscotch submodule is present and carries the BSDBG hooks.
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

if ($Clean) {
    foreach ($arch in $Architectures) {
        $dir = Get-BuildDir $arch
        if (Test-Path $dir) {
            Write-Step "Removing $dir"
            Remove-Item -Recurse -Force $dir
        }
    }
}

$results = @()
foreach ($arch in $Architectures) {
    $BuildDir = Get-BuildDir $arch
    $OutDir = Get-OutDir $arch

    # A cached configure from the other backend carries FORCE'd renderer options
    # (noop disables GL), so start from scratch when the backend changed.
    $cacheFile = Join-Path $BuildDir 'CMakeCache.txt'
    if (Test-Path $cacheFile) {
        $cachedPlatform = Select-String -Path $cacheFile -Pattern '^PLATFORM:' | Select-Object -First 1
        $cachedBackend = Select-String -Path $cacheFile -Pattern '^BACKEND:' | Select-Object -First 1
        $platformOk = $cachedPlatform -and $cachedPlatform.Line -match '=embedded$'
        $backendOk = $cachedBackend -and $cachedBackend.Line -match "=$([regex]::Escape($Backend))$"
        if (-not ($platformOk -and $backendOk)) {
            Write-Step "Build directory configured for a different platform/backend; removing $BuildDir"
            Remove-Item -Recurse -Force $BuildDir
        }
    }

    $VcVars = Find-VcVars
    Write-Step "[$arch] Using $VcVars"

    $cmakeArgs = @(
        '-S', $ButterscotchDir,
        '-B', $BuildDir,
        '-G', 'Ninja',
        '-DPLATFORM=embedded',
        "-DBACKEND=$Backend",
        "-DCMAKE_BUILD_TYPE=$Configuration",
        '-DVIDEO_BACKEND=none',
        '-DENABLE_FMOD=OFF'
    )
    if ($Backend -eq 'sdl2') {
        $cmakeArgs += "-DSDL2_ROOT=$($SDLRoot -replace '\\', '/')"
    }

    Write-Step "[$arch] Configuring"
    Invoke-VcCommand $VcVars ("cmake " + ($cmakeArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' ')

    Write-Step "[$arch] Building"
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
    Write-Step "[$arch] Staged $($dll.FullName) -> $OutDir"

    # Out-of-process debug host. It loads bsdbg.dll dynamically, so it must be
    # compiled for the same architecture and staged next to it.
    $hostSource = Join-Path $RepoRoot 'ButterscotchDbg/host/bsdbg_host.c'
    $hostInclude = Join-Path $RepoRoot 'ButterscotchDbg/include'
    $stagedHost = Join-Path $OutDir 'bsdbg-host.exe'
    $hostObj = Join-Path $BuildDir 'bsdbg_host.obj'
    Write-Step "[$arch] Building bsdbg-host.exe"
    Invoke-VcCommand $VcVars ("cl /nologo /W4 /O2 /MT /I`"$hostInclude`" `"$hostSource`" /Fo:`"$hostObj`" /Fe:`"$stagedHost`" /link /SUBSYSTEM:CONSOLE")
    if (-not (Test-Path $stagedHost)) {
        throw "bsdbg-host.exe was not produced for $arch"
    }
    (Get-Item $stagedHost).LastWriteTime = $stagedAt
    Write-Step "[$arch] Staged $stagedHost"

    if ($Backend -eq 'sdl2') {
        $stagedSdl = Join-Path $OutDir 'SDL2.dll'
        Copy-Item -Force (Join-Path $SDLRoot "lib/$arch/SDL2.dll") $stagedSdl
        # Copy-Item preserves the package's old timestamp; touch the staged copy so
        # MSBuild's incremental Butterscotch target sees it as up to date.
        (Get-Item $stagedSdl).LastWriteTime = $stagedAt
        $sdlLicense = Join-Path $OutDir 'SDL2.COPYING.txt'
        if (-not (Test-Path $sdlLicense)) {
            Copy-Item -Force (Join-Path $SDLRoot 'COPYING.txt') $sdlLicense
        }
        Write-Step "[$arch] Staged SDL2.dll -> $OutDir"
    }

    $results += [pscustomobject]@{ Architecture = $arch; Dll = $stagedDll; Host = $stagedHost }
}

foreach ($result in $results) {
    Write-Host "Build succeeded ($($result.Architecture)): $($result.Dll), $($result.Host)" -ForegroundColor Green
}
