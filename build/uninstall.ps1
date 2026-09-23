#requires -Version 5.1
<#
.SYNOPSIS
    Désinstalle 7pace auto pour l'utilisateur courant, sans droits administrateur.

.DESCRIPTION
    Arrête proprement le collecteur de fond puis l'app, supprime
    %LOCALAPPDATA%\Programs\7pace auto, les raccourcis du menu Démarrer et du dossier
    Démarrage, puis l'entrée de désinstallation HKCU.
    Aucune question n'est posée. Les données de journée et les réglages
    (%LOCALAPPDATA%\7pace-auto) sont conservés par défaut ; seul -PurgeData les efface.

.EXAMPLE
    .\build\uninstall.ps1
    Retire l'application et garde les journées enregistrées.

.EXAMPLE
    .\build\uninstall.ps1 -PurgeData
    Retire l'application et efface aussi réglages, jeton et journées.
#>
[CmdletBinding()]
param(
    # Conserve explicitement %LOCALAPPDATA%\7pace-auto (comportement par défaut).
    [switch] $KeepData,

    # Efface aussi %LOCALAPPDATA%\7pace-auto : réglages, jeton et journées.
    [switch] $PurgeData
)

$ErrorActionPreference = 'Stop'

$NomApplication = '7pace auto'
$NomsProcessus = @('SeptPaceAuto.Agent', 'SeptPaceAuto.App', 'SeptPaceAuto.Terminal')

# Mêmes points de montage que l'installation : SEPTPACE_* n'existe que pour les tests.
function Emplacement([string] $Variable, [scriptblock] $Defaut) {
    $valeur = [Environment]::GetEnvironmentVariable($Variable)
    if ([string]::IsNullOrWhiteSpace($valeur)) { return & $Defaut }
    return [System.IO.Path]::GetFullPath($valeur)
}

$RacineProgrammes = Emplacement 'SEPTPACE_INSTALL_ROOT' { Join-Path $env:LOCALAPPDATA 'Programs' }
$DossierMenu = Emplacement 'SEPTPACE_MENU_DIR' { [Environment]::GetFolderPath('Programs') }
$DossierDemarrage = Emplacement 'SEPTPACE_STARTUP_DIR' { [Environment]::GetFolderPath('Startup') }
$CleDesinstallation = if ([string]::IsNullOrWhiteSpace($env:SEPTPACE_UNINSTALL_KEY)) {
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\7pace-auto'
} else {
    $env:SEPTPACE_UNINSTALL_KEY
}

function Write-Etape([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Info([string] $Message) { Write-Host "    $Message" }

if ($KeepData -and $PurgeData) {
    throw 'Choisis -KeepData ou -PurgeData, pas les deux.'
}

$cible = Join-Path $RacineProgrammes $NomApplication

# Le collecteur ferme d'abord ses créneaux et son battement : désinstaller ne doit pas
# perdre la dernière minute relevée.
$collecteur = Join-Path $cible 'SeptPaceAuto.Agent.exe'
if (Test-Path -LiteralPath $collecteur) {
    Write-Etape 'Arrêt du collecteur de fond'
    try {
        Start-Process -FilePath $collecteur -ArgumentList '--stop' -WindowStyle Hidden -Wait -ErrorAction Stop | Out-Null
    } catch {
        Write-Info "Arrêt du collecteur impossible : $($_.Exception.Message)"
    }
}

# Ferme ce qui reste puis force la fermeture au bout de 5 secondes. Seuls les processus
# lancés depuis le dossier désinstallé sont visés.
$racineInstallee = [System.IO.Path]::GetFullPath($cible).TrimEnd('\')
$processus = @(Get-Process -Name $NomsProcessus -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($racineInstallee, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
})
if ($processus.Count -gt 0) {
    Write-Etape 'Fermeture des processus encore ouverts'
    foreach ($p in $processus) {
        try {
            # L'app ne tient aucune donnée et replie sa fenêtre au lieu de se fermer.
            if ($p.ProcessName -eq 'SeptPaceAuto.App') {
                $p.Kill()
                [void] $p.WaitForExit(5000)
                continue
            }
            [void] $p.CloseMainWindow()
            if (-not $p.WaitForExit(5000)) {
                Write-Info "Fermeture forcée (PID $($p.Id))."
                $p.Kill()
                [void] $p.WaitForExit(5000)
            }
        } catch {
            Write-Info "Processus $($p.Id) déjà terminé."
        }
    }
    Start-Sleep -Milliseconds 500
}

$retires = 0

Write-Etape 'Suppression de l''application'
if (Test-Path -LiteralPath $cible) {
    # Le script vit peut-être dans le dossier qu'il supprime : on travaille depuis
    # ailleurs, puis on rend à l'appelant son dossier courant s'il existe encore.
    $depart = (Get-Location).ProviderPath
    Set-Location ([System.IO.Path]::GetTempPath())
    try {
        Remove-Item -LiteralPath $cible -Recurse -Force
        Write-Info "Retiré : $cible"
        $retires++
    } catch {
        Write-Info "Suppression incomplète de $cible : $($_.Exception.Message)"
        Write-Info 'Quitte l''app puis relance ce script.'
    } finally {
        if (Test-Path -LiteralPath $depart) { Set-Location -LiteralPath $depart }
    }
} else {
    Write-Info 'Aucun dossier d''installation trouvé.'
}

Write-Etape 'Suppression des raccourcis'
$raccourcis = @(
    (Join-Path $DossierMenu "$NomApplication.lnk"),
    (Join-Path $DossierDemarrage "$NomApplication.lnk")
)
foreach ($raccourci in $raccourcis) {
    if (Test-Path -LiteralPath $raccourci) {
        Remove-Item -LiteralPath $raccourci -Force
        Write-Info "Retiré : $raccourci"
        $retires++
    }
}

Write-Etape 'Suppression de l''entrée de désinstallation'
if (Test-Path -LiteralPath $CleDesinstallation) {
    Remove-Item -LiteralPath $CleDesinstallation -Recurse -Force
    Write-Info $CleDesinstallation
    $retires++
} else {
    Write-Info 'Aucune entrée inscrite.'
}

$donnees = Join-Path $env:LOCALAPPDATA '7pace-auto'
if ($PurgeData) {
    Write-Etape 'Suppression des données'
    if (Test-Path -LiteralPath $donnees) {
        Remove-Item -LiteralPath $donnees -Recurse -Force
        Write-Info "Effacé : $donnees"
    } else {
        Write-Info 'Aucune donnée à effacer.'
    }
} elseif (Test-Path -LiteralPath $donnees) {
    Write-Info "Données conservées : $donnees (utilise -PurgeData pour les effacer)"
}

Write-Host ''
if ($retires -gt 0) {
    Write-Host "$NomApplication est désinstallé." -ForegroundColor Green
} else {
    Write-Host "$NomApplication n'était pas installé pour cet utilisateur." -ForegroundColor Yellow
}
