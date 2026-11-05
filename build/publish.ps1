#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a SluPlayer release: the portable folder dist\SluPlayer\, the zip
    dist\SluPlayer-<version>-win-x64.zip and (with -Installer) dist\SluPlayer-Setup-<version>.exe.

.DESCRIPTION
    1. build\fetch-vendor.ps1 makes sure the pinned engine and tools are in the vendor cache
       (SHA-256 verified; nothing is downloaded when they are already there).
    2. dotnet publish: Release, win-x64, self-contained (the .NET runtime is included, users install
       nothing), single-file, ReadyToRun (precompiled: faster start). Output and obj/bin go to a
       staging folder under %LOCALAPPDATA%\SluPlayerDev\publish (outside OneDrive), so a release
       build never disturbs Debug builds.
    3. Assembles the release folder:
         SluPlayer.exe          the app: .NET runtime, WPF and all managed code in one file
         *_cor3.dll, WebView2Loader.dll
                                native helpers of WPF/WebView2, kept next to the exe (bundling
                                them makes .NET unpack them to %TEMP% on first start, which is
                                no faster and leaves files behind; see -BundleNativeLibraries)
         libmpv-2.dll           playback engine (mpv + FFmpeg; GPU decoding and rendering)
         vulkan\vulkan-1.dll    official Khronos loader, only for PCs whose driver has none
         tools\                 yt-dlp\ (yt-dlp.exe + _internal\, the fast-starting "onedir" build),
                                deno.exe, ffmpeg.exe, ffprobe.exe (+ FFmpeg DLLs)
         licenses\              third-party licenses (build\licenses + NuGet package notices)
         README.txt             user guide (the part of README.md before the developer section)
    4. Zips the folder (top-level folder "SluPlayer\") and writes <zip>.sha256.
    5. -Installer: builds the per-user setup program from build\installer and appends the zip.

.PARAMETER Version
    Release version, also stamped into SluPlayer.exe. Default: <Version> in SluPlayer.csproj.
.PARAMETER OutDir
    Where dist\ goes. Default: <repo>\dist.
.PARAMETER Folder
    Classic folder publish (about 250 files) instead of single-file. For troubleshooting.
.PARAMETER BundleNativeLibraries
    Put the native DLLs inside SluPlayer.exe as well (IncludeNativeLibrariesForSelfExtract).
.PARAMETER SkipFetch
    Don't run fetch-vendor.ps1 (the vendor files must already be there).
.PARAMETER SkipZip
    Only assemble dist\SluPlayer\.
.PARAMETER Installer
    Also build dist\SluPlayer-Setup-<version>.exe (no admin rights needed to install).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\publish.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\publish.ps1 -Version 1.0.1 -Installer
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$OutDir = '',
    [switch]$Folder,
    [switch]$BundleNativeLibraries,
    [switch]$SkipFetch,
    [switch]$SkipZip,
    [switch]$Installer
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Repo = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Repo 'src\SluPlayer\SluPlayer.csproj'
$DevDir = Join-Path $env:LOCALAPPDATA 'SluPlayerDev'
$VendorDir = Join-Path $DevDir 'vendor'
$Stage = Join-Path $DevDir 'publish'
if (-not $OutDir) { $OutDir = Join-Path $Repo 'dist' }
$DistApp = Join-Path $OutDir 'SluPlayer'
$clock = [Diagnostics.Stopwatch]::StartNew()

function Write-Step([string]$Message) { Write-Host ('[{0,5:N1}s] {1}' -f $clock.Elapsed.TotalSeconds, $Message) }

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    return ('{0:N0} KB' -f [math]::Ceiling($Bytes / 1KB))
}

function Get-TreeSize([string]$Path) {
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0L }
    return [long]$sum
}

# Deletes a folder this script owns (staging output), after a sanity check on its location.
function Remove-OwnedTree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith([IO.Path]::GetFullPath($Stage) + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete $full (not inside $Stage)"
    }
    Remove-Item -LiteralPath $full -Recurse -Force
}

