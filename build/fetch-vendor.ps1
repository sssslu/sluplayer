#Requires -Version 5.1
<#
.SYNOPSIS
    Fetches SluPlayer's native engine and bundled tools (pinned versions, SHA-256 verified)
    into the developer vendor cache.

.DESCRIPTION
    Idempotent. For every component the script:
      1. checks the files in <VendorDir>\<dir>\ against the pinned SHA-256 hashes
         (nothing is downloaded when they already match),
      2. otherwise reuses the archive in <VendorDir>\<dir>-dl\ if its SHA-256 matches the pin,
      3. otherwise downloads it (curl.exe with retries), verifies the SHA-256 and extracts
         only the files SluPlayer needs (each extracted file is verified as well).
    Folder components (the yt-dlp "onedir" build: an exe plus ~140 files in _internal\) are
    checked with one pinned "tree hash" (SHA-256 over the sorted "path<TAB>sha256" lines of
    all files) and extracted as a whole.
    Anything that exists but does not match its pin is never replaced silently: the script stops,
    unless -Force is given, in which case the old file/folder is renamed to <name>.bak-<timestamp>.
    Nothing in the vendor folder is ever deleted.

    Layout produced (consumed by src\SluPlayer\SluPlayer.csproj and build\publish.ps1):
      libmpv\libmpv-2.dll (+ include\mpv\*.h)     mpv engine (FFmpeg, libplacebo... statically linked)
      yt-dlp-win\yt-dlp.exe + _internal\          online video downloader, "onedir" build (starts
                                                  ~3x faster than the single-file exe; what ships)
      yt-dlp\yt-dlp.exe                           the same version as one file (dev fallback)
      deno\deno.exe                               JavaScript runtime yt-dlp needs for YouTube
      ffmpeg-shared\ffmpeg.exe, ffprobe.exe,      merging/conversion for yt-dlp (shared build:
                    av*.dll, sw*.dll, LICENSE.txt   ~185 MB instead of 2 x 170 MB static exes)
      vulkan\vulkan-1.dll, VulkanRT-License.txt   official Khronos/LunarG loader, used only as a
                                                  fallback when Windows has none (see README)

    Upstream retention (when a pinned URL starts returning 404, bump the pin; -Latest helps):
      - shinchiro/mpv-winbuild-cmake keeps roughly the last 30 GitHub releases (about one month).
      - yt-dlp/FFmpeg-Builds keeps daily builds for ~2 weeks and the last build of every month for
        ~2 years, so a month-end build is pinned.
      - yt-dlp, Deno and LunarG releases are permanent.

.PARAMETER VendorDir
    Vendor cache. Default: %LOCALAPPDATA%\SluPlayerDev\vendor (what Directory.Build.props uses).
.PARAMETER Only
    Restrict to some components: libmpv, yt-dlp, yt-dlp-exe, deno, ffmpeg, vulkan.
.PARAMETER Force
    Replace files that do not match their pin (the old file is kept as *.bak-<timestamp>).
.PARAMETER Verify
    Also run every tool once (--version) from a neutral working directory.
.PARAMETER Latest
    Only print the newest upstream releases with their SHA-256 digests (to bump the pins).
    Downloads nothing.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\fetch-vendor.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\fetch-vendor.ps1 -Only ffmpeg,vulkan -Verify
