namespace ICUpload.Core.Entities
{
    // Espejo de inConcertSDKnet.APIResult (Ok/Info) — el resultado libre en texto que el SDK
    // real devuelve para cada operación. Info es donde hay que buscar el substring exacto de
    // sesión expirada ("Session is not logged in, you must provide a valid session to perform
    // the operation") para decidir si reloguear y reintentar la llamada puntual.
    public record InConcertOperationResult(bool Ok, string Info);
}
