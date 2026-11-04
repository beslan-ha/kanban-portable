# Сборка портативных версий: dist\Kanban (с WebDAV) и dist\Kanban-Basic (без синхронизации)
param(
    [string]$Configuration = "Release",
    [string]$OutDir = "dist\Kanban",
    [string]$BasicOutDir = "dist\Kanban-Basic",
    [switch]$FullOnly
)

$ErrorActionPreference = "Stop"
$dotnet = if (Test-Path "C:\Users\$env:USERNAME\.dotnet8\dotnet.exe") { "C:\Users\$env:USERNAME\.dotnet8\dotnet.exe" } else { "dotnet" }

function Add-Extras([string]$dir, [string]$readme) {
    Copy-Item $readme (Join-Path $dir "README.md") -Force
    Copy-Item "assets\kanban.ico" (Join-Path $dir "kanban.ico") -Force
    Copy-Item "assets\shortcut.ps1" $dir -Force
    Copy-Item "assets\Создать ярлык на рабочем столе.cmd" $dir -Force
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
Write-Host "Публикация в $OutDir ..."
& $dotnet publish "Kanban.csproj" -c $Configuration -r win-x64 --self-contained true -o $OutDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "Публикация в $OutDir завершилась с ошибкой" }
Add-Extras $OutDir "README.md"

if (-not $FullOnly) {
    # Вариант без синхронизации собираем из отдельной копии исходников:
    # так промежуточные файлы не конфликтуют с основной сборкой.
    $srcCopy = Join-Path $env:TEMP "kanban-build-basic"
    if (Test-Path $srcCopy) { Remove-Item $srcCopy -Recurse -Force }
    New-Item -ItemType Directory -Path $srcCopy | Out-Null

    Copy-Item "Kanban.csproj" $srcCopy -Force
    Copy-Item "*.cs" $srcCopy -Force
    Copy-Item "wwwroot" $srcCopy -Recurse -Force
    Copy-Item "assets" $srcCopy -Recurse -Force

    if (Test-Path $BasicOutDir) { Remove-Item $BasicOutDir -Recurse -Force }
    Write-Host "Публикация в $BasicOutDir (без синхронизации) ..."
    & $dotnet publish (Join-Path $srcCopy "Kanban.csproj") -c $Configuration -r win-x64 --self-contained true -o $BasicOutDir `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=false `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:NoSync=true
    if ($LASTEXITCODE -ne 0) { throw "Публикация в $BasicOutDir завершилась с ошибкой" }

    Add-Extras $BasicOutDir "README-basic.md"
}

Get-ChildItem $OutDir | Select-Object Name, @{n = 'Размер'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
if (-not $FullOnly) {
    Write-Host "Версия без синхронизации: $BasicOutDir"
}
Write-Host "Готово: $OutDir"
