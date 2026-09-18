using Dapper;
using Microsoft.Extensions.Logging;
using PacificoSeguros.Core.Entities;
using PacificoSeguros.Core.Interfaces;
using PacificoSeguros.Infraestructure.Data;
using System.Data;

namespace PacificoSeguros.Infraestructure.Repositories
{
    // Todo el acceso SQL del feature de Leads en un repo cohesivo — mismo estilo que
    // InteraccionRepository.cs (Dapper + DbContextApp para la conexión fija). Las
    // lecturas/escrituras de la campaña puntual (ExisteImportacion/ExisteLote, que viven en el
    // servidor/BD propio de cada campaña) usan AmbienteConnectionFactory en su lugar — ver
    // DAL_Campana.ExisteImportacion/ExisteLote en el legacy, que reciben Servidor/BD como
    // parámetros explícitos para armar una conexión CN2 distinta de la fija (CN).
    public class LeadCampaignRepository : ILeadCampaignRepository
    {
        private readonly DbContextApp _context;
        private readonly IAmbienteConnectionFactory _ambienteConnectionFactory;
        private readonly ILogger<LeadCampaignRepository> _logger;

        public LeadCampaignRepository(DbContextApp context, IAmbienteConnectionFactory ambienteConnectionFactory, ILogger<LeadCampaignRepository> logger)
        {
            _context = context;
            _ambienteConnectionFactory = ambienteConnectionFactory;
            _logger = logger;
        }

        // DAL_Campana.ConfiguracionCampana — SppGss_Ap_ConfiguracionCampanaLead. Las columnas
        // del SP (Id, ProcessId, FormatId, FicheroCSV, ServidorSQL, BaseDeDatos, DuracionLote,
        // ImportId, BatchId, Country, TiposTelefono, NameValues, IdUsuario, Usuario, Vcc,
        // Password, SessionId, IdNodo, Ip, Puerto) coinciden 1:1 con las propiedades de
        // BE_Campana — confirmado en el DAL legacy, que las lee por nombre (sqlDR["Id"], etc.).
        // Sin filas → BE_Campana con Id == 0 (mismo contrato que el legacy: "objBECampana.Id != 0").
        public async Task<BE_Campana> ConfiguracionCampana(string campana, CancellationToken ct = default)
        {
            using var connection = _context.CreateConnection();
            connection.Open();
            var command = new CommandDefinition(
                "SppGss_Ap_ConfiguracionCampanaLead",
                new { Campana = campana },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct);
            var result = await connection.QueryFirstOrDefaultAsync<BE_Campana>(command);
            return result ?? new BE_Campana();
        }

        // DAL_Phone.ObtenerAreas — SppGss_ApWSObtenerAreas. El SP devuelve columnas
        // "Prefijo"/"Localidad" (confirmado en el DAL legacy, acceso por nombre), que no
        // coinciden con las propiedades de BE_Phone (Area/Localidad) — de ahí el mapeo manual
        // en vez de un QueryAsync<BE_Phone> directo.
        private sealed record AreaRow(string Prefijo, string Localidad);

        public async Task<IReadOnlyList<BE_Phone>> ObtenerAreas(string pais, CancellationToken ct = default)
        {
            using var connection = _context.CreateConnection();
            connection.Open();
            var command = new CommandDefinition(
                "SppGss_ApWSObtenerAreas",
                new { Pais = pais },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct);
            var rows = await connection.QueryAsync<AreaRow>(command);
            return rows.Select(r => new BE_Phone { Area = r.Prefijo, Localidad = r.Localidad }).ToList();
        }

        // DAL_Nodo.ObtenerNodoUser — sppGSS_Ap_ObtenerNodoUser. El DAL legacy lee las columnas
        // por posición (sqlDR[0]/sqlDR[1]), no por nombre, así que no hay confirmación directa
        // de que se llamen literalmente "Ip"/"Puerto" — se asume que sí (coincide con las
        // propiedades de BE_Nodo) porque es el nombrado consistente con el resto de los SPs de
        // este mismo dominio (ver ConfiguracionCampana). Si el SP real usa otros alias,
        // ajustar este mapeo.
        public async Task<IReadOnlyList<BE_Nodo>> ObtenerNodoUser(int idUsuario, CancellationToken ct = default)
        {
            using var connection = _context.CreateConnection();
            connection.Open();
            var command = new CommandDefinition(
                "sppGSS_Ap_ObtenerNodoUser",
                new { IdUsuario = idUsuario },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct);
            var rows = await connection.QueryAsync<BE_Nodo>(command);
            return rows.ToList();
        }

