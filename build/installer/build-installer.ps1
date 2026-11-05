#Requires -Version 5.1
<#
.SYNOPSIS
    Builds SluPlayer-Setup-<version>.exe = the setup program in this folder + the release zip as payload.
    Normally run by build\publish.ps1 -Installer.

.DESCRIPTION
    Layout of the result: [SluPlayer.Setup.exe][SluPlayer-<version>-win-x64.zip][32-byte trailer]
    The trailer is "SLUSETUP" + int64 zip offset + int64 zip length + int64 0 (little endian).
    The setup program reads the zip from its own file; the copy it installs as Uninstall.exe is the same
    program without the payload.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\installer\build-installer.ps1 -Version 1.0.0 `
        -Payload dist\SluPlayer-1.0.0-win-x64.zip -Output dist\SluPlayer-Setup-1.0.0.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Payload,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Dotnet = '',
    [string]$StageDir = (Join-Path $env:LOCALAPPDATA 'SluPlayerDev\publish\installer')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-Sha256Range([string]$Path, [long]$Offset, [long]$Length) {
    $algo = $null
    try { $algo = New-Object Security.Cryptography.SHA256Cng } catch { $algo = [Security.Cryptography.SHA256]::Create() }
    $fs = [IO.File]::OpenRead($Path)
    try {
        [void]$fs.Seek($Offset, 'Begin')
        $buf = New-Object byte[] (1MB)
        $left = $Length
        while ($left -gt 0) {
            $n = $fs.Read($buf, 0, [int][math]::Min($buf.Length, $left))
            if ($n -le 0) { throw "unexpected end of $Path" }
            [void]$algo.TransformBlock($buf, 0, $n, $null, 0)
            $left -= $n
        }
        [void]$algo.TransformFinalBlock($buf, 0, 0)
        return ([BitConverter]::ToString($algo.Hash) -replace '-', '').ToLowerInvariant()
    } finally { $fs.Dispose(); $algo.Dispose() }
}

$Payload = (Resolve-Path -LiteralPath $Payload).Path
$Output = [IO.Path]::GetFullPath($Output)
if (-not $Dotnet) {
    $userSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $userSdk) { $Dotnet = $userSdk; $env:DOTNET_ROOT = Split-Path -Parent $userSdk }
    else { $Dotnet = (Get-Command dotnet.exe).Source }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# 1. Build the setup program (.NET Framework 4.7.2, part of Windows 10/11).
$bin = Join-Path $StageDir 'bin'
if (Test-Path -LiteralPath $bin) { Remove-Item -LiteralPath $bin -Recurse -Force }
& $Dotnet build (Join-Path $PSScriptRoot 'SluPlayer.Setup.csproj') -c Release --nologo -v q `
    "-p:Version=$Version" "-p:ArtifactsPath=$(Join-Path $StageDir 'artifacts')" -o $bin
if ($LASTEXITCODE -ne 0) { throw "building the setup program failed (exit code $LASTEXITCODE)" }
$setup = Join-Path $bin 'SluPlayer.Setup.exe'
if (-not (Test-Path -LiteralPath $setup)) { throw "missing $setup" }

# 2. setup exe + zip + trailer
$partial = "$Output.partial"
$out = [IO.File]::Open($partial, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $in = [IO.File]::OpenRead($setup); try { $in.CopyTo($out, 1MB) } finally { $in.Dispose() }
    $offset = $out.Position
    $in = [IO.File]::OpenRead($Payload); try { $in.CopyTo($out, 1MB) } finally { $in.Dispose() }
    $length = $out.Position - $offset
    $trailer = New-Object byte[] 32
    [Text.Encoding]::ASCII.GetBytes('SLUSETUP').CopyTo($trailer, 0)
    [BitConverter]::GetBytes([long]$offset).CopyTo($trailer, 8)
    [BitConverter]::GetBytes([long]$length).CopyTo($trailer, 16)
    $out.Write($trailer, 0, $trailer.Length)
} finally { $out.Dispose() }

# 3. Verify the payload region is byte-identical to the zip.
$zipSha = Get-Sha256Range $Payload 0 (Get-Item -LiteralPath $Payload).Length
$embeddedSha = Get-Sha256Range $partial $offset $length
if ($zipSha -ne $embeddedSha) { Remove-Item -LiteralPath $partial -Force; throw 'payload verification failed' }

Move-Item -LiteralPath $partial -Destination $Output -Force
$sha = Get-Sha256Range $Output 0 (Get-Item -LiteralPath $Output).Length
[IO.File]::WriteAllText("$Output.sha256", "$sha *$(Split-Path -Leaf $Output)`r`n", (New-Object Text.UTF8Encoding($false)))
Write-Host ("  setup program {0:N0} KB + payload {1:N1} MB -> {2}" -f ((Get-Item -LiteralPath $setup).Length / 1KB), ($length / 1MB), $Output)
