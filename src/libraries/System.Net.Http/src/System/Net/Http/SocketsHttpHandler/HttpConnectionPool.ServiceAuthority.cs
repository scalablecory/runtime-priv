// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal partial class HttpConnectionPool
    {
        private sealed class ServiceAuthority : ServiceAuthorityBase, IDisposable
        {
            public ServiceAuthority PreviousAuthority;
            public ServiceAuthority NextAuthority;

            public readonly string AltSvcAlpnProtocolName;
            public readonly string Host;
            public readonly int Port;
            public long ExpireTicks;

            public bool IsOrigin => AltSvcAlpnProtocolName == null;

            private readonly Stack<CachedConnection> IdleConnections = new Stack<CachedConnection>();

            public bool Http2Enabled = true;
            public Http2Connection Http2Connection;
            public SemaphoreSlim Http2ConnectionCreateLock;

            public int ActiveRequestCount;

            public int IdleConnectionCount => IdleConnections.Count;

            public bool IsActive => true;

            public ServiceAuthority(string alpnProtocolName, string host, int port, long expireTicks)
            {
                AltSvcAlpnProtocolName = alpnProtocolName;
                Host = host;
                Port = port;
                ExpireTicks = expireTicks;
            }

            public void Dispose()
            {
                foreach (CachedConnection connection in IdleConnections)
                {
                    connection._connection.Dispose();
                }
                IdleConnections.Clear();

                if (Http2Connection != null)
                {
                    Http2Connection.Dispose();
                    Http2Connection = null;
                }
            }

            public void ReserveConnection()
            {
                ++ActiveRequestCount;
            }

            public void RemoveReservation()
            {
                --ActiveRequestCount;
                //TODO: if the authority is shutting down, and count is now 0, destroy the authority.
            }

            public bool TryGetIdleConnection(out CachedConnection cachedConnection)
            {
                //TODO:
                return IdleConnections.TryPop(out cachedConnection);
            }

            public bool TryReturnConnection(HttpConnection connection)
            {
                //TODO:
                IdleConnections.Push(new CachedConnection(connection));
                return true;
            }
        }
    }

    internal class ServiceAuthorityBase
    {
    }
}
