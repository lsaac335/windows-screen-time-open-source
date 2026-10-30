using System;
using System.Collections.Generic;

namespace ScreenTime;

// Keep native sampling independent of WinRT types, so optional media dependencies
// are not loaded at all until the user enables media inference.
internal interface IMediaSessionMonitor : IDisposable
{
    string Status { get; }
    void SetEnabled(bool enabled);
}
