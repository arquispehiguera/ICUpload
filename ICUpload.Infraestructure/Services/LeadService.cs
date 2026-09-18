using Microsoft.Extensions.Logging;
using PacificoSeguros.Core.Entities;
using PacificoSeguros.Core.Interfaces;

namespace PacificoSeguros.Infraestructure.Services
{
    // Puerto fiel de CargarRegistro.InsertarContacto (CargasInconcertWS_C2C_2\CargasInconcertWS_C2C\CargarRegistro.asmx.cs,
    // línea 18) — un intento completo del flujo de 16 pasos de WORKER_SPEC.md. Vive en
    // Infraestructure (no en Process) porque, igual que InConcertGateway/OracleApiClient, es
    // la implementación concreta de un puerto de Core (ILeadService) que solo depende de otras
    // interfaces de Core — el repo no tiene una capa "Application" separada.
    //
    // Reglas de fidelidad importantes (ver plan + WORKER_SPEC.md):
    // - Todos los mensajes de fallo de las distintas ramas se arman byte-a-byte iguales a los
    //   del legacy, incluyendo inconsistencias reales del código original (ver comentarios
    //   puntuales más abajo) — no se "corrige" nada de esto sin haberlo confirmado antes.
    // - AddContact NUNCA reintenta por sesión expirada (bug real, replicado a propósito).
    // - AddPhone/AddContactData/AddContactToBatch/ChangePriority SÍ reintentan una vez tras
    //   relogin, si el fallo contiene el substring exacto de sesión expirada.
    // - RegistrarFin (telemetría) se llama UNA sola vez, al final real del flujo exitoso —
    //   corrige el bug legacy de doble registro (el legacy también la registraba apenas se
    //   creaba el lote, dentro del mismo loop de ExisteLote).
    public class LeadService : ILeadService
    {
        private const string SessionExpiredMarker = "Session is not logged in, you must provide a valid session to perform the operation";
        private const int MinutosLimite = 1;

        // BL_Campana.CrearImportacion — estos 5 ProcessId suprimen la alerta cuando
        // StartContactImport no llega ni a arrancar (Started == false). Si arrancó pero el
        // polling nunca llegó a "Complete", SIEMPRE alerta, sin excepción para estos 5.
        private static readonly HashSet<string> ImportSuppressedProcessIds = new(StringComparer.Ordinal)
        {
            "TmpMovistarTotalAPC", "TmpRetWinbackMtAPC", "TmpRetWinbackFijaOut", "TmpRetencionesFijaOut", "TMP_RETENCIONES_BLINDAJE"
        };

        private static readonly string[] AllowedPhoneTypes = { "HOME", "CELLULAR", "FAX", "OFFICE", "OTHER" };

        private readonly ILeadCampaignRepository _campaignRepository;
        private readonly IInConcertGateway _gateway;
        private readonly IAlertNotifier _alertNotifier;
        private readonly ILogger<LeadService> _logger;

        public LeadService(ILeadCampaignRepository campaignRepository, IInConcertGateway gateway, IAlertNotifier alertNotifier, ILogger<LeadService> logger)
        {
            _campaignRepository = campaignRepository;
            _gateway = gateway;
            _alertNotifier = alertNotifier;
            _logger = logger;
        }

