#requires -Version 5.1
<#
.SYNOPSIS
    Publie 7pace auto pour Windows x64 et produit l'archive de publication.

.DESCRIPTION
    Compile le projet en autonome (self-contained) puis range les fichiers publiés
    dans artifacts\SeptPaceAuto-win-x64.zip, à la racine de l'archive. Ce nom est
    celui attendu par la mise à jour automatique de l'application.

.EXAMPLE
    .\build\publish.ps1
    Publie avec la version inscrite dans le projet.

.EXAMPLE
    .\build\publish.ps1 -Version v1.2.0
    Publie en forçant la version 1.2.0 (le « v » d'un tag Git est retiré).
#>
[CmdletBinding()]
param(
    # Version à graver dans l'exécutable ; accepte « 1.2.0 » comme « v1.2.0 ».
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
$projet = Join-Path $racine 'src\SeptPaceAuto'

if (-not (Test-Path -LiteralPath (Join-Path $projet 'SeptPaceAuto.csproj'))) {
    throw "Projet introuvable : $projet. Lance ce script depuis le dépôt 7pace-auto."
}

if (-not (Get-Command 'dotnet' -ErrorAction SilentlyContinue)) {
    throw "La commande « dotnet » est introuvable. Installe le SDK .NET 8 puis recommence."
}

if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $racine 'artifacts' }
$Output = Resolve-Chemin $Output

$dossierPublication = Join-Path $Output 'publish'
$archive = Join-Path $Output 'SeptPaceAuto-win-x64.zip'

# La version d'un tag Git arrive sous la forme « v1.2.0 » : MSBuild veut « 1.2.0 ».
$versionPropre = ''
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $versionPropre = ($Version.Trim() -replace '^[vV]', '')
    if ($versionPropre -notmatch '^[0-9]+(\.[0-9]+){0,3}([-+].+)?$') {
        throw "Version invalide : « $Version ». Attendu par exemple 1.2.0 ou v1.2.0."
    }
}

Write-Etape 'Préparation du dossier de sortie'
if (Test-Path -LiteralPath $dossierPublication) {
    Remove-Item -LiteralPath $dossierPublication -Recurse -Force
}
New-Item -ItemType Directory -Path $dossierPublication -Force | Out-Null
Write-Info $Output

Write-Etape 'Publication .NET (win-x64, autonome)'
$arguments = @(
    'publish', $projet,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-o', $dossierPublication,
    '--nologo'
)
if ($versionPropre) {
    $arguments += ('-p:Version=' + $versionPropre)
    Write-Info "Version demandée : $versionPropre"
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "La publication a échoué (code $LASTEXITCODE)."
}

$executable = Join-Path $dossierPublication 'SeptPaceAuto.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "La publication n'a pas produit SeptPaceAuto.exe dans $dossierPublication."
}
if (-not (Test-Path -LiteralPath (Join-Path $dossierPublication 'web\index.html'))) {
    throw "Le dossier web\ est absent de la publication : l'interface ne démarrerait pas."
}

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
Write-Info "Archive : $archive ($taille Mo)"
Write-Info "Installation locale : .\build\install.ps1"
