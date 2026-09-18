using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using ICUpload.Core.Interfaces;
using System.Data;

namespace ICUpload.Infraestructure.Data
{
    // Espeja DAL_Conexion.Conectar(Servidor, DataBase): arma la cadena CN2 con
    // string.Format contra la plantilla + credenciales fijas (usraccmw/inc2001) que ya están
    // en ConnectionStrings:AmbienteConnection de appsettings.json. Se mantiene separado de
    // DbContextApp (conexión fija AppConnection/CN) porque cada campaña puede apuntar a un
    // servidor/base distinto (objBECampana.ServidorSQL/BaseDeDatos).
    public class AmbienteConnectionFactory : IAmbienteConnectionFactory
    {
        private readonly string _template;

        public AmbienteConnectionFactory(IConfiguration configuration)
        {
            _template = configuration.GetSection("ConnectionStrings")["AmbienteConnection"]
                ?? throw new ArgumentNullException("ConnectionStrings:AmbienteConnection", "La cadena de conexión no está configurada.");
        }

        public IDbConnection CreateConnection(string servidor, string baseDeDatos) =>
            new SqlConnection(string.Format(_template, servidor, baseDeDatos));
    }
}
