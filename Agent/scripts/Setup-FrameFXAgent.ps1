<#
.SYNOPSIS
  One-time setup for WyneAI: downloads the llama.cpp Vulkan server and the 3B Q4_K_M model (~1.93 GB).
.PARAMETER InstallDir
  Default on MEHPC: W:\FrameFX\agent (MEHPC keeps everything on W:). Elsewhere: %LOCALAPPDATA%\FrameFX\agent.
  Pass -InstallDir to override, e.g. -InstallDir W:\FrameFX\agent to keep it off C:.
  A non-default dir is saved in the user env var FRAMEFX_AGENT_DIR so the app finds it.
.PARAMETER LlamaTag
  llama.cpp release tag (e.g. b6700). Default: latest. Pin it for releases so behaviour is reproducible.
#>
[CmdletBinding()]
param(
  [string]$InstallDir = '',
  [string]$LlamaTag = 'latest',
  [switch]$Force
)
$ErrorActionPreference = 'Stop'
if (-not $InstallDir) {
  if ($env:COMPUTERNAME -eq 'MEHPC') {
    if (-not (Test-Path 'W:\')) { throw 'MEHPC installs go on W:, but W: is not mounted. Mount W: or pass -InstallDir.' }
    $InstallDir = 'W:\FrameFX\agent'
  } else { $InstallDir = Join-Path $env:LOCALAPPDATA 'FrameFX\agent' }
}
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$models = @(
  @{ Url = 'https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf'
     Sha256 = '9c9f56a391a3abbd5b89d0245bf6106081bcc3173119d4229235dd9d23253f94'; Size = 1929903264 },
  @{ Url = 'https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf'
     Sha256 = '626b4a6678b86442240e33df819e00132d3ba7dddfe1cdc4fbb18e0a9615c62d'; Size = 2104932768 }
)

$llamaDir = Join-Path $InstallDir 'llama'
$modelDir = Join-Path $InstallDir 'models'
$modelFile = Join-Path $modelDir 'framefx-assistant.gguf'
New-Item -ItemType Directory -Force -Path $llamaDir, $modelDir, (Join-Path $InstallDir 'LICENSES') | Out-Null

# Free space check (model + llama.cpp + headroom)
$drive = (Get-Item $InstallDir).PSDrive
$freeGB = [math]::Round($drive.Free / 1GB, 1)
if ($drive.Free -lt 3GB) { throw "Need about 3 GB free on $($drive.Name):, only $freeGB GB free." }
Write-Host "Installing WyneAI to $InstallDir ($freeGB GB free)"

function Get-File($url, $dest) {
  $tmp = "$dest.part"
  try { Start-BitsTransfer -Source $url -Destination $tmp -DisplayName 'WyneAI' -ErrorAction Stop }
  catch { Write-Host "  BITS unavailable, using Invoke-WebRequest"; Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing }
  Move-Item -Force $tmp $dest
}

# 1) llama.cpp (Vulkan build works on AMD and NVIDIA)
$serverExe = Join-Path $llamaDir 'llama-server.exe'
if ($Force -or -not (Test-Path $serverExe)) {
  $api = if ($LlamaTag -eq 'latest') { 'https://api.github.com/repos/ggml-org/llama.cpp/releases/latest' }
         else { "https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/$LlamaTag" }
  $rel = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'FrameFX-Setup' }
  $asset = $rel.assets | Where-Object { $_.name -match 'bin-win-vulkan-x64\.zip$' } | Select-Object -First 1
  if (-not $asset) { throw "No Vulkan x64 Windows build in llama.cpp release $($rel.tag_name)." }
  Write-Host "Downloading llama.cpp $($rel.tag_name) ($([math]::Round($asset.size/1MB)) MB)…"
  $zip = Join-Path $InstallDir 'llama.zip'
  Get-File $asset.browser_download_url $zip
  Expand-Archive -Force $zip $llamaDir
  Remove-Item $zip
  # Some releases nest files in a folder: flatten so llama-server.exe sits in $llamaDir.
  $found = Get-ChildItem $llamaDir -Recurse -Filter llama-server.exe | Select-Object -First 1
  if (-not $found) { throw 'llama-server.exe not found in the download.' }
  if ($found.DirectoryName -ne $llamaDir) { Get-ChildItem $found.DirectoryName | Move-Item -Destination $llamaDir -Force }
  Set-Content (Join-Path $llamaDir 'VERSION.txt') $rel.tag_name
}

# 2) Model (resumable via BITS, verified by SHA-256)
if ($Force -or -not (Test-Path $modelFile)) {
  $ok = $false
  foreach ($m in $models) {
    try {
      Write-Host "Downloading the assistant model ($([math]::Round($m.Size/1GB,2)) GB)…"
      Get-File $m.Url $modelFile
      $h = (Get-FileHash $modelFile -Algorithm SHA256).Hash.ToLower()
      if ($h -ne $m.Sha256) { Remove-Item $modelFile; throw "checksum mismatch ($h)" }
      $ok = $true; break
    } catch { Write-Warning "  source failed: $_" }
  }
  if (-not $ok) { throw 'Could not download the model.' }
}

# 3) Licenses (Apache-2.0 for the model weights, MIT for llama.cpp). Kept on disk, not shown in the UI.
$src = Join-Path $PSScriptRoot '..\LICENSES'
if (Test-Path $src) { Copy-Item -Force (Join-Path $src '*') (Join-Path $InstallDir 'LICENSES') }

if ($InstallDir -ne (Join-Path $env:LOCALAPPDATA 'FrameFX\agent')) {
  [Environment]::SetEnvironmentVariable('FRAMEFX_AGENT_DIR', $InstallDir, 'User')
  Write-Host "Saved FRAMEFX_AGENT_DIR=$InstallDir (restart FrameFX to pick it up)."
}

# 4) Smoke test: the server lists a Vulkan device
& $serverExe --list-devices 2>&1 | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
Write-Host 'Done. Open FrameFX → WyneAI.'
