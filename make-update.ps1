# Упаковка обновления: готовит папку updates с новым exe и version.json.
# Использование: pwsh -NoProfile -File .\make-update.ps1 -Version 1.3.0 -Notes "Что нового"
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Notes = "",

    # Собранная версия, из которой берётся exe
    [string]$AppDir = "dist\Kanban",

    # Куда положить готовую папку updates
    [string]$OutDir = "dist\updates"
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $AppDir "Kanban.exe"
if (-not (Test-Path $exe)) { throw "Не найден $exe — сначала соберите версию (build.ps1)" }

# Проверяем, что версия в version.json совпадает с версией самого exe,
# иначе после обновления баннер будет показываться снова.
$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
$short = (($fileVersion -split '\.') | Select-Object -First 3) -join '.'
if ($short -ne $Version) {
    throw "Версия exe ($short) не совпадает с -Version ($Version). Поднимите версию в Program.cs и Kanban.csproj, затем пересоберите."
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

$fileName = "Kanban-$Version.exe"
Copy-Item $exe (Join-Path $OutDir $fileName)

@{
    version = $Version
    file    = $fileName
    notes   = $Notes
} | ConvertTo-Json | Set-Content (Join-Path $OutDir "version.json") -Encoding UTF8

Write-Host "Обновление готово: $OutDir"
Write-Host "  $fileName ($([math]::Round((Get-Item (Join-Path $OutDir $fileName)).Length / 1MB, 1)) МБ)"
Write-Host "  version.json: $Version"
Write-Host ""
Write-Host "Скопируйте папку updates рядом с Kanban.exe на том ПК, где установлена старая версия,"
Write-Host "затем в приложении нажмите «Обновить и перезапустить» (или трей → Проверить обновления)."
