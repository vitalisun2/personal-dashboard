# Деплой Personal Dashboard (Windows / PowerShell на хосте 4060)
# Запуск: powershell -ExecutionPolicy Bypass -File deploy.ps1
$ErrorActionPreference = 'Stop'

$dir = $PSScriptRoot
$dataDir = 'C:\Main\crystal_wave\Data\personal-dashboard'
$dataFile = Join-Path $dataDir 'tasks.json'

Set-Location $dir
git pull --ff-only

if (-not (Test-Path -LiteralPath $dataFile -PathType Leaf)) {
    throw "Не найден файл данных: $dataFile. Восстанови tasks.json из archive или tasks.json.bak, затем запусти деплой снова."
}

# Docker Compose получает абсолютный путь только на этот запуск.
$env:DASHBOARD_DATA_DIR = $dataDir.Replace('\', '/')

if (-not (Test-Path (Join-Path $dir '.env'))) {
    Copy-Item .env.example .env
    Write-Host '.env создан из примера. При необходимости открой и впиши OPENROUTER_API_KEY.' -ForegroundColor Yellow
}

# По умолчанию приложение ходит в Ollama хоста (host.docker.internal:11434) — ничего менять не нужно.
docker compose up -d --build

Write-Host ''
Write-Host 'Готово. Проверка: http://localhost:8080' -ForegroundColor Green
Write-Host "Данные: $dataFile" -ForegroundColor Green
Write-Host 'Логи: docker compose -f ' (Join-Path $dir 'docker-compose.yml') ' logs -f --tail 50' -ForegroundColor Green
