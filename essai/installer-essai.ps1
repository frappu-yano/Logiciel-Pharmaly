<#
  Pharmaly Gestion - installation d'ESSAI sur un PC Windows (donnees fictives uniquement).

  Prerequis : PostgreSQL installe sur ce PC (installateur officiel), avec le mot de passe
  du compte "postgres" choisi pendant son installation.

  Ce script, a lancer une seule fois depuis le dossier qui contient Pharmaly.Server.exe et
  PharmalyGestion.exe (double-clic sur INSTALLER-ESSAI.cmd) :
    1. cree une base "pharmaly_essai" et son compte ;
    2. charge 2 000 produits fictifs avec du stock ;
    3. cree le site d'essai, un compte de caisse et un administrateur ;
    4. ecrit les identifiants et l'empreinte du serveur dans ACCES-ESSAI.txt ;
    5. demarre le serveur et le logiciel.
  Il ne touche a aucune autre base et ne lit rien de BML.
#>
param(
    [string] $PostgresPassword,
    [string] $PostgresBin,
    [switch] $SansLancement
)

$ErrorActionPreference = 'Stop'
$dossier = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dossier

function Etape([string] $texte) { Write-Host ''; Write-Host "==> $texte" -ForegroundColor Cyan }
function Echec([string] $texte) { Write-Host ''; Write-Host "ERREUR : $texte" -ForegroundColor Red; exit 1 }

foreach ($fichier in @('Pharmaly.Server.exe', 'PharmalyGestion.exe')) {
    if (-not (Test-Path (Join-Path $dossier $fichier))) { Echec "Fichier $fichier introuvable dans $dossier. Decompressez toute l'archive dans un meme dossier." }
}
if (Test-Path (Join-Path $dossier 'appsettings.Local.json')) {
    Echec "Une installation d'essai existe deja dans ce dossier (appsettings.Local.json). Utilisez DEMARRER-ESSAI.cmd, ou supprimez ce fichier et la base pharmaly_essai pour tout refaire."
}

Etape 'Recherche de PostgreSQL'
if (-not $PostgresBin) {
    $racine = Join-Path $env:ProgramFiles 'PostgreSQL'
    if (Test-Path $racine) {
        $version = Get-ChildItem $racine -Directory | Where-Object { $_.Name -match '^\d+$' } | Sort-Object { [int] $_.Name } -Descending | Select-Object -First 1
        if ($version) { $PostgresBin = Join-Path $version.FullName 'bin' }
    }
}
if (-not $PostgresBin -or -not (Test-Path (Join-Path $PostgresBin 'psql.exe'))) {
    Echec "PostgreSQL est introuvable. Installez-le (https://www.postgresql.org/download/windows/) puis relancez."
}
$psql = Join-Path $PostgresBin 'psql.exe'
Write-Host "PostgreSQL : $PostgresBin"

if (-not $PostgresPassword) {
    $saisie = Read-Host -AsSecureString 'Mot de passe du compte "postgres" (choisi a l installation de PostgreSQL)'
    $PostgresPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($saisie))
}

function Psql([string] $sql) {
    # Les messages de psql sur la sortie d'erreur ne doivent pas interrompre le script :
    # seul le code de retour compte.
    $ErrorActionPreference = 'Continue'
    $env:PGPASSWORD = $PostgresPassword
    $sortie = & $psql -h 127.0.0.1 -U postgres -d postgres -v ON_ERROR_STOP=1 -tAc $sql 2>&1
    $code = $LASTEXITCODE
    Remove-Item Env:\PGPASSWORD
    if ($code -ne 0) { Echec "PostgreSQL a refuse la commande : $sortie" }
    return $sortie
}

