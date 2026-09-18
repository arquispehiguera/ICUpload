namespace ICUpload.Core.Entities
{
    // Espejo exacto de los parámetros de InsertarContacto (CargarRegistro.asmx.cs) — lo que
    // publica la API pública en la cola RabbitMQ "Lead".
    public record LeadMessage(
        string ContactId,
        string Campaign,
        string Nombre,
        string Telefonos,
        string NameValues,
        int Prioridad,
        DateTime DateScheduled);
}
