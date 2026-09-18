using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ICUpload.Core.Entities
{
    public  class BE_Usuario
    {
        public string Usuario { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Vcc { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }
}
