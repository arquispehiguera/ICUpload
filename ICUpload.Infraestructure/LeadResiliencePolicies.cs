using ICUpload.Core.Entities;
using Polly;

namespace ICUpload.Infraestructure
{
    // Política Polly dedicada al flujo de negocio completo de Leads — no reusa
    // ResiliencePolicies (es internal y sus políticas son para llamadas puntuales, no para
    // reintentar un ProcessAsync entero). Pública a propósito: la consume
    // LeadRabbitMqConsumerService, en el proyecto Process.
    //
    // OrResult(r => !r.Success && r.Retryable): no reintenta ciegamente cualquier fallo.
    // LeadResult.Retryable es false para los fallos de configuración (campaña vacía / campaña
    // no configurada) — WORKER_SPEC.md y el propio plan (escenario de verificación #5) piden
    // explícitamente "sin reintentos" para esos casos, porque son determinísticos: reintentar
    // 3 veces algo que va a fallar exactamente igual las 3 veces solo demora la alerta y
    // spamea al notificador. El resto de los fallos (login, timeouts de importación/lote,
    // fallos de SDK, excepciones inesperadas) son Retryable = true por default.
    public static class LeadResiliencePolicies
    {
        // .Handle<Exception>(ex => ex is not OperationCanceledException): Polly NO trata
        // especial las OperationCanceledException por su cuenta — sin esta exclusión,
        // reintentaría igual una cancelación por shutdown del host (con delays no
        // cancelables) como si fuera cualquier otro fallo transitorio. LeadService.ProcessAsync
        // deja propagar la OperationCanceledException de shutdown sin convertirla en
        // LeadResult; esta política la deja pasar sin reintentar, y
        // LeadRabbitMqConsumerService la distingue para hacer Nack(requeue:true).
        public static readonly IAsyncPolicy<LeadResult> LeadFlowRetry =
            Policy<LeadResult>
                .Handle<Exception>(ex => ex is not OperationCanceledException)
                .OrResult(r => !r.Success && r.Retryable)
                .WaitAndRetryAsync(2, _ => TimeSpan.FromSeconds(1)); // 2 reintentos + intento inicial = 3
    }
}
