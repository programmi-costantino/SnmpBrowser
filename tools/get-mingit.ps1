# Scarica l'ultima MinGit (versione portabile di git per Windows) nella cartella "git" del progetto.
# Il csproj la copia accanto all'eseguibile, cosi' l'app non richiede git installato.
#
# Uso (da PowerShell, nella cartella SnmpBrowser):   .\tools\get-mingit.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'git'

$release = Invoke-RestMethod 'https://api.github.com/repos/git-for-windows/git/releases/latest' -Headers @{ 'User-Agent' = 'snmpbrowser' }
$asset = $release.assets | Where-Object { $_.name -match '^MinGit-.*-64-bit\.zip$' -and $_.name -notmatch 'busybox' } | Select-Object -First 1
if (-not $asset) { throw 'MinGit 64-bit non trovata nell ultima release.' }

$zip = Join-Path $env:TEMP $asset.name
Write-Host "Scarico $($asset.name) ..."
Invoke-WebRequest $asset.browser_download_url -OutFile $zip

if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Expand-Archive $zip -DestinationPath $dest
Remove-Item $zip

Write-Host "MinGit $($release.tag_name) installata in $dest"
Write-Host 'Nota: git e'' distribuito con licenza GPLv2. Includi il file di licenza e il link al sorgente quando pubblichi l app.'
