<#
.SYNOPSIS
  Removes Sidelight from this computer (and optionally from the phone).
.EXAMPLE
  .\scripts\uninstall.ps1               # remove the agent, keep settings
  .\scripts\uninstall.ps1 -Purge -Apk   # also delete settings and remove the phone app
#>
param([switch]$Purge, [switch]$Apk)

Get-Process SidelightAgent -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'Sidelight.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Sidelight.lnk') -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $env:LOCALAPPDATA 'Sidelight') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Agent removed.'

if ($Purge) {
    Remove-Item (Join-Path $env:APPDATA 'Sidelight') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Settings removed.'
}
if ($Apk) {
    $adb = (Get-Command adb -ErrorAction SilentlyContinue).Source
    if ($adb) { & $adb uninstall io.github.devpatel04.sidelight } else { Write-Warning 'adb not found; remove the Sidelight app on the phone manually.' }
}
