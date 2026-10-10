<#
.SYNOPSIS
    Builds Clash Matrix for Navisworks Manage 2024, 2025, 2026 and 2027, assembles
    the Autodesk plug-in bundle, and packages it as a per-user MSI.

.DESCRIPTION
    Everything a colleague needs is in the resulting MSI - they do not need Visual
    Studio, the .NET SDK, or administrator rights.

    This machine needs:
      * the .NET SDK (already there if Visual Studio is installed)
      * the WiX command-line tool, which this script installs on first run
      * NuGet access (the Navisworks API comes from the Speckle.Navisworks.API
        package, so no Navisworks install is needed to build any year)

.EXAMPLE
    .\build-installer.ps1
    Builds all four Navisworks releases and packages them. No Navisworks install
    is required - the API reference assemblies are restored from NuGet.

.EXAMPLE
    .\build-installer.ps1 -Versions 2027
    Builds and packages only Navisworks 2027. The MSI then covers 2027 only.

.EXAMPLE
    .\build-installer.ps1 -SkipBuild
    Repackage what is already in src\ClashMatrix\bin\<year>\ without recompiling.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,

    # WiX v7 refuses to run until you accept the Open Source Maintenance Fee
    # EULA, which carries a fee for commercial use above a revenue threshold.
    # v5.0.1 is the last release under the plain MS-RL open-source licence and
    # builds this installer identically. Pass -WixVersion 7.0.0 if you would
    # rather accept the v7 terms.
    [string]$WixVersion = '5.0.1',

    # Comma-separated Navisworks years to build/package, e.g. "2024,2027".
    # Empty = all four. Only the years listed here end up in the MSI.
    [string]$Versions = ''
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$rootDir      = Split-Path -Parent $installerDir
$projectPath  = Join-Path $rootDir 'src\ClashMatrix\ClashMatrix.csproj'
$stageDir     = Join-Path $installerDir 'stage'
$bundleDir    = Join-Path $stageDir 'ClashMatrix.bundle'
$filesWxs     = Join-Path $installerDir 'Files.wxs'

if (-not (Test-Path $projectPath)) { throw "Cannot find the project at $projectPath" }

# Navisworks release -> build configuration + bundle folder. The folder NN in
# Contents\<NN> is the Navisworks series number (NN = year - 2003): 2024 -> Nw21,
# 2025 -> Nw22, 2026 -> Nw23, 2027 -> Nw24.
$allTargets = [ordered]@{
    '2024' = @{ Config = 'Release N24'; Nn = '21' }
    '2025' = @{ Config = 'Release N25'; Nn = '22' }
    '2026' = @{ Config = 'Release N26'; Nn = '23' }
    '2027' = @{ Config = 'Release N27'; Nn = '24' }
}

if ($Versions) {
    $targets = [ordered]@{}
    foreach ($year in ($Versions -split ',' | ForEach-Object { $_.Trim() })) {
        if (-not $allTargets.Contains($year)) {
            throw "Unknown Navisworks year '$year'. Use one of: $($allTargets.Keys -join ', ')"
        }
        $targets[$year] = $allTargets[$year]
    }
} else {
    $targets = $allTargets
}

# ---------------------------------------------------------------- version ----
$version = [regex]::Match(
    (Get-Content $projectPath -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $version) { throw 'Could not read <Version> from the project file.' }
Write-Host "Clash Matrix $version" -ForegroundColor Cyan

# ------------------------------------------------------------------ build ----
$projectDir = Split-Path -Parent $projectPath

function Get-BinDir([string]$year) {
    # One output folder per Navisworks year - see OutputPath in the .csproj.
    return Join-Path $projectDir "bin\$year"
}

if (-not $SkipBuild) {
    foreach ($year in $targets.Keys) {
        $cfg = $targets[$year].Config
        Write-Host "  building for Navisworks $year ..." -ForegroundColor DarkGray

        # Emptied first, so nothing left over from an older build can be packaged.
        $binDir = Get-BinDir $year
        if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }

        # Each configuration is its own dotnet build (and restore), never one
        # Batch Build: the years restore different Navisworks API packages.
        & dotnet build $projectPath -c $cfg --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Build failed for Navisworks $year" }
    }
}

