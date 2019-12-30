// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Net.Http.QPack;

namespace System.Net.Http
{
    internal sealed class Http3Stream : IHttpHeadersHandler, IAsyncDisposable, IDisposable
    {
        /// <summary>
        /// Unknown frame types with a payload larger than this will result in tearing down the connection.
        /// Frames smaller than this will be ignored and drained.
        /// </summary>
        private const int MaximumUnknownFramePayloadLength = 4096;

        private readonly HttpRequestMessage _request;
        private readonly Http3Connection _connection;
        private QuicStream _stream;
        private ArrayBuffer _buffer;
        private TaskCompletionSource<bool> _expect100ContinueCompletionSource; // true indicates we should send content (e.g. received 100 Continue).
        private QPackDecoder _headerDecoder = new QPackDecoder(0, 0);
        private State _headerState;
        private HttpResponseMessage _response;

        /// <summary>Reusable array used to get the values for each header being written to the wire.</summary>
        private string[] _headerValues = Array.Empty<string>();

        /// <summary>Any trailing headers.</summary>
        private List<(HeaderDescriptor name, string value)> _trailingHeaders;

        public Http3Stream(HttpRequestMessage request, Http3Connection connection, QuicStream stream)
        {
            _request = request;
            _connection = connection;
            _stream = stream;
            _buffer = new ArrayBuffer(initialSize: 64, usePool: true);
        }

        public void Dispose()
        {
            if (_stream != null)
            {
                _stream.Dispose();
                _buffer.Dispose();
                _stream = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_stream != null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _buffer.Dispose();
                _stream = null;
            }
        }

        public async Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken)
        {
            BufferHeaders(_request);

            long firstFrameLengthRemaining = 0;

            if (_request.Content != null && _request.Content.TryComputeLength(out firstFrameLengthRemaining))
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
            else if (_request.Content == null)
            {
                _stream.ShutdownWrite();
            }

            // Empty the buffer so it can be used for receive.
            _buffer.Discard(_buffer.ActiveLength);
            Debug.Assert(_buffer.ActiveLength == 0);

            // If using Expect 100 Continue, setup a TCS to wait to send content until we get a response.
            if (_request.HasHeaders && _request.Headers.ExpectContinue == true)
            {
                _expect100ContinueCompletionSource = new TaskCompletionSource<bool>();
            }

            // If using duplex content, the content will continue sending after this method completes.
            // So, only observe the cancellation token if not using duplex.
            CancellationToken sendContentCancellationToken = _request.Content?.AllowDuplex == false ? cancellationToken : default;

            // In parallel, send content and read response.
            // Depending on Expect 100 Continue usage, one will depend on the other making progress.
            Task sendContentTask = _request.Content != null ? SendContentAsync(_request.Content, firstFrameLengthRemaining, sendContentCancellationToken) : Task.CompletedTask;
            Task readResponseTask = ReadResponseAsync(cancellationToken);

            // If we're not doing duplex, wait for content to finish sending here.
            // If we are doing duplex and have the unlikely event that it completes here, observe the result.
            // See Http2Connection.SendAsync for a full comment on this logic -- it is identical behavior.
            if (sendContentTask.IsCompleted ||
                _request.Content?.AllowDuplex != true ||
                sendContentTask == await Task.WhenAny(sendContentTask, readResponseTask).ConfigureAwait(false) ||
                sendContentTask.IsCompleted)
            {
                try
                {
                    await sendContentTask.ConfigureAwait(false);
                }
                catch
                {
                    // Exceptions will be bubbled up from sendContentTask here,
                    // which means the result of readResponseTask won't be observed directly:
                    // Do a background await to log any exceptions.
                    _connection.LogExceptions(readResponseTask);
                    throw;
                }
            }
            else
            {
                // Duplex is being used, so we can't wait for content to finish sending.
                // Do a background await to log any exceptions.
                _connection.LogExceptions(sendContentTask);
            }

            // Wait for the response headers to be read.
            await readResponseTask.ConfigureAwait(false);

            // Set our content stream.
            var responseContent = (HttpConnectionResponseContent)_response.Content;
            if (responseContent.Headers.ContentLength == 0)
            {
                // For 0-length content, drain the response frames to read any trailing headers.
                await DrainContentLength0Frames(cancellationToken).ConfigureAwait(false);
                responseContent.SetStream(EmptyReadStream.Instance);
            }
            else
            {
                // For responses that may have content, a read stream is required to finish up the request.
                responseContent.SetStream(new Http3ReadStream(this));
            }

            // Process any Set-Cookie headers.
            if (_connection.Pool.Settings._useCookies)
            {
                CookieHelper.ProcessReceivedCookies(_response, _connection.Pool.Settings._cookieContainer);
            }

            // To avoid a circular reference, null out the stream's response.
            HttpResponseMessage response = _response;
            _response = null;
            return response;
        }

