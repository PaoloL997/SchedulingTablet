<#
.SYNOPSIS
    Annulla Enable-Kiosk.ps1 per un utente: niente avvio automatico, niente restrizioni.
.DESCRIPTION
    Da eseguire SUL TABLET da PowerShell "Esegui come amministratore".
    Di default agisce sull'utente corrente; con -UserName su un altro utente locale.
    Ripristina anche Explorer come shell, se una versione precedente dello script lo aveva sostituito.
    La tastiera touch torna all'impostazione predefinita di Windows.
.EXAMPLE
    .\Disable-Kiosk.ps1
    .\Disable-Kiosk.ps1 -UserName Inventario
#>
param(
    [string]$UserName,
    [switch]$RestoreEdgeSwipe
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\KioskHive.ps1"

Assert-Admin
$hive = Open-UserHive -UserName $UserName
try {
    $winlogon  = "$($hive.Root)\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"
    $run       = "$($hive.Root)\Software\Microsoft\Windows\CurrentVersion\Run"
    $system    = "$($hive.Root)\Software\Microsoft\Windows\CurrentVersion\Policies\System"
    $tabletTip = "$($hive.Root)\Software\Microsoft\TabletTip\1.7"

    Remove-ItemProperty -Path $winlogon -Name Shell -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $run -Name Brembana -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $tabletTip -Name TouchKeyboardTapInvoke -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $system -Name DisableTaskMgr -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $system -Name HideFastUserSwitching -ErrorAction SilentlyContinue

    if ($RestoreEdgeSwipe) {
        Remove-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\EdgeUI" -Name AllowEdgeSwipe -ErrorAction SilentlyContinue
    }
}
finally {
    Close-UserHive $hive
}

Write-Host "Kiosk rimosso per l'utente $($hive.User). Riavvia o logout/login." -ForegroundColor Green
if ($hive.IsCurrentUser -and -not (Get-Process explorer -ErrorAction SilentlyContinue)) {
    Start-Process explorer.exe
}
