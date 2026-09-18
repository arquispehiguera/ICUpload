namespace PacificoSeguros.Core.Entities
{
    // ICUpload.Core no puede depender de inConcertSDKnet (solo InConcertGateway, en
    // Infraestructure, tiene esa referencia — ver plan). Este handle opaco es lo que permite
    // que ILeadCampaignRepository/ILeadService/IInConcertGateway vivan en Core sin exponer
    // CSession: Handle carga la instancia real de CSession, pero solo InConcertGateway la
    // desempaqueta (cast interno). El resto de las propiedades espejan lo que LeadService
    // necesita leer para armar los mensajes de error 1:1 con el legacy (SessionId, IsLoggedIn,
    // UserName, VCC aparecen en varios mensajes de "Fallo al ..." de CargarRegistro.asmx.cs).
    public sealed class InConcertSession
    {
        public required object Handle { get; init; }
        public bool IsLoggedIn { get; init; }
        public string SessionId { get; init; } = string.Empty;
        public string UserName { get; init; } = string.Empty;
        public string Vcc { get; init; } = string.Empty;
    }
}