Etape 'Creation de la base d essai'
if ((Psql "SELECT 1 FROM pg_database WHERE datname = 'pharmaly_essai'") -eq '1') {
    Echec "La base pharmaly_essai existe deja. Supprimez-la (pgAdmin) ou utilisez DEMARRER-ESSAI.cmd."
}
$motDePasseBase = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char] $_ })
if ((Psql "SELECT 1 FROM pg_roles WHERE rolname = 'pharmaly_essai'") -eq '1') {
    Psql "ALTER ROLE pharmaly_essai LOGIN PASSWORD '$motDePasseBase'" | Out-Null
} else {
    Psql "CREATE ROLE pharmaly_essai LOGIN PASSWORD '$motDePasseBase'" | Out-Null
}
Psql "CREATE DATABASE pharmaly_essai OWNER pharmaly_essai" | Out-Null

$reglages = @{
    ConnectionStrings = @{ Pharmaly = "Host=127.0.0.1;Database=pharmaly_essai;Username=pharmaly_essai;Password=$motDePasseBase" }
    Postgres = @{ BinDirectory = $PostgresBin }
} | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText((Join-Path $dossier 'appsettings.Local.json'), $reglages, (New-Object Text.UTF8Encoding $false))

function Serveur([string[]] $arguments) {
    $ErrorActionPreference = 'Continue'
    $sortie = & (Join-Path $dossier 'Pharmaly.Server.exe') @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { Echec "Pharmaly.Server.exe $($arguments -join ' ') : $sortie" }
    return $sortie
}
function Secret($sortie) {
    $ligne = $sortie | Where-Object { "$_" -match '^SECRET=' } | Select-Object -First 1
    if (-not $ligne) { Echec 'Secret non affiche par le serveur.' }
    return "$ligne".Substring(7)
}

Etape 'Chargement des donnees fictives (environ 30 secondes)'
Serveur @('--donnees-fictives', '2000') | Out-Null
Serveur @('--initialiser-site', 'ESSAI', 'Pharmaly - site d essai', 'MAGASIN') | Out-Null
$motDePasseCaisse = Secret (Serveur @('--creer-compte-caisse', 'caisse1', 'Caisse 1', 'ESSAI'))
$codeAdmin = Secret (Serveur @('--creer-administrateur', '1', 'Responsable Essai'))
Serveur @('--stock-fictif', 'ESSAI', '50', '1') | Out-Null

$empreinte = (Serveur @('--empreinte')) | Where-Object { "$_".Trim() -match '^([0-9A-F]{4} ){7}[0-9A-F]{4}$' } | ForEach-Object { "   " + "$_".Trim() }
if (@($empreinte).Count -ne 2) { Echec "Empreinte du serveur illisible." }

$acces = @"
PHARMALY GESTION - ESSAI (donnees fictives)
===========================================

1) Premiere ouverture du logiciel : il affiche l'empreinte du serveur.
   Verifiez qu'elle est identique a celle-ci, puis appuyez sur F8 :

$($empreinte -join "`r`n")

2) Compte de caisse (une seule fois par poste)
   Compte       : caisse1
   Mot de passe : $motDePasseCaisse

3) Identification de l'employe (a chaque prise de poste)
   Matricule    : 1
   Code         : $codeAdmin

Codes-barres fictifs : ils commencent par 200 (exemple : 2000000000015).
Dans le logiciel, F4 (Produits) permet de chercher un produit par son nom pour voir son code.

Pour relancer plus tard : double-clic sur DEMARRER-ESSAI.cmd.
Ces identifiants ne valent que pour cet essai. Ne mettez jamais de vraies donnees dans cette base.
"@
$fichierAcces = Join-Path $dossier 'ACCES-ESSAI.txt'
[IO.File]::WriteAllText($fichierAcces, $acces, (New-Object Text.UTF8Encoding $true))

Etape 'Installation terminee'
Write-Host "Identifiants et empreinte enregistres dans : $fichierAcces" -ForegroundColor Green

if (-not $SansLancement) {
    & (Join-Path $dossier 'demarrer-essai.ps1')
    Start-Process notepad.exe -ArgumentList "`"$fichierAcces`""
}
