#requires -Version 5.1
<#
.SYNOPSIS
    Installe 7pace auto pour l'utilisateur courant, sans droits administrateur.

.DESCRIPTION
    Copie l'application dans %LOCALAPPDATA%\Programs\7pace auto, crée le raccourci
    du menu Démarrer vers l'app, éventuellement le raccourci de session qui ouvre l'app
    en widget, et inscrit une entrée de désinstallation dans la ruche de
    l'utilisateur (HKCU). Le script est idempotent : le relancer met à jour
    l'installation en place après avoir arrêté proprement le collecteur. Les données de
    journée (%LOCALAPPDATA%\7pace-auto) ne sont jamais touchées, et un démarrage
    automatique déjà choisi est conservé, y compris celui d'une version terminal.

.EXAMPLE
    .\build\install.ps1
    Installe la dernière publication trouvée dans artifacts\.

.EXAMPLE
    .\build\install.ps1 -Zip .\artifacts\SeptPaceAuto-win-x64.zip -Startup
    Installe depuis une archive et ouvre le widget à chaque ouverture de session.
#>
[CmdletBinding()]
param(
    # Archive SeptPaceAuto-win-x64.zip à installer.
    [string] $Zip,

    # Dossier déjà publié à installer (prioritaire sur la détection automatique).
    [string] $Source,

    # Ouvre l'app en widget à chaque ouverture de session ; elle lance le collecteur.
    [switch] $Startup
)

$ErrorActionPreference = 'Stop'

$NomApplication = '7pace auto'
# L'ancien terminal figure encore ici pour qu'une migration depuis la 1.x le ferme.
$NomsProcessus = @('SeptPaceAuto.Agent', 'SeptPaceAuto.App', 'SeptPaceAuto.Terminal')
$NomExecutable = 'SeptPaceAuto.App.exe'
$NomCollecteur = 'SeptPaceAuto.Agent.exe'

