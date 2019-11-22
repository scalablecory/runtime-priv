using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using System.Net.Test.Common;
using System.Net.Sockets;
using System.Net.Http.Headers;

namespace System.Net.Http.Functional.Tests
{
    public abstract class HttpClientHandler_AltSvc_Test : HttpClientHandlerTestBase
    {
        public HttpClientHandler_AltSvc_Test(ITestOutputHelper output) : base(output) { }

        public async Task AltSvc_KnownAlpn_Follow()
        {
            using HttpClient client = CreateHttpClient();

            var options = new GenericLoopbackOptions();
            using GenericLoopbackServer firstServer = LoopbackServerFactory.CreateServer(options);
            using GenericLoopbackServer secondServer = LoopbackServerFactory.CreateServer(options);

            // Handle first request, sending ALPN.
            await Task.Run(async () =>
            {
                Task<HttpResponseMessage> responseTask = client.GetAsync(firstServer.Address);
                using GenericLoopbackConnection conn = await firstServer.EstablishGenericConnectionAsync();
                await conn.ReadRequestDataAsync(true);
                await conn.SendResponseAsync(HttpStatusCode.OK, new[]
                {
                    new HttpHeaderData("Alt-Svc", $"http={secondServer.Address.IdnHost}:{secondServer.Address.Port}")
                });

                using HttpResponseMessage response = await responseTask;
                AltSvcHeaderValue altSvc = Assert.Single(response.Headers.AltSvc);
                Assert.Equal("http", altSvc.AlpnProtocolName);
                Assert.Equal(secondServer.Address.IdnHost.ToString(), altSvc.Host);
                Assert.Equal(secondServer.Address.Port, altSvc.Port);
            }).TimeoutAfter(60_000);

            // Handle second request, this time on the 2nd server.
            await Task.Run(async () =>
            {
                Task<HttpResponseMessage> responseTask = client.GetAsync(firstServer.Address); // Address should be to the 1st server still.
                using GenericLoopbackConnection conn = await secondServer.EstablishGenericConnectionAsync();
                await conn.ReadRequestDataAsync(true);
                await conn.SendResponseAsync(HttpStatusCode.OK);

                using HttpResponseMessage response = await responseTask;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }).TimeoutAfter(60_000);
        }

        [Fact]
        public async Task AltSvc_UnknownAlpn_Ignore()
        {
            using HttpClient client = CreateHttpClient();
            using GenericLoopbackServer server = LoopbackServerFactory.CreateServer();

            // badListenSocket is expected to never receive a connection.
            // bind to reserve an address, but don't listen.
            using Socket badListenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            badListenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var badEndPoint = (IPEndPoint)badListenSocket.LocalEndPoint;

            // Handle first request, sending unknown ALPN.
            await Task.Run(async () =>
            {
                const string badAlpn = "foo";
                Task<HttpResponseMessage> responseTask = client.GetAsync(server.Address);
                using GenericLoopbackConnection conn = await server.EstablishGenericConnectionAsync();
                await conn.ReadRequestDataAsync(true);
                await conn.SendResponseAsync(HttpStatusCode.OK, new[]
                {
                    new HttpHeaderData("Alt-Svc", $"{badAlpn}={badEndPoint.Address}:{badEndPoint.Port}"),
                    new HttpHeaderData("Connection", "close") //TODO: no connection close on HTTP/2, make this generic.
                });

                using HttpResponseMessage response = await responseTask;
                AltSvcHeaderValue altSvc = Assert.Single(response.Headers.AltSvc);
                Assert.Equal(badAlpn, altSvc.AlpnProtocolName);
                Assert.Equal(badEndPoint.Address.ToString(), altSvc.Host);
                Assert.Equal(badEndPoint.Port, altSvc.Port);
            }).TimeoutAfter(30_000);

            // Handle second request: if client accepted the Alt-Svc header, this will timeout because it'll be sent to badListenSocket.
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                Task<HttpResponseMessage> responseTask = client.GetAsync(server.Address);
                using GenericLoopbackConnection conn = await server.EstablishGenericConnectionAsync();
                await conn.ReadRequestDataAsync(readBody: true);
                await conn.SendResponseAsync(HttpStatusCode.OK);

                using HttpResponseMessage response = await responseTask;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }).TimeoutAfter(30_000);
        }
    }
}