#>
[CmdletBinding()]
param(
    [string]$VendorDir = (Join-Path $env:LOCALAPPDATA 'SluPlayerDev\vendor'),
    [string[]]$Only = @(),
    [switch]$Force,
    [switch]$Verify,
    [switch]$Latest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------------------------------------------------------------------------------------------
# Pins. Url/Sha256 = the downloaded file; Files = what is taken out of it: @(member, target, sha256).
# For a plain file download (no archive) the member is '' and the download itself is the target.
# Tree = the whole archive is the component; its extracted folder must have this tree hash.
# ---------------------------------------------------------------------------------------------
$ff = 'ffmpeg-N-127043-g5a54fcf75e-win64-gpl-shared'
$vk = 'VulkanRT-X64-1.4.363.0-Components'
$Pins = @(
    @{
        Name    = 'libmpv'
        Dir     = 'libmpv'
        Version = 'mpv v0.41.0-1227-ge471496c5 (shinchiro build 20261021, x86_64 baseline)'
        License = 'GPL-2.0-or-later (mpv) / GPL-3.0 (bundled FFmpeg)'
        Url     = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261021/mpv-dev-x86_64-20261021-git-e471496c56.7z'
        Sha256  = 'efc719eff3dc5954b4a5efc800632808bcec01cc9034940dd14e5eb7fe9ea633'
        Files   = @(
            , @('libmpv-2.dll', 'libmpv-2.dll', '04e4d27dc385dc18ddd93fbe69c75e635b864f3c6ec53a9122df54c2f7842f4a')
            , @('include/mpv/client.h', 'include\mpv\client.h', '3da9b8c61c3fb4e2d76bd248cebf72f3ee830331411b71a76adbd5e05660cc50')
            , @('include/mpv/render.h', 'include\mpv\render.h', 'f2550aa29b2e9b20988bd954b4383e9fa05fd9e40d1669fbc6edf2afac603f15')
            , @('include/mpv/render_gl.h', 'include\mpv\render_gl.h', '48662c0ed9872a14dd9e1684105c97f69f94a0414709c254c5d372adc41d2e69')
            , @('include/mpv/stream_cb.h', 'include\mpv\stream_cb.h', '188e58b6d14383e15a5dffdc4ecebbdfbf2e412b9c099ff21b571f747a8ce32d')
        )
    }
    @{
        Name    = 'yt-dlp'
        Dir     = 'yt-dlp-win'
        Version = 'yt-dlp 2026.10.26 (onedir build yt-dlp_win.zip, Python 3.14)'
        License = 'Unlicense (+ bundled Python/third-party licenses in _internal\THIRD_PARTY_LICENSES.txt)'
        Url     = 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp_win.zip'
        Sha256  = 'fd51b212cf8380c7a52ee08c0d6be7f3c59b2f2c832eaf7b45acfae6eab57419'
        Tree    = '4ae72500f5ab236b5e9b50b71748be5494a617433ca6561119b1d8627ac771ed'   # 143 files
        Files   = @()
    }
    @{
        Name    = 'yt-dlp-exe'
        Dir     = 'yt-dlp'
        Version = 'yt-dlp 2026.10.26 (single-file win_exe, Python 3.14)'
        License = 'Unlicense (+ bundled Python/third-party licenses)'
        Url     = 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe'
        Sha256  = 'f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d'
        Files   = @(
            , @('', 'yt-dlp.exe', 'f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d')
        )
    }
    @{
        Name    = 'deno'
        Dir     = 'deno'
        Version = 'Deno 2.9.8 (x86_64-pc-windows-msvc)'
        License = 'MIT'
        Url     = 'https://github.com/denoland/deno/releases/download/v2.9.8/deno-x86_64-pc-windows-msvc.zip'
        Sha256  = 'cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf'
        Files   = @(
            , @('deno.exe', 'deno.exe', '924397800533d3eb0ed0c889b20ad70e18b970d93acd8135bc043f425e528266')
        )
    }
    @{
        Name    = 'ffmpeg'
        Dir     = 'ffmpeg-shared'
        Version = 'FFmpeg N-127043-g5a54fcf75e (yt-dlp/FFmpeg-Builds autobuild-2026-09-30-18-57, win64 gpl shared)'
        License = 'GPL-3.0-or-later'
        Url     = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/autobuild-2026-09-30-18-57/$ff.zip"
        Sha256  = '09948d268b5e78adce8559821cc5cceb5fe13d7c2feb1973f76737edb05db94a'
        Files   = @(
            , @("$ff/bin/ffmpeg.exe", 'ffmpeg.exe', 'fcaa908ca592a5671d6f718471f7f7b73fade6b91ac6decb9f59324552107b85')
            , @("$ff/bin/ffprobe.exe", 'ffprobe.exe', 'd9178579d72e361ad3d00c79fbb774179067fa8f057d3f3f404fae5a8e89cffe')
            , @("$ff/bin/avcodec-63.dll", 'avcodec-63.dll', 'e24230ca2f933e57844d68d3f8ca99fcdb3ee3dcb6ff7f7963950146afdaaca2')
            , @("$ff/bin/avdevice-63.dll", 'avdevice-63.dll', '0b4a5fcac87ca87bd015192069924d6b9b16fa6975b76726c4de513b1402fa48')
            , @("$ff/bin/avfilter-12.dll", 'avfilter-12.dll', '45e606e372d73c3440cf94f1163806f58d5ca61d0d51a299943eafdcc09d39b9')
            , @("$ff/bin/avformat-63.dll", 'avformat-63.dll', 'c46ccb8d919879cb504d1ae1c59bd93f6b1d401f49c229d8808ba4d7f91dfaf0')
            , @("$ff/bin/avutil-61.dll", 'avutil-61.dll', '605ad3a60d0bba7853c2f6d9196181ff1936c33cdb0cea2ef13629c4b016fa04')
            , @("$ff/bin/swresample-7.dll", 'swresample-7.dll', '6598bed7507786ccb59bf279c5f5d9af22e1a80140fefd2d81d8c5634d57313f')
            , @("$ff/bin/swscale-10.dll", 'swscale-10.dll', '22b939d83102bc0c2b4fc59bd76db252754aa7997cf935728edf4d077b4c471d')
            , @("$ff/LICENSE.txt", 'LICENSE.txt', '8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903')
        )
    }
    @{
        Name    = 'vulkan'
        Dir     = 'vulkan'
        Version = 'Khronos Vulkan loader 1.4.363.0 (LunarG Vulkan Runtime components, signed by LunarG)'
        License = 'Apache-2.0 / MIT'
        Url     = "https://sdk.lunarg.com/sdk/download/1.4.363.0/windows/$vk.zip"
        Sha256  = 'a25a927aa8b9f0371048f1861cf88ac3b9bc9b1fb332c42d897c8ab32695769a'
        Files   = @(
            , @("$vk/x64/vulkan-1.dll", 'vulkan-1.dll', 'e1fcfc9489beefa6a6d9d11d6c7517f6a2b7f16e0d3fb8f4103bee0210f314a3')
            , @("$vk/VulkanRT-License.txt", 'VulkanRT-License.txt', '404deb98dabf3e54bf9070d59676b31d69a3da2f9f6e56bdf09ad315c04c718c')
        )
    }
)

$Curl = Join-Path $env:SystemRoot 'System32\curl.exe'
$Tar = Join-Path $env:SystemRoot 'System32\tar.exe'
$UserAgent = 'SluPlayer-build (+fetch-vendor.ps1)'

function Write-Step([string]$Message) { Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message) }

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    return ('{0:N0} KB' -f [math]::Ceiling($Bytes / 1KB))
}

