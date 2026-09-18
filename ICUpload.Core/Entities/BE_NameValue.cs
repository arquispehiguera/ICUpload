using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ICUpload.Core.Entities
{
   public   class BE_NameValue
    {
        public string Name { get; set; }
        public string Value { get; set; }

        public BE_NameValue(string Name, string Value)
        {
            this.Name = Name;
            this.Value = Value;
        }
    }
}
