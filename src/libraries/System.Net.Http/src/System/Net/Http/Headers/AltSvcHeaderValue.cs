using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace System.Net.Http.System.Net.Http.Headers
{
    internal sealed class AltSvcHeaderValue
    {
        public ICollection<AlternateService> Alternatives = new ObjectCollection<AlternateService>();
    }
}