# --------------------------------------------------------------- assemble ----
Write-Host '  assembling the bundle ...' -ForegroundColor DarkGray

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null

# Exactly these files, never "whatever is in bin". Navisworks provides its own API
# DLLs at runtime, so they are not copied. The ribbon XAML and icons are loaded by
# filename from the plugin folder.
$icons     = @('ClashMatrix16.png', 'ClashMatrix32.png', 'ExportResults16.png', 'ExportResults32.png', 'About16.png', 'About32.png')
$shipFiles = @('ClashMatrix.dll', 'AddinRibbon.xaml') + $icons

foreach ($year in $targets.Keys) {
    $nn      = $targets[$year].Nn
    $binDir  = Get-BinDir $year
    if (-not (Test-Path $binDir)) { throw "No build output at $binDir - run without -SkipBuild." }

    $contentsDir = Join-Path $bundleDir "Contents\$nn"
    New-Item -ItemType Directory -Path $contentsDir -Force | Out-Null

    foreach ($name in $shipFiles) {
        $source = Join-Path $binDir $name
        if (Test-Path $source) { Copy-Item $source -Destination $contentsDir }
    }

    if (-not (Test-Path (Join-Path $contentsDir 'ClashMatrix.dll'))) {
        throw "ClashMatrix.dll missing from the Navisworks $year build output."
    }

    # Navisworks also resolves the ribbon XAML and icons from a locale subfolder;
    # mirror them there so the MSI behaves exactly like the debug deploy.
    $localeDir = Join-Path $contentsDir 'en-US'
    New-Item -ItemType Directory -Path $localeDir -Force | Out-Null
    foreach ($name in @('AddinRibbon.xaml') + $icons) {
        $source = Join-Path $contentsDir $name
        if (Test-Path $source) { Copy-Item $source -Destination $localeDir }
    }
}

