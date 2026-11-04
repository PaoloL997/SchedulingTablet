# Funzioni condivise: apertura dell'hive di registro (HKCU) di un utente locale, anche se non loggato.

function Assert-Admin {
    $p = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Esegui PowerShell come amministratore."
    }
}

# Ritorna un oggetto con Root (percorso PSDrive della radice HKCU dell'utente), User, IsCurrentUser, Loaded.
function Open-UserHive {
    param([string]$UserName)

    if ([string]::IsNullOrWhiteSpace($UserName) -or $UserName -eq $env:USERNAME) {
        return [pscustomobject]@{ Root = "HKCU:"; User = $env:USERNAME; IsCurrentUser = $true; Loaded = $false; Sid = $null }
    }

    $account = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
    if (-not $account) { throw "Utente locale non trovato: $UserName" }
    $sid = $account.SID.Value

    if (-not (Test-Path "Registry::HKEY_USERS")) { New-PSDrive -Name HKU -PSProvider Registry -Root HKEY_USERS | Out-Null }

    $loaded = $false
    if (-not (Test-Path "Registry::HKEY_USERS\$sid")) {
        $profilePath = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid" -ErrorAction SilentlyContinue).ProfileImagePath
        if (-not $profilePath) { throw "L'utente $UserName non ha ancora un profilo: fai almeno un login con quell'account, poi riprova." }
        $ntuser = Join-Path $profilePath "NTUSER.DAT"
        $out = & reg.exe load "HKU\$sid" "$ntuser" 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Impossibile caricare l'hive di $UserName ($ntuser): $out`nSe l'utente e' loggato, fai prima il suo logout." }
        $loaded = $true
    }

    return [pscustomobject]@{ Root = "Registry::HKEY_USERS\$sid"; User = $UserName; IsCurrentUser = $false; Loaded = $loaded; Sid = $sid }
}

function Close-UserHive {
    param($Hive)
    if ($Hive.Loaded) {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        & reg.exe unload "HKU\$($Hive.Sid)" | Out-Null
    }
}
