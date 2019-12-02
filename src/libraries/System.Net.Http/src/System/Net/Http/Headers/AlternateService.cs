﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.Net.Http.Headers
{
    public sealed class AlternateService
    {
        public static AlternateService Clear { get; } = new AlternateService("clear", null, 0, TimeSpan.Zero);

        public string AlpnProtocolName { get; }

        /// <summary>
        /// The name of the host serving this alternate service.
        /// If null, the alternate service is on the same host this header was received from.
        /// </summary>
        public string Host { get; }
        public int Port { get; }

        /// <summary>
        /// The time span this alternate service is valid for.
        /// If not specified by the header, defaults to 24 hours.
        /// </summary>
        public TimeSpan MaxAge { get; }

        public AlternateService(string alpnProtocolName, string host, int port, TimeSpan maxAge)
        {
            AlpnProtocolName = alpnProtocolName;
            Host = host;
            Port = port;
            MaxAge = maxAge;
        }
    }
}