        // DAL_Campana.ExisteImportacion — SELECT COUNT(1) directo contra
        // [MMProDat].[dbo].[ContactImport], en el servidor/BD propio de la campaña (CN2,
        // vía AmbienteConnectionFactory), no en la conexión fija.
        public async Task<int> ExisteImportacion(string servidor, string baseDeDatos, string importId, CancellationToken ct = default)
        {
            const string sql = "SELECT COUNT(1) FROM [MMProDat].[dbo].[ContactImport] WITH (NOLOCK) WHERE [ID] = @ImportId AND [DBProvider] = 'GSS' AND [Status] = 'COMPLETE'";
            using var connection = _ambienteConnectionFactory.CreateConnection(servidor, baseDeDatos);
            connection.Open();
            var command = new CommandDefinition(sql, new { ImportId = importId }, commandType: CommandType.Text, cancellationToken: ct);
            return await connection.ExecuteScalarAsync<int>(command);
        }

        // DAL_Campana.ExisteLote — SELECT COUNT(1) directo contra
        // [MMProDat].[dbo].[OutboundProcessBatch], mismo patrón que ExisteImportacion.
        public async Task<int> ExisteLote(string servidor, string baseDeDatos, string batchId, string processId, CancellationToken ct = default)
        {
            const string sql = "SELECT COUNT(1) FROM [MMProDat].[dbo].[OutboundProcessBatch] WITH (NOLOCK) WHERE [OutboundProcessId] = @ProcessId AND [BatchId] = @BatchId";
            using var connection = _ambienteConnectionFactory.CreateConnection(servidor, baseDeDatos);
            connection.Open();
            var command = new CommandDefinition(sql, new { BatchId = batchId, ProcessId = processId }, commandType: CommandType.Text, cancellationToken: ct);
            return await connection.ExecuteScalarAsync<int>(command);
        }

        // DAL_Campana.RegistrarFin — SppGss_ApWSRegistroLog_C2C. @FechaFin se calcula acá
        // (DateTime.Now), igual que en el legacy — no es un parámetro de negocio, es el
        // timestamp de cuándo se persistió el registro.
        public async Task RegistrarFin(
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
            CancellationToken ct = default)
        {
            var parameters = new DynamicParameters();
            parameters.Add("@ContactId", contactId, DbType.String, size: 100);
            parameters.Add("@IdCampana", campana.Id, DbType.Int32);
            parameters.Add("@ImportId", campana.ImportId, DbType.String, size: 100);
            parameters.Add("@BatchId", campana.BatchId, DbType.String, size: 100);
            parameters.Add("@Prioridad", prioridad, DbType.Int32);
            parameters.Add("@Telefono", telefono, DbType.String, size: 200);
            parameters.Add("@NameValue", nameValue, DbType.String, size: 1500);
            parameters.Add("@Resultado", resultado, DbType.String, size: 1500);
            parameters.Add("@FechaRegistro", fechaRegistro, DbType.DateTime);
            parameters.Add("@ListoParaIniciarSesion", listoParaIniciarSesion, DbType.DateTime);
            parameters.Add("@SesionIniciada", sesionIniciada, DbType.DateTime);
            parameters.Add("@ValidoExistenciaImportBatch", validoExistenciaImportBatch, DbType.DateTime);
            parameters.Add("@AddContactFin", addContactFin, DbType.DateTime);
            parameters.Add("@AddPhoneFin", addPhoneFin, DbType.DateTime);
            parameters.Add("@AddNameValuesFin", addNameValuesFin, DbType.DateTime);
            parameters.Add("@AddContactToBatchFin", addContactToBatchFin, DbType.DateTime);
            parameters.Add("@FechaFin", DateTime.Now, DbType.DateTime);
            parameters.Add("@DateScheduled", dateScheduled, DbType.DateTime);

            using var connection = _context.CreateConnection();
            connection.Open();
            var command = new CommandDefinition(
                "SppGss_ApWSRegistroLog_C2C",
                parameters,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct);
            await connection.ExecuteAsync(command);
        }
    }
}
