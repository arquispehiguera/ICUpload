namespace PacificoSeguros.Core.Interfaces
{
    // Reemplaza el mail hardcodeado del legacy (BL_Mail: SMTP + password en texto plano,
    // destinatario fijo). campaign espeja el segundo parámetro de BL_Mail.EnviarMail(mensaje,
    // campaign) — puede ser el nombre real de campaña, "No definida" (campaña vacía) o null.
    public interface IAlertNotifier
    {
        Task NotifyAsync(string message, string? campaign, CancellationToken ct = default);
    }
}