# Emplacements de l'installation. Les variables SEPTPACE_* ne servent qu'aux tests
# d'installation : elles permettent de poser une installation complète dans un dossier
# jetable, sans toucher à celle de l'utilisateur ni à ses raccourcis.
function Emplacement([string] $Variable, [scriptblock] $Defaut) {
    $valeur = [Environment]::GetEnvironmentVariable($Variable)
    if ([string]::IsNullOrWhiteSpace($valeur)) { return & $Defaut }
    if (-not (Test-Path -LiteralPath $valeur)) { New-Item -ItemType Directory -Path $valeur -Force | Out-Null }
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

function Resolve-Chemin([string] $Chemin) {
    if (-not [System.IO.Path]::IsPathRooted($Chemin)) {
        $Chemin = Join-Path (Get-Location).ProviderPath $Chemin
    }
    return [System.IO.Path]::GetFullPath($Chemin)
}

# Arrête le collecteur par son propre protocole : il ferme ses créneaux et son battement
# avant de sortir, donc aucune minute relevée n'est perdue par l'installation.
function Stop-Collecteur([string] $Executable) {
    if (-not (Test-Path -LiteralPath $Executable)) { return }
    try {
        $arret = Start-Process -FilePath $Executable -ArgumentList '--stop' -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
        if ($arret.ExitCode -ne 0) { Write-Info 'Le collecteur n''a pas confirmé son arrêt.' }
    } catch {
        Write-Info "Arrêt du collecteur impossible : $($_.Exception.Message)"
    }
}

# Ferme ce qui reste, puis force la fermeture au bout de 5 secondes. Seuls les processus
# lancés depuis le dossier installé sont visés : un collecteur d'un autre profil, ou une
# compilation locale, ne doit pas être arrêté par une installation.
function Stop-Application([string] $Cible) {
    Stop-Collecteur (Join-Path $Cible $NomCollecteur)

    $racine = [System.IO.Path]::GetFullPath($Cible).TrimEnd('\')
    $processus = @(Get-Process -Name $NomsProcessus -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($racine, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    if ($processus.Count -eq 0) { return }

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
    # Laisse Windows relâcher les fichiers avant la copie.
    Start-Sleep -Milliseconds 500
}

function New-Raccourci([string] $Chemin, [string] $Cible, [string] $Description, [string] $Arguments = '') {
    $dossier = Split-Path -Parent $Chemin
    if (-not (Test-Path -LiteralPath $dossier)) {
        New-Item -ItemType Directory -Path $dossier -Force | Out-Null
    }
    if (Test-Path -LiteralPath $Chemin) { Remove-Item -LiteralPath $Chemin -Force }

    $shell = New-Object -ComObject 'WScript.Shell'
    try {
        $raccourci = $shell.CreateShortcut($Chemin)
        $raccourci.TargetPath = $Cible
        $raccourci.Arguments = $Arguments
        $raccourci.WorkingDirectory = Split-Path -Parent $Cible
        $raccourci.IconLocation = "$Cible,0"
        $raccourci.Description = $Description
        $raccourci.Save()
    } finally {
        try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) } catch { }
    }
}


Add-Type -AssemblyName 'System.IO.Compression.FileSystem' -ErrorAction SilentlyContinue

$racine = Split-Path -Parent $PSScriptRoot
$temporaire = $null

try {
    # --- Origine des fichiers ------------------------------------------------
    if (-not [string]::IsNullOrWhiteSpace($Zip)) {
        $Zip = Resolve-Chemin $Zip
        if (-not (Test-Path -LiteralPath $Zip)) { throw "Archive introuvable : $Zip" }

        Write-Etape 'Extraction de l''archive'
        $temporaire = Join-Path ([System.IO.Path]::GetTempPath()) ('7pace-auto-' + [guid]::NewGuid().ToString('N'))
        [System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $temporaire)
        $origine = $temporaire
    } elseif (-not [string]::IsNullOrWhiteSpace($Source)) {
        $origine = Resolve-Chemin $Source
    } else {
        $publication = Join-Path $racine 'artifacts\publish'
        $archive = Join-Path $racine 'artifacts\SeptPaceAuto-win-x64.zip'

        if (Test-Path -LiteralPath (Join-Path $PSScriptRoot $NomExecutable)) {
            $origine = $PSScriptRoot
        } elseif (Test-Path -LiteralPath (Join-Path $publication $NomExecutable)) {
            $origine = $publication
        } elseif (Test-Path -LiteralPath $archive) {
            Write-Etape 'Extraction de l''archive trouvée dans artifacts'
            $temporaire = Join-Path ([System.IO.Path]::GetTempPath()) ('7pace-auto-' + [guid]::NewGuid().ToString('N'))
            [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $temporaire)
            $origine = $temporaire
        } else {
            throw "Rien à installer : lance d'abord .\build\publish.ps1, ou passe -Zip vers une archive."
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $origine $NomExecutable))) {
        throw "$NomExecutable est absent de $origine."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $origine $NomCollecteur))) {
        throw "$NomCollecteur est absent de $origine : republie avec .\build\publish.ps1."
    }

    # --- Copie ---------------------------------------------------------------
    $cible = Join-Path $RacineProgrammes $NomApplication
    $misAJour = Test-Path -LiteralPath (Join-Path $cible $NomExecutable)

    Stop-Application $cible

    if ($misAJour) { Write-Etape "Mise à jour de l'installation existante" }
    else { Write-Etape 'Installation' }

    if (Test-Path -LiteralPath $cible) {
        Get-ChildItem -LiteralPath $cible -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path $cible -Force | Out-Null
    }

    Copy-Item -Path (Join-Path $origine '*') -Destination $cible -Recurse -Force
    $executable = Join-Path $cible $NomExecutable
    $collecteur = Join-Path $cible $NomCollecteur
    Write-Info $cible

    # Le script de désinstallation voyage avec l'application : l'entrée HKCU le cible.
    $desinstallateurSource = Join-Path $PSScriptRoot 'uninstall.ps1'
    if (-not (Test-Path -LiteralPath $desinstallateurSource)) {
        $desinstallateurSource = Join-Path $origine 'uninstall.ps1'
    }
    $desinstallateur = Join-Path $cible 'uninstall.ps1'
    if (Test-Path -LiteralPath $desinstallateurSource) {
        Copy-Item -LiteralPath $desinstallateurSource -Destination $desinstallateur -Force
    } else {
        Write-Info 'uninstall.ps1 introuvable : entrée de désinstallation non renseignée.'
        $desinstallateur = $null
    }

    # --- Raccourcis ----------------------------------------------------------
    Write-Etape 'Raccourcis'
    $menu = Join-Path $DossierMenu "$NomApplication.lnk"
    New-Raccourci -Chemin $menu -Cible $executable -Description 'Suivi automatique du temps et imputation 7pace'
    Write-Info "Menu Démarrer : $menu"

    # Le démarrage de session ouvre l'app en widget ; elle rejoint ou lance le collecteur,
    # et ouvre sa fenêtre d'elle-même quand une journée attend.
    $demarrage = Join-Path $DossierDemarrage "$NomApplication.lnk"
    $auDemarrage = $Startup -or (Test-Path -LiteralPath $demarrage)
    if ($auDemarrage) {
        # Un démarrage déjà choisi est conservé : son ancienne cible (terminal ou
        # collecteur seul) passe à l'app en widget.
        New-Raccourci -Chemin $demarrage -Cible $executable -Description 'Widget de suivi du temps' -Arguments '--widget'
        Write-Info "Démarrage automatique : $demarrage"
    }

    # --- Entrée de désinstallation (utilisateur courant) ---------------------
    Write-Etape 'Entrée « Applications et fonctionnalités »'
    $version = ''
    try {
        $version = [string](Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
        if ($version) { $version = ($version -split '\+')[0].Trim() }
    } catch {
        $version = ''
    }
    if ([string]::IsNullOrWhiteSpace($version)) { $version = '1.0.0' }

    if (-not (Test-Path -LiteralPath $CleDesinstallation)) {
        New-Item -Path $CleDesinstallation -Force | Out-Null
    }

    $poids = 0
    $mesure = Get-ChildItem -LiteralPath $cible -Recurse -File | Measure-Object -Property Length -Sum
    if ($mesure -and $mesure.Sum) { $poids = [int] ($mesure.Sum / 1KB) }

    $valeurs = @{
        'DisplayName'     = $NomApplication
        'DisplayVersion'  = $version
        'Publisher'       = $NomApplication
        'InstallLocation' = $cible
        'DisplayIcon'     = $executable
        'NoModify'        = 1
        'NoRepair'        = 1
    }
    if ($desinstallateur) {
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $commande = '"' + $powershell + '" -NoProfile -ExecutionPolicy Bypass -File "' + $desinstallateur + '"'
        $valeurs['UninstallString'] = $commande
        $valeurs['QuietUninstallString'] = $commande
    }

    foreach ($nom in $valeurs.Keys) {
        $valeur = $valeurs[$nom]
        $type = 'String'
        if ($valeur -is [int]) { $type = 'DWord' }
        New-ItemProperty -Path $CleDesinstallation -Name $nom -Value $valeur -PropertyType $type -Force | Out-Null
    }
    New-ItemProperty -Path $CleDesinstallation -Name 'EstimatedSize' -Value $poids -PropertyType DWord -Force | Out-Null
    Write-Info "Version inscrite : $version"

    # --- Reprise du suivi ----------------------------------------------------
    # L'installation vient d'arrêter le collecteur : sans cela, plus rien ne serait relevé
    # jusqu'à la prochaine ouverture de session. L'app en widget le relance.
    if ($auDemarrage) {
        Write-Etape 'Démarrage du widget et du collecteur'
        try {
            Start-Process -FilePath $executable -ArgumentList '--widget' | Out-Null
            Write-Info 'Le suivi tourne en arrière-plan.'
        } catch {
            Write-Info "Démarrage de l'app impossible : $($_.Exception.Message)"
        }
    }

    Write-Host ''
    Write-Host "$NomApplication est installé." -ForegroundColor Green
    Write-Info "Dossier      : $cible"
    Write-Info "App          : $executable"
    Write-Info "Collecteur   : $collecteur"
    Write-Info "Menu Démarrer: $NomApplication"
    Write-Info "Désinstaller : .\build\uninstall.ps1 (ou depuis Applications installées)"
    Write-Info 'Les réglages et les journées restent dans %LOCALAPPDATA%\7pace-auto.'
    if (-not $auDemarrage) {
        Write-Info 'Sans -Startup, le collecteur démarre à la première ouverture de l''app.'
    }
} finally {
    if ($temporaire -and (Test-Path -LiteralPath $temporaire)) {
        Remove-Item -LiteralPath $temporaire -Recurse -Force -ErrorAction SilentlyContinue
    }
}