        /// <summary>
        /// Waits for the initial response headers to be completed, including e.g. Expect 100 Continue.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task ReadResponseAsync(CancellationToken cancellationToken)
        {
            do
            {
                _headerState = State.StatusHeader;

                (Http3FrameType? frameType, long payloadLength) = await ReadFrameEnvelopeAsync(cancellationToken).ConfigureAwait(false);

                if (frameType != Http3FrameType.Headers)
                {
                    throw new HttpRequestException($"Expected HEADERS frame, got {frameType}.");
                }

                await ReadHeadersAsync(payloadLength, cancellationToken).ConfigureAwait(false);
            }
            while ((int)_response.StatusCode < 200);

            _headerState = State.TrailingHeaders;
        }

        private async Task SendContentAsync(HttpContent content, long firstFrameLengthRemaining, CancellationToken cancellationToken)
        {
            // If we're using Expect 100 Continue, wait to send content
            // until we get a response back or until our timeout elapses.
            if (_expect100ContinueCompletionSource != null)
            {
                Timer timer = null;

                try
                {
                    if (_connection.Pool.Settings._expect100ContinueTimeout != Timeout.InfiniteTimeSpan)
                    {
                        timer = new Timer(o =>
                        {
                            ((Http3Stream)o)._expect100ContinueCompletionSource.TrySetResult(true);
                        }, this, _connection.Pool.Settings._expect100ContinueTimeout, Timeout.InfiniteTimeSpan);
                    }

                    if (!await _expect100ContinueCompletionSource.Task.ConfigureAwait(false))
                    {
                        // We received an error response code, so the body should not be sent.
                        return;
                    }
                }
                finally
                {
                    if (timer != null)
                    {
                        await timer.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            using (var writeStream = new Http3WriteStream(_stream, content.Headers.ContentLength, firstFrameLengthRemaining))
            {
                await content.CopyToAsync(writeStream, null, cancellationToken).ConfigureAwait(false);
            }
            _stream.ShutdownWrite();
        }

        private async ValueTask DrainContentLength0Frames(CancellationToken cancellationToken)
        {
            Http3FrameType? frameType;
            long payloadLength;

            while (true)
            {
                (frameType, payloadLength) = await ReadFrameEnvelopeAsync(cancellationToken).ConfigureAwait(false);

                switch (frameType)
                {
                    case null:
                        // Done receiving, shutdown stream, dispose of buffer, and copy over trailing headers.
                        _stream.ShutdownRead();
                        _buffer.Dispose();

                        CopyTrailersToResponseMessage(_response);
                        return;
                    case Http3FrameType.Headers:
                        // Pick up any trailing headers.
                        await ReadHeadersAsync(payloadLength, cancellationToken).ConfigureAwait(false);


                        // Stop looping after a trailing header.
                        // There may be extra frames after this one, but they would all be unknown extension
                        // frames that can be safely ignored. Just stop reading here.
                        goto case null;
                    case Http3FrameType.Data:
                        // The sum of data frames must equal content length. Because this method is only
                        // called for a Content-Length of 0, anything other than 0 here would be an error.
                        // Per spec, 0-length payload is allowed.
                        if (payloadLength != 0)
                        {
                            throw new HttpRequestException($"Content larger than content-length.");
                        }
                        break;
                    default:
                        Debug.Fail($"Recieved unexpected frame type {frameType}.");
                        return;
                }
            }
        }

        private void CopyTrailersToResponseMessage(HttpResponseMessage responseMessage)
        {
            if (_trailingHeaders?.Count > 0)
            {
                foreach ((HeaderDescriptor name, string value) in _trailingHeaders)
                {
                    responseMessage.TrailingHeaders.TryAddWithoutValidation(name, value);
                }
                _trailingHeaders.Clear();
            }
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
                // Expect 100 Continue requires content.
                request.Headers.ExpectContinue = null;

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

            while (true)
            {
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

                switch ((Http3FrameType)frameType)
                {
                    case Http3FrameType.Headers:
                    case Http3FrameType.Data:
                        return ((Http3FrameType)frameType, payloadLength);
                    case Http3FrameType.Settings:
                    case Http3FrameType.GoAway:
                    case Http3FrameType.MaxPushId:
                        // These frames should only be received on a control stream, not a response stream.
                        // TODO: close the connection with H3_FRAME_UNEXPECTED.
                        throw new HttpRequestException($"Server sent inappropriate frame type {frameType}.");
                    case Http3FrameType.DuplicatePush:
                    case Http3FrameType.PushPromise:
                    case Http3FrameType.CancelPush:
                        // Because we haven't sent any MAX_PUSH_ID frames, any of these push-related
                        // frames that the server sends will have an out-of-range push ID.
                        // TODO: close the conenction with H3_ID_ERROR.
                        throw new HttpRequestException($"Server sent inappropriate frame type {frameType}.");
                    default:
                        // Unknown frame types should be skipped.
                        await SkipUnknownPayloadAsync((Http3FrameType)frameType, payloadLength, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
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

        private static ReadOnlySpan<byte> StatusHeaderNameBytes => new byte[] { (byte)'s', (byte)'t', (byte)'a', (byte)'t', (byte)'u', (byte)'s' };

        void IHttpHeadersHandler.OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            Debug.Assert(name.Length > 0);

            if (name[0] == ':')
            {
                if (!name.Slice(1).SequenceEqual(StatusHeaderNameBytes))
                {
                    throw new HttpRequestException(SR.net_http_invalid_response);
                }

                if (_headerState != State.StatusHeader)
                {
                    if (NetEventSource.IsEnabled) Trace("Received extra status header.");
                    throw new HttpRequestException(SR.Format(SR.net_http_invalid_response_status_code, "duplicate status"));
                }

                int statusCode = HttpConnectionBase.ParseStatusCode(value);

                _response = new HttpResponseMessage()
                {
                    Version = HttpVersion.Version30,
                    RequestMessage = _request,
                    Content = new HttpConnectionResponseContent(),
                    StatusCode = (HttpStatusCode)statusCode
                };

                if (statusCode < 200)
                {
                    // Expect 100 continue response.
                    _headerState = State.SkipExpect100Headers;

                    if (_response.StatusCode == HttpStatusCode.Continue && _expect100ContinueCompletionSource != null)
                    {
                        _expect100ContinueCompletionSource.TrySetResult(true);
                    }
                }
                else
                {
                    _headerState = State.ResponseHeaders;
                    if (_expect100ContinueCompletionSource != null)
                    {
                        // If the final status code is >= 300, skip sending the body.
                        bool shouldSendBody = (statusCode < 300);

                        if (NetEventSource.IsEnabled) Trace($"Expecting 100 Continue but received final status {statusCode}.");
                        _expect100ContinueCompletionSource.TrySetResult(shouldSendBody);
                    }
                }
            }
            else if (_headerState == State.SkipExpect100Headers)
            {
                // Ignore any headers that came as part of a 100 Continue response.
                return;
            }
            else
            {
                if (!HeaderDescriptor.TryGet(name, out HeaderDescriptor descriptor))
                {
                    // Invalid header name
                    throw new HttpRequestException(SR.Format(SR.net_http_invalid_response_header_name, Encoding.ASCII.GetString(name)));
                }

                string headerValue = descriptor.GetHeaderValue(value);

                switch (_headerState)
                {
                    case State.ResponseHeaders when descriptor.HeaderType.HasFlag(HttpHeaderType.Content):
                        _response.Content.Headers.TryAddWithoutValidation(descriptor, headerValue);
                        break;
                    case State.ResponseHeaders:
                        _response.Headers.TryAddWithoutValidation(descriptor.HeaderType.HasFlag(HttpHeaderType.Request) ? descriptor.AsCustomHeader() : descriptor, headerValue);
                        break;
                    case State.TrailingHeaders:
                        _response.TrailingHeaders.TryAddWithoutValidation(descriptor.HeaderType.HasFlag(HttpHeaderType.Request) ? descriptor.AsCustomHeader() : descriptor, headerValue);
                        break;
                    default:
                        Debug.Fail($"Unexpected {nameof(Http3Stream)}.{nameof(_headerState)} '{_headerState}'.");
                        break;
                }
            }
        }

        void IHttpHeadersHandler.OnHeadersComplete(bool endStream)
        {
            throw new NotImplementedException();
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

        public void Trace(string message, [CallerMemberName] string memberName = null) =>
            _connection.Trace(_stream.StreamId, message, memberName);

        private sealed class Http3ReadStream : HttpBaseStream
        {
            private Http3Stream _stream;
            private HttpResponseMessage _response;

            public override bool CanRead => true;

            public override bool CanWrite => false;

            public Http3ReadStream(Http3Stream stream)
            {
                _stream = stream;
            }

            protected override void Dispose(bool disposing)
            {
                if (_stream != null)
                {
                    _stream.Dispose();
                    _stream = null;
                    _response = null;
                }
            }

            public override async ValueTask DisposeAsync()
            {
                if (_stream != null)
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                    _stream = null;
                    _response = null;
                }
            }

            // This uses sync over async but if QUIC implementation will just do that anyway, doing it here may not matter.
            public override int Read(Span<byte> buffer)
            {
                if (_stream == null)
                {
                    throw new ObjectDisposedException(nameof(Http3Stream));
                }

                if (_response == null)
                {
                    // Response is nulled out on EOS.
                    return 0;
                }

                Http3FrameType? frameType;
                long payloadLength;
                int totalRead = 0;

                while (!buffer.IsEmpty)
                {
                    (frameType, payloadLength) = _stream.ReadFrameEnvelopeAsync(CancellationToken.None).GetAwaiter().GetResult();

                    switch (frameType)
                    {
                        case Http3FrameType.Data:
                            // There was probably some data read into our recv buffer, so copy that out first.
                            int copyLength = Math.Min(_stream._buffer.ActiveLength, buffer.Length);
                            _stream._buffer.ActiveSpan.Slice(copyLength).CopyTo(buffer);
                            _stream._buffer.Discard(copyLength);
                            buffer = buffer.Slice(copyLength);
                            payloadLength -= copyLength;
                            totalRead += copyLength;

                            // Read out any remaining part of the payload directly into the buffer.
                            while (!buffer.IsEmpty && payloadLength != 0)
                            {
                                copyLength = _stream._stream.Read(buffer);
                                buffer = buffer.Slice(copyLength);
                                payloadLength -= copyLength;
                                totalRead += copyLength;
                            }
                            break;
                        case Http3FrameType.Headers:
                            // Read any trailing headers.
                            _stream.ReadHeadersAsync(payloadLength, CancellationToken.None).GetAwaiter().GetResult();

                            // There may be more frames after this one, but they would all be unknown extension
                            // frames that we are allowed to skip. Just close the stream early.
                            goto case null;
                        case null:
                            // End of stream.
                            _stream._stream.ShutdownRead();
                            _stream._buffer.Dispose();
                            _stream.CopyTrailersToResponseMessage(_response);
                            break;
                    }

                }

                return totalRead;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_stream == null)
                {
                    throw new ObjectDisposedException(nameof(Http3Stream));
                }

                if (_response == null)
                {
                    // Response is nulled out on EOS.
                    return 0;
                }

                Http3FrameType? frameType;
                long payloadLength;
                int totalRead = 0;

                while (!buffer.IsEmpty)
                {
                    (frameType, payloadLength) = await _stream.ReadFrameEnvelopeAsync(cancellationToken).ConfigureAwait(false);

                    switch (frameType)
                    {
                        case Http3FrameType.Data:
                            // There was probably some data read into our recv buffer, so copy that out first.
                            int copyLength = Math.Min(_stream._buffer.ActiveLength, buffer.Length);
                            _stream._buffer.ActiveSpan.Slice(copyLength).CopyTo(buffer.Span);
                            _stream._buffer.Discard(copyLength);
                            buffer = buffer.Slice(copyLength);
                            payloadLength -= copyLength;
                            totalRead += copyLength;

                            // Read out any remaining part of the payload directly into the buffer.
                            while (!buffer.IsEmpty && payloadLength != 0)
                            {
                                copyLength = await _stream._stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                                buffer = buffer.Slice(copyLength);
                                payloadLength -= copyLength;
                                totalRead += copyLength;
                            }
                            break;
                        case Http3FrameType.Headers:
                            // Read any trailing headers.
                            await _stream.ReadHeadersAsync(payloadLength, cancellationToken).ConfigureAwait(false);

                            // There may be more frames after this one, but they would all be unknown extension
                            // frames that we are allowed to skip. Just close the stream early.
                            goto case null;
                        case null:
                            // End of stream.
                            _stream._stream.ShutdownRead();
                            _stream._buffer.Dispose();
                            _stream.CopyTrailersToResponseMessage(_response);
                            break;
                    }

                }

                return totalRead;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }
        }

        private sealed class Http3WriteStream : HttpBaseStream
        {
            private QuicStream _stream;
            private long? _contentLengthRemaining;
            private long _firstFrameLengthRemaining;
            private byte[] _dataFrame;

            public override bool CanRead => false;

            public override bool CanWrite => true;

            public long FirstFrameLengthRemaining => _firstFrameLengthRemaining;

            public Http3WriteStream(QuicStream stream, long? contentLength, long firstFrameLengthRemaining)
            {
                _stream = stream;
                _firstFrameLengthRemaining = firstFrameLengthRemaining;
                _contentLengthRemaining = contentLength;
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

                if (buffer.Length > _contentLengthRemaining)
                {
                    string msg = "Unable to write content larger than what was specified in Content-Length header.";
                    throw new IOException(msg, new HttpRequestException(msg));
                }
                _contentLengthRemaining -= buffer.Length;

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

        private enum State
        {
            StatusHeader,
            SkipExpect100Headers,
            ResponseHeaders,
            TrailingHeaders
        }
    }
}
