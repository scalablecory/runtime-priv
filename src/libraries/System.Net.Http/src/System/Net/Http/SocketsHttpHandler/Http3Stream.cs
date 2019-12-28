// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3.QPack;

namespace System.Net.Http
{
    internal sealed class Http3Stream : IHttpHeadersHandler
    {
        /// <summary>
        /// Unknown frame types with a payload larger than this will result in tearing down the connection.
        /// Frames smaller than this will be ignored and drained.
        /// </summary>
        private const int MaximumUnknownFramePayloadLength = 4096;

        private Http3Connection _connection;
        private QuicStream _stream;
        private ArrayBuffer _buffer;
        private TaskCompletionSource<int> _expect100ContinueCompletionSource;
        private QPackDecoder _headerDecoder = new QPackDecoder(0, 0);

        /// <summary>Reusable array used to get the values for each header being written to the wire.</summary>
        private string[] _headerValues = Array.Empty<string>();

        public Http3Stream(Http3Connection connection, QuicStream stream)
        {
            _connection = connection;
            _stream = stream;
            _buffer = new ArrayBuffer(initialSize: 64, usePool: true);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.HasHeaders && request.Headers.ExpectContinue == true)
            {
                _expect100ContinueCompletionSource = new TaskCompletionSource<int>();
            }

            BufferHeaders(request);

            long firstFrameLengthRemaining = 0;

            if (request.Content != null && request.Content.TryComputeLength(out firstFrameLengthRemaining))
            {
                BufferFrameEnvelope((byte)Http3FrameType.Data, firstFrameLengthRemaining);
            }

            // Send headers.
            // TODO: if the request content is buffered, use a gathered write.
            await _stream.WriteAsync(_buffer.ActiveMemory, cancellationToken).ConfigureAwait(false);
            if (_expect100ContinueCompletionSource != null)
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_buffer.Capacity < 512)
            {
                // We should have sent everything, so empty the buffer so it can be used for recv.
                _buffer.Discard(_buffer.ActiveLength);
                Debug.Assert(_buffer.ActiveLength == 0);
            }
            else
            {
                // Return large amount of pooled bytes. Start smaller for recv buffer.
                _buffer.Dispose();
                _buffer = new ArrayBuffer(64, usePool: true);
            }

            // Send content in parallel with reading response.
            Task sendContentTask = request.Content != null ? SendContentAsync(request.Content, firstFrameLengthRemaining, cancellationToken) : Task.CompletedTask;


