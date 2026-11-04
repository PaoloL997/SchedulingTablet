<#
.SYNOPSIS
    Avvia Brembana al login di un utente e ne blocca le uscite, lasciando Explorer attivo.
.DESCRIPTION
    Da eseguire SUL TABLET da PowerShell "Esegui come amministratore".
    Di default agisce sull'utente corrente. Con -UserName agisce su un ALTRO utente locale
    (utile quando l'utente kiosk e' standard e si lavora da un account amministratore).

    Explorer NON viene sostituito: e' lui che apre la tastiera touch quando si tocca un campo
    di testo. Senza Explorer la tastiera di sistema non comparirebbe piu' da sola. L'app copre
    comunque tutto lo schermo e resta in primo piano (KIOSK_ENABLED nel .env).

    - Run\Brembana -> Brembana.exe all'accesso   (solo per l'utente indicato)
    - Winlogon\Shell rimosso: ripristina Explorer se una versione precedente lo aveva sostituito
    - Tastiera touch "Sempre": compare anche con una tastiera fisica o un lettore collegato
    - Nasconde Task Manager e "Cambia utente" da Ctrl+Alt+Canc per quell'utente (Task Manager resta con -KeepTaskManager)
    - (opzionale, -DisableEdgeSwipe) disabilita lo swipe dal bordo: unica impostazione di SISTEMA (HKLM), vale per tutti
    Per tornare indietro: Disable-Kiosk.ps1 [-UserName ...]
.EXAMPLE
    .\Enable-Kiosk.ps1 -KeepTaskManager -UserName Inventario -ExePath "C:\Brembana\BrembanaTablet\Brembana.exe"
#>
param(
    [string]$ExePath = "C:\Program Files\Brembana\BrembanaPlatform\Brembana.exe",
    [string]$UserName,
    [switch]$KeepTaskManager,
    [switch]$DisableEdgeSwipe
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\KioskHive.ps1"

Assert-Admin
if (-not (Test-Path $ExePath)) { throw "Eseguibile non trovato: $ExePath  (usa -ExePath per indicare il percorso corretto)" }

$hive = Open-UserHive -UserName $UserName
try {
    $winlogon  = "$($hive.Root)\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"
    $run       = "$($hive.Root)\Software\Microsoft\Windows\CurrentVersion\Run"
    $system    = "$($hive.Root)\Software\Microsoft\Windows\CurrentVersion\Policies\System"
    $tabletTip = "$($hive.Root)\Software\Microsoft\TabletTip\1.7"

    # Explorer resta la shell: senza, la tastiera touch non si apre al tocco di un campo.
    Remove-ItemProperty -Path $winlogon -Name Shell -ErrorAction SilentlyContinue

    New-Item -Path $run -Force | Out-Null
    Set-ItemProperty -Path $run -Name Brembana -Value "`"$ExePath`"" -Type String

    # Impostazioni > Tastiera touch > "Mostra la tastiera touch": 2 = Sempre.
    New-Item -Path $tabletTip -Force | Out-Null
    Set-ItemProperty -Path $tabletTip -Name TouchKeyboardTapInvoke -Value 2 -Type DWord

    New-Item -Path $system -Force | Out-Null
    Set-ItemProperty -Path $system -Name HideFastUserSwitching -Value 1 -Type DWord
    if (-not $KeepTaskManager) {
        Set-ItemProperty -Path $system -Name DisableTaskMgr -Value 1 -Type DWord
    } else {
        Remove-ItemProperty -Path $system -Name DisableTaskMgr -ErrorAction SilentlyContinue
    }

    if ($DisableEdgeSwipe) {
        $edgeUi = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\EdgeUI"
        New-Item -Path $edgeUi -Force | Out-Null
        Set-ItemProperty -Path $edgeUi -Name AllowEdgeSwipe -Value 0 -Type DWord
    }
}
finally {
    Close-UserHive $hive
}

Write-Host ""
Write-Host "Kiosk impostato per l'utente $($hive.User): Brembana parte all'accesso -> $ExePath" -ForegroundColor Green
Write-Host "Explorer resta attivo, quindi la tastiera touch si apre toccando un campo di testo."
Write-Host "Riavvia il tablet (o logout/login di quell'utente) per applicare."
Write-Host ""
Write-Host "USCITA DI EMERGENZA: menu PIN dell'app -> Chiudi applicazione: si torna al desktop di Windows." -ForegroundColor Yellow
if (-not $KeepTaskManager) {
    Write-Host "ATTENZIONE: Task Manager disabilitato per $($hive.User). La manutenzione va fatta da un altro account amministratore." -ForegroundColor Yellow
}
