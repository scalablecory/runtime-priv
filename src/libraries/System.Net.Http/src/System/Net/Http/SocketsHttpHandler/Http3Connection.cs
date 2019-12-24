using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Net.Quic;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3;
using System.Net.Http.HPack;
using System.IO;

namespace System.Net.Http
{
    internal sealed class Http3Connection : HttpConnectionBase, IAsyncDisposable
    {
        private const int MaximumSettingsPayloadLength = 4096;

        /// <summary>
        /// Unknown frame types with a payload larger than this will result in tearing down the connection.
        /// Frames smaller than this will be ignored and drained.
        /// </summary>
        private const int MaximumUnknownFramePayloadLength = 4096;

        private readonly HttpConnectionPool _pool;
        private readonly HttpAuthority _origin;
        private readonly HttpAuthority _authority;
        private readonly QuicConnection _connection;

        private CancellationTokenSource _cancellationSource = new CancellationTokenSource();

        // Our control stream.
        private QuicStream _clientControl;

        // Current SETTINGS from the server.
        private int _maximumHeadersLength = -1;

        // Once the server's control stream is received, this is set to 1. Further control streams will result in a connection error.
        private int _haveServerControlStream = 0;

        public HttpAuthority Authority => _authority;

        public Http3Connection(HttpConnectionPool pool, HttpAuthority origin, HttpAuthority authority, QuicConnection connection)
        {
            _pool = pool;
            _origin = origin;
            _authority = authority;
            _connection = connection;

            _ = SendSettingsAsync();
            _ = AcceptStreams();
        }

