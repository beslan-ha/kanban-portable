# Создаёт ярлык Kanban на рабочем столе (иконка берётся из Kanban.exe).
$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $dir 'Kanban.exe'

if (-not (Test-Path $exe)) {
    Write-Host 'Kanban.exe не найден рядом с этим скриптом.' -ForegroundColor Red
    exit 1
}

$folder = Split-Path -Leaf $dir
$name = if ($folder -like '*-Basic') { 'Kanban (офлайн)' } else { 'Kanban' }

$desktop = [Environment]::GetFolderPath('Desktop')
if ([string]::IsNullOrWhiteSpace($desktop)) {
    Write-Host 'Не удалось определить папку рабочего стола.' -ForegroundColor Red
    exit 1
}

$lnk = Join-Path $desktop ($name + '.lnk')
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnk)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $dir
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'Kanban — портативная доска задач'
$shortcut.WindowStyle = 1
$shortcut.Save()

Write-Host "Ярлык создан: $lnk" -ForegroundColor Green
Write-Host 'Иконку можно перетащить на панель задач или закрепить в меню «Пуск».'
