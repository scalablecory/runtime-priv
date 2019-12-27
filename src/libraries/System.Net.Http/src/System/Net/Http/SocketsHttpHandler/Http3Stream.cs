// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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
    internal sealed class Http3Stream
    {
        private Http3Connection _connection;
        private QuicStream _stream;
        private ArrayBuffer _sendBuffer;

        /// <summary>Reusable array used to get the values for each header being written to the wire.</summary>
        private string[] _headerValues = Array.Empty<string>();

        public Http3Stream(Http3Connection connection, QuicStream stream)
        {
            _connection = connection;
            _stream = stream;
            _sendBuffer = new ArrayBuffer(initialSize: 64, usePool: true);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SerializeHeaders(request);

            bool singleDataFrame = false;
            if (request.Content != null && request.Content.TryComputeLength(out long contentLength))
            {
                WriteFrameEnvelope((byte)Http3FrameType.Data, contentLength);
                //TODO: send content as gather op if possible.
                singleDataFrame = true;
            }

            await _stream.WriteAsync(_sendBuffer.ActiveMemory, cancellationToken).ConfigureAwait(false);

            if (request.Content != null)
            {
                if (singleDataFrame)
                {
                    // write content directly.
                }
                else
                {
                    // write content with multiple data frames.
                }
            }
        }

        private void SerializeHeaders(HttpRequestMessage request)
        {
            // Reserve space for frame type + payload length.
            // These values will be written after headers are serialized, as the variable-length payload is an int.
            const int PreHeadersReserveSpace = 1 + 8;

            // This should be the first write to our buffer.
            Debug.Assert(_sendBuffer.ActiveLength == 0);

            // Reserve space for header frame envelope.
            _sendBuffer.Commit(PreHeadersReserveSpace);

            // Add header block prefix. We aren't using dynamic table, so these are simple zeroes.
            _sendBuffer.AvailableSpan[0] = 0x00; // required insert count.
            _sendBuffer.AvailableSpan[1] = 0x00; // s + delta base.
            _sendBuffer.Commit(2);

            HttpMethod normalizedMethod = HttpMethod.Normalize(request.Method);
            if (normalizedMethod.Http3EncodedBytes != null)
            {
                WriteBytes(normalizedMethod.Http3EncodedBytes);
            }
            else
            {
                WriteLiteralHeaderWithStaticNameReference(H3StaticTable.MethodGet, normalizedMethod.Method);
            }

            WriteIndexedHeader(H3StaticTable.SchemeHttps);

            if (request.HasHeaders && request.Headers.Host != null)
            {
                WriteLiteralHeaderWithStaticNameReference(H3StaticTable.Authority, request.Headers.Host);
            }
            else
            {
                WriteBytes(_connection.Pool._http3EncodedAuthorityHostHeader);
            }

            string pathAndQuery = request.RequestUri.PathAndQuery;
            if (pathAndQuery == "/")
            {
                WriteIndexedHeader(H3StaticTable.PathSlash);
            }
            else
            {
                WriteLiteralHeaderWithStaticNameReference(H3StaticTable.PathSlash, pathAndQuery);
            }

            if (request.HasHeaders)
            {
                // H3 does not support Transfer-Encoding: chunked.
                request.Headers.TransferEncodingChunked = false;

                WriteHeaderCollection(request.Headers);
            }

            if (_connection.Pool.Settings._useCookies)
            {
                string cookiesFromContainer = _connection.Pool.Settings._cookieContainer.GetCookieHeader(request.RequestUri);
                if (cookiesFromContainer != string.Empty)
                {
                    WriteLiteralHeaderWithStaticNameReference(H3StaticTable.Cookie, cookiesFromContainer);
                }
            }

            if (request.Content == null)
            {
                if (normalizedMethod.MustHaveRequestBody)
                {
                    WriteIndexedHeader(H3StaticTable.ContentLength0);
                }
            }
            else
            {
                WriteHeaderCollection(request.Content.Headers);
            }

            // Determine our header envelope size.
            // The reserved space was the maximum required; discard what wasn't used.
            int headersLength = _sendBuffer.ActiveLength - PreHeadersReserveSpace;
            int headersLengthEncodedSize = VariableLengthIntegerHelper.GetByteCount(headersLength);
            _sendBuffer.Discard(PreHeadersReserveSpace - headersLengthEncodedSize - 1);

            // Encode header type in first byte, and payload length in subsequent bytes.
            _sendBuffer.ActiveSpan[0] = (byte)Http3FrameType.Headers;
            int actualHeadersLengthEncodedSize = VariableLengthIntegerHelper.WriteInteger(_sendBuffer.ActiveSpan.Slice(1, headersLengthEncodedSize), headersLength);
            Debug.Assert(actualHeadersLengthEncodedSize == headersLengthEncodedSize);
        }

        // TODO: use known headers to preencode names.
        // TODO: special-case Content-Type for static table values values.
        private void WriteHeaderCollection(HttpHeaders headers)
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
                                    WriteLiteralHeaderWithoutNameReference("TE", value);
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

                        WriteLiteralHeaderWithoutNameReference(knownHeader.Name, headerValues, separator);
                    }
                }
                else
                {
                    // The header is not known: fall back to just encoding the header name and value(s).
                    WriteLiteralHeaderWithoutNameReference(header.Key.Name, headerValues, ", ");
                }
            }
        }

        private void WriteIndexedHeader(int index)
        {
            int bytesWritten;
            while (!QPackEncoder.EncodeIndexedHeaderField(index, _sendBuffer.AvailableSpan, out bytesWritten))
            {
                _sendBuffer.Grow();
            }
            _sendBuffer.Commit(bytesWritten);
        }

        private void WriteLiteralHeaderWithStaticNameReference(int nameIndex, string value)
        {
            int bytesWritten;
            while (!QPackEncoder.EncodeLiteralHeaderFieldWithStaticNameReference(nameIndex, value, _sendBuffer.AvailableSpan, out bytesWritten))
            {
                _sendBuffer.Grow();
            }
            _sendBuffer.Commit(bytesWritten);
        }

        private void WriteLiteralHeaderWithoutNameReference(string name, ReadOnlySpan<string> values, string separator)
        {
            int bytesWritten;
            while (!QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReference(name, values, separator, _sendBuffer.AvailableSpan, out bytesWritten))
            {
                _sendBuffer.Grow();
            }
            _sendBuffer.Commit(bytesWritten);
        }

        private void WriteLiteralHeaderWithoutNameReference(string name, string value)
        {
            int bytesWritten;
            while (!QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReference(name, value, _sendBuffer.AvailableSpan, out bytesWritten))
            {
                _sendBuffer.Grow();
            }
            _sendBuffer.Commit(bytesWritten);
        }

        private void WriteFrameEnvelope(byte preEncodedFrameTypeId, long payloadLength)
        {
            int bytesWritten;
            while (!Http3Frame.TryWriteFrameEnvelope(preEncodedFrameTypeId, payloadLength, _sendBuffer.AvailableSpan, out bytesWritten))
            {
                _sendBuffer.Grow();
            }
            _sendBuffer.Commit(bytesWritten);
        }

        private void WriteBytes(ReadOnlySpan<byte> span)
        {
            _sendBuffer.EnsureAvailableSpace(span.Length);
            span.CopyTo(_sendBuffer.AvailableSpan);
            _sendBuffer.Commit(span.Length);
        }
    }
}
