# Arranca el backend de Plura y lo reinicia si se cae.
#
# Uso normal (doble clic o desde PowerShell):
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\start-backend.ps1
#
# Opciones:
#   -Port 8787        puerto del backend
#   -NodePath <ruta>  ruta a node.exe (si no está en el PATH)
#   -FfmpegPath <ruta> ruta a ffmpeg.exe
#   -Once             arranca una sola vez, sin bucle de reinicio
#   -NoKill           no detiene un backend que ya esté escuchando en el puerto

[CmdletBinding()]
param(
  [int]$Port = 8787,
  [string]$NodePath = "",
  [string]$FfmpegPath = "",
  [switch]$Once,
  [switch]$NoKill
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$entry = Join-Path $root "src\index.js"
$outLog = Join-Path $root "server.log"
$errLog = Join-Path $root "server.err.log"
$restartLog = Join-Path $root "server.restarts.log"

function Write-RestartLog([string]$message) {
  $line = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss") + "  " + $message
  Add-Content -Path $restartLog -Value $line -Encoding UTF8
  Write-Host $line
}

function Resolve-Tool {
  param(
    [string]$Explicit,
    [string]$EnvName,
    [string]$CommandName,
    [string[]]$Candidates
  )

  if ($Explicit -and (Test-Path -LiteralPath $Explicit)) { return (Resolve-Path -LiteralPath $Explicit).Path }

  if ($EnvName) {
    $envValue = [Environment]::GetEnvironmentVariable($EnvName)
    if ($envValue -and (Test-Path -LiteralPath $envValue)) { return $envValue }
  }

  # Rutas conocidas primero: es inmediato y evita esperar a que Windows
  # recorra todo el PATH al arrancar la tarea programada.
  foreach ($candidate in $Candidates) {
    if ($candidate -and (Test-Path -LiteralPath $candidate)) {
      return (Resolve-Path -LiteralPath $candidate).Path
    }
  }

  $command = Get-Command $CommandName -ErrorAction SilentlyContinue
  if ($command -and $command.Source) { return $command.Source }

  return ""
}

$nodeCandidates = @(
  (Join-Path $env:LOCALAPPDATA "Temp\nodejs\node-v26.13.0-win-x64\node.exe"),
  "C:\Program Files\nodejs\node.exe",
  "C:\Program Files (x86)\nodejs\node.exe"
)
$node = Resolve-Tool -Explicit $NodePath -EnvName "PLURA_NODE_PATH" -CommandName "node" -Candidates $nodeCandidates

if (-not $node) {
  $found = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA "Temp\nodejs") -Filter "node.exe" -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if ($found) { $node = $found.FullName }
}

if (-not $node) {
  Write-RestartLog "ERROR: no se encontró node.exe. Instala Node.js 18+ o pasa -NodePath."
  exit 1
}

$ffmpegCandidates = @(
  (Join-Path $env:LOCALAPPDATA "Temp\ffmpeg\ffmpeg-9.0.2-essentials_build\bin\ffmpeg.exe"),
  (Join-Path $root "bin\ffmpeg.exe")
)
$ffmpeg = Resolve-Tool -Explicit $FfmpegPath -EnvName "FFMPEG_PATH" -CommandName "ffmpeg" -Candidates $ffmpegCandidates

if (-not $ffmpeg) {
  $found = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA "Temp\ffmpeg") -Filter "ffmpeg.exe" -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if ($found) { $ffmpeg = $found.FullName }
}

$env:PORT = "$Port"
$env:HOST = "0.0.0.0"

if ($ffmpeg) {
  $env:FFMPEG_PATH = $ffmpeg
}

if (-not $env:PUBLIC_BASE_URL) {
  $ip = ""
  try {
    $config = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
      Where-Object { $_.IPv4DefaultGateway -ne $null -and $_.IPv4Address } |
      Select-Object -First 1
    if ($config) { $ip = $config.IPv4Address.IPAddress }
  } catch { }

  if (-not $ip) {
    $ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
      Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } |
      Select-Object -First 1).IPAddress
  }

  if ($ip) { $env:PUBLIC_BASE_URL = "http://${ip}:$Port" }
}

# Si ya hay un backend escuchando en el puerto, lo detenemos para evitar
# el error "EADDRINUSE" y quedarnos con un proceso viejo.
if (-not $NoKill) {
  try {
    $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    foreach ($listener in $listeners) {
      $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
      if ($process -and $process.ProcessName -ieq "node") {
        Write-RestartLog "Deteniendo backend anterior (pid $($process.Id))."
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
      }
    }
  } catch { }
}

Write-RestartLog "Iniciando backend de Plura"
Write-RestartLog "  node:   $node"
Write-RestartLog "  ffmpeg: $(if ($ffmpeg) { $ffmpeg } else { 'no encontrado (el relay fallará)' })"
Write-RestartLog "  puerto: $Port"

$attempt = 0

while ($true) {
  $attempt += 1
  $startedAt = Get-Date

  # Conserva la salida del arranque anterior para diagnosticar caídas.
  if (Test-Path -LiteralPath $outLog) {
    Move-Item -LiteralPath $outLog -Destination "$outLog.prev" -Force -ErrorAction SilentlyContinue
  }
  if (Test-Path -LiteralPath $errLog) {
    Move-Item -LiteralPath $errLog -Destination "$errLog.prev" -Force -ErrorAction SilentlyContinue
  }

  Write-RestartLog "Arranque #$attempt"

  try {
    $process = Start-Process -FilePath $node `
      -ArgumentList @("`"$entry`"") `
      -WorkingDirectory $root `
      -RedirectStandardOutput $outLog `
      -RedirectStandardError $errLog `
      -PassThru `
      -WindowStyle Hidden
  } catch {
    Write-RestartLog ("No se pudo arrancar node: " + $_.Exception.Message)
    if ($Once) { exit 1 }
    Start-Sleep -Seconds 5
    continue
  }

  try {
    $process.WaitForExit()
  } catch {
    Wait-Process -Id $process.Id -ErrorAction SilentlyContinue
  }

  $exitCode = $null
  try {
    $process.Refresh()
    $exitCode = $process.ExitCode
  } catch { }
  $uptime = [int]((Get-Date) - $startedAt).TotalSeconds

  Write-RestartLog "El backend terminó (código $exitCode, $uptime s)."

  if ($Once) { exit $(if ($null -ne $exitCode) { $exitCode } else { 1 }) }

  Start-Sleep -Seconds 3
}
