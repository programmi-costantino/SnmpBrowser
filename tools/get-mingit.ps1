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

# Licenza e sorgenti: git e' GPLv2, quindi chi riceve l'app deve poter leggere la licenza e ottenere i sorgenti.
$tag = $release.tag_name
$info = @"
MinGit (Git for Windows) $tag
Licenza: GNU General Public License v2 - vedi COPYING-GPL-2.0.txt in questa cartella.
Progetto:  https://gitforwindows.org/
Sorgenti:  https://github.com/git-for-windows/git/tree/$tag
Archivio:  https://github.com/git-for-windows/git/archive/refs/tags/$tag.zip
Release:   https://github.com/git-for-windows/git/releases/tag/$tag
"@
Set-Content -Path (Join-Path $dest 'SOURCE-AND-LICENSE.txt') -Value $info -Encoding UTF8

try {
    Invoke-WebRequest 'https://www.gnu.org/licenses/old-licenses/gpl-2.0.txt' -OutFile (Join-Path $dest 'COPYING-GPL-2.0.txt')
} catch {
    Write-Warning 'Impossibile scaricare il testo della GPLv2: scaricalo da https://www.gnu.org/licenses/old-licenses/gpl-2.0.txt e salvalo come git\COPYING-GPL-2.0.txt.'
}

Write-Host "MinGit $($release.tag_name) installata in $dest"
Write-Host 'Licenza GPLv2 e link ai sorgenti salvati in git\SOURCE-AND-LICENSE.txt e git\COPYING-GPL-2.0.txt (inclusi nell installer).'
