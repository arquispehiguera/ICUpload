using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ICUpload.Core.Interfaces;

namespace ICUpload.Infraestructure.Notifications
{
    // Reemplaza BL_Mail.EnviarMail (SMTP + password en texto plano, destinatario fijo) por un
    // LogWarning estructurado con marcador fijo ("ALERTA DE NEGOCIO") — el sink de Serilog a
    // SQL (GSS_LogPacifico) ya captura Warning+ , así que basta con filtrar por ese marcador
    // para reconstruir el mismo canal de alertas que antes iba por mail. AlertNotifier:Enabled
    // (default true) es el punto de extensión explícito para conectar una implementación SMTP
    // real más adelante sin tocar LeadService (que solo conoce IAlertNotifier).
    public class LogAlertNotifier : IAlertNotifier
    {
        private const string Marker = "ALERTA DE NEGOCIO";

        private readonly ILogger<LogAlertNotifier> _logger;
        private readonly bool _enabled;

        public LogAlertNotifier(ILogger<LogAlertNotifier> logger, IConfiguration configuration)
        {
            _logger = logger;
            _enabled = configuration.GetValue("AlertNotifier:Enabled", true);
        }

        public Task NotifyAsync(string message, string? campaign, CancellationToken ct = default)
        {
            if (_enabled)
            {
                _logger.LogWarning("{Marker} — Campaña: {Campaign} — {Message}", Marker, campaign ?? "(sin campaña)", message);
            }
            return Task.CompletedTask;
        }
    }
}