# Manifest. install\PackageContents.xml is the same file the csproj copies on a
# Debug build; its version fields are stamped here from the project, so there is a
# single source of truth. The negative look-behind leaves SchemaVersion untouched.
$manifest = Get-Content (Join-Path $installerDir 'PackageContents.xml') -Raw
$manifest = [regex]::Replace($manifest, '(?<![A-Za-z])(FriendlyVersion|AppVersion|Version)="[^"]+"', {
    param($m) "{0}=`"{1}`"" -f $m.Groups[1].Value, $version
})
Set-Content (Join-Path $bundleDir 'PackageContents.xml') -Value $manifest -Encoding UTF8

Copy-Item (Join-Path $installerDir 'readme.txt') (Join-Path $bundleDir 'Contents\readme.txt')

# ------------------------------------------------------- generate Files.wxs --
Write-Host '  generating Files.wxs ...' -ForegroundColor DarkGray

$components = New-Object System.Collections.Generic.List[string]
$script:index = 0

# Per-user components cannot use a file as their KeyPath (ICE38): each one gets
# an HKCU value instead. This key is the add-in's install record.
$regKey = 'Software\Mohamed Ali Harby\ClashMatrix\Components'

# A component that holds a file plus a registry KeyPath cannot have a generated
# GUID (WIX0230). Derive one from the file's path instead: stable from build to
# build, so an upgrade still recognises each component.
function Get-StableGuid([string]$Text) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes('ClashMatrix:' + $Text.ToLowerInvariant()))
    return (New-Object System.Guid -ArgumentList (, $bytes)).ToString().ToUpperInvariant()
}

function New-FileComponent([string]$DirId, [string]$Relative) {
    $script:index++
    $id = "cmp$script:index"
    $guid = Get-StableGuid $Relative
    $script:components.Add(
        "      <Component Id=""$id"" Directory=""$DirId"" Guid=""$guid"">" + [Environment]::NewLine +
        "        <File Source=""$Relative"" KeyPath=""no"" />" + [Environment]::NewLine +
        "        <RegistryValue Root=""HKCU"" Key=""$regKey"" Name=""$id"" Type=""integer"" Value=""1"" KeyPath=""yes"" />" + [Environment]::NewLine +
        "      </Component>")
}

# Every folder the MSI creates under the user profile must also be removed on
# uninstall (ICE64), or empty Contents\<NN> folders are left behind.
function New-RemoveFolderComponent([string]$DirId) {
    $id = "rm$DirId"
    $script:components.Add(
        "      <Component Id=""$id"" Directory=""$DirId"">" + [Environment]::NewLine +
        "        <RemoveFolder Id=""rf$DirId"" On=""uninstall"" />" + [Environment]::NewLine +
        "        <RegistryValue Root=""HKCU"" Key=""$regKey"" Name=""$id"" Type=""integer"" Value=""1"" KeyPath=""yes"" />" + [Environment]::NewLine +
        "      </Component>")
}

# Emits the <Directory> tree for a folder (recursing into subfolders such as the
# en-US locale mirror) and records one <Component> per file it finds.
function Get-DirXml([string]$SourceDir, [string]$Name, [string]$Id) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("      <Directory Id=""$Id"" Name=""$Name"">")
    foreach ($file in Get-ChildItem $SourceDir -File | Sort-Object Name) {
        New-FileComponent $Id ($file.FullName.Substring($installerDir.Length + 1))
    }
    New-RemoveFolderComponent $Id
    foreach ($sub in Get-ChildItem $SourceDir -Directory | Sort-Object Name) {
        # Id is the parent's id + the folder name, so sibling folders that share a
        # name (e.g. the en-US locale mirror under every Contents\<NN>) stay unique.
        $childId = $Id + ($sub.Name -replace '[^A-Za-z0-9]', '')
        [void]$sb.Append((Get-DirXml $sub.FullName $sub.Name $childId))
    }
    [void]$sb.AppendLine("      </Directory>")
    return $sb.ToString()
}

# The one file at the bundle root (PackageContents.xml) lives in INSTALLFOLDER.
foreach ($file in Get-ChildItem $bundleDir -File | Sort-Object Name) {
    New-FileComponent 'INSTALLFOLDER' ($file.FullName.Substring($installerDir.Length + 1))
}

$dirXml = Get-DirXml (Join-Path $bundleDir 'Contents') 'Contents' 'dirContents'

@"
<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by build-installer.ps1. Do not edit; your changes will be overwritten. -->
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <DirectoryRef Id="INSTALLFOLDER">
$dirXml
    </DirectoryRef>

    <ComponentGroup Id="BundleFiles">
$($components -join [Environment]::NewLine)
    </ComponentGroup>
  </Fragment>
</Wix>
"@ | Set-Content $filesWxs -Encoding UTF8

Write-Host "  $($script:index) files staged" -ForegroundColor DarkGray

# ---------------------------------------------------------- native tools ----
# Runs a program and hands back its exit code and everything it printed.
#
# Windows PowerShell 5.1 turns each line a program writes to its error stream
# into an error record once that stream is redirected, and under
# $ErrorActionPreference = 'Stop' the first such line ends the script - before
# the exit code is ever looked at. A tool that prints a warning and then
# succeeds would stop the build, and the "carry on without the wizard" path
# below could never be reached. So the preference is relaxed for the call and
# the exit code decides.
function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string]$Exe,
        [string[]]$Arguments = @()
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # A program that cannot be started at all leaves the exit code of
        # whatever ran before it - which may well be 0.
        $global:LASTEXITCODE = -1
        $lines = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
        $code  = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }

    return [pscustomobject]@{
        ExitCode = $code
        Output   = (($lines | Out-String).Trim())
    }
}

# -------------------------------------------------------------------- wix ----
# WiX is kept in a folder of this script's own (installer\.tools), not as a .NET
# "global tool". There is one global copy per user, and it cannot be moved to an
# older version in place - so making it the version this script wants used to
# mean uninstalling whatever version another project on this PC had installed.
# A global copy that already IS the right version is used as it stands.
$toolDir     = Join-Path $installerDir '.tools'
$privateWix  = Join-Path $toolDir 'wix.exe'
$dotnetTools = Join-Path $env:USERPROFILE '.dotnet\tools'

function Get-WixVersion([string]$exe) {
    if ((-not $exe) -or (-not (Test-Path $exe))) { return '' }

    $result = Invoke-Native $exe @('--version')
    if ($result.ExitCode -ne 0) { return '' }

    # "5.0.1+a1b2c3" -> "5.0.1". The first line that IS a version: anything the
    # tool prints around it (a notice, a warning) is not one.
    foreach ($line in ($result.Output -split "`r?`n")) {
        if ($line.Trim() -match '^(\d+\.\d+\.\d+)') { return $Matches[1] }
    }
    return ''
}

$wix = $null

if ((Get-WixVersion $privateWix) -eq $WixVersion) {
    $wix = $privateWix
}

if (-not $wix) {
    # A global tool installed a moment ago is not on this process's PATH yet.
    $globalWix = Join-Path $dotnetTools 'wix.exe'
    $onPath    = Get-Command wix -ErrorAction SilentlyContinue
    if ($onPath) { $globalWix = $onPath.Source }

    if ((Get-WixVersion $globalWix) -eq $WixVersion) { $wix = $globalWix }
}

if (-not $wix) {
    Write-Host "  installing WiX $WixVersion into installer\.tools (one time) ..." -ForegroundColor DarkGray

    # Another version left here by an earlier run. Only this private copy is
    # replaced - nothing installed elsewhere on the machine is touched.
    if (Test-Path $toolDir) { Remove-Item $toolDir -Recurse -Force }

    $install = Invoke-Native 'dotnet' @('tool', 'install', 'wix', '--version', $WixVersion, '--tool-path', $toolDir)
    if ($install.ExitCode -ne 0) {
        Write-Host $install.Output -ForegroundColor DarkGray
        throw "Could not install WiX $WixVersion."
    }

    if (-not (Test-Path $privateWix)) {
        throw "WiX installed but wix.exe was not found under $toolDir."
    }
    $wix = $privateWix
}

$installedVersion = Get-WixVersion $wix
Write-Host "  using WiX $installedVersion ($wix)" -ForegroundColor DarkGray

# ------------------------------------------------------------- ui extension --
# The install wizard lives in a separate extension that has to be fetched. It
# must be pinned to the same version as the wix tool, or the build reports
# WIX0144 "the extension could not be found".
#
# If it cannot be added - no NuGet access on this machine, say - the MSI is built
# without the wizard rather than failing. It still installs correctly; it just
# shows a progress bar instead of a licence page.
$uiExtension = 'WixToolset.UI.wixext'
$useUI = $false

$extensionSpec = if ($installedVersion) { "$uiExtension/$installedVersion" } else { $uiExtension }

Write-Host "  adding the WiX UI extension ($extensionSpec) ..." -ForegroundColor DarkGray
$add = Invoke-Native $wix @('extension', 'add', '-g', $extensionSpec)

if ($add.ExitCode -eq 0) {
    $useUI = $true
} else {
    Write-Warning "Could not add $extensionSpec - building without the install wizard."
    Write-Host $add.Output -ForegroundColor DarkGray
}

# -------------------------------------------------------------------- build --
$msiPath = Join-Path $installerDir "ClashMatrix-$version.msi"

$wixArgs = @(
    'build'
    (Join-Path $installerDir 'ClashMatrix.wxs')
    $filesWxs
    '-d'; "ProductVersion=$version"
    '-arch'; 'x64'
    '-o'; $msiPath
)

if ($useUI) { $wixArgs += @('-d'; 'UseUI=1'; '-ext'; $uiExtension) }

Write-Host '  building the MSI ...' -ForegroundColor DarkGray

# From the installer folder, whatever folder the script was started in: the
# file paths in Files.wxs, and the icon and licence in ClashMatrix.wxs, are
# relative to it. Started from the repository root they used to resolve against
# that instead, and the build failed on the first file.
Push-Location $installerDir
try {
    $build = Invoke-Native $wix $wixArgs
} finally {
    Pop-Location
}

if ($build.Output) { Write-Host $build.Output }
if ($build.ExitCode -ne 0) { throw 'wix build failed.' }

Write-Host ''
Write-Host "Done: $msiPath" -ForegroundColor Green
Write-Host 'Send that one file. Double-click to install, no admin rights needed.' -ForegroundColor Green
