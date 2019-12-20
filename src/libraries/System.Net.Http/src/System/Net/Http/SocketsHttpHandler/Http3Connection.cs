using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Net.Quic;

namespace System.Net.Http
{
    internal sealed class Http3Connection : HttpConnectionBase, IAsyncDisposable
    {
        private readonly HttpAuthority _origin;
        private readonly HttpAuthority _authority;
        private readonly QuicConnection _connection;

        public HttpAuthority Authority => _authority;

        public Http3Connection(HttpAuthority origin, HttpAuthority authority, QuicConnection connection)
        {
            _origin = origin;
            _authority = authority;
            _connection = connection;
        }

        public ValueTask DisposeAsync()
        {
            //TODO.
            return default;
        }

        public override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(10).ConfigureAwait(false);
            throw new NotImplementedException();
        }

        public override void Trace(string message, [CallerMemberName] string memberName = null)
        {
        }
    }
}
