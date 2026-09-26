[CmdletBinding()]
param(
    [string]$Commit = 'HEAD',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$stand = 'C:\Main\crystal_wave\personal-dashboard'
$canonicalStand = [System.IO.Path]::GetFullPath($stand).TrimEnd('\', '/')
$root = (& git rev-parse --show-toplevel).Trim()
if ([System.IO.Path]::GetFullPath($root).TrimEnd('\', '/') -ine $canonicalStand) { exit 0 }
if ((& git branch --show-current).Trim() -ne 'main') { exit 0 }

$changed = @(& git diff-tree --no-commit-id --name-only -r $Commit)
if ($LASTEXITCODE -ne 0) { throw "Cannot inspect commit $Commit" }
$needsRebuild = $changed | Where-Object {
    $_ -match '^v2/(backend/|frontend/|Dockerfile$|compose\.yml$|\.dockerignore$|PersonalDashboard\.V2\.slnx$|post-commit-rebuild\.ps1$)'
}
if (-not $needsRebuild) { exit 0 }
if ($DryRun) {
    Write-Host "Personal OS V2 post-commit: $Commit requires a rebuild."
    exit 0
}

$v2Root = Join-Path $stand 'v2'
$envFile = Join-Path $v2Root '.env'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
    # Reuse the configuration of the running V2 stand without writing secrets to disk.
    function Get-ComposeContainer([string]$service) {
        $id = @(& docker ps --filter 'label=com.docker.compose.project=personal-os-v2' --filter "label=com.docker.compose.service=$service" --format '{{.ID}}') | Select-Object -First 1
        if (-not $id) { throw "V2 $service container is unavailable. Configure v2/.env before rebuilding." }
        return (docker inspect $id | ConvertFrom-Json)[0]
    }

    function Get-ContainerEnv($container, [string]$name) {
        $prefix = $name + '='
        $entry = $container.Config.Env | Where-Object { $_.StartsWith($prefix, [System.StringComparison]::Ordinal) } | Select-Object -First 1
        if ($entry) { return $entry.Substring($prefix.Length) }
        return $null
    }

    $db = Get-ComposeContainer 'db'
    $app = Get-ComposeContainer 'app'
    if (-not $env:V2_DB_PASSWORD) {
        $password = Get-ContainerEnv $db 'POSTGRES_PASSWORD'
        if (-not $password) { throw 'The running V2 database has no password configuration.' }
        $env:V2_DB_PASSWORD = $password
    }
    if (-not $env:V2_OPENROUTER_API_KEY_FILE) {
        $secret = $app.Mounts | Where-Object { $_.Destination -eq '/run/secrets/openrouter-api-key' } | Select-Object -First 1
        if (-not $secret -or -not (Test-Path -LiteralPath $secret.Source -PathType Leaf)) {
            throw 'The running V2 app has no accessible OpenRouter key mount.'
        }
        $env:V2_OPENROUTER_API_KEY_FILE = $secret.Source
    }

    $options = @{
        'V2_OLLAMA_URL' = 'OLLAMA_URL'
        'V2_GEMMA_MODEL' = 'OLLAMA_CHAT_MODEL'
        'V2_EMBED_MODEL' = 'OLLAMA_EMBED_MODEL'
        'V2_OPENROUTER_URL' = 'OPENROUTER_URL'
        'V2_DEEPSEEK_MODEL' = 'OPENROUTER_MODEL'
    }
    foreach ($name in $options.Keys) {
        if (-not [Environment]::GetEnvironmentVariable($name)) {
            $value = Get-ContainerEnv $app $options[$name]
            if ($value) { [Environment]::SetEnvironmentVariable($name, $value, 'Process') }
        }
    }
}

# Build exactly the committed V2 tree, excluding unrelated staged or working changes.
$tempBase = Join-Path ([System.IO.Path]::GetFullPath($env:TEMP)) 'personal-os-v2-build'
$tempRoot = Join-Path $tempBase ([guid]::NewGuid().ToString('N'))
$resolvedBase = [System.IO.Path]::GetFullPath($tempBase).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$resolvedTemp = [System.IO.Path]::GetFullPath($tempRoot)
if (-not $resolvedTemp.StartsWith($resolvedBase, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe V2 build directory.'
}

New-Item -ItemType Directory -Path $resolvedTemp -Force | Out-Null
try {
    $archive = Join-Path $resolvedTemp 'v2.tar'
    $buildRoot = Join-Path $resolvedTemp 'source'
    New-Item -ItemType Directory -Path $buildRoot | Out-Null
    & git archive --format=tar --output=$archive ($Commit + ':v2')
    if ($LASTEXITCODE -ne 0) { throw "Cannot archive $Commit`:v2" }
    $windowsTar = Join-Path $env:WINDIR 'System32\tar.exe'
    & $windowsTar -xf $archive -C $buildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Cannot extract committed V2 source.' }

    $envArgs = @()
    if (Test-Path -LiteralPath $envFile -PathType Leaf) { $envArgs += @('--env-file', $envFile) }
    $buildArgs = $envArgs + @('--project-directory', $buildRoot, '-f', (Join-Path $buildRoot 'compose.yml'), 'build', 'app')
    Write-Host "Personal OS V2 post-commit: building $Commit..."
    & docker compose @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "V2 Docker build exited with code $LASTEXITCODE" }

    # Recreate from the original Compose file so container metadata keeps a stable path.
    $upArgs = $envArgs + @('--project-directory', $v2Root, '-f', (Join-Path $v2Root 'compose.yml'), 'up', '-d', '--no-build', '--no-deps', '--force-recreate', 'app')
    & docker compose @upArgs
    if ($LASTEXITCODE -ne 0) { throw "V2 Docker startup exited with code $LASTEXITCODE" }
}
finally {
    Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
}
