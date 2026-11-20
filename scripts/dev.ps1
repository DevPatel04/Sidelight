<#
.SYNOPSIS
  Development loop for Windows: run the agent from source and push panel changes to the phone.

.DESCRIPTION
  Same commands as scripts/dev.sh on Linux/macOS.

  .\scripts\dev.ps1 restart   # rebuild and restart the agent, rebuild the app and reinstall it on the phone
  .\scripts\dev.ps1 start     # build and start the agent (no-op when it is already running)
  .\scripts\dev.ps1 stop      # stop the agent
  .\scripts\dev.ps1 agent     # rebuild and restart only the agent (settings.html, C# changes)
  .\scripts\dev.ps1 phone     # rebuild and reinstall only the app, then reopen it (panel www/ changes)
  .\scripts\dev.ps1 test      # release metadata, agent unit tests, and Playwright UI (incl. i18n) — run before push
  .\scripts\dev.ps1 status    # is the agent running, is a phone connected
  .\scripts\dev.ps1 logs      # follow the agent's output

  The phone loads the panel from the APK's own assets, not from the agent, so changes under www/ (themes,
  widgets, styles) reach the phone only after "phone" or "restart". The settings page and the browser
  preview at /panel/ are embedded in the agent, so they need "agent" (then reload the browser tab).

.EXAMPLE
  .\scripts\dev.ps1 restart
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'agent', 'phone', 'restart', 'test', 'status', 'logs')]
    [string]$Command = '',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PlaywrightArgs = @()
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
$tfm = 'net10.0-windows10.0.19041.0'
$exe = Join-Path $root "agent\Sidelight.Agent\bin\Debug\$tfm\SidelightAgent.exe"
$url = 'http://127.0.0.1:8765'
$pkg = 'io.github.devpatel04.sidelight'
$apk = Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk'
# The WinForms agent has no console; FileLogger writes here whether the exe is installed or run from source.
$log = Join-Path $env:LOCALAPPDATA 'Sidelight\agent.log'

if (-not $env:ANDROID_HOME) { $env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
if (-not $env:JAVA_HOME) {
    $jdk = Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -like 'jdk-17*' | Select-Object -First 1
    if ($jdk) { $env:JAVA_HOME = $jdk.FullName }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) {
    $env:PATH = "C:\Program Files\dotnet;$env:PATH"
}

function Find-Adb {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        (Join-Path $env:ANDROID_HOME 'platform-tools\adb.exe')
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe')
    ) + @(
        Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Directory -Filter 'Google.PlatformTools*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'platform-tools\adb.exe' }
    )
    $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}

function Get-AgentProcess {
    # Prefer the debug build from this checkout; fall back to any SidelightAgent (installed copy also holds :8765).
    $all = @(Get-Process -Name SidelightAgent -ErrorAction SilentlyContinue)
    if ($all.Count -eq 0) { return $null }
    $mine = @($all | Where-Object { $_.Path -and ($_.Path -ieq $exe) })
    if ($mine.Count -gt 0) { return $mine[0] }
    return $all[0]
}

function Stop-Agent {
    $procs = @(Get-Process -Name SidelightAgent, DeskAgent -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { Write-Host 'agent: not running'; return }
    Write-Host ("agent: stopping ({0})" -f (($procs | ForEach-Object Id) -join ', '))
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Process -Name SidelightAgent -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    Get-Process -Name SidelightAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Start-Agent {
    $running = Get-AgentProcess
    if ($running) {
        Write-Host ("agent: already running ({0}), use restart to pick up changes" -f $running.Id)
        return
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET 10 SDK is required: winget install Microsoft.DotNet.SDK.10'
    }
    Write-Host 'agent: building'
    & dotnet build (Join-Path $root 'agent\Sidelight.Agent') -f $tfm --nologo -v q -clp:ErrorsOnly
    if ($LASTEXITCODE) { throw "dotnet build failed (exit code $LASTEXITCODE)" }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "built agent not found: $exe" }

    Write-Host "agent: starting (log: $log)"
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
    for ($i = 0; $i -lt 60; $i++) {
        try {
            Invoke-RestMethod "$url/api/state" -TimeoutSec 2 | Out-Null
            Write-Host "agent: ready on $url"
            return
        } catch {
            if (-not (Get-AgentProcess)) {
                Write-Host 'agent: exited during start-up:' -ForegroundColor Red
                if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 20 | Write-Host }
                throw 'agent failed to start'
            }
            Start-Sleep -Seconds 1
        }
    }
    throw "agent: no answer on $url after 60 s; see $log"
}

function Test-PhoneConnected {
    $adb = Find-Adb
    if (-not $adb) { return $false }
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $adb get-state 2>&1 | Out-Null; return ($LASTEXITCODE -eq 0) }
    finally { $ErrorActionPreference = $old }
}

