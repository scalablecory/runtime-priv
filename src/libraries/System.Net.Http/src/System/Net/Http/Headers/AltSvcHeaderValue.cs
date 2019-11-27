using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace System.Net.Http.Headers
{
    internal sealed class AltSvcHeaderValue
    {
        /// <summary>
        /// If true, Alt-Svc should be cleared for this authority: the client should use the original.
        /// </summary>
        public bool Clear { get; }
        public ICollection<AlternateService> Alternatives = new ObjectCollection<AlternateService>();

        public AltSvcHeaderValue(bool clear)
        {
            Clear = clear;
        }
    }
}
