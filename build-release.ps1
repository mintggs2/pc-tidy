<#
.SYNOPSIS
  Builds a PC Tidy release into .\publish\:
    PC Tidy Setup <version>.exe      our own installer: per-machine, %ProgramFiles%\PC Tidy, one admin prompt
                                     (app payload appended, see below)
    PC Tidy <version> portable.zip   the app files + README + THIRD-PARTY-NOTICES + PRIVACY + CHANGELOG (runs from any folder,
                                     but never runs admin steps: those need the Program Files install)
    PC Tidy Rescue <version>.exe     the standalone Rescue download: one self-contained file (it runs on its own, never
                                     with admin rights; the installed Rescue does admin undo)
    SHA256SUMS.txt                   checksums of the three files above
  plus "PC Tidy Setup <version> (Inno Setup).exe" when Inno Setup 6 (ISCC.exe) is installed (also listed in SHA256SUMS.txt).

.DESCRIPTION
  1. Publishes src\PcTidy.App and src\PcTidy.Rescue self-contained, single-file, win-x64, WITHOUT self-extracting
     native files: WPF's native DLLs (wpfgfx_cor3.dll, ...) ship next to the exe in Program Files instead of being
     unpacked into the user's %TEMP% at start-up. PcTidy.exe and Rescue are also PC Tidy's admin helper, and a helper
     that loaded DLLs from a folder any program can write to would run whatever was put there as admin.
     Startup hooks are switched off in both (StartupHookSupport=false), so DOTNET_STARTUP_HOOKS is ignored.
     src\PcTidy.Setup is published twice: uninstall.exe (no self-extraction, installed in Program Files next to the
     same native DLLs, because Settings > Apps runs it and it asks for admin rights) and, AFTER the payload is built,
     the download. WPF can't be trimmed. Startup hooks are off in both Setup builds too.
     The download is one file, so its WPF DLLs are unpacked into %TEMP% for its window. Its admin copy never uses WPF,
     refuses to go on if any module came from %TEMP% or from its own (user) folder, and refuses to be started at all
     while a startup-hook/profiler variable is set for the user (src\PcTidy.Setup\Windows\ElevationGuard.cs).
     Intermediate files go to publish\.work\artifacts, so this never disturbs normal Debug builds.
  2. Zips the app payload (with payload.json = version), publishes the download Setup with the zip's SHA-256 compiled
     in (-p:PcTidyPayloadSha256) and APPENDS the zip to it:
       [Setup exe][payload.zip][SHA-256 of zip: 32 bytes][zip length: int64 LE][ASCII "PCTIDYP1"]
     Setup's admin part copies the payload into an admin-only folder and installs it only if it matches the hash
     compiled into the running program (the trailer's copy alone proves nothing: whoever swaps the file writes it too).
     (Format and reasons: src\PcTidy.Setup\Payload\PayloadFile.cs, PayloadTrust.cs, StagedPayload.cs.)
     Signing later still works: signtool puts the certificate after the payload and Setup looks just before it.
  3. Runs "PC Tidy Setup <version>.exe --verify" (checks the payload; installs nothing, asks for nothing).
  4. Builds the portable zip, publishes the standalone one-file "PC Tidy Rescue <version>.exe" (runs on its own, never
     with admin rights) and writes SHA256SUMS.txt for every download.
  Only writes under this folder. Version comes from src\Directory.Build.props.

.PARAMETER KeepWork
  Keep publish\.work (published files, payload folder) for inspection.
#>
[CmdletBinding()]
param([switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = $PSScriptRoot
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'src\Directory.Build.props')
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
$version = if ($versionNode) { $versionNode.InnerText.Trim() } else { $null }
if (-not $version) { throw 'No <Version> in src\Directory.Build.props.' }
Write-Host "PC Tidy $version release build" -ForegroundColor Cyan

$publish = Join-Path $root 'publish'
$work = Join-Path $publish '.work'
$artifacts = Join-Path $work 'artifacts'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Invoke-Publish([string]$project, [string]$out, [string[]]$extra) {
    Write-Host "Publishing $project ..." -ForegroundColor Cyan
    $arguments = @('publish', (Join-Path $root $project), '-c', 'Release', '-r', 'win-x64', '--self-contained',
        '-p:PublishSingleFile=true', '-p:DebugType=none',
        '--artifacts-path', $artifacts, '-o', $out, '-nologo', '-v', 'q') + $extra
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project (exit $LASTEXITCODE)." }
}

# Program files that run with admin rights: native DLLs beside the exe (never unpacked to %TEMP%), no startup hooks.
$adminSafe = @('-p:IncludeNativeLibrariesForSelfExtract=false', '-p:StartupHookSupport=false')

# Every published file except symbols; the named exe must be among them.
function Get-Published([string]$folder, [string]$exeName) {
    $files = @(Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Extension -ne '.pdb' })
    if (-not ($files | Where-Object { $_.Name -eq $exeName })) { throw "$exeName not found in $folder." }
    $unexpected = @($files | Where-Object { $_.Name -ne $exeName -and $_.Extension -ne '.dll' })
    if ($unexpected.Count -gt 0) { Write-Warning "$folder has unexpected files (shipped anyway): $($unexpected.Name -join ', ')" }
    return $files
}

# Copies files into $dest. A file of the same name must be byte-identical (the WPF native DLLs are shared).
function Copy-Shared([System.IO.FileInfo[]]$files, [string]$dest, [hashtable]$rename = @{}) {
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($f in $files) {
        $name = if ($rename.ContainsKey($f.Name)) { $rename[$f.Name] } else { $f.Name }
        $target = Join-Path $dest $name
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $f.FullName).Hash) {
                throw "$name differs between published projects; they must share one copy in the install folder."
            }
            continue
        }
        Copy-Item -LiteralPath $f.FullName -Destination $target
    }
}

