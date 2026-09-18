using inConcertSDKnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PacificoSeguros.Core.Entities;
using PacificoSeguros.Core.Interfaces;

namespace PacificoSeguros.Infraestructure.Services
{
    // Única clase que referencia inConcertSDKnet (CSession, OEManager, CPhone, CContactData,
    // PhoneType, DuplicateCheck, DuplicateSolver, ImportationStatus, CContact) — ver plan.
    // Firmas de SDK confirmadas por reflexión contra inConcertSDKnet.dll (no había fuente
    // disponible), no solo inferidas del legacy.
    public class InConcertGateway : IInConcertGateway
    {
        private const string OutboundEngineClient = "outboundengine";

        // ProcessId que fuerzan Vcc = "tmp2" al resolver la sesión — BL_Usuario.IniciarSesion.
        private static readonly HashSet<string> PhoenixOnlineProcessIds = new(StringComparer.Ordinal)
        {
            "Tmp_Fija_OnlineBases", "TMP_CAEQ_PHOENIX_1", "TMP_CAEQ_PHOENIX_2",
            "TMP_CAEQ_PHOENIX_3", "TMP_CAEQ_PHOENIX_BO", "Tmp_Fija_OnLine", "Tmp_Movil_Online_2"
        };

        private static readonly HashSet<string> TmpCloudVccs = new(StringComparer.Ordinal)
        {
            "tmp_cloud", "tmp_cloud2", "tmp_cloud3", "tmp_cloud4"
        };

        private readonly ILeadCampaignRepository _campaignRepository;
        private readonly ILogger<InConcertGateway> _logger;
        private readonly string _vsUser;
        private readonly string _vsPassword;
        private readonly int _vsPort;

        public InConcertGateway(ILeadCampaignRepository campaignRepository, ILogger<InConcertGateway> logger, IConfiguration configuration)
        {
            _campaignRepository = campaignRepository;
            _logger = logger;
            _vsUser = configuration["InConcert:VsUser"] ?? throw new ArgumentNullException("InConcert:VsUser");
            _vsPassword = configuration["InConcert:VsPassword"] ?? throw new ArgumentNullException("InConcert:VsPassword");
            _vsPort = configuration.GetValue<int>("InConcert:VsPort");
        }

        // Legacy: `objBEContacto.ContactId = ContactId.Trim() != "" ? ContactId : (new CContact()).Id;`
        // — ojo, usa el ContactId ORIGINAL (sin trim) cuando no está vacío, no la versión
        // trimeada; se preserva tal cual.
        public string ResolveContactId(string contactId) =>
            !string.IsNullOrEmpty(contactId) && contactId.Trim() != "" ? contactId : new CContact().Id;

        // BL_Usuario.IniciarSesion — sin cache de archivo (decisión confirmada), con failover
        // de nodo preservado. Resuelve primero el Vcc efectivo (casos tmp_cloud* y
        // Phoenix/OnlineBases), intenta login directo contra la IP+Password de la campaña y,
        // si falla, itera ObtenerNodoUser con la password genérica de config hasta el primer
        // login exitoso.
        public async Task<InConcertSession> LoginAsync(BE_Campana campana, CancellationToken ct = default)
        {
            CSession session;
            if (TmpCloudVccs.Contains(campana.Vcc))
            {
                session = new CSession(_vsUser, "tmp");
            }
            else
            {
                var vcc = PhoenixOnlineProcessIds.Contains(campana.ProcessId) ? "tmp2" : campana.Vcc;
                session = new CSession(_vsUser, vcc);
            }

            var loginResult = session.Login(campana.Password, OutboundEngineClient, campana.Ip, _vsPort);
            if (loginResult.OK)
            {
                return ToInConcertSession(session);
            }

            var nodos = await _campaignRepository.ObtenerNodoUser(campana.IdUsuario, ct);
            foreach (var nodo in nodos)
            {
                loginResult = session.Login(_vsPassword, OutboundEngineClient, nodo.Ip, _vsPort);
                if (loginResult.OK)
                {
                    break;
                }
                _logger.LogWarning("Login fallido contra nodo de failover — Ip: {Ip} / Vcc: {Vcc} / Info: {Info}", nodo.Ip, session.VCC, loginResult.Info);
            }

            return ToInConcertSession(session);
        }

