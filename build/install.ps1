#requires -Version 5.1
<#
.SYNOPSIS
    Installe 7pace auto pour l'utilisateur courant, sans droits administrateur.

.DESCRIPTION
    Copie l'application dans %LOCALAPPDATA%\Programs\7pace auto, crée le raccourci
    du menu Démarrer, éventuellement le raccourci de démarrage automatique, et
    inscrit une entrée de désinstallation dans la ruche de l'utilisateur (HKCU).
    Le script est idempotent : le relancer met à jour l'installation en place après
    avoir fermé l'application si elle tourne. Les données de journée
    (%LOCALAPPDATA%\7pace-auto) ne sont jamais touchées.

.EXAMPLE
    .\build\install.ps1
    Installe la dernière publication trouvée dans artifacts\.

.EXAMPLE
    .\build\install.ps1 -Zip .\artifacts\SeptPaceAuto-win-x64.zip -Startup
    Installe depuis une archive et lance l'application à l'ouverture de session.
#>
[CmdletBinding()]
param(
    # Archive SeptPaceAuto-win-x64.zip à installer.
    [string] $Zip,

    # Dossier déjà publié à installer (prioritaire sur la détection automatique).
    [string] $Source,

    # Ajoute un raccourci dans le dossier Démarrage de l'utilisateur.
    [switch] $Startup
)

$ErrorActionPreference = 'Stop'

$NomApplication = '7pace auto'
$NomProcessus = 'SeptPaceAuto'
$NomExecutable = 'SeptPaceAuto.exe'
$CleDesinstallation = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\7pace-auto'

function Write-Etape([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Info([string] $Message) { Write-Host "    $Message" }

function Resolve-Chemin([string] $Chemin) {
    if (-not [System.IO.Path]::IsPathRooted($Chemin)) {
        $Chemin = Join-Path (Get-Location).ProviderPath $Chemin
    }
    return [System.IO.Path]::GetFullPath($Chemin)
}

# Ferme proprement l'application puis force la fermeture au bout de 5 secondes.
function Stop-Application {
    $processus = @(Get-Process -Name $NomProcessus -ErrorAction SilentlyContinue)
    if ($processus.Count -eq 0) { return }

    Write-Etape 'Fermeture de l''application en cours'
    foreach ($p in $processus) {
        try {
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

function New-Raccourci([string] $Chemin, [string] $Cible, [string] $Description) {
    $dossier = Split-Path -Parent $Chemin
    if (-not (Test-Path -LiteralPath $dossier)) {
        New-Item -ItemType Directory -Path $dossier -Force | Out-Null
    }
    if (Test-Path -LiteralPath $Chemin) { Remove-Item -LiteralPath $Chemin -Force }

    $shell = New-Object -ComObject 'WScript.Shell'
    try {
        $raccourci = $shell.CreateShortcut($Chemin)
        $raccourci.TargetPath = $Cible
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

        if (Test-Path -LiteralPath (Join-Path $publication $NomExecutable)) {
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

    # --- Copie ---------------------------------------------------------------
    $cible = Join-Path $env:LOCALAPPDATA (Join-Path 'Programs' $NomApplication)
    $misAJour = Test-Path -LiteralPath (Join-Path $cible $NomExecutable)

    Stop-Application

    if ($misAJour) { Write-Etape "Mise à jour de l'installation existante" }
    else { Write-Etape 'Installation' }

    if (Test-Path -LiteralPath $cible) {
        Get-ChildItem -LiteralPath $cible -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path $cible -Force | Out-Null
    }

    Copy-Item -Path (Join-Path $origine '*') -Destination $cible -Recurse -Force
    $executable = Join-Path $cible $NomExecutable
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
    $menu = Join-Path ([Environment]::GetFolderPath('Programs')) "$NomApplication.lnk"
    New-Raccourci -Chemin $menu -Cible $executable -Description 'Suivi automatique du temps et imputation 7pace'
    Write-Info "Menu Démarrer : $menu"

    $demarrage = Join-Path ([Environment]::GetFolderPath('Startup')) "$NomApplication.lnk"
    if ($Startup) {
        New-Raccourci -Chemin $demarrage -Cible $executable -Description 'Suivi automatique du temps et imputation 7pace'
        Write-Info "Démarrage automatique : $demarrage"
    } elseif (Test-Path -LiteralPath $demarrage) {
        # Une installation précédente avait activé le démarrage : la cible est rafraîchie.
        New-Raccourci -Chemin $demarrage -Cible $executable -Description 'Suivi automatique du temps et imputation 7pace'
        Write-Info "Démarrage automatique conservé : $demarrage"
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

    Write-Host ''
    Write-Host "$NomApplication est installé." -ForegroundColor Green
    Write-Info "Dossier      : $cible"
    Write-Info "Exécutable   : $executable"
    Write-Info "Menu Démarrer: $NomApplication"
    Write-Info "Désinstaller : .\build\uninstall.ps1 (ou depuis Applications installées)"
    Write-Info 'Les réglages et les journées restent dans %LOCALAPPDATA%\7pace-auto.'
} finally {
    if ($temporaire -and (Test-Path -LiteralPath $temporaire)) {
        Remove-Item -LiteralPath $temporaire -Recurse -Force -ErrorAction SilentlyContinue
    }
}
