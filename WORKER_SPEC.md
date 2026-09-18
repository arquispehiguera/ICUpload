# Spec: Worker de carga de Leads a Inconcert

Este documento es el contrato funcional para el consumer/worker de `ICUploadLead` que reemplaza
la llamada síncrona SOAP `InsertarContacto` por un flujo asíncrono vía RabbitMQ. Está escrito para
que cualquier sesión de Claude (u otra persona) que trabaje en este repo tenga el contexto completo
sin tener que releer el código legacy desde cero.

## 1. Contexto

- Sistema legacy: `CargasInconcertWS_C2C_2` (.NET Framework 4.8, SOAP/ASMX), en
  `C:\Users\developer\Desktop\RabbitNET\CargasInconcertWS_C2C_2\`. El método a reemplazar es
  `CargarRegistro.InsertarContacto` (ver `CargasInconcert.Negocio\BL_CargaContacto.cs` — ahí está
  el flujo completo ya extraído del ASMX original, es la fuente de verdad del comportamiento).
- Existe un intento previo de rewrite en Clean Architecture, `CargasInconcertC2C` (.NET 9), en una
  rama git (`feat/cargasinconcert-c2c-pr2-infrastructure`) dentro de `RabbitNET\`. **Sus archivos
  están borrados del working tree sin commitear el borrado** — hay que decidir si se restauran
  (`git checkout`/`git restore` sobre esa rama) o se descartan; no se tocó nada ahí todavía.
  Tiene interfaces ya diseñadas (`IInConcertGateway`, `ICampaignRepository`, etc.) que sirven como
  referencia de diseño aunque `ICUploadLead` use sus propios nombres (`LeadService`,
  `IInConcertService`).
- Solo se migra el método **`InsertarContacto`** (no `InsertarContacto3` ni `InsertarContacto4` —
  decisión explícita, esas variantes quedan fuera de alcance).

## 2. Contrato RabbitMQ (ya configurado en el broker, no hay que crearlo)

- Cola principal: **`Lead`** (durable), con argumentos:
  - `x-dead-letter-exchange`: `""` (exchange por defecto)
  - `x-dead-letter-routing-key`: `LeadFail`
- Cola de fallos: **`LeadFail`** (durable) — recibe automáticamente todo lo que el worker
  `Nack`-ee con `requeue: false`. No hace falta lógica de enrutamiento manual, RabbitMQ lo hace solo.
- Usuario del broker: `admin_ic`, con permisos completos (`configure`/`write`/`read` = `.*`) sobre
  el vhost `/`. Host local: `localhost` (RabbitMQ corre como servicio de Windows en esta máquina).
- **Sin correlation ID ni cola de éxito separada**: el sistema que publica en `Lead` (la API pública,
  no incluida en este repo) no necesita enterarse del resultado. El worker es el final de la cadena.
- **No hay retry vía RabbitMQ** (nada de colas intermedias con TTL): los reintentos son en memoria,
  dentro del propio worker, antes de decidir Ack o Nack. Ver sección 5.

## 3. Mensaje de entrada (lo que publica la API pública en `Lead`)

Espejo exacto de los parámetros de `InsertarContacto`:

```json
{
  "ContactId": "string",
  "Campaign": "string",
  "Nombre": "string",
  "Telefonos": "string (CSV, ej: '1123456789,1198765432')",
  "NameValues": "string (CSV, alineado posicionalmente con los NameValues configurados en la campaña)",
  "Prioridad": 0,
  "DateScheduled": "2026-01-01T00:00:00"
}
```

## 4. Flujo de negocio requerido (puerto fiel de `BL_CargaContacto.InsertarContacto`)

Orden exacto de operaciones — **no reordenar ni "mejorar" la lógica**, es una migración 1:1:

1. Si `Campaign` viene vacío → falla inmediata, mensaje "El campo campaña es obligatorio...".
2. Resolver configuración de campaña (`ConfiguracionCampana(campaign)`). Si no existe
   (`Id == 0`) → falla, "Campaña {campaign} no configurada..., solicitar incluirla a GSS", y
   alertar (mail/notificación) — **no reintentar esto**, es un error de configuración, no transitorio.
3. Armar `ImportId`/`BatchId` a partir de templates con placeholders de fecha (`yyyy`, etc.) —
   la campaña trae el template, hay que resolverlo con la fecha de hoy.
4. Si `Country` de la campaña es `"54"` (Argentina) → resolver códigos de área. Si es `"52"`
   (México) → ídem. Otros países no cargan áreas (comportamiento legacy conocido, no es un bug a
   corregir).
5. Iniciar sesión en Inconcert (`IniciarSesion`). Si falla el login dentro de la ventana de tiempo
   límite (1 minuto en el legacy) → falla, "no se pudo iniciar sesión".
6. Verificar/crear la importación (`ExisteImportacion` → si no existe, `CrearImportacion`), con
   reintento en loop acotado por tiempo límite (1 minuto).
7. `AddContact` — con 3 campañas especiales que reescriben el `ProcessId`/`ImportId` a valores fijos
   (`TMP_RETENCIONES_BLINDAJE_TENTADO`, `TMP_RETENCIONES_BLINDAJE_2`,
   `TMP_RETENCIONES_BLINDAJE_TENTADO_FIJA` → `"TMP_RETENSEG"`; `Tmp_MovistarTotal_ABD_Reiteradas` →
   `"Tmp_MovistarTotal_ABD"`). Esto es un caso especial real del negocio, no un placeholder.
8. Si `AddContact` falla porque **la sesión expiró** (el SDK devuelve el string exacto
   `"Session is not logged in, you must provide a valid session to perform the operation"`) →
   reloguear una vez y reintentar esa misma llamada. Este patrón de "detectar sesión expirada por
   substring y relogear" se repite en CADA llamada al SDK (AddPhone, AddContactData,
   AddContactToBatch, ChangePriority) — no es exclusivo de AddContact.
9. `AddPhone` por cada teléfono en `Telefonos` (split por `,`), solo para tipos
   `HOME`/`CELLULAR`/`FAX`/`OFFICE`/`OTHER`. Fallos individuales de teléfono NO abortan el flujo —
   se loguean/alertan y se sigue con el resto.
10. `AddContactData` con los `NameValues` (split por `,`, alineados con los nombres de campo que
    trae la config de campaña) — solo si `NameValues` no viene vacío.
11. Verificar/crear el lote (`ExisteLote` → si no existe, `CreateBatchFromImport`), mismo patrón de
    loop acotado por tiempo límite que en el paso 6.
12. `AddContactToBatch`.
13. `ChangePriority` con la `Prioridad` recibida.
14. Si `DateScheduled` es una fecha "real" (año mayor a año-actual-menos-1, filtro legacy para
    descartar fechas default/vacías) → `ReSchedule`.
15. Registrar el resultado final (telemetría: 7 timestamps de cada etapa + mensaje) — **esto se
    llama SIEMPRE**, tanto en el camino de éxito como en cada rama de fallo.
16. Devolver un resultado. **Contrato de éxito/fallo: el string literal `"Carga Terminada"` es el
    ÚNICO indicador de éxito.** Cualquier otro valor de retorno (incluyendo mensajes descriptivos
    de fallo tipo `"Fallo al añadir Contacto a Importación: ..."`) es una falla de negocio —
    **el método original NUNCA lanza excepción para fallos de negocio**, siempre retorna un string.
    Si tu implementación en `ICUploadLead` modela esto como un objeto (`Ok: bool` + `Info`/`Mensaje`),
    mantené la misma semántica: fallo de negocio ≠ excepción.

## 5. Reintentos y decisión Ack/Nack en el worker

- **3 reintentos en memoria** (no vía RabbitMQ), con **1 segundo de espera entre intentos** — este
  valor no es arbitrario: hay código comentado en el legacy (`InsertarContacto4`, líneas ~817-857)
  que ya intentaba este patrón exacto (`Thread.Sleep(1000)`, 3 intentos) y nunca se terminó de activar.
- Cada intento = correr el flujo completo de la sección 4 de punta a punta (no solo el último paso
  que falló).
- Si el resultado final (tras agotar los 3 intentos) NO es "Carga Terminada" (o el objeto equivalente
  no marca éxito) → `Nack(requeue: false)` → cae solo a `LeadFail` por el DLX ya configurado.
- Si el mensaje de RabbitMQ no deserializa a un objeto válido (JSON malformado o `null`) → **no
  reintentar**, `Nack(requeue: false)` directo — no tiene sentido reprocesar un mensaje que nunca
  fue válido.
- Excepciones inesperadas (no fallos de negocio) — ej. la config de BD no resuelve, timeout de SQL —
  también cuentan como intento fallido para el contador de reintentos.

## 6. Interfaces de referencia (diseñadas en `CargasInconcertC2C`, mismo dominio de negocio)

No hace falta copiar estos nombres literalmente si `ICUploadLead` ya tiene los suyos
(`IInConcertService`, `LeadService`), pero las operaciones que exponen son las que realmente hacen
falta — usalas para no reinventar la lista de llamadas al SDK:

```csharp
Task<InConcertOperationResult> AddContactAsync(string contactId, string nombre, string processId, string importId, CancellationToken ct = default);
Task<InConcertOperationResult> AddPhoneAsync(string contactId, string country, string telefono, IReadOnlyList<Phone> areas, string tipoTelefono, CancellationToken ct = default);
Task<InConcertOperationResult> AddContactDataAsync(string contactId, IReadOnlyList<NameValue> nameValues, CancellationToken ct = default);
Task<InConcertOperationResult> CreateBatchFromImportAsync(string batchId, string importId, string processId, DateTime fechaInicio, DateTime fechaFin, int prioridad, CancellationToken ct = default);
Task<InConcertOperationResult> AddContactToBatchAsync(string processId, string batchId, string contactId, CancellationToken ct = default);
Task<InConcertOperationResult> ChangePriorityAsync(string processId, string batchId, string contactId, int prioridad, CancellationToken ct = default);
Task<InConcertOperationResult> ReScheduleAsync(string processId, string batchId, string contactId, DateTime dateScheduled, CancellationToken ct = default);
```

`InConcertOperationResult` ahí se modela como `record(bool Ok, string Info)` — `Info` es donde hay
que buscar el substring de sesión expirada para decidir si reloguear y reintentar (paso 8).

## 7. Gotchas conocidos (no "corregir" sin discutirlo primero)

- El chequeo de sesión expirada es por **substring exacto** en el campo de info del resultado del
  SDK, no por código de error tipado. Es frágil pero es el contrato real del SDK de Inconcert.
- Los países sin código de área (nada de `"54"`/`"52"`) simplemente no cargan `lstAreas` — es
  intencional en el legacy, no un bug.
- `AddPhone` fallido en un teléfono individual no aborta el contacto — se seguía cargando el resto.
- El resultado de negocio es siempre un string libre, nunca un enum/código HTTP — mantené esa
  semántica en el objeto de resultado que uses.

## 8. Explícitamente fuera de alcance

- `InsertarContacto3` e `InsertarContacto4` (variantes con lógica distinta de armado de lote).
- La API pública que publica en `Lead` (sistema aparte, no se toca desde este repo).
- Cola de éxito / notificación de vuelta — no hace falta, confirmado por el usuario.
- Reintentos vía cola intermedia con TTL — se descartó a favor de reintento en memoria (Polly u
  equivalente), más simple para el volumen actual.
