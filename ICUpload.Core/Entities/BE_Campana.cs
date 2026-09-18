using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PacificoSeguros.Core.Entities
{
   public   class BE_Campana
    {
        public int Id { get; set; }
        public string ProcessId { get; set; } = string.Empty;
        public string FormatId { get; set; } = string.Empty;
        public string FicheroCSV { get; set; } = string.Empty;
        public string ServidorSQL { get; set; } = string.Empty;
        public string BaseDeDatos { get; set; } = string.Empty;
        public int DuracionLote { get; set; }
        public string ImportId { get; set; } = string.Empty;
        public string BatchId { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string TiposTelefono { get; set; } = string.Empty;
        public string NameValues { get; set; } = string.Empty;
        public string Skill { get; set; } = string.Empty;
        //Usuario
        public int IdUsuario { get; set; }
        public string Usuario { get; set; } = string.Empty;
        public string Vcc { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        //Nodo
        public int IdNodo { get; set; }
        public string Ip { get; set; } = string.Empty;
        public int Puerto { get; set; }
    }
}
