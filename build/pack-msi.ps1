#requires -Version 5.1
<#
.SYNOPSIS
    Construit l'installateur 7pace-auto-setup.msi à partir d'une publication existante.

.DESCRIPTION
    Empaquette le contenu de artifacts\publish dans un MSI par utilisateur : pas
    d'élévation, dossier cible %LOCALAPPDATA%\Programs\7pace auto, raccourci dans le
    menu Démarrer et entrée « Applications installées ». Les données de l'utilisateur
    (%LOCALAPPDATA%\7pace-auto) ne sont ni copiées ni supprimées.

    L'outil WiX est installé à la demande comme outil global .NET.

.EXAMPLE
    .\build\pack-msi.ps1 -Version 1.2.0
    Empaquette artifacts\publish en artifacts\7pace-auto-setup.msi.
#>
[CmdletBinding()]
param(
    # Version du produit ; accepte « 1.2.0 » comme « v1.2.0 ». Par défaut, celle de l'exécutable publié.
    [string] $Version,

    # Dossier publié à empaqueter ; par défaut artifacts\publish.
    [string] $PublishDir,

    # Chemin du MSI produit ; par défaut artifacts\7pace-auto-setup.msi.
    [string] $Output,

    # Version de l'outil WiX utilisée (et installée si besoin).
    [string] $WixVersion = '5.0.2'
)

$ErrorActionPreference = 'Stop'

function Write-Etape([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Info([string] $Message) { Write-Host "    $Message" }

function Resolve-Chemin([string] $Chemin) {
    if (-not [System.IO.Path]::IsPathRooted($Chemin)) {
        $Chemin = Join-Path (Get-Location).ProviderPath $Chemin
    }
    return [System.IO.Path]::GetFullPath($Chemin)
}

# Windows Installer n'accepte qu'un numéro « majeur.mineur.build » strictement numérique :
# le « v » d'un tag et un éventuel suffixe de pré-version sont retirés.
function ConvertTo-VersionMsi([string] $Brut) {
    $texte = ($Brut.Trim() -replace '^[vV]', '')
    $texte = ($texte -split '[-+]')[0]
    if ($texte -notmatch '^[0-9]+(\.[0-9]+){0,2}$') {
        throw "Version invalide : « $Brut ». Attendu par exemple 1.2.0 ou v1.2.0."
    }
    $morceaux = @($texte -split '\.')
    while ($morceaux.Count -lt 3) { $morceaux += '0' }

    $limites = @(255, 255, 65535)
    for ($i = 0; $i -lt 3; $i++) {
        if ([int] $morceaux[$i] -gt $limites[$i]) {
            throw "Version hors limites Windows Installer : « $Brut » (champ $($i + 1) au maximum $($limites[$i]))."
        }
    }
    return ($morceaux -join '.')
}

# Renvoie le chemin de wix.exe, en l'installant comme outil global .NET si nécessaire.
function Get-OutilWix([string] $VersionSouhaitee) {
    $dossierOutils = Join-Path $env:USERPROFILE '.dotnet\tools'
    if ($env:PATH -notlike "*$dossierOutils*") { $env:PATH = "$env:PATH;$dossierOutils" }

    $commande = Get-Command 'wix' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($commande) { return $commande.Source }

    if (-not (Get-Command 'dotnet' -ErrorAction SilentlyContinue)) {
        throw "La commande « dotnet » est introuvable : impossible d'installer WiX."
    }

    Write-Etape "Installation de l'outil WiX $VersionSouhaitee"
    & dotnet tool install --global wix --version $VersionSouhaitee 2>&1 | ForEach-Object { Write-Info $_ }

    $commande = Get-Command 'wix' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($commande) { return $commande.Source }

    $secours = Join-Path $dossierOutils 'wix.exe'
    if (Test-Path -LiteralPath $secours) { return $secours }

    throw "WiX reste introuvable après installation. Essaie : dotnet tool install --global wix --version $VersionSouhaitee"
}

$racine = Split-Path -Parent $PSScriptRoot
$source = Join-Path $PSScriptRoot 'installer\Package.wxs'
$icone = Join-Path $PSScriptRoot 'installer\7pace-auto.ico'

foreach ($fichier in @($source, $icone)) {
    if (-not (Test-Path -LiteralPath $fichier)) { throw "Fichier d'installateur introuvable : $fichier" }
}

if ([string]::IsNullOrWhiteSpace($PublishDir)) { $PublishDir = Join-Path $racine 'artifacts\publish' }
$PublishDir = (Resolve-Chemin $PublishDir).TrimEnd('\')

$executable = Join-Path $PublishDir 'SeptPaceAuto.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Publication introuvable : $executable. Lance d'abord .\build\publish.ps1."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string] (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '1.0.0' }
}
$versionMsi = ConvertTo-VersionMsi $Version

if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $racine 'artifacts\7pace-auto-setup.msi' }
$Output = Resolve-Chemin $Output
$dossierSortie = Split-Path -Parent $Output
if (-not (Test-Path -LiteralPath $dossierSortie)) {
    New-Item -ItemType Directory -Path $dossierSortie -Force | Out-Null
}
if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }

$wix = Get-OutilWix $WixVersion

Write-Etape 'Construction de l''installateur'
Write-Info "Version  : $versionMsi"
Write-Info "Contenu  : $PublishDir"

$arguments = @(
    'build', $source,
    '-arch', 'x64',
    '-d', ('Version=' + $versionMsi),
    '-d', ('PublishDir=' + $PublishDir),
    '-d', ('IconFile=' + $icone),
    '-pdbtype', 'none',
    '-o', $Output
)

& $wix @arguments
if ($LASTEXITCODE -ne 0) {
    throw "La construction du MSI a échoué (code $LASTEXITCODE)."
}
if (-not (Test-Path -LiteralPath $Output)) {
    throw "WiX n'a produit aucun fichier : $Output"
}

$taille = [math]::Round(((Get-Item -LiteralPath $Output).Length / 1MB), 1)
Write-Info "Installateur : $Output ($taille Mo)"