        public ValueTask DisposeAsync()
        {
            _cancellationSource.Dispose();
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

        private async ValueTask SendSettingsAsync()
        {
            _clientControl = _connection.OpenUnidirectionalStream();

            var buffer = new byte[12];

            int payloadLength = 1 + VariableLengthIntegerHelper.WriteInteger(buffer.AsSpan(3), _pool.Settings._maxResponseHeadersLength * 1024);
            buffer[0] = 0x00; // Control stream type identifier.
            buffer[1] = (byte)Http3FrameType.Settings;
            buffer[2] = (byte)payloadLength;
            buffer[3] = (byte)Http3SettingType.MaxHeaderListSize;

            await _clientControl.WriteAsync(buffer.AsMemory(4 + payloadLength), _cancellationSource.Token).ConfigureAwait(false);
        }

        private async Task AcceptStreams()
        {
            while (true)
            {
                QuicStream stream = await _connection.AcceptStreamAsync(_cancellationSource.Token).ConfigureAwait(false);
                _ = ProcessServerControlStreamAsync(stream);
            }
        }

        private async Task ProcessServerControlStreamAsync(QuicStream stream)
        {
            var buffer = new ArrayBuffer(512);

            using (stream)
            {
                // Do an initial read so we can check our stream type.

                int bytesRead = await stream.ReadAsync(buffer.AvailableMemory, _cancellationSource.Token).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    throw new HttpRequestException($"Stream closed prematurely, expected stream type identifier.");
                }

                buffer.Commit(bytesRead);

                if (buffer.ActiveSpan[0] != 0x00)
                {
                    // Anything other than a unidirectional control stream (stream type identifier 0x00) is an unsupported stream type.
                    stream.Close(); // TODO: abort the stream with H3_STREAM_CREATION_ERROR.
                    return;
                }

                if (Interlocked.Exchange(ref _haveServerControlStream, 1) != 0)
                {
                    // A second control stream has been received.
                    _connection.Close(); // TODO: abort the connection with H3_STREAM_CREATION_ERROR.
                }

                (Http3FrameType? frameType, long payloadLength) = await ReadFrameEnvelopeAsync().ConfigureAwait(false);

                if (frameType == null)
                {
                    throw new HttpRequestException($"Control stream closed prematurely, expected SETTINGS.");
                }

                if (frameType != Http3FrameType.Settings)
                {
                    throw new HttpRequestException($"Control stream initiated with frame type of {frameType}, expected SETTINGS.");
                }

                await ProcessSettingsFrameAsync(payloadLength).ConfigureAwait(false);

                while (true)
                {
                    (frameType, payloadLength) = await ReadFrameEnvelopeAsync().ConfigureAwait(false);

                    switch (frameType)
                    {
                        case Http3FrameType.Settings:
                            await ProcessSettingsFrameAsync(payloadLength).ConfigureAwait(false);
                            break;
                        case Http3FrameType.GoAway:
                            // TODO: shut down connection.
                            break;
                        case Http3FrameType.CancelPush:
                        case Http3FrameType.PushPromise:
                        case Http3FrameType.MaxPushId:
                        case Http3FrameType.DuplicatePush:
                            // Servers should not send push promise to us.
                            throw new HttpRequestException($"Server sent inappropriate frame type {frameType}.");
                        default:
                            await SkipUnknownPayloadAsync(frameType.GetValueOrDefault(), payloadLength).ConfigureAwait(false);
                            break;
                        case null:
                            // TODO: end of stream reached. check if we're shutting down, or if this is an error (this stream should not be closed for life of connection).
                            break;
                    }
                }
            }

            async ValueTask<(Http3FrameType? frameType, long payloadLength)> ReadFrameEnvelopeAsync()
            {
                long frameType, payloadLength;
                int bytesRead;

                while (!Http3Frame.TryReadIntegerPair(buffer.ActiveSpan, out frameType, out payloadLength, out bytesRead))
                {
                    buffer.EnsureAvailableSpace(1);
                    bytesRead = await stream.ReadAsync(buffer.AvailableMemory, _cancellationSource.Token).ConfigureAwait(false);

                    if (bytesRead != 0)
                    {
                        buffer.Commit(bytesRead);
                    }
                    else if (buffer.ActiveLength == 0)
                    {
                        // End of stream.
                        return (null, 0);
                    }
                    else
                    {
                        // Our buffer has partial frame data in it but not enough to complete the read: bail out.
                        throw new HttpRequestException("Partial frame envelope received; unexpected end of stream.");
                    }
                }

                buffer.Discard(bytesRead);

                return ((Http3FrameType)frameType, payloadLength);
            }

            async ValueTask ProcessSettingsFrameAsync(long settingsPayloadLength)
            {
                if (settingsPayloadLength > MaximumSettingsPayloadLength)
                {
                    throw new HttpRequestException($"SETTINGS frame with payload of length {settingsPayloadLength} is too large; exceeds {MaximumSettingsPayloadLength} bytes.");
                }

                while (settingsPayloadLength != 0)
                {
                    long settingId, settingValue;
                    int bytesRead;

                    while (!Http3Frame.TryReadIntegerPair(buffer.ActiveSpan, out settingId, out settingValue, out bytesRead))
                    {
                        buffer.EnsureAvailableSpace(1);
                        bytesRead = await stream.ReadAsync(buffer.AvailableMemory, _cancellationSource.Token).ConfigureAwait(false);

                        if (bytesRead != 0)
                        {
                            buffer.Commit(bytesRead);
                        }
                        else
                        {
                            // Our buffer has partial frame data in it but not enough to complete the read: bail out.
                            throw new HttpRequestException("Partial SETTINGS frame received; unexpected end of stream.");
                        }
                    }

                    settingsPayloadLength -= bytesRead;

                    // Only support this single setting. Skip others.
                    if (settingId == (long)Http3SettingType.MaxHeaderListSize)
                    {
                        _maximumHeadersLength = (int)Math.Min(settingValue, int.MaxValue);
                    }
                }
            }

            async ValueTask SkipUnknownPayloadAsync(Http3FrameType frameType, long payloadLength)
            {
                if (payloadLength > MaximumUnknownFramePayloadLength)
                {
                    throw new HttpRequestException($"Frame type {frameType} with payload of length {payloadLength} exceeds maximum unknown frame payload length of of {MaximumUnknownFramePayloadLength}.");
                }

                while (payloadLength != 0)
                {
                    if (buffer.ActiveLength == 0)
                    {
                        int bytesRead = await stream.ReadAsync(buffer.AvailableMemory, _cancellationSource.Token).ConfigureAwait(false);

                        if (bytesRead != 0)
                        {
                            buffer.Commit(bytesRead);
                        }
                        else
                        {
                            // Our buffer has partial frame data in it but not enough to complete the read: bail out.
                            throw new HttpRequestException("Partial frame received; unexpected end of stream.");
                        }
                    }

                    long readLength = Math.Min(payloadLength, buffer.ActiveLength);
                    buffer.Discard((int)readLength);
                    payloadLength -= readLength;
                }
            }
        }
    }
}
