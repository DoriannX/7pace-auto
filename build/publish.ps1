#requires -Version 5.1
<#
.SYNOPSIS
    Publie 7pace auto pour Windows x64 et produit son archive de release.

.DESCRIPTION
    Compile le collecteur de fond en autonome (self-contained) puis l'app Tauri, range les
    deux exécutables dans le même dossier et produit artifacts\SeptPaceAuto-win-x64.zip.
    Les deux exécutables et les scripts d’installation sont à la racine de l’archive,
    disposition vérifiée par la mise à jour automatique.

    Prérequis : SDK .NET 8, Node.js avec corepack, et Rust (stable-x86_64-pc-windows-msvc)
    avec les Build Tools Visual Studio 2022 (charge « Développement Desktop en C++ »).

.EXAMPLE
    .\build\publish.ps1
    Publie avec la version inscrite dans les projets.

.EXAMPLE
    .\build\publish.ps1 -Version v1.2.0
    Publie en forçant la version 1.2.0 (le « v » d'un tag Git est retiré).
#>
[CmdletBinding()]
param(
    # Version à graver dans les exécutables ; accepte « 1.2.0 » comme « v1.2.0 ».
    [string] $Version,

    # Dossier de sortie ; par défaut artifacts\ à la racine du dépôt.
    [string] $Output
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

$racine = Split-Path -Parent $PSScriptRoot
$projetCollecteur = Join-Path $racine 'src\SeptPaceAuto.Agent\SeptPaceAuto.Agent.csproj'
$dossierApp = Join-Path $racine 'app'

if (-not (Test-Path -LiteralPath $projetCollecteur) -or -not (Test-Path -LiteralPath (Join-Path $dossierApp 'package.json'))) {
    throw "Projets introuvables : lance ce script depuis le dépôt 7pace-auto."
}

foreach ($outil in @('dotnet', 'corepack', 'cargo')) {
    if (-not (Get-Command $outil -ErrorAction SilentlyContinue)) {
        throw "La commande « $outil » est introuvable. Voir les prérequis en tête de ce script."
    }
}

if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $racine 'artifacts' }
$Output = Resolve-Chemin $Output

$dossierPublication = Join-Path $Output 'publish'
$archive = Join-Path $Output 'SeptPaceAuto-win-x64.zip'

# La version d'un tag Git arrive sous la forme « v1.2.0 » : MSBuild et Tauri veulent « 1.2.0 ».
$versionPropre = ''
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $versionPropre = ($Version.Trim() -replace '^[vV]', '')
    if ($versionPropre -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-+].+)?$') {
        throw "Version invalide : « $Version ». Attendu par exemple 1.2.0 ou v1.2.0."
    }
}

Write-Etape 'Préparation du dossier de sortie'
if (Test-Path -LiteralPath $dossierPublication) {
    Remove-Item -LiteralPath $dossierPublication -Recurse -Force
}
New-Item -ItemType Directory -Path $dossierPublication -Force | Out-Null
Write-Info $Output
if ($versionPropre) { Write-Info "Version demandée : $versionPropre" }

Write-Etape 'Publication du collecteur (win-x64, autonome)'
$arguments = @(
    'publish', $projetCollecteur,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:SeptPaceSansCopieCollecteur=true',
    '-o', $dossierPublication,
    '--nologo'
)
if ($versionPropre) { $arguments += ('-p:Version=' + $versionPropre) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "La publication du collecteur a échoué (code $LASTEXITCODE)." }

Write-Etape 'Compilation de l''app (Tauri)'
& corepack pnpm --dir $dossierApp install --frozen-lockfile
if ($LASTEXITCODE -ne 0) { throw "L'installation des dépendances de l'app a échoué (code $LASTEXITCODE)." }

$tauri = @('pnpm', '--dir', $dossierApp, 'tauri', 'build', '--no-bundle')
$surcharge = $null
if ($versionPropre) {
    # La version passe par un fichier : un JSON en ligne de commande survit mal aux guillemets.
    $surcharge = Join-Path ([System.IO.Path]::GetTempPath()) ('7pace-version-' + [guid]::NewGuid().ToString('N') + '.json')
    Set-Content -LiteralPath $surcharge -Value ('{"version":"' + $versionPropre + '"}') -Encoding ascii
    $tauri += @('--config', $surcharge)
}
try {
    & corepack @tauri
    if ($LASTEXITCODE -ne 0) { throw "La compilation de l'app a échoué (code $LASTEXITCODE)." }
} finally {
    if ($surcharge) { Remove-Item -LiteralPath $surcharge -Force -ErrorAction SilentlyContinue }
}

$sortieTauri = Join-Path $dossierApp 'src-tauri\target\release'
$app = @('SeptPaceAuto.App.exe', 'septpaceauto-app.exe') |
    ForEach-Object { Join-Path $sortieTauri $_ } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $app) { throw "La compilation n'a pas produit SeptPaceAuto.App.exe dans $sortieTauri." }
Copy-Item -LiteralPath $app -Destination (Join-Path $dossierPublication 'SeptPaceAuto.App.exe') -Force

# Le collecteur et l'app voyagent ensemble : une archive amputée casserait la mise à jour.
foreach ($nom in @('SeptPaceAuto.Agent.exe', 'SeptPaceAuto.App.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $dossierPublication $nom))) {
        throw "La publication n'a pas produit $nom dans $dossierPublication."
    }
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $dossierPublication -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $dossierPublication -Force

Write-Etape 'Création de l''archive'
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }

# Compression par la BCL : plus rapide que Compress-Archive et présente sur 5.1 comme sur 7.
Add-Type -AssemblyName 'System.IO.Compression.FileSystem' -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $dossierPublication,
    $archive,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false) # $false : les fichiers sont à la racine de l'archive, pas dans un sous-dossier.

$taille = [math]::Round(((Get-Item -LiteralPath $archive).Length / 1MB), 1)
$nombre = (Get-ChildItem -LiteralPath $dossierPublication -Recurse -File).Count

Write-Host ''
Write-Host 'Publication terminée.' -ForegroundColor Green
Write-Info "Fichiers publiés : $nombre"
Write-Info "Archive          : $archive ($taille Mo)"
Write-Info "Installation : extraire l’archive puis lancer .\install.ps1."
