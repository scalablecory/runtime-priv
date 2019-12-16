// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace System.Net.Http
{
    internal partial class HttpConnectionPool
    {
        /// <summary>A cached idle connection and metadata about it.</summary>
        [StructLayout(LayoutKind.Auto)]
        internal readonly struct CachedConnection : IEquatable<CachedConnection>
        {
            /// <summary>The cached connection.</summary>
            internal readonly HttpConnection _connection;
            /// <summary>The last tick count at which the connection was used.</summary>
            internal readonly long _returnedTickCount;

            /// <summary>Initializes the cached connection and its associated metadata.</summary>
            /// <param name="connection">The connection.</param>
            public CachedConnection(HttpConnection connection)
            {
                Debug.Assert(connection != null);
                _connection = connection;
                _returnedTickCount = Environment.TickCount64;
            }

            /// <summary>Gets whether the connection is currently usable.</summary>
            /// <param name="nowTicks">The current tick count.  Passed in to amortize the cost of calling Environment.TickCount.</param>
            /// <param name="pooledConnectionLifetime">How long a connection can be open to be considered reusable.</param>
            /// <param name="pooledConnectionIdleTimeout">How long a connection can have been idle in the pool to be considered reusable.</param>
            /// <returns>
            /// true if we believe the connection can be reused; otherwise, false.  There is an inherent race condition here,
            /// in that the server could terminate the connection or otherwise make it unusable immediately after we check it,
            /// but there's not much difference between that and starting to use the connection and then having the server
            /// terminate it, which would be considered a failure, so this race condition is largely benign and inherent to
            /// the nature of connection pooling.
            /// </returns>
            public bool IsUsable(
                long nowTicks,
                TimeSpan pooledConnectionLifetime,
                TimeSpan pooledConnectionIdleTimeout,
                bool poll = false)
            {
                // Validate that the connection hasn't been idle in the pool for longer than is allowed.
                if ((pooledConnectionIdleTimeout != Timeout.InfiniteTimeSpan) &&
                    ((nowTicks - _returnedTickCount) > pooledConnectionIdleTimeout.TotalMilliseconds))
                {
                    if (NetEventSource.IsEnabled) _connection.Trace($"Connection no longer usable. Idle {TimeSpan.FromMilliseconds((nowTicks - _returnedTickCount))} > {pooledConnectionIdleTimeout}.");
                    return false;
                }

                // Validate that the connection hasn't been alive for longer than is allowed.
                if (_connection.LifetimeExpired(nowTicks, pooledConnectionLifetime))
                {
                    return false;
                }

                // Validate that the connection hasn't received any stray data while in the pool.
                if (poll && _connection.PollRead())
                {
                    if (NetEventSource.IsEnabled) _connection.Trace($"Connection no longer usable. Unexpected data received.");
                    return false;
                }

                // The connection is usable.
                return true;
            }

            public bool Equals(CachedConnection other) => ReferenceEquals(other._connection, _connection);
            public override bool Equals(object obj) => obj is CachedConnection && Equals((CachedConnection)obj);
            public override int GetHashCode() => _connection?.GetHashCode() ?? 0;
        }
    }
}
