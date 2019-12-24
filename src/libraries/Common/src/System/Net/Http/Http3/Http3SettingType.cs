// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3
{
    internal enum Http3SettingType : ulong
    {
        /// <summary>
        /// The maximum dynamic table size; the default is 0.
        /// </summary>
        QPackMaxTableCapacity = 0x1,
        /// <summary>
        /// SETTINGS_MAX_HEADER_LIST_SIZE, default is unlimited.
        /// </summary>
        MaxHeaderListSize = 0x6,

        /// <summary>
        /// Default is 0.
        /// </summary>
        QPackBlockedStreams = 0x7
    }
}