        private static InConcertSession ToInConcertSession(CSession session) => new()
        {
            Handle = session,
            IsLoggedIn = session.IsLoggedIn,
            SessionId = session.SessionId ?? string.Empty,
            UserName = session.UserName ?? string.Empty,
            Vcc = session.VCC ?? string.Empty
        };

        private static CSession Unwrap(InConcertSession session) => (CSession)session.Handle;

        // Sin retry de sesión expirada — bug legacy real, replicado a propósito (ver plan).
        public Task<InConcertOperationResult> AddContactAsync(InConcertSession session, string nombre, string processId, string importId, string contactId, CancellationToken ct = default)
        {
            var result = OEManager.AddContact(Unwrap(session), nombre, "", "", "", processId, importId, false, false, contactId, "");
            return Task.FromResult(new InConcertOperationResult(result.OK, result.Info));
        }

        // BL_Phone.CrearTelefono + ExtraerTelefonoArea — únicas clases que construyen un
        // CPhone del SDK, absorbidas acá porque son SDK-específicas.
        public Task<InConcertOperationResult> AddPhoneAsync(InConcertSession session, string contactId, string country, string telefono, IReadOnlyList<BE_Phone> areas, string tipoTelefono, CancellationToken ct = default)
        {
            var phone = CrearTelefono(contactId, country, telefono, areas, tipoTelefono);
            var result = OEManager.AddPhone(Unwrap(session), phone);
            return Task.FromResult(new InConcertOperationResult(result.OK, result.Info));
        }

        public Task<InConcertOperationResult> AddContactDataAsync(InConcertSession session, string contactId, IReadOnlyList<BE_NameValue> nameValues, CancellationToken ct = default)
        {
            var contactData = new CContactData[nameValues.Count];
            for (var i = 0; i < nameValues.Count; i++)
            {
                contactData[i] = new CContactData(nameValues[i].Name, nameValues[i].Value, 1);
            }
            var result = OEManager.AddContactData(Unwrap(session), contactId, contactData);
            return Task.FromResult(new InConcertOperationResult(result.OK, result.Info));
        }