function Get-Sha256([string]$Path) {
    $algo = $null
    try { $algo = New-Object Security.Cryptography.SHA256Cng } catch { $algo = [Security.Cryptography.SHA256]::Create() }
    $stream = [IO.File]::OpenRead($Path)
    try { $hash = $algo.ComputeHash($stream) } finally { $stream.Dispose(); $algo.Dispose() }
    return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

# Zip with forward-slash entry names under one top-level folder. (ZipFile.CreateFromDirectory in
# Windows PowerShell writes backslashes, which the zip format does not allow.)
function New-ZipFromFolder([string]$Folder, [string]$ZipPath, [string]$RootName) {
    $base = [IO.Path]::GetFullPath($Folder).TrimEnd([char]92) + [char]92
    $stream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew)
    try {
        $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($f in Get-ChildItem -LiteralPath $Folder -Recurse -File | Sort-Object FullName) {
                $name = $RootName + '/' + $f.FullName.Substring($base.Length).Replace([char]92, [char]47)
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}

function Find-Dotnet {
    $userSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $userSdk) {
        # Per-user SDK: the apphost and MSBuild need DOTNET_ROOT to find it.
        $env:DOTNET_ROOT = Split-Path -Parent $userSdk
        return $userSdk
    }
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw '.NET SDK 10 was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 (no admin rights needed with dotnet-install.ps1).'
}

# ---------------------------------------------------------------------------------------------
# 0. Inputs
# ---------------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Project)) { throw "Project not found: $Project" }
if (-not $Version) {
    [xml]$xml = [IO.File]::ReadAllText($Project)
    $node = $xml.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $node -or -not $node.InnerText.Trim()) { throw 'No <Version> in SluPlayer.csproj; pass -Version.' }
    $Version = $node.InnerText.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw "Invalid version '$Version' (expected e.g. 1.2.3 or 1.2.3-beta.1)" }

$dotnet = Find-Dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$sdk = ([string](& $dotnet --version | Select-Object -Last 1)).Trim()
if ([int]($sdk.Split('.')[0]) -lt 10) { throw ".NET SDK 10 or newer is required (found $sdk at $dotnet)" }
Write-Step "SluPlayer $Version  |  .NET SDK $sdk  |  $(if ($Folder) { 'folder' } else { 'single-file' }) publish"

$running = @(Get-Process -Name SluPlayer -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($DistApp + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
    throw "SluPlayer is running from $DistApp (PID $(($running | ForEach-Object { $_.Id }) -join ', ')). Close it first."
}

# ---------------------------------------------------------------------------------------------
# 1. Vendor cache (pinned, SHA-256 verified)
# ---------------------------------------------------------------------------------------------
if (-not $SkipFetch) {
    Write-Step 'checking the vendor cache (build\fetch-vendor.ps1)'
    & (Join-Path $PSScriptRoot 'fetch-vendor.ps1') -VendorDir $VendorDir
}
$vendorFiles = [ordered]@{
    'libmpv-2.dll'       = Join-Path $VendorDir 'libmpv\libmpv-2.dll'
    'vulkan-1.dll'       = Join-Path $VendorDir 'vulkan\vulkan-1.dll'
    'yt-dlp.exe'         = Join-Path $VendorDir 'yt-dlp-win\yt-dlp.exe'
    'deno.exe'           = Join-Path $VendorDir 'deno\deno.exe'
    'ffmpeg.exe'         = Join-Path $VendorDir 'ffmpeg-shared\ffmpeg.exe'
    'ffprobe.exe'        = Join-Path $VendorDir 'ffmpeg-shared\ffprobe.exe'
}
foreach ($k in $vendorFiles.Keys) {
    if (-not (Test-Path -LiteralPath $vendorFiles[$k])) { throw "Missing vendor file $($vendorFiles[$k]). Run build\fetch-vendor.ps1." }
}
$ffmpegDlls = @(Get-ChildItem -LiteralPath (Join-Path $VendorDir 'ffmpeg-shared') -Filter '*.dll' -File)
if ($ffmpegDlls.Count -eq 0) { throw 'FFmpeg DLLs are missing from vendor\ffmpeg-shared. Run build\fetch-vendor.ps1.' }

# ---------------------------------------------------------------------------------------------
# 2. dotnet publish
# ---------------------------------------------------------------------------------------------
$pubOut = Join-Path $Stage 'app'
$artifacts = Join-Path $Stage 'artifacts'
$noVendor = Join-Path $Stage 'no-vendor'   # empty on purpose: the vendor files are laid out in step 3
Remove-OwnedTree $pubOut
New-Item -ItemType Directory -Force $noVendor | Out-Null
$publishArgs = @(
    'publish', $Project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $pubOut, '--nologo',
    "-p:Version=$Version", '-p:PublishReadyToRun=true', '-p:DebugType=none', '-p:DebugSymbols=false',
    '-p:PublishReferencesDocumentationFiles=false', '-p:GenerateDocumentationFile=false',
    "-p:ArtifactsPath=$artifacts", "-p:SluVendorDir=$noVendor"
)
if ($Folder) {
    $publishArgs += '-p:PublishSingleFile=false'
} else {
    $publishArgs += '-p:PublishSingleFile=true'
    $publishArgs += ('-p:IncludeNativeLibrariesForSelfExtract=' + $(if ($BundleNativeLibraries) { 'true' } else { 'false' }))
}
Write-Step 'dotnet publish (Release, win-x64, self-contained, ReadyToRun)'
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }
$exe = Join-Path $pubOut 'SluPlayer.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "dotnet publish did not produce $exe" }
$fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
Write-Step "published SluPlayer.exe $(Format-Size (Get-Item -LiteralPath $exe).Length) (product version $fileVersion)"

