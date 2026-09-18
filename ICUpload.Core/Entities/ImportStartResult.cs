namespace PacificoSeguros.Core.Entities
{
    // No está en la lista de entidades del plan, pero hace falta para portar fielmente
    // BL_Campana.CrearImportacion: el legacy distingue dos fallos distintos de
    // StartContactImport (SDK) que requieren alertas distintas —
    //   - Started = false: el propio StartContactImport no arrancó. Para 5 ProcessId
    //     especiales (TmpMovistarTotalAPC, TmpRetWinbackMtAPC, TmpRetWinbackFijaOut,
    //     TmpRetencionesFijaOut, TMP_RETENCIONES_BLINDAJE) esta falla NO alerta.
    //   - Started = true pero Completed = false: el polling de GetImportStatus nunca llegó a
    //     "Complete" (terminó en "Aborted"/"Unknown" u otro estado) — esta falla SIEMPRE
    //     alerta, sin excepción para los 5 ProcessId especiales.
    // Con un solo InConcertOperationResult (Ok/Info) no alcanza para distinguir ambos casos
    // sin recurrir a parsear el texto de Info — de ahí este tipo dedicado.
    public record ImportStartResult(bool Started, bool Completed, string Info);
}
