# Деплой Personal Dashboard (Windows / PowerShell на хосте 4060)
# Запуск: powershell -ExecutionPolicy Bypass -File deploy.ps1
$ErrorActionPreference = 'Stop'

$dir = 'C:\Personal\personal-dashboard'

if (-not (Test-Path (Join-Path $dir '.git'))) {
    git clone https://github.com/vitalisun2/personal-dashboard.git $dir
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Клон не удался. Если репозиторий приватный — сначала: gh auth login (или задай PAT в git credential).' -ForegroundColor Yellow
        exit 1
    }
}

Set-Location $dir
git pull --ff-only

if (-not (Test-Path (Join-Path $dir '.env'))) {
    Copy-Item .env.example .env
    Write-Host '.env создан из примера. При необходимости открой и впиши OPENROUTER_API_KEY.' -ForegroundColor Yellow
}

# По умолчанию приложение ходит в Ollama хоста (host.docker.internal:11434) — ничего менять не нужно.
docker compose up -d --build

Write-Host ''
Write-Host 'Готово. Проверка: http://localhost:8080' -ForegroundColor Green
Write-Host 'Логи: docker compose -f ' (Join-Path $dir 'docker-compose.yml') ' logs -f --tail 50' -ForegroundColor Green