# ---------------------------------------------------------------------------------------------
# 3. Assemble the release folder in staging
# ---------------------------------------------------------------------------------------------
$stageApp = Join-Path $Stage 'dist\SluPlayer'
Remove-OwnedTree $stageApp
New-Item -ItemType Directory -Force $stageApp | Out-Null
Copy-Item -Path (Join-Path $pubOut '*') -Destination $stageApp -Recurse -Force
# Never ship debug symbols, XML docs or a stray portable marker / data folder.
# (Filtered with Where-Object: -Include is unreliable together with -LiteralPath in Windows PowerShell.)
Get-ChildItem -LiteralPath $stageApp -Recurse -File |
    Where-Object { $_.Extension -eq '.pdb' -or $_.Extension -eq '.xml' -or $_.Name -eq 'portable.txt' } |
    Remove-Item -Force
$strayData = Join-Path $stageApp 'data'
if (Test-Path -LiteralPath $strayData) { Remove-Item -LiteralPath $strayData -Recurse -Force }

Copy-Item -LiteralPath $vendorFiles['libmpv-2.dll'] -Destination $stageApp
$vkDir = New-Item -ItemType Directory -Force (Join-Path $stageApp 'vulkan')
Copy-Item -LiteralPath $vendorFiles['vulkan-1.dll'] -Destination $vkDir.FullName
$toolsDir = New-Item -ItemType Directory -Force (Join-Path $stageApp 'tools')
# yt-dlp "onedir": the exe needs its _internal\ folder next to it (ToolLocator looks in tools\yt-dlp\).
Copy-Item -LiteralPath (Split-Path -Parent $vendorFiles['yt-dlp.exe']) -Destination (Join-Path $toolsDir.FullName 'yt-dlp') -Recurse
Get-ChildItem -LiteralPath (Join-Path $toolsDir.FullName 'yt-dlp') -Recurse -File |
    Where-Object { $_.Name -like '*.partial' -or $_.Name -match '\.bak-\d{8}-\d{6}$' } | Remove-Item -Force
foreach ($k in 'deno.exe', 'ffmpeg.exe', 'ffprobe.exe') { Copy-Item -LiteralPath $vendorFiles[$k] -Destination $toolsDir.FullName }
foreach ($dll in $ffmpegDlls) { Copy-Item -LiteralPath $dll.FullName -Destination $toolsDir.FullName }

