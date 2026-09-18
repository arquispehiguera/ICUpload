using PacificoSeguros.Core.Entities;

namespace PacificoSeguros.Core.Interfaces
{
    public interface IInteraccionRepository
    {
        Task<IReadOnlyList<CtiInteraccion>> PopulateIniLLamada(int top, string origen);
        Task<bool> UpdateIniLLamada(string jsonIni, string jsonRespuestaIni, int envioIniLLamada, string lastInteractionId, long idOracle, string urlOracle, string? resultado, string? motivo);
        Task<bool> ReleaseIniLLamadaClaim(string lastInteractionId);
        Task<bool> MarkIniLLamadaConfirmedUnpersisted(string lastInteractionId);
        Task<int> ReclaimOrphanedIniLLamada(int timeoutMinutes);
        Task InsertMachineOracle();
    }
}
