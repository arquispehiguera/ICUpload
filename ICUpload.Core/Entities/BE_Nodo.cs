using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ICUpload.Core.Entities
{
    public  class BE_Nodo
    {
        public string Ip { set; get; } = string.Empty;
        public int Puerto { set; get; }
    }
}
