namespace Unity.NetCode.Tests
{
    /// <summary>
    /// Static-optimization can APPEAR to work when acking is 100% reliable and instantaneous
    /// (i.e. on ServerTick:3, snapshot for ServerTick:2 is already acked), so test other cases too.
    /// </summary>
    public enum NetCodeTestLatencyProfile
    {
        NoLatency,
        RTT60ms,
        PL33,
        RTT16ms_PL5,
    }
}