Invoke-Publish 'src\PcTidy.App\PcTidy.App.csproj' (Join-Path $work 'app') $adminSafe
Invoke-Publish 'src\PcTidy.Rescue\PcTidy.Rescue.csproj' (Join-Path $work 'rescue') ($adminSafe + '-p:EnableCompressionInSingleFile=true')
Invoke-Publish 'src\PcTidy.Setup\PcTidy.Setup.csproj' (Join-Path $work 'uninstaller') ($adminSafe + '-p:EnableCompressionInSingleFile=true')

$appFiles = Get-Published (Join-Path $work 'app') 'PcTidy.exe'
$rescueFiles = Get-Published (Join-Path $work 'rescue') 'PC Tidy Rescue.exe'
$uninstallerFiles = Get-Published (Join-Path $work 'uninstaller') 'PC Tidy Setup.exe'
$docs = 'README.md', 'THIRD-PARTY-NOTICES.txt', 'PRIVACY.md', 'CHANGELOG.md' | ForEach-Object { Join-Path $root $_ }
foreach ($d in $docs) { if (-not (Test-Path -LiteralPath $d)) { throw "Missing $d." } }

# ---- payload (what Setup installs in Program Files) ----
$payloadDir = Join-Path $work 'payload'
Copy-Shared $appFiles $payloadDir
Copy-Shared $rescueFiles $payloadDir
Copy-Shared $uninstallerFiles $payloadDir @{ 'PC Tidy Setup.exe' = 'uninstall.exe' }
Copy-Item -LiteralPath $docs -Destination $payloadDir
[System.IO.File]::WriteAllText((Join-Path $payloadDir 'payload.json'), "{ `"version`": `"$version`" }", (New-Object System.Text.UTF8Encoding($false)))
$payloadZip = Join-Path $work 'payload.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($payloadDir, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$zipBytes = [System.IO.File]::ReadAllBytes($payloadZip)
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $zipHash = $sha.ComputeHash($zipBytes) } finally { $sha.Dispose() }
$zipHashHex = -join ($zipHash | ForEach-Object { $_.ToString('x2') })

# ---- the download Setup, with the payload's hash compiled in (its admin part installs nothing else) ----
# One file: its WPF DLLs are unpacked into %TEMP% for the window only (the admin part never loads them, see the top).
Invoke-Publish 'src\PcTidy.Setup\PcTidy.Setup.csproj' (Join-Path $work 'setup') @(
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:StartupHookSupport=false',
    "-p:PcTidyPayloadSha256=$zipHashHex")
$setupFiles = @(Get-ChildItem -LiteralPath (Join-Path $work 'setup') -File | Where-Object { $_.Extension -ne '.pdb' })
if ($setupFiles.Count -ne 1 -or $setupFiles[0].Name -ne 'PC Tidy Setup.exe') { throw "The download Setup must be one file: $($setupFiles.Name -join ', ')" }
$setupStub = $setupFiles[0].FullName