function Get-Sha256([string]$Path) {
    # SHA256Cng is several times faster than the default managed implementation in Windows PowerShell.
    $algo = $null
    try { $algo = New-Object Security.Cryptography.SHA256Cng } catch { $algo = [Security.Cryptography.SHA256]::Create() }
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 1MB)
    try { $hash = $algo.ComputeHash($stream) } finally { $stream.Dispose(); $algo.Dispose() }
    return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

# Runs a native program without letting Windows PowerShell turn its stderr into terminating errors.
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @Arguments | Out-Host
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $saved }
}

function Save-Download([string]$Url, [string]$OutFile, [string]$Sha256) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $OutFile) | Out-Null
    $partial = "$OutFile.partial"
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    Write-Step "  downloading $Url"
    if (Test-Path -LiteralPath $Curl) {
        $code = Invoke-Native $Curl @('--fail', '--location', '--silent', '--show-error', '--retry', '4', '--retry-delay', '3',
            '--connect-timeout', '30', '--user-agent', $UserAgent, '--output', $partial, $Url)
        if ($code -ne 0) { throw "Download failed (curl exit code $code): $Url" }
    } else {
        Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing -UserAgent $UserAgent
    }
    $actual = Get-Sha256 $partial
    if ($actual -ne $Sha256) {
        Remove-Item -LiteralPath $partial -Force
        throw "SHA-256 mismatch for $Url`n  expected $Sha256`n  actual   $actual`nThe upstream file changed or the download is corrupt. Nothing was installed."
    }
    Move-Item -LiteralPath $partial -Destination $OutFile -Force
    Write-Step ("  verified {0} ({1})" -f (Split-Path -Leaf $OutFile), (Format-Size (Get-Item -LiteralPath $OutFile).Length))
}

