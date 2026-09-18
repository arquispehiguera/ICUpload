<#
.SYNOPSIS
    Registra ICUpload.Process como Windows Service, con recovery automático.

.DESCRIPTION
    UseWindowsService() en Program.cs solo hace que el proceso SEPA comportarse como
    servicio (respeta Start/Stop del SCM) — no registra el servicio en Windows ni
    configura qué hacer si el proceso muere. Eso es lo que hace este script:
      1. New-Service (si no existe ya).
      2. sc.exe failure: reinicia el servicio automáticamente si el proceso termina
         inesperadamente (ej. RabbitMQ/SQL no estaban arriba en el primer intento de
         conexión al bootear el server, o una excepción no controlada tira el host).
      3. (Opcional) dependencia de servicio sobre RabbitMQ, si corre local en este mismo
         server como Windows Service — así el SCM no intenta arrancar ICUploadProcess
         antes de que RabbitMQ esté disponible.

    Requiere PowerShell elevado (Administrador).

.PARAMETER InstallPath
    Carpeta donde ya se copió el resultado de publish.ps1 (+ appsettings.Local.json).
    Tiene que coincidir con el basePath hardcodeado en Program.cs para el caso no-debug.

.PARAMETER RabbitMqServiceName
    Nombre del servicio de Windows de RabbitMQ en este server, si corre local. Dejar
    vacío si RabbitMQ corre en otra máquina (no se puede declarar dependencia cross-server).

.EXAMPLE
    .\deploy\install-service.ps1
    .\deploy\install-service.ps1 -RabbitMqServiceName "RabbitMQ"
#>
param(
    [string]$ServiceName = "ICUploadProcess",
    [string]$InstallPath = "C:\JobsDeployment\ICUpload",
    [string]$RabbitMqServiceName = ""
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $InstallPath "ICUpload.Process.exe"
if (-not (Test-Path $exePath)) {
    throw "No se encontró $exePath — corré publish.ps1 y copiá el resultado a $InstallPath antes de instalar el servicio."
}
if (-not (Test-Path (Join-Path $InstallPath "appsettings.Local.json"))) {
    Write-Warning "No hay appsettings.Local.json en $InstallPath — el servicio va a arrancar con los placeholders CHANGEME y va a fallar al primer login/query real. Completalo antes de arrancar el servicio."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "El servicio '$ServiceName' ya existe (estado: $($existing.Status)) — no se vuelve a crear."
} else {
    Write-Host "Creando servicio '$ServiceName' -> $exePath"
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$exePath`"" `
        -DisplayName "ICUpload - Lead to Inconcert Worker" `
        -Description "Worker que consume la cola RabbitMQ 'Lead' y carga contactos a Inconcert (reemplazo async de CargarRegistro.InsertarContacto)." `
        -StartupType Automatic
}

if ($RabbitMqServiceName -ne "") {
    Write-Host "Configurando dependencia de servicio sobre '$RabbitMqServiceName' ..."
    sc.exe config $ServiceName depend= $RabbitMqServiceName | Out-Null
}

# Recovery: reinicia a los 10s en el 1er y 2do fallo, y cada 60s de ahí en más (reset del
# contador de fallos a las 24hs sin caerse). No sustituye arreglar la causa del fallo, pero
# evita que un problema transitorio de arranque (broker/SQL no listos todavía) deje el
# servicio "Detenido" hasta que alguien lo note manualmente.
Write-Host "Configurando recovery automático (restart on failure) ..."
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/10000/restart/60000 | Out-Null

Write-Host ""
Write-Host "Listo. Para arrancarlo: Start-Service $ServiceName"
Write-Host "Para ver el estado: Get-Service $ServiceName"
Write-Host "Logs en: $InstallPath\Logs\log-<fecha>.txt (además del sink SQL configurado en appsettings.json)"