        // BL_Campana.CrearImportacion — arma la ruta del CSV relativa al BaseDirectory actual
        // (reemplaza el path legacy hardcodeado del webservice viejo), llama StartContactImport
        // y, si arrancó, poll-ea GetImportStatus cada 1s hasta Complete/Unknown/Aborted, con un
        // límite de tiempo propio (pollTimeout, pasado por el llamador — LeadService usa el
        // mismo 1 minuto que sus loops de ExisteImportacion/ExisteLote) para que este método
        // SIEMPRE termine y devuelva un resultado en vez de bloquear indefinidamente si
        // Inconcert nunca llega a un estado terminal.
        public async Task<ImportStartResult> StartImportAsync(InConcertSession session, BE_Campana campana, TimeSpan pollTimeout, CancellationToken ct = default)
        {
            var csv = AppContext.BaseDirectory + campana.FicheroCSV.Replace(@"D:\ProduccionW\Aplicaciones\CargasInconcertWebService\", "");
            var startResult = OEManager.StartContactImport(Unwrap(session), csv, campana.ImportId, "GSS", campana.FormatId, DuplicateCheck.None, DuplicateSolver.KeepNew, false, false);

            if (!startResult.OK)
            {
                return new ImportStartResult(false, false, startResult.Info);
            }

            var deadline = DateTime.UtcNow.Add(pollTimeout);
            ImportationStatus status;
            do
            {
                await Task.Delay(1000, ct);
                status = OEManager.GetImportStatus(Unwrap(session), campana.ImportId);
            } while (status != ImportationStatus.Complete && status != ImportationStatus.Unknown && status != ImportationStatus.Aborted && DateTime.UtcNow < deadline);

            if (status != ImportationStatus.Complete && status != ImportationStatus.Unknown && status != ImportationStatus.Aborted)
            {
                return new ImportStartResult(true, false, $"Timeout esperando estado terminal de importación tras {pollTimeout.TotalSeconds:0}s (último estado: {status})");
            }

            return new ImportStartResult(true, status == ImportationStatus.Complete, status.ToString());
        }

        public Task<InConcertOperationResult> CreateBatchFromImportAsync(InConcertSession session, string batchId, string importId, string processId, DateTime fechaInicio, DateTime fechaFin, int prioridad, CancellationToken ct = default)
        {
            var result = OEManager.CreateBatchFromImport(Unwrap(session), batchId, importId, processId, fechaInicio, fechaFin, false, prioridad, 1, "", false);
            return Task.FromResult(new InConcertOperationResult(result.OK, result.Info));
        }

        public Task<InConcertOperationResult> AddContactToBatchAsync(InConcertSession session, string processId, string batchId, string contactId, CancellationToken ct = default)
        {
            var result = OEManager.AddContactToBatch(Unwrap(session), processId, batchId, contactId);
            return Task.FromResult(new InConcertOperationResult(result.OK, result.Info));
        }

        // ChangePriority del SDK toma la prioridad como Int64 (confirmado por reflexión), no
        // Int32 — LeadMessage.Prioridad es int, se ensancha acá.
        public Task<InConcertOperationResult> ChangePriorityAsync(InConcertSession session, string processId, string batchId, string contactId, int prioridad, CancellationToken ct = default)
        {
            var result = OEManager.ChangePriority(Unwrap(session), processId, batchId, contactId, prioridad);
            return Task.FromResult(new InConcertOperationResult(result.OK, result.Info));
        }

        public Task<InConcertOperationResult> ReScheduleAsync(InConcertSession session, string processId, string batchId, string contactId, DateTime dateScheduled, CancellationToken ct = default)
        {
            var result = OEManager.ReScheduleContact(Unwrap(session), processId, batchId, contactId, null!, dateScheduled, false);
            return Task.FromResult(new InConcertOperationResult(result.OK, result.Info));
        }

        // BL_Phone.CrearTelefono
        private static CPhone CrearTelefono(string contactId, string country, string telefono, IReadOnlyList<BE_Phone> lstAreas, string tipoTelefono)
        {
            var telefonoArea = ExtraerTelefonoArea(country, telefono.Trim(), lstAreas);
            var numero = telefonoArea.Split('-')[0];
            var area = telefonoArea.Split('-')[1];

            return tipoTelefono.Trim().ToUpper() switch
            {
                "HOME" => new CPhone(contactId, PhoneType.HOME, country, area, numero, "-1", ""),
                "CELLULAR" => new CPhone(contactId, PhoneType.CELLULAR, country, area, numero, "-1", ""),
                "FAX" => new CPhone(contactId, PhoneType.FAX, country, area, numero, "-1", ""),
                "OFFICE" => new CPhone(contactId, PhoneType.OFFICE, country, area, numero, "-1", ""),
                "OTHER" => new CPhone(contactId, PhoneType.OTHER, country, area, numero, "-1", ""),
                _ => new CPhone()
            };
        }

        // BL_Phone.ExtraerTelefonoArea — porteado 1:1, incluidas las ramas de Perú (51) y
        // España (34) que el legacy soporta aunque WORKER_SPEC solo mencione 54/52 (lstAreas
        // llega vacía para cualquier país que no sea 54/52, pero estas dos ramas no la usan).
        private static string ExtraerTelefonoArea(string country, string telefonoIn, IReadOnlyList<BE_Phone> lstAreas)
        {
            var telefono = "";
            var area = "";
            var tel = telefonoIn.Trim().Replace("-", "").Replace("_", "").Replace(" ", "");

            if (country == "54") // ARGENTINA
            {
                if (tel.Substring(0, 2) == "54")
                {
                    tel = tel.Substring(2, tel.Length - 2);
                }
                if (tel.Substring(0, 1) == "9")
                {
                    // Bug fix: la aritmética original (Substring(2, tel.Length - 1)) pide
                    // startIndex(2) + length(tel.Length-1) = tel.Length+1, que excede el
                    // string en 1 y tira ArgumentOutOfRangeException SIEMPRE que se ejercita
                    // esta rama — confirmado que el legacy real (BL_Phone.ExtraerTelefonoArea)
                    // tiene el mismo error matemático, así que no hay "comportamiento correcto"
                    // que portar 1:1 acá. Se corrige la aritmética para que sea consistente con
                    // el patrón usado en la rama "54" de arriba (startIndex=N, length=Length-N).
                    tel = tel.Substring(2, tel.Length - 2);
                }
                if (tel.Substring(0, 1) == "0")
                {
                    // Mismo bug matemático que la rama "9" de arriba, misma corrección.
                    tel = tel.Substring(2, tel.Length - 2);
                }
                if (tel.Substring(0, 2) == "11")
                {
                    tel = tel.Substring(2, tel.Length - 2);
                    if (tel.Substring(0, 2) == "11")
                    {
                        tel = tel.Substring(2, tel.Length - 2);
                    }
                    if (tel.Substring(0, 2) == "15")
                    {
                        tel = tel.Substring(2, tel.Length - 2);
                    }
                    telefono = tel;
                    area = "11";
                }
                else if (tel.Substring(0, 2) == "15")
                {
                    tel = tel.Substring(2, tel.Length - 2);
                    if (tel.Substring(0, 2) == "11")
                    {
                        tel = tel.Substring(2, tel.Length - 2);
                    }
                    if (tel.Substring(0, 2) == "15")
                    {
                        tel = tel.Substring(2, tel.Length - 2);
                    }
                    telefono = tel;
                    area = "15";
                }
                else
                {
                    foreach (var item in lstAreas)
                    {
                        if (tel.Substring(0, item.Area.Length) == item.Area)
                        {
                            if (tel.Substring(0, 2) == "15")
                            {
                                tel = tel.Substring(2, tel.Length - 2);
                            }
                            telefono = tel.Substring(item.Area.Length, tel.Length - item.Area.Length);
                            area = item.Area + "15";
                            break;
                        }
                    }
                    if (area == "")
                    {
                        telefono = tel;
                        area = "15";
                    }
                }
            }
            else if (country == "51") // PERU
            {
                if (tel.Substring(0, 1) == "9")
                {
                    telefono = tel.Substring(1, tel.Length - 1);
                    area = "9";
                }
                else if (tel.Substring(0, 1) == "1")
                {
                    telefono = tel.Substring(1, tel.Length - 1);
                    area = "1";
                }
                else
                {
                    telefono = tel.Substring(2, tel.Length - 2);
                    area = tel.Substring(0, 2);
                }
            }
            else if (country == "34") // ESPAÑA
            {
                telefono = tel.Substring(3, tel.Length - 3);
                area = tel.Substring(0, 3);
            }
            else if (country == "52") // MEXICO
            {
                foreach (var item in lstAreas.OrderByDescending(x => x.Area.Length))
                {
                    if (tel.StartsWith(item.Area))
                    {
                        area = item.Area;
                        telefono = tel.Substring(item.Area.Length);
                        break;
                    }
                }
                if (area == "")
                {
                    area = tel.Substring(0, 2);
                    telefono = tel.Substring(2);
                }
            }

            return telefono + "-" + area;
        }
    }
}
