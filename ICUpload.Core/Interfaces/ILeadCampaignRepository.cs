using PacificoSeguros.Core.Entities;

namespace PacificoSeguros.Core.Interfaces
{
    // Todo el acceso SQL del feature de Leads en un solo repo cohesivo (no se separa por
    // clase BL_*/DAL_* legacy porque son lecturas/escrituras simples) — ver plan.
    public interface ILeadCampaignRepository
    {
        // SP SppGss_Ap_ConfiguracionCampanaLead (DAL_Campana.ConfiguracionCampana). Devuelve
        // una BE_Campana con Id == 0 (default) si la campaña no está configurada.
        Task<BE_Campana> ConfiguracionCampana(string campana, CancellationToken ct = default);

        // SP SppGss_ApWSObtenerAreas (DAL_Phone.ObtenerAreas). Solo se llama para
        // Country "54" (ARGENTINA) / "52" (MEXICO).
        Task<IReadOnlyList<BE_Phone>> ObtenerAreas(string pais, CancellationToken ct = default);

        // SP sppGSS_Ap_ObtenerNodoUser (DAL_Nodo.ObtenerNodoUser) — nodos de failover del
        // usuario, usados por InConcertGateway cuando el login directo contra la IP de la
        // campaña falla.
        Task<IReadOnlyList<BE_Nodo>> ObtenerNodoUser(int idUsuario, CancellationToken ct = default);

        // SELECT COUNT(1) directo contra [MMProDat].[dbo].[ContactImport] (DAL_Campana.ExisteImportacion),
        // vía AmbienteConnectionFactory (servidor/BD de la campaña, no la conexión fija).
        Task<int> ExisteImportacion(string servidor, string baseDeDatos, string importId, CancellationToken ct = default);

        // SELECT COUNT(1) directo contra [MMProDat].[dbo].[OutboundProcessBatch] (DAL_Campana.ExisteLote),
        // vía AmbienteConnectionFactory.
        Task<int> ExisteLote(string servidor, string baseDeDatos, string batchId, string processId, CancellationToken ct = default);

        // SP SppGss_ApWSRegistroLog_C2C (DAL_Campana.RegistrarFin) — telemetría de las 7
        // etapas del flujo + resultado final. Se llama una sola vez, al final real del flujo
        // (ver LeadService), corrigiendo el bug legacy de doble registro.
        Task RegistrarFin(
            BE_Campana campana,
            string contactId,
            string resultado,
            int prioridad,
            string telefono,
            string nameValue,
            DateTime fechaRegistro,
            DateTime listoParaIniciarSesion,
            DateTime sesionIniciada,
            DateTime validoExistenciaImportBatch,
            DateTime addContactFin,
            DateTime addPhoneFin,
            DateTime addNameValuesFin,
            DateTime addContactToBatchFin,
            DateTime dateScheduled,
            CancellationToken ct = default);
    }
}
