<#
  Pharmaly Gestion - demarre le serveur d'essai (s'il ne tourne pas deja) puis le logiciel.
#>
$ErrorActionPreference = 'Stop'
$dossier = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dossier

if (-not (Test-Path (Join-Path $dossier 'appsettings.Local.json'))) {
    Write-Host "Aucune installation d'essai ici : lancez d'abord INSTALLER-ESSAI.cmd." -ForegroundColor Red
    exit 1
}

if (-not (Get-Process -Name 'Pharmaly.Server' -ErrorAction SilentlyContinue)) {
    Write-Host 'Demarrage du serveur...'
    Start-Process -FilePath (Join-Path $dossier 'Pharmaly.Server.exe') -WorkingDirectory $dossier -WindowStyle Minimized
    $pret = $false
    for ($essai = 0; $essai -lt 30 -and -not $pret; $essai++) {
        Start-Sleep -Seconds 1
        $client = New-Object Net.Sockets.TcpClient
        try { $client.Connect('127.0.0.1', 5443); $pret = $true } catch { } finally { $client.Dispose() }
    }
    if (-not $pret) { Write-Host 'Le serveur ne repond pas sur le port 5443.' -ForegroundColor Red; exit 1 }
}

Start-Process -FilePath (Join-Path $dossier 'PharmalyGestion.exe') -WorkingDirectory $dossier
