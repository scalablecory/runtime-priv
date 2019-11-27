using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http.System.Net.Http.Headers;
using System.Resources;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Headers
{
    internal class AltSvcHeaderParser : BaseHeaderParser
    {
        public AltSvcHeaderParser()
            : base(true)
        {
        }

        protected override int GetParsedValueLength(string value, int startIndex, object storeValue,
            out object parsedValue)
        {
            Debug.Assert(startIndex >= 0);

            if (string.IsNullOrEmpty(value) || startIndex >= value.Length)
            {
                parsedValue = null;
                return 0;
            }

            int idx = startIndex;

            int alpnProtocolNameLength = HttpRuleParser.GetTokenLength(value, idx);
            string alpnProtocolName = ReadPercentEncodedAlpnProtocolName(value.AsSpan(idx, alpnProtocolNameLength));
            if (alpnProtocolName == null)
            {
                throw new Exception("Unknown ALPN protocol name.");
            }
            idx += alpnProtocolNameLength;

            // Clear should be used alone with no parameters.
            if (ReferenceEquals(alpnProtocolName, "clear"))
            {
                var ret = new AltSvcHeaderValue();
                ret.Alternatives.Add(AlternateService.Clear);

                parsedValue = ret;
                return idx - startIndex;
            }

            if (idx == value.Length || value[idx++] != '=')
            {
                throw new Exception("Invalid alternative, expected '='.");
            }

            if (!TryReadAltAuthority(value, idx, out string altAuthorityHost, out int altAuthorityPort, out int altAuthorityLength))
            {
                throw new Exception("Invalid alt-authority, excepected quoted string.");
            }
            idx += altAuthorityLength;

            // Parse parameters.
            int? maxAge = null;

            while (idx != value.Length)
            {
                // Skip OWS.
                while (idx != value.Length && IsOptionalWhiteSpace(value[idx])) ++idx;

                // Get the parameter key length.
                int tokenLength = HttpRuleParser.GetTokenLength(value, idx);
                if (tokenLength == 0)
                {
                    // End of header.
                    break;
                }

                if (value[idx + tokenLength + 1] != '=')
                {
                    throw new Exception("Expected = in parameter.");
                }

                if (tokenLength == 2 && value[idx] == 'm' && value[idx + 1] == 'a')
                {
                    // Parse "ma" or Max Age.

                    idx += 3;
                    if (TryReadTokenOrQuotedInt32(value, idx, out int maxAgeTmp, out int parameterLength))
                    {
                        // TODO: what is behavior if duplicate parameter found?
                        maxAge ??= maxAgeTmp;
                    }
                    idx += parameterLength;
                }
                else
                {
                    // Some unknown parameter.
                    idx += tokenLength + 1;
                    if (!TrySkipTokenOrQuoted(value, idx, out int parameterLength))
                    {
                    }

                }


            }
        }

        private static bool IsOptionalWhiteSpace(char ch)
        {
            return ch == ' ' || ch == '\t';
        }

        private static string ReadPercentEncodedAlpnProtocolName(ReadOnlySpan<char> span)
        {
            switch (span.Length)
            {
                case 2:
                    if (span[0] == 'h')
                    {
                        char ch = span[1];
                        if (ch == '3') return "h3";
                        if (ch == '2') return "h2";
                    }
                    goto default;
                case 3:
                    if (span[0] == 'h' && span[1] == '2' && span[2] == 'c')
                    {
                        return "h2c";
                    }
                    goto default;
                case 5:
                    if (span.SequenceEqual("clear")) return "clear";
                    goto default;
                case 10:
                    if (span.StartsWith("http%2") && span[7] == '1' && span[8] == '.')
                    {
                        char ch = span[6];
                        if (ch == 'F' || ch == 'f')
                        {
                            ch = span[9];
                            if (ch == '1') return "http/1.1";
                            if (ch == '0') return "http/1.0";
                        }
                    }

                    goto default;
                default:
                    // Unrecognized ALPN protocol name.
                    // It could be percent-decoded and returned, but that's not needed given current implementation.
                    return null;
            }
        }

        private static bool TryReadAltAuthority(string value, int startIndex, out string host, out int port, out int readLength)
        {
            if (HttpRuleParser.GetQuotedStringLength(value, startIndex, out int quotedLength) != HttpParseResult.Parsed)
            {
                goto parseError;
            }

            Debug.Assert(value[startIndex] == '"' && value[startIndex + quotedLength - 1] == '"', $"{nameof(HttpRuleParser.GetQuotedStringLength)} should return {nameof(HttpParseResult.NotParsed)} if the opening/closing quotes are missing.");
            ReadOnlySpan<char> quoted = value.AsSpan(startIndex + 1, quotedLength - 2);

            int idx = quoted.IndexOf(':');
            if (idx == -1)
            {
                goto parseError;
            }

            // Parse out the port.
            if (!TryReadQuotedInt32Value(quoted.Slice(idx + 1), out port))
            {
                goto parseError;
            }

            // Parse out the optional host.
            if (--idx == 0)
            {
                host = null;
            }
            else if (!TryReadQuotedValue(quoted.Slice(0, idx), out host))
            {
                goto parseError;
            }

            readLength = quotedLength;
            return true;

        parseError:
            host = null;
            port = 0;
            readLength = 0;
            return false;
        }

        private static bool TryReadQuotedValue(ReadOnlySpan<char> value, out string result)
        {
            int idx = value.IndexOf('\\');

            if (idx == -1)
            {
                // Hostnames shouldn't require quoted pairs, so this should be the hot path.
                result = value.Length != 0 ? new string(value) : null;
                return true;
            }

            var builder = new ValueStringBuilder(stackalloc char[128]);

            do
            {
                if (idx + 1 == value.Length)
                {
                    // quoted-pair requires two characters: the quote, and the quoted character.
                    builder.Dispose();
                    result = null;
                    return false;
                }

                if (idx != 0)
                {
                    builder.Append(value.Slice(0, idx));
                }

                builder.Append(value[idx + 1]);

                value = value.Slice(idx + 2);
                idx = value.IndexOf('\\');
            }
            while (idx != -1);

            if (value.Length != 0)
            {
                builder.Append(value);
            }

            result = builder.ToString();
            return true;
        }

        private static bool TryReadTokenOrQuotedInt32(string value, int startIndex, out int result, out int readLength)
        {
            if (startIndex >= value.Length)
            {
                result = 0;
                readLength = 0;
                return false;
            }

            if (HttpRuleParser.IsTokenChar(value[startIndex]))
            {
                // No reason for integers to be quoted, so this should be the hot path.

                int tokenLength = HttpRuleParser.GetTokenLength(value, startIndex);

                readLength = tokenLength;
                return HeaderUtilities.TryParseInt32(value, startIndex, tokenLength, out result);
            }

            if (HttpRuleParser.GetQuotedStringLength(value, startIndex, out int quotedLength) == HttpParseResult.Parsed)
            {
                readLength = quotedLength;
                return TryReadQuotedInt32Value(value.AsSpan(1, quotedLength - 2), out result);
            }

            result = 0;
            readLength = 0;
            return false;
        }

        private static bool TryReadQuotedInt32Value(ReadOnlySpan<char> value, out int result)
        {
            if (value.Length == 0)
            {
                result = 0;
                return false;
            }

            int port = 0;

            foreach (char ch in value)
            {
                // The port shouldn't ever need a quoted-pair, but they're still valid... skip if found.
                if (ch == '\\') continue;

                if (ch < '0' || ch > '9')
                {
                    result = 0;
                    return false;
                }

                long portTmp = port * 10L + (ch - '0');

                if (portTmp > int.MaxValue)
                {
                    result = 0;
                    return false;
                }

                port = (int)portTmp;
            }

            result = port;
            return true;
        }

        private static bool TrySkipTokenOrQuoted(string value, int startIndex, out int readLength)
        {
            if (startIndex >= value.Length)
            {
                readLength = 0;
                return false;
            }

            if (HttpRuleParser.IsTokenChar(value[startIndex]))
            {
                readLength = HttpRuleParser.GetTokenLength(value, startIndex);
                return true;
            }

            if (HttpRuleParser.GetQuotedStringLength(value, startIndex, out int quotedLength) == HttpParseResult.Parsed)
            {
                readLength = quotedLength;
                return true;
            }

            readLength = 0;
            return false;
        }
    }
}
