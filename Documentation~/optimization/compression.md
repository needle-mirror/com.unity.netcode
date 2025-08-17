# Data compression

Use data compression to reduce bandwidth consumption, minimizing the likelihood that a player will experience gameplay issues as a result of bandwidth limitations.

> [!NOTE]
> Netcode for Entities defaults to a bandwidth-intensive snapshot send configuration to enable you to get up and running quickly. It's expected and recommended that you modify the default bandwidth consumption before releasing a game into production. Refer to the [optimizing performance page](../optimizations.md) for more information about ways to optimize your game.

## Quantization

Quantization involves limiting the precision of data for the sake of reducing the number of bits required to send and receive that data. A float takes up 32 bits, giving it an approximate range of `±1.5 x 10^−45 to ±3.4 x 10^38` with the IEC 60559 standard, which is more precision than most games need. For example, if you don't need millimeter precision, setting a quantization value of `100` cuts off all sub-millimeter noise from your floats, reducing the amount of bits required to send your float values.

Quantization can cause issues when used with client-side prediction. Refer to the [prediction edge cases page](../prediction-details.md) for more details.

### Compression model

Netcode's quantization is optimized for [Huffman delta compression](https://en.wikipedia.org/wiki/Huffman_coding) to be executed on top of it, which means that you'll get the most bandwidth gains by sending small values (including small deltas between values).

For example, sending `123456789.123456789` (for a new ghost spawn, for example, where delta compression will delta against a baseline of 0) with a quantization value of `10` would result in Netcode replicating a value of `1234567891`, which wouldn't produce much optimization at all, since the number of bits used to Huffman encode a delta of `1234567891` is large. Since the Netcode for Entities compression model uses buckets of values to compress, with lower values getting a lower bit count, you won't see much difference in compression between different high values, but you will with low values.

So sending `0.123456789` with a quantization value of `10` would send only the value `1`. Huffman compression would use only 3 bits for this. Quantization of `100` would use 7 bits, `1000` would use 13 bits etc. You can test this yourself using `StreamCompressionModel.Default.GetCompressedSizeInBits(some_uint_value)`. To test the size of `0.123456789` with Quantization of `100`, multiply 0.123456789 by 100, cast to uint (cut off the digits after the comma) and call `StreamCompressionModel.Default.GetCompressedSizeInBits(12)`.

## Delta compression

As mentioned above, sending smaller values results in smaller amount of bits needed for the same type. A 32 bit float can be sent using less than 8 bits if it only changes a little. Games are usually composed of objects moving in small steps (rather than constantly teleporting), and sending the delta between each value change instead of the absolute value each time results in great bandwidth optimization gains. Use the [`Composite` property on `GhostFieldAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_Composite) to customize delta compression for a component.

Note that delta compression is calculated against a baseline. For [pre-spawned ghosts](../ghost-spawning.md#pre-spawned-ghosts), this baseline is updated against the ghost's initial value instead of zero.
