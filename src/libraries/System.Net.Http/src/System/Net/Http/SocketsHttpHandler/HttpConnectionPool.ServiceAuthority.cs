// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace System.Net.Http
{
    internal partial class HttpConnectionPool
    {
        internal sealed class ServiceAuthority : IDisposable
        {
            public const string UnknownAlpnProtocolName = null;

            private int _activeRequestCount;
            private AuthorityState _state = AuthorityState.TakingRequests;

            public ServiceAuthority PreviousAuthority;
            public ServiceAuthority NextAuthority;

            public readonly string AltSvcAlpnProtocolName;
            public readonly string Host;
            public readonly int Port;
            public long ExpireTicks;

            public bool IsOrigin => AltSvcAlpnProtocolName == null;

            private readonly List<CachedConnection> IdleConnections = new List<CachedConnection>();

            public bool Http2Enabled = true;
            public Http2Connection Http2Connection;
            public SemaphoreSlim Http2ConnectionCreateLock;
            public bool IsHttp2Connecting = false;

            public int ActiveRequestCount => _activeRequestCount;
            public int IdleConnectionCount => IdleConnections.Count;
            public int OpenConnectionCount => IdleConnectionCount + (Http2Connection != null ? 1 : 0);

            public bool IsTakingRequests => _state.HasFlag(AuthorityState.TakingRequests);

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

            /// <summary>
            /// Increments the request count, preventing disposal.
            /// </summary>
            public void IncrementActiveRequestCount()
            {
                Debug.Assert(_state.HasFlag(AuthorityState.TakingRequests) && Environment.TickCount64 < ExpireTicks);
                ++_activeRequestCount;
            }

            /// <summary>
            /// Decrements the active request count. If the authority is shutting down and the active count reaches 0, this disposes the authority.
            /// </summary>
            public void DecrementActiveRequestCount()
            {
                if (--_activeRequestCount == 0 && !_state.HasFlag(AuthorityState.TakingRequests))
                {
                    // TODO: dispose the authority.
                }
            }

            /// <summary>
            /// Sets the authority to a forced transition state.
            /// </summary>
            public void SetAsDefunct()
            {
                _state = AuthorityState.Transitioning;
            }

            public bool TryGetIdleHttp11Connection(out CachedConnection cachedConnection)
            {
                List<CachedConnection> idleConnections = IdleConnections;

                if (idleConnections.Count == 0)
                {
                    cachedConnection = default;
                    return false;
                }

                cachedConnection = idleConnections[idleConnections.Count - 1];
                idleConnections.RemoveAt(idleConnections.Count - 1);
                return true;
            }

            public bool TryReturnConnection(HttpConnection connection)
            {
                //TODO:
                IdleConnections.Add(new CachedConnection(connection));
                return true;
            }

            public bool TryGetHttp2Connection(out Http2Connection connection)
            {
                connection = null;
                return false;
            }

            /// <summary>
            /// Removes any idle connections from the pool.
            /// </summary>
            public void CleanupIdleConnections(TimeSpan pooledConnectionLifetime, TimeSpan pooledConnectionIdleTimeout, ref List<HttpConnection> connectionsToDispose)
            {
                long nowTicks = Environment.TickCount64;
                Http2Connection http2Connection = Http2Connection;
                List<CachedConnection> list = IdleConnections;

                if (http2Connection != null)
                {
                    if (http2Connection.IsExpired(nowTicks, pooledConnectionLifetime, pooledConnectionIdleTimeout))
                    {
                        http2Connection.Dispose();
                        // We can set _http2Connection directly while holding lock instead of calling InvalidateHttp2Connection().
                        Http2Connection = null;
                    }
                }

                // Find the first item which needs to be removed.
                int freeIndex = 0;
                while (freeIndex < list.Count && list[freeIndex].IsUsable(nowTicks, pooledConnectionLifetime, pooledConnectionIdleTimeout, poll: true))
                {
                    freeIndex++;
                }

                // If freeIndex == list.Count, nothing needs to be removed.
                // But if it's < list.Count, at least one connection needs to be purged.
                if (freeIndex < list.Count)
                {
                    // We know the connection at freeIndex is unusable, so dispose of it.
                    connectionsToDispose ??= new List<HttpConnection>();
                    connectionsToDispose.Add(list[freeIndex]._connection);

                    // Find the first item after the one to be removed that should be kept.
                    int current = freeIndex + 1;
                    while (current < list.Count)
                    {
                        // Look for the first item to be kept.  Along the way, any
                        // that shouldn't be kept are disposed of.
                        while (current < list.Count && !list[current].IsUsable(nowTicks, pooledConnectionLifetime, pooledConnectionIdleTimeout, poll: true))
                        {
                            connectionsToDispose.Add(list[current]._connection);
                            current++;
                        }

                        // If we found something to keep, copy it down to the known free slot.
                        if (current < list.Count)
                        {
                            // copy item to the free slot
                            list[freeIndex++] = list[current++];
                        }

                        // Keep going until there are no more good items.
                    }

                    // At this point, good connections have been moved below freeIndex, and garbage connections have
                    // been added to the dispose list, so clear the end of the list past freeIndex.
                    list.RemoveRange(freeIndex, list.Count - freeIndex);
                }
            }

            [Flags]
            private enum AuthorityState
            {
                /// <summary>
                /// The authority is taking requests.
                /// </summary>
                TakingRequests = 1,

                /// <summary>
                /// The authority in is transitioning to <seealso cref="NextAuthority"/>.
                /// If <see cref="TakingRequests"/> is set, this authority can still be used if the next authority has no pooled connections.
                /// If <see cref="TakingRequests"/> is not set, this authority must not be used.
                /// </summary>
                Transitioning = 2
            }
        }
    }
}
