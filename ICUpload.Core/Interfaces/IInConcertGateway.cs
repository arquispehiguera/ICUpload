using PacificoSeguros.Core.Entities;

namespace PacificoSeguros.Core.Interfaces
{
    // Única interfaz que representa el SDK inConcertSDKnet hacia el resto de la aplicación.
    // Solo InConcertGateway (Infraestructure) referencia el ensamblado real — ver plan.
    public interface IInConcertGateway
    {
        // Legacy: `objBEContacto.ContactId = ContactId.Trim() != "" ? ContactId : (new CContact()).Id;`
        // — cuando el ContactId de entrada viene vacío, el SDK genera uno nuevo.
        string ResolveContactId(string contactId);

        // Login sin cache de archivo (decisión confirmada), pero CON failover de nodo:
        // intento directo contra objCampana.Ip/Password; si falla, itera
        // ILeadCampaignRepository.ObtenerNodoUser con password genérica (InConcert:VsPassword)
        // hasta el primer login exitoso. Incluye también la resolución del Vcc efectivo
        // (casos tmp_cloud* / Phoenix-OnlineBases) — ver BL_Usuario.IniciarSesion.
        Task<InConcertSession> LoginAsync(BE_Campana campana, CancellationToken ct = default);

        // Sin retry de sesión expirada (bug legacy real, replicado a propósito — ver plan).
        Task<InConcertOperationResult> AddContactAsync(InConcertSession session, string nombre, string processId, string importId, string contactId, CancellationToken ct = default);

        Task<InConcertOperationResult> AddPhoneAsync(InConcertSession session, string contactId, string country, string telefono, IReadOnlyList<BE_Phone> areas, string tipoTelefono, CancellationToken ct = default);

        Task<InConcertOperationResult> AddContactDataAsync(InConcertSession session, string contactId, IReadOnlyList<BE_NameValue> nameValues, CancellationToken ct = default);

        // Encapsula StartContactImport + polling de GetImportStatus cada 1s (BL_Campana.CrearImportacion).
        // pollTimeout acota el propio loop de polling (nunca bloquea indefinidamente); el
        // llamador (LeadService) además acota el loop externo (ExisteImportacion/CrearImportacion)
        // a 1 minuto.
        Task<ImportStartResult> StartImportAsync(InConcertSession session, BE_Campana campana, TimeSpan pollTimeout, CancellationToken ct = default);

        Task<InConcertOperationResult> CreateBatchFromImportAsync(InConcertSession session, string batchId, string importId, string processId, DateTime fechaInicio, DateTime fechaFin, int prioridad, CancellationToken ct = default);

        Task<InConcertOperationResult> AddContactToBatchAsync(InConcertSession session, string processId, string batchId, string contactId, CancellationToken ct = default);

        Task<InConcertOperationResult> ChangePriorityAsync(InConcertSession session, string processId, string batchId, string contactId, int prioridad, CancellationToken ct = default);

        // Nombre real del método SDK es ReScheduleContact — el llamador decide si corresponde
        // llamarlo (DateScheduled.Year > DateTime.Now.Year - 1).
        Task<InConcertOperationResult> ReScheduleAsync(InConcertSession session, string processId, string batchId, string contactId, DateTime dateScheduled, CancellationToken ct = default);
    }
}