# Licenses: the curated texts in build\licenses. NuGet packages (and the .NET runtime) are checked
# against the versions those texts were taken from; anything new or different gets its own notice
# files copied from the NuGet cache, with a warning to update build\licenses.
$licDir = Join-Path $stageApp 'licenses'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $licDir -Recurse
# SluPlayer's own license (GPL-3.0).
Copy-Item -LiteralPath (Join-Path $Repo 'LICENSE') -Destination (Join-Path $licDir 'SluPlayer-LICENSE.txt')
function Copy-PackageNotices([string]$From, [string]$To) {
    $files = @(Get-ChildItem -LiteralPath $From -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES|ThirdPartyNotices)' })
    New-Item -ItemType Directory -Force $To | Out-Null
    foreach ($f in $files) { Copy-Item -LiteralPath $f.FullName -Destination $To -Force }
    return $files.Count
}
$curated = @{   # NuGet id -> folder in build\licenses, version the texts were taken from
    'communitytoolkit.mvvm'                 = @('communitytoolkit-mvvm', '8.4.2')
    'microsoft.web.webview2'                = @('webview2', '1.0.4314.35')
    'microsoft.netcore.app.runtime.win-x64' = @('dotnet', '10.0.14')
}
$assetsFile = Join-Path $artifacts 'obj\SluPlayer\project.assets.json'
if (Test-Path -LiteralPath $assetsFile) {
    $assets = [IO.File]::ReadAllText($assetsFile) | ConvertFrom-Json
    $pkgRoot = @($assets.packageFolders.PSObject.Properties | ForEach-Object { $_.Name })[0]
    # Only packages that put files into the app (runtime/native assets for win-x64); build-time
    # packages such as Microsoft.NET.ILLink.Tasks are not shipped.
    $shipped = @{}
    foreach ($t in $assets.targets.PSObject.Properties) {
        if ($t.Name -notlike '*/win-x64') { continue }
        foreach ($lib in $t.Value.PSObject.Properties) {
            foreach ($kind in 'runtime', 'native', 'runtimeTargets') {
                if ($lib.Value.PSObject.Properties[$kind] -and
                    @($lib.Value.$kind.PSObject.Properties | Where-Object { $_.Name -notlike '*/_._' }).Count -gt 0) { $shipped[$lib.Name] = $true }
            }
        }
    }
    $packages = New-Object Collections.Generic.List[object]
    foreach ($lib in $assets.libraries.PSObject.Properties) {
        if ($lib.Value.type -eq 'package' -and $shipped.ContainsKey($lib.Name)) {
            $packages.Add(@(($lib.Name -split '/')[0], ($lib.Name -split '/')[1], $lib.Value.path))
        }
    }
    foreach ($fw in $assets.project.frameworks.PSObject.Properties) {
        if (-not $fw.Value.PSObject.Properties['downloadDependencies']) { continue }
        foreach ($dd in $fw.Value.downloadDependencies) {
            if ($dd.name -notlike 'Microsoft.NETCore.App.Runtime.*') { continue }
            $v = ($dd.version -replace '[\[\]\s]', '').Split(',')[0]
            $packages.Add(@($dd.name, $v, ($dd.name.ToLowerInvariant() + '/' + $v)))
        }
    }
    foreach ($p in $packages) {
        $id = $p[0]; $ver = $p[1]; $src = Join-Path $pkgRoot $p[2]
        $known = $curated[$id.ToLowerInvariant()]
        if ($known -and $known[1] -eq $ver) { continue }
        if ($known) {
            $n = Copy-PackageNotices $src (Join-Path $licDir $known[0])
            Write-Warning "$id is $ver but build\licenses\$($known[0]) was taken from $($known[1]); used the $n notice file(s) of $ver. Please update build\licenses."
        } else {
            $n = Copy-PackageNotices $src (Join-Path $licDir "nuget\$id-$ver")
            Write-Warning "NuGet package $id $ver is not covered by build\licenses; copied $n notice file(s) to licenses\nuget. Please review."
        }
    }
} else {
    Write-Warning "project.assets.json not found ($assetsFile); NuGet notices were not checked."
}

# README.txt = the user part of README.md (everything before the developer marker).
$readmeMd = Join-Path $Repo 'README.md'
if (Test-Path -LiteralPath $readmeMd) {
    $md = [IO.File]::ReadAllText($readmeMd, [Text.Encoding]::UTF8)
    $cut = $md.IndexOf('<!-- developer -->')
    if ($cut -ge 0) { $md = $md.Substring(0, $cut) }
    $md = [regex]::Replace($md, '<!--.*?-->', '', [Text.RegularExpressions.RegexOptions]::Singleline)
    # Light Markdown clean-up for Notepad: no bold markers, "text (url)" links, version in the title.
    $md = $md -replace '\*\*([^*]+)\*\*', '$1'
    $md = [regex]::Replace($md, '\[([^\]]+)\]\((https?://[^)]+)\)', '$1 ($2)')
    $md = [regex]::Replace($md, '^# SluPlayer\s*$', "# SluPlayer $Version", [Text.RegularExpressions.RegexOptions]::Multiline)
    $text = (($md -replace "`r?`n", "`r`n").Trim()) + "`r`n"
    [IO.File]::WriteAllText((Join-Path $stageApp 'README.txt'), $text, (New-Object Text.UTF8Encoding($true)))
} else {
    Write-Warning 'README.md not found; README.txt was not written.'
}

