namespace PacificoSeguros.Core.Entities
{
    // Contrato de éxito/fallo del legacy: el string literal "Carga Terminada" es el ÚNICO
    // indicador de éxito (WORKER_SPEC.md #4.16) — por eso Success se calcula a partir de
    // Message en vez de ser un campo independiente que se pueda desalinear del mensaje real.
    //
    // Retryable no está en el diseño original del plan (que solo pedía Success/Message), pero
    // es necesario para que LeadResiliencePolicies pueda distinguir un fallo de configuración
    // (campaña vacía / no configurada — determinístico, "sin reintentar" según el plan y el
    // escenario de verificación #5) de un fallo transitorio (SQL/SDK — sí reintentable, según
    // el escenario de verificación #6). Sin este campo, la política de Polly definida en el
    // plan (`OrResult(r => !r.Success)`) reintentaría CIEGAMENTE también los fallos de
    // configuración, contradiciendo el propio documento del plan. Ver reporte final para el
    // detalle de esta decisión.
    public sealed record LeadResult(string Message, bool Retryable = true)
    {
        public bool Success => Message == "Carga Terminada";

        public static LeadResult Ok() => new("Carga Terminada");
        public static LeadResult Fail(string message, bool retryable = true) => new(message, retryable);
    }
}
