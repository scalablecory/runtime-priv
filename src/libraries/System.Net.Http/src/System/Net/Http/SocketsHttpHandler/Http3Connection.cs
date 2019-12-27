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
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3.QPack;

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

        // Used to tear down background tasks.
        private CancellationTokenSource _cancellationSource = new CancellationTokenSource();

        // Our control stream.
        private QuicStream _clientControl;

        // Current SETTINGS from the server.
        private int _maximumHeadersLength = -1;

        // Once the server's streams are received, these are set to 1. Further receipt of these streams results in a connection error.
        private int _haveServerControlStream = 0;
        private int _haveServerQpackDecodeStream = 0;
        private int _haveServerQpackEncodeStream = 0;

        private int _nextRequestStreamId = 0;

        public HttpAuthority Authority => _authority;
        public HttpConnectionPool Pool => _pool;

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

        public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            //TODO: respect server's QUIC stream maximum.
            int streamId = Interlocked.Add(ref _nextRequestStreamId, 4);
            var stream = new Http3Stream(this, _connection.OpenBidirectionalStream()); // TODO: open the bidi stream using the stream ID.
            return stream.SendAsync(request, cancellationToken);
        }

        public override void Trace(string message, [CallerMemberName] string memberName = null) =>
            Trace(0, message, memberName);

        internal void Trace(int streamId, string message, [CallerMemberName] string memberName = null) =>
            NetEventSource.Log.HandlerMessage(
                _pool?.GetHashCode() ?? 0,    // pool ID
                GetHashCode(),                // connection ID
                streamId,                     // stream ID
                memberName,                   // method name
                message);                     // message

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
                _ = ProcessServerStream(stream);
            }
        }

        private async Task ProcessServerStream(QuicStream stream)
        {
            var buffer = new ArrayBuffer(initialSize: 32, usePool: true);

            try
            {
                if (stream.CanWrite)
                {
                    if (NetEventSource.IsEnabled)
                    {
                        NetEventSource.Error(this, "Server initiated bidirectional stream received without prior negotiation.");
                    }

                    stream.Close(); // TODO: abort the connection with H3_STREAM_CREATION_ERROR.
                    return;
                }

                int bytesRead = await stream.ReadAsync(buffer.AvailableMemory, _cancellationSource.Token).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    if (NetEventSource.IsEnabled)
                    {
                        NetEventSource.Error(this, "Server initiated stream received, but got EOF before stream type.");
                    }

                    stream.Close(); // TODO: abort the stream with H3_STREAM_CREATION_ERROR.
                    return;
                }

                buffer.Commit(bytesRead);

                // Stream type is a variable-length integer, but we only check the first byte. There is no known type requiring more than 1 byte.
                switch (buffer.ActiveSpan[0])
                {
                    case (byte)Http3StreamType.Control:
                        if (Interlocked.Exchange(ref _haveServerControlStream, 1) != 0)
                        {
                            // A second control stream has been received.
                            _connection.Close(); // TODO: abort the connection with H3_STREAM_CREATION_ERROR.
                            throw new HttpRequestException("More than one control stream received.");
                        }

                        buffer.Discard(1);

                        _ = ProcessServerControlStreamAsync(stream, buffer);
                        stream = null;
                        buffer = default;
                        return;
                    case (byte)Http3StreamType.QPackDecoder:
                        if (Interlocked.Exchange(ref _haveServerQpackDecodeStream, 1) != 0)
                        {
                            _connection.Close(); // TODO: abort the connection with H3_STREAM_CREATION_ERROR.
                            throw new HttpRequestException("More than one QPACK decode stream received.");
                        }

                        // The stream must not be closed, but we aren't using QPACK right now -- ignore.
                        _ = stream.CopyToAsync(Stream.Null);
                        stream = null;
                        return;
                    case (byte)Http3StreamType.QPackEncoder:
                        if (Interlocked.Exchange(ref _haveServerQpackEncodeStream, 1) != 0)
                        {
                            _connection.Close(); // TODO: abort the connection with H3_STREAM_CREATION_ERROR.
                            throw new HttpRequestException("More than one QPACK encode stream received.");
                        }

                        // The stream must not be closed, but we aren't using QPACK right now -- ignore.
                        _ = stream.CopyToAsync(Stream.Null);
                        stream = null;
                        return;
                    case (byte)Http3StreamType.Push:
                        // We don't support push streams.
                        // Because no maximum push stream ID was negotiated via a MAX_PUSH_ID frame, server should not have sent this. Abort the connection with H3_ID_ERROR.
                        stream.Close(); // TODO: abort the connection with H3_ID_ERROR.
                        throw new HttpRequestException("Received push stream without prior negotiation.");
                    default:
                        // Unknown stream type. Per spec, these must be ignored and aborted but not be considered a connection-level error.

                        if (NetEventSource.IsEnabled)
                        {
                            // Read the rest of the integer, which might be more than 1 byte, so we can log it.

                            long unknownStreamType;
                            while (!VariableLengthIntegerHelper.TryRead(buffer.ActiveSpan, out unknownStreamType, out _))
                            {
                                buffer.EnsureAvailableSpace(1);
                                bytesRead = await stream.ReadAsync(buffer.AvailableMemory, _cancellationSource.Token).ConfigureAwait(false);

                                if (bytesRead == 0)
                                {
                                    unknownStreamType = -1;
                                    break;
                                }

                                buffer.Commit(bytesRead);
                            }

                            NetEventSource.Info(this, $"Ignoring server-initiated stream of unknown type {unknownStreamType}.");
                        }

                        stream.Close(); // TODO: abort the stream with H3_STREAM_CREATION_ERROR.
                        return;
                }
            }
            finally
            {
                if (stream != null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                buffer.Dispose();
            }
        }

        private async Task ProcessServerControlStreamAsync(QuicStream stream, ArrayBuffer buffer)
        {
            using (stream)
            using (buffer)
            {
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
                        case Http3FrameType.Headers:
                        case Http3FrameType.Data:
                        case Http3FrameType.MaxPushId:
                        case Http3FrameType.DuplicatePush:
                            // Servers should not send these frames to a control stream.
                            // TODO: close connection with H3_FRAME_UNEXPECTED.
                            throw new HttpRequestException($"Server sent inappropriate frame type {frameType}.");
                        case Http3FrameType.PushPromise:
                        case Http3FrameType.CancelPush:
                            // Because we haven't sent any MAX_PUSH_ID frame, it is invalid to receive any push-related frames as they will all reference a too-large ID.
                            // TODO: close connection with H3_FRAME_UNEXPECTED.
                            break;
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