# Sanity checks on the assembled folder.
$mustExist = @('SluPlayer.exe', 'libmpv-2.dll', 'vulkan\vulkan-1.dll', 'tools\yt-dlp\yt-dlp.exe', 'tools\yt-dlp\_internal', 'tools\deno.exe',
    'tools\ffmpeg.exe', 'tools\ffprobe.exe', 'licenses\THIRD-PARTY-NOTICES.txt')
foreach ($m in $mustExist) { if (-not (Test-Path -LiteralPath (Join-Path $stageApp $m))) { throw "Release folder is missing $m" } }
foreach ($k in $vendorFiles.Keys) {
    $copy = Get-ChildItem -LiteralPath $stageApp -Recurse -File -Filter $k | Select-Object -First 1
    if ((Get-Item -LiteralPath $vendorFiles[$k]).Length -ne $copy.Length) { throw "Copy of $k differs in size from the vendor file" }
}

# ---------------------------------------------------------------------------------------------
# 4. Zip + mirror into dist\
# ---------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $OutDir | Out-Null
$zipName = "SluPlayer-$Version-win-x64.zip"
$zipPath = Join-Path $OutDir $zipName
if (-not $SkipZip) {
    Write-Step "zipping $zipName"
    $zipTmp = Join-Path $Stage $zipName
    if (Test-Path -LiteralPath $zipTmp) { Remove-Item -LiteralPath $zipTmp -Force }
    New-ZipFromFolder $stageApp $zipTmp 'SluPlayer'
    Move-Item -LiteralPath $zipTmp -Destination $zipPath -Force
    $zipSha = Get-Sha256 $zipPath
    [IO.File]::WriteAllText("$zipPath.sha256", "$zipSha *$zipName`r`n", (New-Object Text.UTF8Encoding($false)))
}

Write-Step "updating $DistApp"
# /MIR keeps dist\SluPlayer identical to the staged build; a portable test setup (data\, portable.txt)
# that someone created inside it is left alone.
& robocopy.exe $stageApp $DistApp /MIR /XD data /XF portable.txt /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit code $LASTEXITCODE) while updating $DistApp" }

# ---------------------------------------------------------------------------------------------
# 5. Optional installer
# ---------------------------------------------------------------------------------------------
$setupPath = $null
if ($Installer) {
    if ($SkipZip) { throw '-Installer needs the zip; do not combine it with -SkipZip.' }
    $setupScript = Join-Path $PSScriptRoot 'installer\build-installer.ps1'
    if (-not (Test-Path -LiteralPath $setupScript)) { throw "Installer project not found: $setupScript" }
    $setupPath = Join-Path $OutDir "SluPlayer-Setup-$Version.exe"
    & $setupScript -Version $Version -Payload $zipPath -Output $setupPath -Dotnet $dotnet -StageDir (Join-Path $Stage 'installer')
}

# ---------------------------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------------------------
$rows = foreach ($item in Get-ChildItem -LiteralPath $stageApp -Force | Sort-Object { -not $_.PSIsContainer }, Name) {
    $size = if ($item.PSIsContainer) { Get-TreeSize $item.FullName } else { $item.Length }
    [pscustomobject]@{ Item = $(if ($item.PSIsContainer) { $item.Name + '\' } else { $item.Name }); Size = (Format-Size $size) }
}
$rows | Format-Table -AutoSize | Out-String | Write-Host
$fileCount = @(Get-ChildItem -LiteralPath $stageApp -Recurse -File).Count
Write-Host ("  folder  {0}  ({1}, {2} files)" -f $DistApp, (Format-Size (Get-TreeSize $stageApp)), $fileCount)
if (-not $SkipZip) { Write-Host ("  zip     {0}  ({1})`n          sha256 {2}" -f $zipPath, (Format-Size (Get-Item -LiteralPath $zipPath).Length), $zipSha) }
if ($setupPath) { Write-Host ("  setup   {0}  ({1})" -f $setupPath, (Format-Size (Get-Item -LiteralPath $setupPath).Length)) }
Write-Step 'done'