        public async Task<LeadResult> ProcessAsync(LeadMessage message, CancellationToken ct = default)
        {
            // Legacy: `mensaje = "El campo campaña es obligatorio Telefono :[" + Telefonos + "]"`.
            // Campaña vacía → falla inmediata, SIN telemetría (no hay campana resuelta para loggear).
            // No es transitorio (es determinístico) → Retryable = false, judgment call: el plan
            // no lo dice explícito para este caso puntual (sí para "no configurada"), pero
            // reintentar una campaña vacía 3 veces siempre da el mismo resultado.
            if (string.IsNullOrEmpty(message.Campaign) || message.Campaign.Trim() == "")
            {
                var mensajeVacio = $"El campo campaña es obligatorio Telefono :[{message.Telefonos ?? string.Empty}]";
                await SafeNotifyAsync(mensajeVacio, "No definida", ct);
                return LeadResult.Fail(mensajeVacio, retryable: false);
            }

            // OJO: campaign se usa SIN trim en el resto del método (comparaciones exactas
            // contra los 4 nombres de campaña especiales de AddContact) — igual que el legacy.
            var campaign = message.Campaign;

            BE_Campana campana;
            try
            {
                campana = await _campaignRepository.ConfiguracionCampana(campaign, ct);
            }
            catch (Exception ex)
            {
                // No es parte del legacy (que dejaba esto escapar sin control, crasheando el
                // ASMX) — necesario acá porque ProcessAsync no puede lanzar excepciones nunca
                // (contrato del plan). Sin BE_Campana resuelta no hay Id/servidor para loggear
                // telemetría, así que no se llama RegistrarFin. Retryable = true: puede ser un
                // problema transitorio de conexión a la base fija (AppConnection).
                _logger.LogError(ex, "Excepción inesperada al resolver ConfiguracionCampana para {Campaign}", campaign);
                return LeadResult.Fail($"Fallo Inesperado: {ex.Message} / StackTrace: {ex.StackTrace}");
            }

            if (campana.Id == 0)
            {
                // Legacy: sin RegistrarFin, con alerta detallada — falla de configuración, no
                // transitoria (WORKER_SPEC #4.2 y escenario de verificación #5 del plan).
                var mensajeNoConfig = $"Campaña {campaign} no configurada en el servicio web, solicitar incluirla a GSS";
                var detalle = mensajeNoConfig + string.Format(
                    " / Datos: ContactId: {0} / campaign: {1} / Nombre: {2} / Telefonos: {3} / NameValues: {4} / Prioridad {5}: / DateScheduled: {6}",
                    message.ContactId, campaign, message.Nombre, message.Telefonos, message.NameValues, message.Prioridad,
                    message.DateScheduled.ToString("dd/MM/yyyy hh:mm:ss"));
                await SafeNotifyAsync(detalle, campaign, ct);
                return LeadResult.Fail(mensajeNoConfig, retryable: false);
            }

            var fechaInicio = DateTime.Now;
            var listoParaIniciarSesion = DateTime.Now;
            var sesionIniciada = DateTime.Now;
            var validoExistenciaImportBatch = DateTime.Now;
            var addContactFin = DateTime.Now;
            var addPhoneFin = DateTime.Now;
            var addNameValuesFin = DateTime.Now;
            var addContactToBatchFin = DateTime.Now;

            try
            {
                var tiempoLimite = DateTime.Now.AddMinutes(MinutosLimite);
                var fecha1 = DateTime.Today;
                var fecha2 = DateTime.Today.AddDays(campana.DuracionLote);
                ResolveTemplates(campana);

                var lstAreas = new List<BE_Phone>();
                if (campana.Country == "54")
                {
                    lstAreas = (await _campaignRepository.ObtenerAreas("ARGENTINA", ct)).ToList();
                }
                if (campana.Country == "52")
                {
                    lstAreas = (await _campaignRepository.ObtenerAreas("MEXICO", ct)).ToList();
                }

                listoParaIniciarSesion = DateTime.Now;
                var session = await _gateway.LoginAsync(campana, ct);
                sesionIniciada = DateTime.Now;

                if (!session.IsLoggedIn)
                {
                    var mensaje = $"Superó los {MinutosLimite} minutos, Carga Terminada (no se pudo iniciar sesion)";
                    await SafeNotifyAsync(mensaje, campaign, ct);
                    await TryRegistrarFinAsync(campana, message.ContactId, mensaje, message, fechaInicio, listoParaIniciarSesion, sesionIniciada, validoExistenciaImportBatch, addContactFin, addPhoneFin, addNameValuesFin, addContactToBatchFin, ct);
                    return LeadResult.Fail(mensaje);
                }

                var existeImportacion = 0;
                do
                {
                    existeImportacion = await _campaignRepository.ExisteImportacion(campana.ServidorSQL, campana.BaseDeDatos, campana.ImportId, ct);
                    validoExistenciaImportBatch = DateTime.Now;
                    if (existeImportacion == 0)
                    {
                        existeImportacion = await CrearImportacionAsync(session, campana, campaign, ct);
                    }
                } while (existeImportacion == 0 && tiempoLimite > DateTime.Now);

                if (existeImportacion == 0)
                {
                    var mensaje = $"Superó los {MinutosLimite} minutos, Carga Terminada (no se pudo crear importación)";
                    await SafeNotifyAsync(mensaje, campaign, ct);
                    await TryRegistrarFinAsync(campana, message.ContactId, mensaje, message, fechaInicio, listoParaIniciarSesion, sesionIniciada, validoExistenciaImportBatch, addContactFin, addPhoneFin, addNameValuesFin, addContactToBatchFin, ct);
                    return LeadResult.Fail(mensaje);
                }

                var contactId = _gateway.ResolveContactId(message.ContactId);
                var nombre = message.Nombre;

                // 3 campañas especiales reescriben SOLO el ProcessId (no el ImportId — el
                // WORKER_SPEC original se equivoca en esto, ver plan).
                var effectiveProcessId = campaign switch
                {
                    "TMP_RETENCIONES_BLINDAJE_TENTADO" or "TMP_RETENCIONES_BLINDAJE_2" or "TMP_RETENCIONES_BLINDAJE_TENTADO_FIJA" => "TMP_RETENSEG",
                    "Tmp_MovistarTotal_ABD_Reiteradas" => "Tmp_MovistarTotal_ABD",
                    _ => campana.ProcessId
                };

                // Sin retry de sesión expirada acá — bug legacy real, replicado a propósito.
                var addContact = await _gateway.AddContactAsync(session, nombre, effectiveProcessId, campana.ImportId, contactId, ct);
                if (!addContact.Ok)
                {
                    var mensaje = addContact.Info.Contains(SessionExpiredMarker, StringComparison.Ordinal)
                        ? string.Format("Fallo al añadir Contacto a Importación: {0} / SessionId: {1} / IsLoggedIn; {2} / UserName; {3} / VCC; {4}", addContact.Info, session.SessionId, session.IsLoggedIn, session.UserName, session.Vcc)
                        : string.Format("Fallo al añadir Contacto a Importación: {0} / Nombre: {1} / ContactId; {2}", addContact.Info, nombre, contactId);
                    await SafeNotifyAsync(mensaje, campaign, ct);
                    await TryRegistrarFinAsync(campana, message.ContactId, mensaje, message, fechaInicio, listoParaIniciarSesion, sesionIniciada, validoExistenciaImportBatch, addContactFin, addPhoneFin, addNameValuesFin, addContactToBatchFin, ct);
                    return LeadResult.Fail(mensaje);
                }
                addContactFin = DateTime.Now;

                // AddPhone por cada teléfono — fallos individuales no abortan el flujo. Se
                // itera por la cantidad REAL de teléfonos que trae el mensaje (no por la
                // cantidad de tipos configurados en la campaña): si el mensaje trae menos
                // teléfonos que tipos, simplemente no hay más teléfonos que procesar. Si trae
                // MÁS teléfonos que tipos configurados (índice de tipo inexistente), se trata
                // como un teléfono sin tipo válido: se loguea/alerta y se sigue con el resto.
                // Además, toda la resolución de un teléfono (incluida ExtraerTelefonoArea, que
                // puede lanzar para formatos no contemplados) está envuelta en try/catch: una
                // excepción de UN teléfono no puede abortar el contacto entero.
                var arrayTelefono = (message.Telefonos ?? string.Empty).Split(',');
                var arrayTiposTelefono = campana.TiposTelefono.Split(',');
                for (var i = 0; i < arrayTelefono.Length; i++)
                {
                    if (arrayTelefono[i].Trim() == "")
                    {
                        continue;
                    }

                    if (i >= arrayTiposTelefono.Length)
                    {
                        _logger.LogWarning("Teléfono en posición {Index} sin tipo configurado en la campaña {Campaign} para ContactId {ContactId}", i, campaign, contactId);
                        await SafeNotifyAsync(string.Format("Fallo al añadir Telefono al ContactId: {0}, Posición: {1}, Telefono: {2}, Error: No hay tipo de teléfono configurado en la campaña para esa posición", contactId, i, arrayTelefono[i]), campaign, ct);
                        continue;
                    }

                    var tipo = arrayTiposTelefono[i].Trim();
                    if (!AllowedPhoneTypes.Contains(tipo))
                    {
                        continue;
                    }

                    try
                    {
                        var addPhone = await _gateway.AddPhoneAsync(session, contactId, campana.Country, arrayTelefono[i], lstAreas, tipo, ct);
                        if (!addPhone.Ok)
                        {
                            if (addPhone.Info.Contains(SessionExpiredMarker, StringComparison.Ordinal))
                            {
                                session = await _gateway.LoginAsync(campana, ct);
                                if (session.IsLoggedIn)
                                {
                                    addPhone = await _gateway.AddPhoneAsync(session, contactId, campana.Country, arrayTelefono[i], lstAreas, tipo, ct);
                                    if (!addPhone.Ok)
                                    {
                                        await SafeNotifyAsync(string.Format("Fallo al añadir Telefono al ContactId: {0} / SessionId: {1} / IsLoggedIn; {2} / UserName; {3} / VCC; {4}", addPhone.Info, session.SessionId, session.IsLoggedIn, session.UserName, session.Vcc), campaign, ct);
                                    }
                                }
                                // Si el relogin falla (session.IsLoggedIn == false), el legacy
                                // NO manda alerta acá — se preserva ese comportamiento tal cual.
                            }
                            else
                            {
                                // Bug legacy real: el formato dice "Tipo: {1}" pero {1} y {2}
                                // reciben el mismo valor (el teléfono, no el tipo) — se
                                // preserva tal cual, no se corrige.
                                await SafeNotifyAsync(string.Format("Fallo al añadir Telefono al ContactId: {0}, Tipo: {1}, Telefono: {2}, Error: {3}", contactId, arrayTelefono[i], arrayTelefono[i], addPhone.Info), campaign, ct);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Una excepción al resolver/agregar ESTE teléfono (p.ej. un formato de
                        // área no contemplado en ExtraerTelefonoArea) no puede tirar abajo el
                        // contacto entero — se loguea, se alerta y se sigue con el resto.
                        _logger.LogError(ex, "Excepción no controlada al añadir el teléfono en posición {Index} para ContactId {ContactId} / Campaign {Campaign}", i, contactId, campaign);
                        await SafeNotifyAsync(string.Format("Fallo al añadir Telefono al ContactId: {0}, Tipo: {1}, Telefono: {2}, Error: {3}", contactId, tipo, arrayTelefono[i], ex.Message), campaign, ct);
                    }
                }
                addPhoneFin = DateTime.Now;

                // AddContactData — solo si NameValues no viene vacío.
                var nameValuesRaw = message.NameValues ?? string.Empty;
                var nameValuesEmpty = nameValuesRaw.Trim() == "";
                var addContactData = new InConcertOperationResult(false, string.Empty);
                if (!nameValuesEmpty)
                {
                    var arrayValues = nameValuesRaw.Split(',');
                    var arrayNames = campana.NameValues.Split(',');
                    var nameValueList = new List<BE_NameValue>();
                    for (var i = 0; i < arrayNames.Length; i++)
                    {
                        if (arrayValues[i].Trim() != "")
                        {
                            nameValueList.Add(new BE_NameValue(arrayNames[i].Trim(), arrayValues[i].Trim()));
                        }
                    }
                    addContactData = await _gateway.AddContactDataAsync(session, contactId, nameValueList, ct);
                    addNameValuesFin = DateTime.Now;
                    if (!addContactData.Ok && addContactData.Info.Contains(SessionExpiredMarker, StringComparison.Ordinal))
                    {
                        session = await _gateway.LoginAsync(campana, ct);
                        if (session.IsLoggedIn)
                        {
                            addContactData = await _gateway.AddContactDataAsync(session, contactId, nameValueList, ct);
                            addNameValuesFin = DateTime.Now;
                        }
                    }
                }
                else
                {
                    addNameValuesFin = addPhoneFin;
                }

                if (!nameValuesEmpty && !addContactData.Ok)
                {
                    var mensaje = addContactData.Info.Contains(SessionExpiredMarker, StringComparison.Ordinal)
                        ? string.Format("Fallo al añadir NameValues: {0} / SessionId: {1} / IsLoggedIn; {2} / UserName; {3} / VCC; {4}", addContactData.Info, session.SessionId, session.IsLoggedIn, session.UserName, session.Vcc)
                        : string.Format("Fallo al añadir NameValues: {0} / Nombre: {1} / NameValues; {2}", addContactData.Info, nombre, nameValuesRaw);
                    await SafeNotifyAsync(mensaje, campaign, ct);
                    await TryRegistrarFinAsync(campana, message.ContactId, mensaje, message, fechaInicio, listoParaIniciarSesion, sesionIniciada, validoExistenciaImportBatch, addContactFin, addPhoneFin, addNameValuesFin, addContactToBatchFin, ct);
                    return LeadResult.Fail(mensaje);
                }

                // Verifica/crea el lote — mismo patrón de loop acotado a 1 minuto.
                var existeLote = 0;
                do
                {
                    existeLote = await _campaignRepository.ExisteLote(campana.ServidorSQL, campana.BaseDeDatos, campana.BatchId, campana.ProcessId, ct);
                    if (existeLote == 0)
                    {
                        var createBatch = await _gateway.CreateBatchFromImportAsync(session, campana.BatchId, campana.ImportId, campana.ProcessId, fecha1, fecha2, message.Prioridad, ct);
                        if (createBatch.Ok)
                        {
                            // Legacy registraba telemetría acá también (doble registro) —
                            // corregido a propósito: solo se registra al final real del flujo.
                            existeLote = 1;
                        }
                    }
                } while (existeLote == 0 && tiempoLimite > DateTime.Now);

                if (existeLote == 0)
                {
                    var mensaje = $"Superó los {MinutosLimite} minutos, Carga Terminada (no se pudo crear lote)";
                    await SafeNotifyAsync(mensaje, campaign, ct);
                    await TryRegistrarFinAsync(campana, message.ContactId, mensaje, message, fechaInicio, listoParaIniciarSesion, sesionIniciada, validoExistenciaImportBatch, addContactFin, addPhoneFin, addNameValuesFin, addContactToBatchFin, ct);
                    return LeadResult.Fail(mensaje);
                }

                var addContactToBatch = await _gateway.AddContactToBatchAsync(session, campana.ProcessId, campana.BatchId, contactId, ct);
                if (!addContactToBatch.Ok && addContactToBatch.Info.Contains(SessionExpiredMarker, StringComparison.Ordinal))
                {
                    session = await _gateway.LoginAsync(campana, ct);
                    if (session.IsLoggedIn)
                    {
                        addContactToBatch = await _gateway.AddContactToBatchAsync(session, campana.ProcessId, campana.BatchId, contactId, ct);
                    }
                }

                if (!addContactToBatch.Ok)
                {
                    // Legacy no manda alerta en esta rama puntual, solo registra telemetría.
                    var mensaje = addContactToBatch.Info.Contains(SessionExpiredMarker, StringComparison.Ordinal)
                        ? string.Format("Fallo al añadir Contacto al Lote: {0} / SessionId: {1} / IsLoggedIn; {2} / UserName; {3} / VCC; {4}", addContactToBatch.Info, session.SessionId, session.IsLoggedIn, session.UserName, session.Vcc)
                        : string.Format("Fallo al añadir Contacto al Lote: {0} / Nombre: {1} / ContactId; {2}", addContactToBatch.Info, nombre, contactId);
                    await TryRegistrarFinAsync(campana, message.ContactId, mensaje, message, fechaInicio, listoParaIniciarSesion, sesionIniciada, validoExistenciaImportBatch, addContactFin, addPhoneFin, addNameValuesFin, addContactToBatchFin, ct);
                    return LeadResult.Fail(mensaje);
                }

                addContactToBatchFin = DateTime.Now;
                var mensajeExito = "Carga Terminada";

                // ChangePriority — no aborta el flujo. Legacy puede mandar HASTA DOS alertas
                // acá (una dentro del bloque de session-retry, otra en el chequeo final) —
                // se preserva tal cual, no es un error de este port.
                var setPriority = await _gateway.ChangePriorityAsync(session, campana.ProcessId, campana.BatchId, contactId, message.Prioridad, ct);
                if (!setPriority.Ok && setPriority.Info.Contains(SessionExpiredMarker, StringComparison.Ordinal))
                {
                    session = await _gateway.LoginAsync(campana, ct);
                    if (session.IsLoggedIn)
                    {
                        setPriority = await _gateway.ChangePriorityAsync(session, campana.ProcessId, campana.BatchId, contactId, message.Prioridad, ct);
                        if (!setPriority.Ok)
                        {
                            await SafeNotifyAsync(string.Format("Fallo al actualizar la prioridad: {0} / SessionId:{1} / IsLoggedIn:{2} / UserName; {3} / VCC; {4}", setPriority.Info, session.SessionId, session.IsLoggedIn, session.UserName, session.Vcc), campaign, ct);
                        }
                    }
                }
                if (!setPriority.Ok)
                {
                    await SafeNotifyAsync(string.Format("Fallo al actualizar la prioridad: {0} / ContactId:{1} / Prioridad{2}", setPriority.Info, contactId, message.Prioridad), campaign, ct);
                }

                // ReSchedule — solo si la fecha es "real" (filtro legacy contra fechas
                // default/vacías). OJO: usa el ContactId ORIGINAL del mensaje (no el
                // resuelto/generado) para la llamada al SDK, igual que el legacy — pero el
                // mensaje de error sí usa el contactId resuelto.
                if (message.DateScheduled.Year > DateTime.Now.Year - 1)
                {
                    try
                    {
                        var reprogramar = await _gateway.ReScheduleAsync(session, campana.ProcessId, campana.BatchId, message.ContactId, message.DateScheduled, ct);
                        if (!reprogramar.Ok)
                        {
                            await SafeNotifyAsync(string.Format("Fallo al reprogramar contacto:{0} / ContactId:{1} / FechaReprogramada:{2}", reprogramar.Info, contactId, message.DateScheduled), campaign, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        await SafeNotifyAsync(string.Format("Formato de Fecha incorrecto: {0}, debe ser DD/MM/YYYY HH:MM:SS / Detalle:{1}", message.DateScheduled, ex.Message), campaign, ct);
                    }
                }

                // Único registro de telemetría del camino exitoso — con try/catch + alerta,
                // igual que el legacy (acá sí, porque es exactamente lo que hacía el original
                // en su único punto de RegistrarFin "bueno").
                try
                {
                    await _campaignRepository.RegistrarFin(campana, contactId, mensajeExito, message.Prioridad, message.Telefonos ?? string.Empty, message.NameValues ?? string.Empty, fechaInicio, listoParaIniciarSesion, sesionIniciada, validoExistenciaImportBatch, addContactFin, addPhoneFin, addNameValuesFin, addContactToBatchFin, message.DateScheduled, ct);
                }
                catch (Exception ex)
                {
                    await SafeNotifyAsync(string.Format("Fallo al registrar fin de carga, ContactId {1}: {0}", ex.Message, contactId), campaign, ct);
                }

                return LeadResult.Ok();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelación real del CancellationToken recibido (shutdown del host) — el
                // único CT que fluye por todo ProcessAsync es el que pasa el llamador; los
                // timeouts de negocio (import/lote) se controlan con DateTime, no con un CT
                // interno, así que si ct.IsCancellationRequested es true acá SIEMPRE es
                // shutdown, nunca un timeout de negocio. No es un fallo del lead: no se
                // convierte en LeadResult (Polly lo reintentaría con delays no cancelables
                // contra un host que ya se está apagando, y terminaría marcando el lead como
                // fallido permanente aunque era válido). Se deja propagar — LeadResiliencePolicies
                // excluye OperationCanceledException de sus reintentos, y
                // LeadRabbitMqConsumerService.HandleMessageAsync la distingue para hacer
                // Nack(requeue:true) en vez de mandarla a LeadFail.
                throw;
            }
            catch (Exception ex)
            {
                var mensaje = string.Format("Fallo Inesperado: {0} / StackTrace: {1}", ex.Message, ex.StackTrace);
                await SafeNotifyAsync(mensaje, campaign, ct);
                await TryRegistrarFinAsync(campana, message.ContactId, mensaje, message, fechaInicio, listoParaIniciarSesion, sesionIniciada, validoExistenciaImportBatch, addContactFin, addPhoneFin, addNameValuesFin, addContactToBatchFin, ct);
                return LeadResult.Fail(mensaje);
            }
        }

        // BL_Campana.CrearImportacion — StartContactImport + polling, con la lista de 5
        // ProcessId que suprimen la alerta cuando ni siquiera arrancó.
        private async Task<int> CrearImportacionAsync(InConcertSession session, BE_Campana campana, string campaign, CancellationToken ct)
        {
            var startResult = await _gateway.StartImportAsync(session, campana, TimeSpan.FromMinutes(MinutosLimite), ct);

            if (!startResult.Started)
            {
                if (!ImportSuppressedProcessIds.Contains(campana.ProcessId))
                {
                    await SafeNotifyAsync(string.Format("No se pudo iniciar la creación de la Importación: {0}, Error: {1}", campana.ImportId, startResult.Info), campaign, ct);
                }
                return 0;
            }

            if (!startResult.Completed)
            {
                // Arrancó pero el polling nunca llegó a "Complete" — siempre alerta, sin
                // excepción para los 5 ProcessId especiales.
                await SafeNotifyAsync(string.Format("Fallo al crear Importación: {0}, Error: {1},", campana.ImportId, startResult.Info), campaign, ct);
                return 0;
            }

            return 1;
        }

        // BL_Campana.ConfiguracionCampana — templates de ImportId/BatchId con placeholders de
        // fecha. La condición se evalúa SOLO sobre el primer segmento de ImportId y se aplica
        // igual a ImportId y BatchId — así es el legacy, no es un error de este port.
        private static void ResolveTemplates(BE_Campana campana)
        {
            var importParts = campana.ImportId.Split('|');
            var batchParts = campana.BatchId.Split('|');
            if (importParts[0].Contains("yyyy"))
            {
                campana.ImportId = DateTime.Today.ToString(importParts[0]) + importParts[1];
                campana.BatchId = DateTime.Today.ToString(batchParts[0]) + batchParts[1];
            }
            else
            {
                campana.ImportId = importParts[0] + DateTime.Today.ToString(importParts[1]);
                campana.BatchId = batchParts[0] + DateTime.Today.ToString(batchParts[1]);
            }
        }

        private async Task SafeNotifyAsync(string message, string? campaign, CancellationToken ct)
        {
            try
            {
                await _alertNotifier.NotifyAsync(message, campaign, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo al notificar alerta de negocio para la campaña {Campaign}", campaign);
            }
        }

        // A diferencia del único RegistrarFin del camino exitoso (que replica el try/catch +
        // alerta del legacy), las ramas de fallo del legacy llamaban a RegistrarFin SIN
        // try/catch — si fallaba, se colaba una excepción no controlada. Acá se envuelve
        // siempre (solo log, sin alerta nueva) porque ProcessAsync no puede lanzar excepciones
        // bajo ninguna circunstancia (contrato del plan).
        private async Task TryRegistrarFinAsync(
            BE_Campana campana, string contactId, string mensaje, LeadMessage message,
            DateTime fechaInicio, DateTime listoParaIniciarSesion, DateTime sesionIniciada, DateTime validoExistenciaImportBatch,
            DateTime addContactFin, DateTime addPhoneFin, DateTime addNameValuesFin, DateTime addContactToBatchFin,
            CancellationToken ct)
        {
            try
            {
                // Mismo saneo que aplica el camino de éxito (línea ~357) antes de llamar
                // RegistrarFin — sin esto, Telefonos/NameValues en null puede tirar una
                // excepción SQL que este catch traga silenciosamente, perdiendo el registro de
                // telemetría justo en las ramas de fallo, que son las que más importa auditar.
                await _campaignRepository.RegistrarFin(campana, contactId, mensaje, message.Prioridad, message.Telefonos ?? string.Empty, message.NameValues ?? string.Empty, fechaInicio, listoParaIniciarSesion, sesionIniciada, validoExistenciaImportBatch, addContactFin, addPhoneFin, addNameValuesFin, addContactToBatchFin, message.DateScheduled, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo al registrar telemetría (RegistrarFin) para ContactId {ContactId} / Campaign {Campaign}", contactId, message.Campaign);
            }
        }
    }
}