function Update-Phone {
    $gradlew = Join-Path $root 'android\gradlew.bat'
    if (-not (Test-Path -LiteralPath $gradlew)) { throw "gradlew.bat not found: $gradlew" }
    Write-Host 'app: building'
    Push-Location (Join-Path $root 'android')
    try {
        & .\gradlew.bat assembleDebug -q --console=plain
        if ($LASTEXITCODE) { throw "the Android build failed (exit code $LASTEXITCODE)" }
    } finally { Pop-Location }

    $adb = Find-Adb
    if (-not $adb -or -not (Test-PhoneConnected)) {
        Write-Host "app: no phone on adb, built only ($apk)"
        return
    }

    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $devices = @(& $adb devices 2>&1 | Select-Object -Skip 1 | Where-Object { $_ -match "`tdevice$" } | ForEach-Object { ($_ -split "`t")[0] })
    } finally { $ErrorActionPreference = $old }
    if ($devices.Count -eq 0) { Write-Host "app: no phone on adb, built only ($apk)"; return }
    if ($devices.Count -gt 1) { throw "Several devices are connected; unplug extras or install by hand with: adb -s <serial> install -r `"$apk`"" }
    $serial = $devices[0]

    Write-Host "app: installing on $serial"
    . (Join-Path $PSScriptRoot 'install-apk.ps1')
    try {
        Install-SidelightApk -Adb $adb -ApkPath $apk -Serial $serial | Out-Null
    } catch {
        if ("$_" -match 'signing key differs|UPDATE_INCOMPATIBLE') {
            Write-Host 'app: the installed app is signed with another key. Uninstall it once (this clears the app''s data):' -ForegroundColor Yellow
            Write-Host "     adb shell pm uninstall $pkg" -ForegroundColor Yellow
        }
        throw
    }
    & $adb -s $serial shell am force-stop $pkg
    & $adb -s $serial shell am start -n "$pkg/.MainActivity" | Out-Null
    Write-Host 'app: reinstalled and reopened'
}

function Show-Status {
    $p = Get-AgentProcess
    if ($p) { Write-Host ("agent: running ({0}) on {1}" -f $p.Id, $url) } else { Write-Host 'agent: not running' }
    $adb = Find-Adb
    if ($adb -and (Test-PhoneConnected)) {
        $old = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { Write-Host ("phone: {0}" -f (& $adb get-serialno 2>$null)) }
        finally { $ErrorActionPreference = $old }
    } else {
        Write-Host 'phone: none on adb'
    }
}

function Show-Logs {
    if (-not (Test-Path -LiteralPath $log)) {
        New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
        New-Item -ItemType File -Path $log -Force | Out-Null
    }
    Get-Content -LiteralPath $log -Tail 50 -Wait
}

function Invoke-Tests {
    # Same checks CI runs for metadata, agent and the browser panel/settings (including Vietnamese leftover scans).
    # Extra args go to Playwright (e.g. .\scripts\dev.ps1 test i18n-coverage.spec.js).
    Write-Host 'tests: release metadata'
    & python (Join-Path $root 'tools\check-release.py')
    if ($LASTEXITCODE) { throw "check-release.py failed (exit code $LASTEXITCODE)" }

    Write-Host 'tests: agent'
    & dotnet test (Join-Path $root 'agent\Sidelight.Agent.Tests') --nologo
    if ($LASTEXITCODE) { throw "dotnet test failed (exit code $LASTEXITCODE)" }

    Write-Host 'tests: UI (Playwright)'
    $ui = Join-Path $root 'tools\ui-tests'
    Push-Location $ui
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $ui 'node_modules\@playwright\test'))) {
            & npm ci
            if ($LASTEXITCODE) { throw "npm ci failed (exit code $LASTEXITCODE)" }
        }
        & npx playwright install chromium
        if ($LASTEXITCODE) { throw "playwright install failed (exit code $LASTEXITCODE)" }
        & npx playwright test @PlaywrightArgs
        if ($LASTEXITCODE) { throw "playwright test failed (exit code $LASTEXITCODE)" }
    } finally { Pop-Location }
}

function Show-Usage {
    @'
Development loop for Windows (same as scripts/dev.sh):

  .\scripts\dev.ps1 restart   # rebuild and restart the agent, rebuild and reinstall the phone app
  .\scripts\dev.ps1 start     # build and start the agent (no-op when already running)
  .\scripts\dev.ps1 stop      # stop the agent
  .\scripts\dev.ps1 agent     # rebuild and restart only the agent
  .\scripts\dev.ps1 phone     # rebuild and reinstall only the phone app
  .\scripts\dev.ps1 test      # release metadata, agent unit tests, and Playwright UI (incl. i18n)
  .\scripts\dev.ps1 status    # is the agent running, is a phone connected
  .\scripts\dev.ps1 logs      # follow the agent log
'@ | Write-Host
}

switch ($Command) {
    'start'   { Start-Agent }
    'stop'    { Stop-Agent }
    'agent'   { Stop-Agent; Start-Agent }
    'phone'   { Update-Phone }
    'restart' { Stop-Agent; Start-Agent; Update-Phone }
    'test'    { Invoke-Tests }
    'status'  { Show-Status }
    'logs'    { Show-Logs }
    default   { Show-Usage; exit 2 }
}
