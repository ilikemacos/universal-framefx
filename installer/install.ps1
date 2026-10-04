# Universal-FrameFX installer for Windows 10/11 (x64). Per-user, no admin needed.
#
#   irm https://chopstickshq.com/universal-framefx/install.ps1 | iex
#
# Installs to %LOCALAPPDATA%\Programs\Universal-FrameFX, verifies the zip's
# SHA-256 against version.json, adds a Start Menu shortcut and runs the GPU
# self-test. Installs the current version, which also ships the AMD FidelityFX
# and Intel XeSS runtimes and their licences next to the exe. To install an older build:
#   & ([scriptblock]::Create((irm https://chopstickshq.com/universal-framefx/install.ps1))) -Version 1.2
# To uninstall:
#   & ([scriptblock]::Create((irm https://chopstickshq.com/universal-framefx/install.ps1))) -Uninstall
param([switch]$Uninstall, [switch]$NoLaunch, [string]$Version = '')

& {
    param([bool]$Uninstall, [bool]$NoLaunch, [string]$Version)
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $Base = if ($env:UFX_BASE) { $env:UFX_BASE.TrimEnd('/') } else { 'https://chopstickshq.com/universal-framefx' }
    $Dir = Join-Path $env:LOCALAPPDATA 'Programs\Universal-FrameFX'
    $Lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Universal-FrameFX.lnk'
    $Exe = Join-Path $Dir 'Universal-FrameFX.exe'
    function Say($m) { Write-Host "==> $m" -ForegroundColor Cyan }

    Get-Process -Name 'Universal-FrameFX' -ErrorAction SilentlyContinue | ForEach-Object {
        Say "Closing running Universal-FrameFX (pid $($_.Id))"; $_.CloseMainWindow() | Out-Null
        if (-not $_.WaitForExit(5000)) { $_ | Stop-Process -Force }
    }

    if ($Uninstall) {
        Remove-Item -Recurse -Force $Dir -ErrorAction SilentlyContinue
        Remove-Item -Force $Lnk -ErrorAction SilentlyContinue
        Say 'Universal-FrameFX removed.'
        return
    }

    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Universal-FrameFX needs 64-bit Windows.' }
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 19041) { throw "Windows 10 version 2004 (build 19041) or later is required; this PC is build $build." }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Say 'Reading version.json'
    $qs = if ($Base -like 'http*') { "?t=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())" } else { '' }
    $v = Invoke-RestMethod -UseBasicParsing "$Base/version.json$qs"
    $w = $v.windows
    if (-not $w) { throw 'version.json has no Windows build.' }
    if ($Version -and $Version -ne $w.version) {
        $old = @($v.previous) | Where-Object { $_.version -eq $Version } | Select-Object -First 1
        if (-not $old) { throw "Version $Version is not available. Current: $($w.version)." }
        $w = [pscustomobject]@{ version = $old.version; zip = $old.zip; url = $old.url; size = $old.size; sha256 = $old.sha256 }
    }
    $url = if ($w.url -and -not $env:UFX_BASE) { $w.url } else { "$Base/$($w.zip)" }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("ufx-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $zip = Join-Path $tmp $w.zip
        Say "Downloading Universal-FrameFX $($w.version) ($([math]::Round($w.size / 1MB, 1)) MB)"
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip
        $hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
        if ($hash -ne $w.sha256.ToLowerInvariant()) { throw "SHA-256 mismatch: expected $($w.sha256), got $hash. Nothing was installed." }
        Say 'SHA-256 verified'

        $stage = Join-Path $tmp 'x'
        Expand-Archive -Path $zip -DestinationPath $stage -Force
        if (-not (Test-Path (Join-Path $stage 'Universal-FrameFX.exe'))) { throw 'Archive is missing Universal-FrameFX.exe.' }
        New-Item -ItemType Directory -Force -Path $Dir | Out-Null
        Copy-Item -Path (Join-Path $stage '*') -Destination $Dir -Recurse -Force
        Get-ChildItem $Dir -Recurse | Unblock-File
        Say "Installed to $Dir"
    } finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }

    $sh = New-Object -ComObject WScript.Shell
    # Remove stale Start Menu shortcuts left by the old PowerShell-based "Universal FrameFX" tool (v0.x),
    # which point at powershell.exe + UniversalFrameFX.ps1. The old tool's files and data are left alone.
    $progs = [Environment]::GetFolderPath('Programs')
    Get-ChildItem -Path $progs -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $Lnk -and $_.Name -match 'Frame\s*FX' } | ForEach-Object {
            try {
                $old = $sh.CreateShortcut($_.FullName)
                if ("$($old.TargetPath) $($old.Arguments)" -match 'UniversalFrameFX\.ps1|\\Programs\\UniversalFrameFX\\') {
                    Remove-Item -LiteralPath $_.FullName -Force
                    Say "Removed stale shortcut '$($_.Name)' (old PowerShell tool)"
                }
            } catch { }
        }
    $s = $sh.CreateShortcut($Lnk)
    $s.TargetPath = $Exe; $s.WorkingDirectory = $Dir; $s.IconLocation = "$Exe,0"
    $s.Description = 'Universal-FrameFX: real-time window upscaler and frame generator'
    $s.Save()
    Say 'Start Menu shortcut added'

    Say 'Running GPU self-test'
    $log = Join-Path $Dir 'selftest.txt'
    $p = Start-Process -FilePath $Exe -ArgumentList '--selftest', '--out', "`"$log`"" -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -eq 0) { Say "Self-test passed ($log)" }
    else { Write-Warning "Self-test failed (exit $($p.ExitCode)); see $log. The app may not run on this GPU." }

    Write-Host ''
    Write-Host "Universal-FrameFX $($w.version) is installed. Start it from the Start Menu or: `"$Exe`"" -ForegroundColor Green
    if (-not $NoLaunch -and $p.ExitCode -eq 0) { Start-Process -FilePath $Exe }
} ([bool]$Uninstall) ([bool]$NoLaunch) $Version