# Puts a verified file into place; the source is a temporary file of ours.
function Install-VerifiedFile([string]$Source, [string]$Target, [string]$Sha256, [string]$Label) {
    $actual = Get-Sha256 $Source
    if ($actual -ne $Sha256) {
        Remove-Item -LiteralPath $Source -Force
        throw "SHA-256 mismatch for $Label`n  expected $Sha256`n  actual   $actual"
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $Target) | Out-Null
    Move-Item -LiteralPath $Source -Destination $Target -Force
}

function Expand-FromZip([string]$Archive, [object[]]$Members, [string]$DestDir) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $byName = @{}
        foreach ($e in $zip.Entries) { $byName[($e.FullName -replace '\\', '/')] = $e }
        foreach ($m in $Members) {
            $entry = $byName[$m[0]]
            if ($null -eq $entry) { throw "'$($m[0])' is not in $Archive" }
            $target = Join-Path $DestDir $m[1]
            $tmp = "$target.partial"
            New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tmp, $true)
            Install-VerifiedFile $tmp $target $m[2] $m[0]
        }
    } finally { $zip.Dispose() }
}

function Expand-From7z([string]$Archive, [object[]]$Members, [string]$DestDir, [string]$ScratchParent) {
    if (-not (Test-Path -LiteralPath $Tar)) { throw "$Tar not found (needed to read .7z; Windows 10 1803 or newer ships it)" }
    $scratch = Join-Path $ScratchParent ('extract-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    try {
        $names = @($Members | ForEach-Object { $_[0] })
        # bsdtar refuses absolute paths and '..' by default, and only extracts the listed members.
        $code = Invoke-Native $Tar (@('-x', '-f', $Archive, '-C', $scratch) + $names)
        if ($code -ne 0) { throw "tar.exe could not extract from $Archive (exit code $code)" }
        foreach ($m in $Members) {
            $src = Join-Path $scratch ($m[0] -replace '/', '\')
            if (-not (Test-Path -LiteralPath $src)) { throw "'$($m[0])' is not in $Archive" }
            Install-VerifiedFile $src (Join-Path $DestDir $m[1]) $m[2] $m[0]
        }
    } finally {
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Tree hash of a folder: SHA-256 over "relative/path<TAB>sha256<LF>" lines, sorted case-insensitively.
# Our own leftovers (*.partial, *.bak-<timestamp>) are ignored.
function Get-TreeHash([string]$Dir) {
    $base = [IO.Path]::GetFullPath($Dir).TrimEnd([char]92) + [char]92
    $lines = New-Object Collections.Generic.List[string]
    foreach ($f in Get-ChildItem -LiteralPath $Dir -Recurse -File -Force) {
        if ($f.Name -like '*.partial' -or $f.Name -match '\.bak-\d{8}-\d{6}$') { continue }
        $rel = $f.FullName.Substring($base.Length).Replace([char]92, [char]47)
        $lines.Add($rel + "`t" + (Get-Sha256 $f.FullName))
    }
    $lines.Sort([StringComparer]::OrdinalIgnoreCase)
    $bytes = [Text.Encoding]::UTF8.GetBytes((($lines.ToArray()) -join "`n") + "`n")
    $algo = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($algo.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant() } finally { $algo.Dispose() }
    [pscustomobject]@{ Hash = $hash; Count = $lines.Count }
}

# Extracts every file of a zip below $DestDir (entries with rooted paths or '..' are refused).
function Expand-ZipAll([string]$Archive, [string]$DestDir) {
    $base = [IO.Path]::GetFullPath($DestDir).TrimEnd([char]92) + [char]92
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($e in $zip.Entries) {
            if ([string]::IsNullOrEmpty($e.Name)) { continue }
            $rel = $e.FullName.Replace([char]47, [char]92)
            $target = [IO.Path]::GetFullPath((Join-Path $DestDir $rel))
            if ([IO.Path]::IsPathRooted($rel) -or -not $target.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing unsafe path '$($e.FullName)' in $Archive"
            }
            New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $target, $false)
        }
    } finally { $zip.Dispose() }
}

function Get-ArchivePath([hashtable]$Pin) {
    $fileName = [IO.Path]::GetFileName(([Uri]$Pin.Url).AbsolutePath)
    return (Join-Path (Join-Path $VendorDir ($Pin.Dir + '-dl')) $fileName)
}

# Makes sure the pinned archive is in <dir>-dl (reused when its SHA-256 matches). Returns 'downloaded' or 'extracted'.
function Get-PinnedArchive([hashtable]$Pin) {
    $archive = Get-ArchivePath $Pin
    if ((Test-Path -LiteralPath $archive) -and ((Get-Sha256 $archive) -eq $Pin.Sha256)) {
        Write-Step "  using the cached archive $archive"
        return 'extracted'
    }
    Save-Download $Pin.Url $archive $Pin.Sha256
    return 'downloaded'
}

function Sync-Tree([hashtable]$Pin) {
    $dir = Join-Path $VendorDir $Pin.Dir
    if (Test-Path -LiteralPath $dir) {
        $now = Get-TreeHash $dir
        if ($now.Hash -eq $Pin.Tree) {
            Write-Step "  up to date ($($now.Count) files match the pinned tree hash)"
            return 'cached'
        }
        if ($now.Count -gt 0) {
            if (-not $Force) {
                throw "$dir differs from the pinned $($Pin.Name) ($($now.Count) files, tree hash $($now.Hash)).`nRe-run with -Force to replace it (the old folder is kept as $($Pin.Dir).bak-<timestamp>)."
            }
            $bak = $dir + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
            Move-Item -LiteralPath $dir -Destination $bak
            Write-Step "  kept the old folder as $(Split-Path -Leaf $bak)"
        }
    }
    $result = Get-PinnedArchive $Pin
    $archive = Get-ArchivePath $Pin
    $scratch = Join-Path (Split-Path -Parent $archive) ('extract-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        Expand-ZipAll $archive $scratch
        $got = Get-TreeHash $scratch
        if ($got.Hash -ne $Pin.Tree) { throw "Tree hash mismatch after extracting $archive`n  expected $($Pin.Tree)`n  actual   $($got.Hash)" }
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Force }   # empty folder only (checked above)
        Move-Item -LiteralPath $scratch -Destination $dir
        Write-Step "  installed $($got.Count) files into $dir"
    } finally {
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
    }
    return $result
}

