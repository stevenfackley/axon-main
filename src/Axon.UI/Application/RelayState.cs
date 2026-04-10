namespace Axon.UI.Application;

internal enum RelayState : byte
{
    Idle = 0,
    Syncing = 1,
    Error = 2,
    AirGapped = 3
}
