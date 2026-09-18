namespace ICUpload.Core.Interfaces
{
    // Arma la conexión dinámica CN2 del legacy (DAL_Conexion.Conectar(Servidor, DataBase)):
    // cada campaña puede vivir en un servidor/base distinto (objBECampana.ServidorSQL /
    // BaseDeDatos), a diferencia de AppConnection/CN, que es fija (ver DbContextApp). La
    // interfaz vive en Core, igual que IInteraccionRepository/IOracleApiClient, aunque
    // DbContextApp (la conexión fija) no tenga una — el plan pide explícitamente
    // IAmbienteConnectionFactory para esta, así que se sigue la convención ya existente en
    // el repo de declarar la interfaz en Core.Interfaces.
    public interface IAmbienteConnectionFactory
    {
        System.Data.IDbConnection CreateConnection(string servidor, string baseDeDatos);
    }
}
