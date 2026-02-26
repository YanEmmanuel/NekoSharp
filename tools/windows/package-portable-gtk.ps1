[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageRoot,

    [string]$ExeName,

    [string]$Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-GtkRoot {
    $candidateRoots = @()

    if ($env:MSYS2_ROOT) {
        $candidateRoots += (Join-Path $env:MSYS2_ROOT "ucrt64")
    }
    if ($env:RUNNER_TEMP) {
        $candidateRoots += (Join-Path $env:RUNNER_TEMP "msys64\ucrt64")
    }

    $candidateRoots += @(
        "C:\msys64\ucrt64",
        "D:\a\_temp\msys64\ucrt64"
    )

    $glibFromPath = Get-Command glib-compile-schemas.exe -ErrorAction SilentlyContinue
    if ($glibFromPath) {
        $rootFromPath = Split-Path (Split-Path $glibFromPath.Source -Parent) -Parent
        $candidateRoots = @($rootFromPath) + $candidateRoots
    }

    $candidateRoots = $candidateRoots | Where-Object { $_ } | Select-Object -Unique

    foreach ($candidate in $candidateRoots) {
        if (Test-Path (Join-Path $candidate "bin\glib-compile-schemas.exe") -PathType Leaf) {
            return $candidate
        }
    }

    throw "Could not locate MSYS2 UCRT64 root. Checked: $($candidateRoots -join ', ')"
}

function Copy-OneOf {
    param(
        [string[]]$Sources,
        [string]$Dest,
        [switch]$Required
    )

    foreach ($source in $Sources) {
        if (Test-Path $source) {
            New-Item -ItemType Directory -Force -Path (Split-Path $Dest -Parent) | Out-Null
            Copy-Item $source $Dest -Recurse -Force
            Write-Host "Copied: $source -> $Dest"
            return
        }
    }

    if ($Required) {
        throw "None of the required paths exist for $Dest. Checked: $($Sources -join ', ')"
    }

    Write-Warning "Missing optional paths (skipped): $($Sources -join ', ')"
}

if (-not (Test-Path $PublishDir -PathType Container)) {
    throw "Publish dir not found: $PublishDir"
}
$publishDirFull = (Resolve-Path $PublishDir).Path
$packageRootFull = [System.IO.Path]::GetFullPath($PackageRoot)

if ($Tag) {
    Write-Host "Packaging tag: $Tag"
}

$gtkRoot = Get-GtkRoot
Write-Host "Using GTK root: $gtkRoot"

if (Test-Path $packageRootFull) {
    Remove-Item $packageRootFull -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageRootFull | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packageRootFull "gtk") | Out-Null

Copy-Item (Join-Path $publishDirFull "*") $packageRootFull -Recurse -Force

if (-not $ExeName) {
    $exe = Get-ChildItem -Path $packageRootFull -Filter *.exe -File | Sort-Object Name | Select-Object -First 1
    if (-not $exe) { throw "No .exe found in $packageRootFull" }
    $ExeName = $exe.Name
}
elseif (-not (Test-Path (Join-Path $packageRootFull $ExeName) -PathType Leaf)) {
    throw "EXE '$ExeName' was not found in package root '$packageRootFull'."
}

# Required runtime folders
Copy-OneOf @("$gtkRoot\bin") "$packageRootFull\gtk\bin" -Required
Copy-OneOf @("$gtkRoot\lib\gio") "$packageRootFull\gtk\lib\gio" -Required
Copy-OneOf @("$gtkRoot\lib\gtk-4.0") "$packageRootFull\gtk\lib\gtk-4.0" -Required
Copy-OneOf @("$gtkRoot\lib\gdk-pixbuf-2.0") "$packageRootFull\gtk\lib\gdk-pixbuf-2.0" -Required
Copy-OneOf @("$gtkRoot\share\glib-2.0") "$packageRootFull\gtk\share\glib-2.0" -Required
Copy-OneOf @("$gtkRoot\share\libadwaita-1") "$packageRootFull\gtk\share\libadwaita-1" -Required

# Optional runtime folders
Copy-OneOf @("$gtkRoot\etc\gtk-4.0", "$gtkRoot\share\gtk-4.0") "$packageRootFull\gtk\etc\gtk-4.0"
Copy-OneOf @("$gtkRoot\etc\fonts", "$gtkRoot\share\fontconfig") "$packageRootFull\gtk\etc\fonts"
Copy-OneOf @("$gtkRoot\share\icons") "$packageRootFull\gtk\share\icons"
Copy-OneOf @("$gtkRoot\share\themes") "$packageRootFull\gtk\share\themes"
Copy-OneOf @("$gtkRoot\share\locale") "$packageRootFull\gtk\share\locale"

$glibCompileSchemas = "$gtkRoot\bin\glib-compile-schemas.exe"
$schemaDir = "$packageRootFull\gtk\share\glib-2.0\schemas"
if (Test-Path $glibCompileSchemas -PathType Leaf) {
    if (-not (Test-Path $schemaDir -PathType Container)) {
        throw "Schema directory not found: $schemaDir"
    }
    & $glibCompileSchemas $schemaDir
    if ($LASTEXITCODE -ne 0) { throw "glib-compile-schemas failed" }
}
else {
    Write-Warning "Skipping glib-compile-schemas (tool not found)."
}

$requiredDllGroups = @(
    @{ Name = "GTK4"; Candidates = @("libgtk-4-1.dll") },
    @{ Name = "libadwaita"; Candidates = @("libadwaita-1-0.dll") },
    @{ Name = "GLib"; Candidates = @("libglib-2.0-0.dll", "glib-2.0-0.dll") }
)

foreach ($group in $requiredDllGroups) {
    $found = $false
    foreach ($candidate in $group.Candidates) {
        if (Test-Path (Join-Path $packageRootFull "gtk\bin\$candidate") -PathType Leaf) {
            $found = $true
            break
        }
    }

    if (-not $found) {
        throw "Required DLL group missing ($($group.Name)). Checked: $($group.Candidates -join ', ')"
    }
}

$cmdPath = Join-Path $packageRootFull "Run-NekoSharp.cmd"
@"
@echo off
setlocal

set "APPDIR=%~dp0"
set "GTKDIR=%APPDIR%gtk"

set "PATH=%GTKDIR%\bin;%PATH%"
set "XDG_DATA_DIRS=%GTKDIR%\share"
set "GSETTINGS_SCHEMA_DIR=%GTKDIR%\share\glib-2.0\schemas"
set "GTK_DATA_PREFIX=%GTKDIR%"
set "GIO_MODULE_DIR=%GTKDIR%\lib\gio\modules"

set "GDK_PIXBUF_MODULEDIR="
for /d %%D in ("%GTKDIR%\lib\gdk-pixbuf-2.0\*\loaders") do set "GDK_PIXBUF_MODULEDIR=%%~fD"
if not defined GDK_PIXBUF_MODULEDIR set "GDK_PIXBUF_MODULEDIR=%GTKDIR%\lib\gdk-pixbuf-2.0\2.10.0\loaders"

if exist "%GTKDIR%\bin\gdk-pixbuf-query-loaders.exe" (
  "%GTKDIR%\bin\gdk-pixbuf-query-loaders.exe" > "%GDK_PIXBUF_MODULEDIR%\loaders.cache" 2>nul
  if exist "%GDK_PIXBUF_MODULEDIR%\loaders.cache" (
    set "GDK_PIXBUF_MODULE_FILE=%GDK_PIXBUF_MODULEDIR%\loaders.cache"
  )
)

start "" "%APPDIR%$ExeName"
exit /b %errorlevel%
"@ | Set-Content -Path $cmdPath -Encoding ascii

Write-Host "Portable package ready: $packageRootFull"
Write-Host "Executable: $ExeName"