# ---- Setup exe = stub + payload + trailer ----
New-Item -ItemType Directory -Force -Path $publish | Out-Null
$setupOut = Join-Path $publish "PC Tidy Setup $version.exe"
$out = [System.IO.File]::Create($setupOut)
try {
    $stubBytes = [System.IO.File]::ReadAllBytes($setupStub)
    $out.Write($stubBytes, 0, $stubBytes.Length)
    $out.Write($zipBytes, 0, $zipBytes.Length)
    $out.Write($zipHash, 0, 32)
    $out.Write([System.BitConverter]::GetBytes([long]$zipBytes.Length), 0, 8)   # little-endian on x64 Windows
    $magic = [System.Text.Encoding]::ASCII.GetBytes('PCTIDYP1')
    $out.Write($magic, 0, $magic.Length)
} finally { $out.Dispose() }

Write-Host 'Checking the Setup payload (--verify, installs nothing) ...' -ForegroundColor Cyan
$check = Start-Process -FilePath $setupOut -ArgumentList '--verify' -Wait -PassThru -WindowStyle Hidden
if ($check.ExitCode -ne 0) { throw "PC Tidy Setup --verify failed (exit $($check.ExitCode))." }

# ---- portable zip (no uninstaller: nothing to uninstall; admin steps are refused from a portable folder) ----
$portableOut = Join-Path $publish "PC Tidy $version portable.zip"
$portableDir = Join-Path $work "portable\PC Tidy $version"
Copy-Shared $appFiles $portableDir
Copy-Shared $rescueFiles $portableDir
Copy-Item -LiteralPath $docs -Destination $portableDir
if (Test-Path -LiteralPath $portableOut) { Remove-Item -LiteralPath $portableOut -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($portableDir, $portableOut, [System.IO.Compression.CompressionLevel]::Optimal, $true)

# ---- standalone Rescue download: ONE file that runs on its own (WPF's native DLLs inside, unpacked to %TEMP% for the
# window). Like a portable copy it runs from a user folder, so it never does admin steps (PC Tidy refuses to elevate
# from a folder other programs can change); the Rescue installed in Program Files keeps its native DLLs beside it. ----
Invoke-Publish 'src\PcTidy.Rescue\PcTidy.Rescue.csproj' (Join-Path $work 'rescue-standalone') @(
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:StartupHookSupport=false')
$standalone = @(Get-ChildItem -LiteralPath (Join-Path $work 'rescue-standalone') -File | Where-Object { $_.Extension -ne '.pdb' })
if ($standalone.Count -ne 1 -or $standalone[0].Name -ne 'PC Tidy Rescue.exe') { throw "The standalone Rescue must be one file: $($standalone.Name -join ', ')" }
$rescueOut = Join-Path $publish "PC Tidy Rescue $version.exe"
Copy-Item -LiteralPath $standalone[0].FullName -Destination $rescueOut -Force

# ---- optional: Inno Setup build (per-machine too; it has its own uninstaller, so uninstall.exe is left out) ----
$outputs = @($setupOut, $portableOut, $rescueOut)
$iscc = @(
    (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if ($iscc) {
    Write-Host "Building the Inno Setup installer with $iscc ..." -ForegroundColor Cyan
    & $iscc "/DMyAppVersion=$version" "/DSourceDir=$payloadDir" "/O$publish" (Join-Path $root 'installer\PcTidy.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)." }
    $outputs += (Join-Path $publish "PC Tidy Setup $version (Inno Setup).exe")
} else {
    Write-Host 'Inno Setup 6 (ISCC.exe) not found: skipping installer\PcTidy.iss. PC Tidy Setup (our own installer) was built.' -ForegroundColor Yellow
}

# ---- checksums ----
$sums = foreach ($f in $outputs) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $f)
}
[System.IO.File]::WriteAllLines((Join-Path $publish 'SHA256SUMS.txt'), [string[]]$sums, (New-Object System.Text.UTF8Encoding($false)))

if (-not $KeepWork) { Remove-Item -LiteralPath $work -Recurse -Force }

Write-Host ''
Write-Host "Release $version in $publish" -ForegroundColor Green
foreach ($f in $outputs + (Join-Path $publish 'SHA256SUMS.txt')) {
    $i = Get-Item -LiteralPath $f
    Write-Host ('  {0,-45} {1,10:N1} MB' -f $i.Name, ($i.Length / 1MB))
}