            // Read response in parallel with sending content.
        }

        private async Task ReadResponseAsync(CancellationToken cancellationToken)
        {
            // Buffer should be empty right now.
            Debug.Assert(_buffer.ActiveLength == 0);

            // Loop while reading frames.
            while (true)
            {
                (Http3FrameType? frameType, long payloadLength) = await ReadFrameEnvelopeAsync(cancellationToken).ConfigureAwait(false);

                switch (frameType)
                {
                    case Http3FrameType.Headers:
                        await ReadHeadersAsync(payloadLength, cancellationToken).ConfigureAwait(false);
                        break;
                    case Http3FrameType.Data:
                        await ReadDataAsync(payloadLength, cancellationToken).ConfigureAwait(false);
                        break;
                    case Http3FrameType.Settings:
                    case Http3FrameType.GoAway:
                    case Http3FrameType.MaxPushId:
                    case Http3FrameType.DuplicatePush:
                    case Http3FrameType.PushPromise:
                    case Http3FrameType.CancelPush:
                        // TODO: close connection with H3_FRAME_UNEXPECTED.
                        throw new HttpRequestException($"Server sent inappropriate frame type {frameType}.");
                    case null:
                        // TODO: end of stream reached. check if we're shutting down, or if this is an error.
                        break;
                    default:
                        await SkipUnknownPayloadAsync(frameType.GetValueOrDefault(), payloadLength, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }

        private async Task SendContentAsync(HttpContent content, long firstFrameLengthRemaining, CancellationToken cancellationToken)
        {
            if (_expect100ContinueCompletionSource != null)
            {
                await _expect100ContinueCompletionSource.Task.ConfigureAwait(false);
            }

            using (var writeStream = new Http3WriteStream(_stream, firstFrameLengthRemaining))
            {
                await content.CopyToAsync(writeStream, null, cancellationToken).ConfigureAwait(false);
            }
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private void BufferHeaders(HttpRequestMessage request)
        {
            // Reserve space for frame type + payload length.
            // These values will be written after headers are serialized, as the variable-length payload is an int.
            const int PreHeadersReserveSpace = 1 + 8;

            // This should be the first write to our buffer.
            Debug.Assert(_buffer.ActiveLength == 0);

            // Reserve space for header frame envelope.
            _buffer.Commit(PreHeadersReserveSpace);

            // Add header block prefix. We aren't using dynamic table, so these are simple zeroes.
            _buffer.AvailableSpan[0] = 0x00; // required insert count.
            _buffer.AvailableSpan[1] = 0x00; // s + delta base.
            _buffer.Commit(2);

            HttpMethod normalizedMethod = HttpMethod.Normalize(request.Method);
            if (normalizedMethod.Http3EncodedBytes != null)
            {
                BufferBytes(normalizedMethod.Http3EncodedBytes);
            }
            else
            {
                BufferLiteralHeaderWithStaticNameReference(H3StaticTable.MethodGet, normalizedMethod.Method);
            }

            BufferIndexedHeader(H3StaticTable.SchemeHttps);

            if (request.HasHeaders && request.Headers.Host != null)
            {
                BufferLiteralHeaderWithStaticNameReference(H3StaticTable.Authority, request.Headers.Host);
            }
            else
            {
                BufferBytes(_connection.Pool._http3EncodedAuthorityHostHeader);
            }

            string pathAndQuery = request.RequestUri.PathAndQuery;
            if (pathAndQuery == "/")
            {
                BufferIndexedHeader(H3StaticTable.PathSlash);
            }
            else
            {
                BufferLiteralHeaderWithStaticNameReference(H3StaticTable.PathSlash, pathAndQuery);
            }

            if (request.HasHeaders)
            {
                // H3 does not support Transfer-Encoding: chunked.
                request.Headers.TransferEncodingChunked = false;

                BufferHeaderCollection(request.Headers);
            }

            if (_connection.Pool.Settings._useCookies)
            {
                string cookiesFromContainer = _connection.Pool.Settings._cookieContainer.GetCookieHeader(request.RequestUri);
                if (cookiesFromContainer != string.Empty)
                {
                    BufferLiteralHeaderWithStaticNameReference(H3StaticTable.Cookie, cookiesFromContainer);
                }
            }

            if (request.Content == null)
            {
                if (normalizedMethod.MustHaveRequestBody)
                {
                    BufferIndexedHeader(H3StaticTable.ContentLength0);
                }
            }
            else
            {
                BufferHeaderCollection(request.Content.Headers);
            }

            // Determine our header envelope size.
            // The reserved space was the maximum required; discard what wasn't used.
            int headersLength = _buffer.ActiveLength - PreHeadersReserveSpace;
            int headersLengthEncodedSize = VariableLengthIntegerHelper.GetByteCount(headersLength);
            _buffer.Discard(PreHeadersReserveSpace - headersLengthEncodedSize - 1);

            // Encode header type in first byte, and payload length in subsequent bytes.
            _buffer.ActiveSpan[0] = (byte)Http3FrameType.Headers;
            int actualHeadersLengthEncodedSize = VariableLengthIntegerHelper.WriteInteger(_buffer.ActiveSpan.Slice(1, headersLengthEncodedSize), headersLength);
            Debug.Assert(actualHeadersLengthEncodedSize == headersLengthEncodedSize);
        }

        // TODO: use known headers to preencode names.
        // TODO: special-case Content-Type for static table values values.
        private void BufferHeaderCollection(HttpHeaders headers)
        {
            if (headers.HeaderStore == null)
            {
                return;
            }

            foreach (KeyValuePair<HeaderDescriptor, HttpHeaders.HeaderStoreItemInfo> header in headers.HeaderStore)
            {
                int headerValuesCount = HttpHeaders.GetValuesAsStrings(header.Key, header.Value, ref _headerValues);
                Debug.Assert(headerValuesCount > 0, "No values for header??");
                ReadOnlySpan<string> headerValues = _headerValues.AsSpan(0, headerValuesCount);

                KnownHeader knownHeader = header.Key.KnownHeader;
                if (knownHeader != null)
                {
                    // The Host header is not sent for HTTP2 because we send the ":authority" pseudo-header instead
                    // (see pseudo-header handling below in WriteHeaders).
                    // The Connection, Upgrade and ProxyConnection headers are also not supported in HTTP2.
                    if (knownHeader != KnownHeaders.Host && knownHeader != KnownHeaders.Connection && knownHeader != KnownHeaders.Upgrade && knownHeader != KnownHeaders.ProxyConnection)
                    {
                        if (header.Key.KnownHeader == KnownHeaders.TE)
                        {
                            // HTTP/2 allows only 'trailers' TE header. rfc7540 8.1.2.2
                            // HTTP/3 does not mention this one way or another; assume it has the same rule.
                            foreach (string value in headerValues)
                            {
                                if (string.Equals(value, "trailers", StringComparison.OrdinalIgnoreCase))
                                {
                                    BufferLiteralHeaderWithoutNameReference("TE", value);
                                    break;
                                }
                            }
                            continue;
                        }

                        string separator = null;
                        if (headerValues.Length > 1)
                        {
                            HttpHeaderParser parser = header.Key.Parser;
                            if (parser != null && parser.SupportsMultipleValues)
                            {
                                separator = parser.Separator;
                            }
                            else
                            {
                                separator = HttpHeaderParser.DefaultSeparator;
                            }
                        }

                        BufferLiteralHeaderWithoutNameReference(knownHeader.Name, headerValues, separator);
                    }
                }
                else
                {
                    // The header is not known: fall back to just encoding the header name and value(s).
                    BufferLiteralHeaderWithoutNameReference(header.Key.Name, headerValues, ", ");
                }
            }
        }

        private void BufferIndexedHeader(int index)
        {
            int bytesWritten;
            while (!QPackEncoder.EncodeIndexedHeaderField(index, _buffer.AvailableSpan, out bytesWritten))
            {
                _buffer.Grow();
            }
            _buffer.Commit(bytesWritten);
        }

        private void BufferLiteralHeaderWithStaticNameReference(int nameIndex, string value)
        {
            int bytesWritten;
            while (!QPackEncoder.EncodeLiteralHeaderFieldWithStaticNameReference(nameIndex, value, _buffer.AvailableSpan, out bytesWritten))
            {
                _buffer.Grow();
            }
            _buffer.Commit(bytesWritten);
        }

        private void BufferLiteralHeaderWithoutNameReference(string name, ReadOnlySpan<string> values, string separator)
        {
            int bytesWritten;
            while (!QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReference(name, values, separator, _buffer.AvailableSpan, out bytesWritten))
            {
                _buffer.Grow();
            }
            _buffer.Commit(bytesWritten);
        }

        private void BufferLiteralHeaderWithoutNameReference(string name, string value)
        {
            int bytesWritten;
            while (!QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReference(name, value, _buffer.AvailableSpan, out bytesWritten))
            {
                _buffer.Grow();
            }
            _buffer.Commit(bytesWritten);
        }

        private void BufferFrameEnvelope(Http3FrameType frameType, long payloadLength)
        {
            int bytesWritten;
            while (!Http3Frame.TryWriteFrameEnvelope(frameType, payloadLength, _buffer.AvailableSpan, out bytesWritten))
            {
                _buffer.Grow();
            }
            _buffer.Commit(bytesWritten);
        }

        private void BufferBytes(ReadOnlySpan<byte> span)
        {
            _buffer.EnsureAvailableSpace(span.Length);
            span.CopyTo(_buffer.AvailableSpan);
            _buffer.Commit(span.Length);
        }

        private async ValueTask<(Http3FrameType? frameType, long payloadLength)> ReadFrameEnvelopeAsync(CancellationToken cancellationToken)
        {
            long frameType, payloadLength;
            int bytesRead;

            while (!Http3Frame.TryReadIntegerPair(_buffer.ActiveSpan, out frameType, out payloadLength, out bytesRead))
            {
                _buffer.EnsureAvailableSpace(2);
                bytesRead = await _stream.ReadAsync(_buffer.AvailableMemory, cancellationToken).ConfigureAwait(false);

                if (bytesRead != 0)
                {
                    _buffer.Commit(bytesRead);
                }
                else if (_buffer.ActiveLength == 0)
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

            _buffer.Discard(bytesRead);

            return ((Http3FrameType)frameType, payloadLength);
        }

        private async ValueTask ReadHeadersAsync(long headersLength, CancellationToken cancellationToken)
        {
            while (headersLength != 0)
            {
                if (_buffer.ActiveLength == 0)
                {
                    _buffer.EnsureAvailableSpace(1);

                    int bytesRead = await _stream.ReadAsync(_buffer.AvailableMemory, cancellationToken).ConfigureAwait(false);
                    if (bytesRead != 0)
                    {
                        _buffer.Commit(bytesRead);
                    }
                    else
                    {
                        throw new HttpRequestException("Partial frame received; unexpected end of stream.");
                    }
                }

                int processLength = (int)Math.Min(headersLength, _buffer.ActiveLength);

                _headerDecoder.Decode(_buffer.ActiveSpan.Slice(processLength), this);
                _buffer.Discard(processLength);
                headersLength -= processLength;
            }
        }

        void IHttpHeadersHandler.OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {

        }

        void IHttpHeadersHandler.OnHeadersComplete(bool endStream)
        {
            throw new NotImplementedException();
        }

        private async ValueTask ReadDataAsync(long dataLength, CancellationToken cancellationToken)
        {
        }

        private async ValueTask SkipUnknownPayloadAsync(Http3FrameType frameType, long payloadLength, CancellationToken cancellationToken)
        {
            if (payloadLength > MaximumUnknownFramePayloadLength)
            {
                throw new HttpRequestException($"Frame type {frameType} with payload of length {payloadLength} exceeds maximum unknown frame payload length of of {MaximumUnknownFramePayloadLength}.");
            }

            while (payloadLength != 0)
            {
                if (_buffer.ActiveLength == 0)
                {
                    _buffer.EnsureAvailableSpace(1);
                    int bytesRead = await _stream.ReadAsync(_buffer.AvailableMemory, cancellationToken).ConfigureAwait(false);

                    if (bytesRead != 0)
                    {
                        _buffer.Commit(bytesRead);
                    }
                    else
                    {
                        // Our buffer has partial frame data in it but not enough to complete the read: bail out.
                        throw new HttpRequestException("Partial frame received; unexpected end of stream.");
                    }
                }

                long readLength = Math.Min(payloadLength, _buffer.ActiveLength);
                _buffer.Discard((int)readLength);
                payloadLength -= readLength;
            }
        }

        private sealed class Http3WriteStream : HttpBaseStream
        {
            private QuicStream _stream;
            private long _firstFrameLengthRemaining;
            private byte[] _dataFrame;

            public override bool CanRead => false;

            public override bool CanWrite => true;

            public long FirstFrameLengthRemaining => _firstFrameLengthRemaining;

            public Http3WriteStream(QuicStream stream, long firstFrameLengthRemaining)
            {
                _stream = stream;
                _firstFrameLengthRemaining = firstFrameLengthRemaining;
            }

            protected override void Dispose(bool disposing)
            {
                _stream = null;
                base.Dispose(disposing);
            }

            public override int Read(Span<byte> buffer)
            {
                throw new NotImplementedException();
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                QuicStream stream = _stream;

                if (stream == null)
                {
                    throw new ObjectDisposedException(nameof(Http3WriteStream));
                }

                if (buffer.Length == 0)
                {
                    return;
                }

                long remaining = _firstFrameLengthRemaining;
                if (remaining > 0)
                {
                    // This HttpContent had a precomputed length, and a data frame was written as part of the headers. We're writing against the first frame.

                    int sendLength = (int)Math.Min(remaining, buffer.Length);
                    await stream.WriteAsync(buffer.Slice(0, sendLength), cancellationToken).ConfigureAwait(false);

                    buffer = buffer.Slice(sendLength);
                    _firstFrameLengthRemaining -= sendLength;

                    if (buffer.Length == 0)
                    {
                        return;
                    }
                }

                // This is only reached if the HttpContent couldn't precompute its length, or if it
                // writes more bytes than was precomputed (this latter case is probably an error -- TODO).
                // In this case, each write results in a data frame. TODO: optimize this to avoid small frame sizes.

                byte[] dataFrame = _dataFrame;

                if (_dataFrame == null)
                {
                    _dataFrame = dataFrame = new byte[9];
                }

                bool serializeFrameSuccess = Http3Frame.TryWriteFrameEnvelope(Http3FrameType.Data, buffer.Length, dataFrame, out int frameLength);
                Debug.Assert(serializeFrameSuccess == true);

                // TODO: gathered write.
                await stream.WriteAsync(dataFrame, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _stream.FlushAsync(cancellationToken);
            }
        }
    }
}
