<#
.SYNOPSIS
    Publica ICUpload.Process en modo Release para deployar como Windows Service.

.DESCRIPTION
    Framework-dependent (no self-contained) porque el server destino ya tiene el runtime
    de .NET instalado — mantiene el paquete de deploy chico. No fija RuntimeIdentifier
    porque la DLL de inConcertSDKnet.dll ya se verificó compatible con AnyCPU/x64
    (Assembly.LoadFrom exitoso en proceso de 64 bits), así que no hace falta forzar x86.

.PARAMETER OutputPath
    Carpeta de salida del publish. Default: .\publish (relativa a este script).

.EXAMPLE
    .\deploy\publish.ps1
    .\deploy\publish.ps1 -OutputPath C:\Temp\ICUploadPublish
#>
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "publish")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "Publicando ICUpload.Process (Release) en $OutputPath ..."
dotnet publish (Join-Path $repoRoot "ICUpload.Process\ICUpload.Process.csproj") `
    -c Release `
    -o $OutputPath `
    --no-self-contained

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish falló (exit code $LASTEXITCODE)"
}

Write-Host ""
Write-Host "Publish OK en: $OutputPath"
Write-Host ""
Write-Host "IMPORTANTE - antes de instalar el servicio:"
Write-Host "  1. Copiar appsettings.Local.json (con los secretos reales de este entorno) a $OutputPath"
Write-Host "     - No se publica automaticamente porque esta gitignoreado a proposito."
Write-Host "  2. Completar RabbitMq:Password en ese appsettings.Local.json (no tiene valor real todavia)."
Write-Host "  3. Confirmar que este server tiene acceso a D:\Deployments\ServiciosWeb(Apis)\CargasInconcertC2CWebservice\SessionKeys\"
Write-Host "     (cache de session key de Inconcert, path compartido con el webservice legacy)."
