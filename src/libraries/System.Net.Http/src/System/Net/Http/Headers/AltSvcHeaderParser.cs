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

            int alpnProtocolNameLength = HttpRuleParser.GetTokenLength(value, startIndex);

            if (alpnProtocolNameLength == 0)
            {
                throw new Exception("Invalid ALPN protocol name: must not be empty.");
            }

            string alpnProtocolName = ReadPercentEncodedAlpnProtocolName(value.AsSpan(startIndex, alpnProtocolNameLength));

            // Clear should be used alone with no parameters.
            if (ReferenceEquals(alpnProtocolName, "clear"))
            {
                var ret = new AltSvcHeaderValue();
                ret.Alternatives.Add(AlternateService.Clear);

                parsedValue = ret;
                return alpnProtocolNameLength;
            }

            int pos = startIndex + alpnProtocolNameLength;

            if (value[pos] != '=')
            {
                throw new Exception("Invalid alternative, expected '='.");
            }

            // alt-authority as a quoted string.
            if (value[++pos] != '"')
            {
                int 
            }
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
                    if (span.StartsWith("http%2") && span[8] == '.')
                    {
                        char ch = span[6];
                        if ((ch == 'F' || ch == 'f') && span[7] == '1')
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

        private static bool TryParseQuotedString(string value, int startIndex, out string readValue, out int readLength)
        {
        }
    }
}
