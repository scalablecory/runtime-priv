using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.Net.Http
{

    internal sealed class HttpAuthority : IEquatable<HttpAuthority>
    {
        // ALPN Protocol Name should also be part of an authority, but we are special-casing for HTTP/3, so this can be assumed to be "H3".

        public string IdnHost { get; }
        public int Port { get; }

        public HttpAuthority(string host, int port)
        {
            Debug.Assert(host != null);

            // This is very rarely called, but could be optimized to avoid the URI-specific stuff by bringing in DomainNameHelpers from System.Private.Uri.

            UriBuilder builder = new UriBuilder();
            builder.Scheme = Uri.UriSchemeHttp;
            builder.Host = host;
            builder.Port = port;

            IdnHost = builder.Uri.IdnHost;
            Port = port;
        }

        public bool Equals(HttpAuthority other)
        {
            return string.Equals(IdnHost, other.IdnHost) && Port == other.Port;
        }

        public override bool Equals(object obj)
        {
            return obj is HttpAuthority other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(IdnHost, Port);
        }

        // For diagnostics
        public override string ToString()
        {
            return IdnHost != null ? $"{IdnHost}:{Port}" : "<empty>";
        }
    }
}
