# Deploy de ICUpload.Process como Windows Service

## Pre-requisitos en el server destino

- .NET 9 Runtime instalado (publish es framework-dependent, no self-contained).
- Acceso a `D:\Deployments\ServiciosWeb(Apis)\CargasInconcertC2CWebservice\SessionKeys\`
  (cache de session key de Inconcert, path compartido con el webservice legacy — confirmado
  que el worker corre en el mismo server o un share accesible desde ahí).
- Conectividad a RabbitMQ (colas `Lead`/`LeadFail` ya deben existir en el broker) y a los
  SQL Server de `AppConnection` y `AmbienteConnection`.

## Pasos

1. **Publicar**: `.\deploy\publish.ps1` (por default publica a `.\deploy\publish`).
2. **Copiar** el contenido publicado a `C:\JobsDeployment\ICUpload` en el server destino
   (ese path está hardcodeado en `Program.cs` como `basePath` cuando no hay debugger
   attached — si se cambia, hay que actualizar `Program.cs` también).
3. **Completar `appsettings.Local.json`** en esa carpeta con los secretos reales de ese
   entorno (no se publica automáticamente, está gitignoreado a propósito — ver
   `ICUpload.Process/appsettings.Local.json` local como referencia de qué claves lleva).
   Como mínimo falta completar `RabbitMq:Password`.
4. **Instalar el servicio**: `.\deploy\install-service.ps1` (agregar
   `-RabbitMqServiceName "RabbitMQ"` si el broker corre como Windows Service en el mismo
   server, para que el SCM no arranque el worker antes que el broker).
5. **Arrancar**: `Start-Service ICUploadProcess`.
6. **Verificar**: `Get-Service ICUploadProcess`, logs en
   `C:\JobsDeployment\ICUpload\Logs\log-<fecha>.txt`, y la tabla `GSS_LogICUpload` en SQL.

## Qué NO cubre este script

- No crea las colas `Lead`/`LeadFail` en RabbitMQ (ya existen, ver `WORKER_SPEC.md` sección 2).
- No valida que el usuario del servicio (por default `LocalSystem`) tenga permisos de
  lectura/escritura sobre el path de `SessionKeys` — si ese path es un share de red y no un
  disco local del server, puede hacer falta correr el servicio con una cuenta con permisos
  ahí (`sc.exe config ICUploadProcess obj= DOMINIO\usuario password= ...`), no lo asume este
  script por default.
- No reemplaza monitoreo real — el recovery de `sc.exe failure` reinicia el proceso, pero si
  la causa del fallo es persistente (ej. credenciales vencidas), va a reintentar en loop sin
  alertar a nadie más que los logs.
