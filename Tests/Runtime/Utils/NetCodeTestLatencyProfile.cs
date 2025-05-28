namespace Unity.NetCode.Tests
{
    /// <summary>
    /// Static-optimization can APPEAR to work when acking is 100% reliable and instantaneous
    /// (i.e. on ServerTick:3, snapshot for ServerTick:2 is already acked), so test other cases too.
    /// </summary>
    internal enum NetCodeTestLatencyProfile
    {
        None,
        /// <summary>Round trip time (i.e. latency) of 60ms (rounds up to 4 ticks).</summary>
        RTT60ms,
        /// <summary>Packet loss of 33% (every 3rd packet).</summary>
        PL33,
        /// <summary>Round trip time (i.e. latency) of 16ms (one tick), and packet loss of 33% (every 3rd packet).</summary>
        RTT16ms_PL5,
    }
}