function Get-FileState([hashtable]$Pin, [string]$Dir) {
    foreach ($f in $Pin.Files) {
        $path = Join-Path $Dir $f[1]
        $state = 'missing'
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $state = if ((Get-Sha256 $path) -eq $f[2]) { 'ok' } else { 'mismatch' }
        }
        [pscustomobject]@{ Member = $f[0]; Target = $f[1]; Sha256 = $f[2]; Path = $path; State = $state }
    }
}

function Sync-Component([hashtable]$Pin) {
    $dir = Join-Path $VendorDir $Pin.Dir
    Write-Step "$($Pin.Name): $($Pin.Version)"
    if ($Pin.ContainsKey('Tree')) { return Sync-Tree $Pin }
    $states = @(Get-FileState $Pin $dir)
    $todo = @($states | Where-Object { $_.State -ne 'ok' })
    if ($todo.Count -eq 0) {
        Write-Step "  up to date ($($states.Count) file(s) match the pinned SHA-256)"
        return 'cached'
    }
    $bad = @($todo | Where-Object { $_.State -eq 'mismatch' })
    if ($bad.Count -gt 0) {
        $list = ($bad | ForEach-Object { '    ' + $_.Path }) -join "`n"
        if (-not $Force) {
            throw "These files differ from the pinned version:`n$list`nRe-run with -Force to replace them (the old files are kept as *.bak-<timestamp>)."
        }
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        foreach ($b in $bad) {
            Move-Item -LiteralPath $b.Path -Destination "$($b.Path).bak-$stamp"
            Write-Step "  kept the old file as $(Split-Path -Leaf $b.Path).bak-$stamp"
        }
    }

    $fileName = [IO.Path]::GetFileName(([Uri]$Pin.Url).AbsolutePath)
    $isArchive = $fileName -match '\.(zip|7z)$'
    if (-not $isArchive) {
        # Plain file download straight into place.
        Save-Download $Pin.Url (Join-Path $dir $Pin.Files[0][1]) $Pin.Sha256
        return 'downloaded'
    }

    $result = Get-PinnedArchive $Pin
    $archive = Get-ArchivePath $Pin
    $members = @($todo | ForEach-Object { , @($_.Member, $_.Target, $_.Sha256) })
    if ($fileName.EndsWith('.7z')) { Expand-From7z $archive $members $dir (Split-Path -Parent $archive) }
    else { Expand-FromZip $archive $members $dir }
    Write-Step "  installed $($members.Count) file(s) into $dir"
    return $result
}

