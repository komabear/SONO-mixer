# SONO Mixer — VAC removal helper (run elevated from the SONO uninstaller).
# Removes ALL Virtual Audio Cable pieces the SONO setup may have created:
#   1. device nodes (working + phantom leftovers)
#   2. the driver service
#   3. the driver package from the component store
#   4. the driver file itself
#   5. VAC registry settings
$ErrorActionPreference = 'Continue'
$transcript = Join-Path $env:LOCALAPPDATA 'Temp\sono_vac_uninstall.log'
Start-Transcript -Path $transcript -Force | Out-Null
Write-Host "=== SONO: removing Virtual Audio Cable ==="

# 1) device nodes (0000 = live; 0001+ = phantom leftovers from failed attempts)
Get-PnpDevice | Where-Object { $_.InstanceId -like "*83ed7f0e*" } | ForEach-Object {
    Write-Host "removing device: $($_.InstanceId)"
    pnputil /remove-device "$($_.InstanceId)" 2>&1 | Out-Host
}

# 2) service
sc.exe stop VirtualAudioCable_83ed7f0e-2028-4956-b0b4-39c76fdaef1d 2>&1 | Out-Host
sc.exe delete VirtualAudioCable_83ed7f0e-2028-4956-b0b4-39c76fdaef1d 2>&1 | Out-Host

# 3) driver package(s) from the store
$enum = pnputil /enum-drivers | Out-String
$blocks = $enum -split "(?=Published Name)" | Where-Object { $_ -match "vrtaucbl" }
# pt-BR fallback ("Nome Publicado")
if ($blocks.Count -eq 0) {
    $blocks = $enum -split "(?=Nome Publicado)" | Where-Object { $_ -match "vrtaucbl" }
}
foreach ($b in $blocks) {
    if ($b -match "(oem\d+\.inf)") {
        Write-Host "deleting driver package: $($Matches[1])"
        pnputil /delete-driver "$($Matches[1])" /force 2>&1 | Out-Host
    }
}

# 4) driver file (may be locked until reboot)
Remove-Item "C:\Windows\System32\drivers\vrtaucbl.sys" -Force -ErrorAction SilentlyContinue
if (Test-Path "C:\Windows\System32\drivers\vrtaucbl.sys") {
    Write-Host "driver file still locked — a reboot will release and remove it"
} else {
    Write-Host "driver file deleted"
}

# 5) registry settings
reg delete "HKLM\SOFTWARE\EuMus Design" /f 2>&1 | Out-Host

Write-Host "=== DONE — a reboot is recommended ==="
Stop-Transcript | Out-Null
