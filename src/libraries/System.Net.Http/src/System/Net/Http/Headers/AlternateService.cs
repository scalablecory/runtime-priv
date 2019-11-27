using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.Net.Http.Headers
{
    internal sealed class AlternateService
    {
        public static AlternateService Clear { get; } = new AlternateService(null, null, 0, TimeSpan.Zero);

        public string AlpnProtocolName { get; }
        public string Host { get; }
        public int Port { get; }
        public TimeSpan MaxAge { get; }

        public AlternateService(string alpnProtocolName, string host, int port, TimeSpan maxAge)
        {
            AlpnProtocolName = alpnProtocolName;
            Host = host;
            Port = port;
            MaxAge = maxAge;
        }
    }
}