function Invoke-Capture([string]$Exe, [string[]]$Arguments, [string]$WorkDir) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $WorkDir
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(120000)) { try { $p.Kill() } catch { }; throw "Timed out: $Exe" }
    $p.WaitForExit()
    [pscustomobject]@{ Code = $p.ExitCode; Text = ($out.Result + "`n" + $err.Result).Trim() }
}

function Test-Tools {
    $neutral = Join-Path ([IO.Path]::GetTempPath()) ('sluplayer-vendor-check-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $neutral | Out-Null
    try {
        $checks = @(
            @('yt-dlp', (Join-Path $VendorDir 'yt-dlp-win\yt-dlp.exe'), @('--version')),
            @('yt-dlp-exe', (Join-Path $VendorDir 'yt-dlp\yt-dlp.exe'), @('--version')),
            @('deno', (Join-Path $VendorDir 'deno\deno.exe'), @('--version')),
            @('ffmpeg', (Join-Path $VendorDir 'ffmpeg-shared\ffmpeg.exe'), @('-hide_banner', '-version')),
            @('ffmpeg', (Join-Path $VendorDir 'ffmpeg-shared\ffprobe.exe'), @('-hide_banner', '-version'))
        )
        foreach ($c in $checks) {
            if ($Only.Count -gt 0 -and $Only -notcontains $c[0]) { continue }
            if (-not (Test-Path -LiteralPath $c[1])) { continue }
            $r = Invoke-Capture $c[1] $c[2] $neutral
            $first = ($r.Text -split "`r?`n")[0]
            if ($r.Code -ne 0) { throw "$(Split-Path -Leaf $c[1]) failed to run (exit code $($r.Code)): $first" }
            $label = (Split-Path -Leaf (Split-Path -Parent $c[1])) + '\' + (Split-Path -Leaf $c[1])
            Write-Step ("verify {0,-26} {1}" -f $label, $first)
        }
    } finally {
        Remove-Item -LiteralPath $neutral -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Show-Latest {
    $headers = @{ 'User-Agent' = $UserAgent; 'Accept' = 'application/vnd.github+json' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
    function Get-Json([string]$Url) { Invoke-RestMethod -Uri $Url -Headers $headers -UseBasicParsing }
    function Get-Digest($Asset) { if ($Asset.PSObject.Properties['digest'] -and $Asset.digest) { ($Asset.digest -replace '^sha256:', '') } else { '(no digest; download to hash)' } }

    $rows = New-Object Collections.Generic.List[object]
    $r = Get-Json 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest'
    $a = @($r.assets | Where-Object { $_.name -match '^mpv-dev-x86_64-\d{8}-git-[0-9a-f]+\.7z$' })[0]
    $rows.Add([pscustomobject]@{ Component = 'libmpv'; Release = $r.tag_name; Url = $a.browser_download_url; Sha256 = (Get-Digest $a) })

    $r = Get-Json 'https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest'
    $a = @($r.assets | Where-Object { $_.name -eq 'yt-dlp_win.zip' })[0]
    $rows.Add([pscustomobject]@{ Component = 'yt-dlp'; Release = $r.tag_name; Url = $a.browser_download_url; Sha256 = (Get-Digest $a) + '  (tree hash: install it with this script, then read it from the error message)' })
    $a = @($r.assets | Where-Object { $_.name -eq 'yt-dlp.exe' })[0]
    $rows.Add([pscustomobject]@{ Component = 'yt-dlp-exe'; Release = $r.tag_name; Url = $a.browser_download_url; Sha256 = (Get-Digest $a) })

    $r = Get-Json 'https://api.github.com/repos/denoland/deno/releases/latest'
    $a = @($r.assets | Where-Object { $_.name -eq 'deno-x86_64-pc-windows-msvc.zip' })[0]
    $rows.Add([pscustomobject]@{ Component = 'deno'; Release = $r.tag_name; Url = $a.browser_download_url; Sha256 = (Get-Digest $a) })

    # Month-end FFmpeg builds are kept for about two years (dailies only ~2 weeks): show the newest
    # daily and the newest completed month's last build. Tags look like autobuild-2026-09-30-18-57.
    $all = @(Get-Json 'https://api.github.com/repos/yt-dlp/FFmpeg-Builds/releases?per_page=60' |
        ForEach-Object { $_ } | Where-Object { $_.tag_name -like 'autobuild-*' } | Sort-Object tag_name -Descending)
    $monthEnd = $null
    for ($i = 1; $i -lt $all.Count; $i++) {
        if ($all[$i].tag_name.Substring(10, 7) -ne $all[$i - 1].tag_name.Substring(10, 7)) { $monthEnd = $all[$i]; break }
    }
    foreach ($rel in @($all[0], $monthEnd)) {
        if ($null -eq $rel) { continue }
        $a = @($rel.assets | Where-Object { $_.name -like '*-win64-gpl-shared.zip' })[0]
        $rows.Add([pscustomobject]@{ Component = 'ffmpeg'; Release = $rel.tag_name; Url = $a.browser_download_url; Sha256 = (Get-Digest $a) })
    }

    $v = (Invoke-WebRequest -Uri 'https://vulkan.lunarg.com/sdk/latest/windows.txt' -UseBasicParsing -UserAgent $UserAgent).Content.Trim()
    $rows.Add([pscustomobject]@{ Component = 'vulkan'; Release = $v; Url = "https://sdk.lunarg.com/sdk/download/$v/windows/VulkanRT-X64-$v-Components.zip"; Sha256 = '(no digest; download to hash)' })

    $rows | Format-List
    Write-Host 'Pins in this script:'
    $Pins | ForEach-Object { Write-Host ("  {0,-10} {1}" -f $_.Name, $_.Url) }
}

# ---------------------------------------------------------------------------------------------
# "-Only a,b" arrives as one string under "powershell -File": accept both forms.
$Only = @($Only | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
$names = @($Pins | ForEach-Object { $_.Name })
foreach ($o in $Only) {
    if ($names -notcontains $o) { throw "Unknown component '$o' (use: $($names -join ', '))" }
}
if ($Latest) { Show-Latest; return }

New-Item -ItemType Directory -Force $VendorDir | Out-Null
$selected = @($Pins | Where-Object { $Only.Count -eq 0 -or $Only -contains $_.Name })
$summary = New-Object Collections.Generic.List[object]
foreach ($pin in $selected) {
    $result = Sync-Component $pin
    $dir = Join-Path $VendorDir $pin.Dir
    if ($pin.ContainsKey('Tree')) {
        $all = @(Get-ChildItem -LiteralPath $dir -Recurse -File)
        $count = $all.Count
        $bytes = [long](($all | Measure-Object -Property Length -Sum).Sum)
    } else {
        $count = $pin.Files.Count
        $bytes = 0L
        foreach ($f in $pin.Files) { $bytes += (Get-Item -LiteralPath (Join-Path $dir $f[1])).Length }
    }
    $summary.Add([pscustomobject]@{ Component = $pin.Name; Result = $result; Files = $count; Size = (Format-Size $bytes); Folder = $dir })
}
if ($Verify) { Test-Tools }
$summary | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Step "vendor cache ready: $VendorDir"
