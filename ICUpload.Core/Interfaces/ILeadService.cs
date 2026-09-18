using PacificoSeguros.Core.Entities;

namespace PacificoSeguros.Core.Interfaces
{
    // Puerto fiel de CargarRegistro.InsertarContacto (un intento completo del flujo de 16
    // pasos). Nunca lanza excepción de negocio — siempre devuelve un LeadResult; la capa de
    // arriba (LeadResiliencePolicies, en el consumer de RabbitMQ) es la que decide reintentar
    // en base al resultado.
    public interface ILeadService
    {
        Task<LeadResult> ProcessAsync(LeadMessage message, CancellationToken ct = default);
    }
}
