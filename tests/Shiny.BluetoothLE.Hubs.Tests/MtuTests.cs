using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// A new BLE connection asks for the largest ATT MTU, and Shiny's payload sizes become ATT MTUs inside blehubs
/// </summary>
public class MtuTests
{
    [Fact]
    public async Task EveryConnectionAsksForTheLargestAttMtu()
    {
        var p = new FakeMtuPeripheral("p");
        var attMtu = await BleHubClient.NegotiateMtu(p, null, CancellationToken.None);

        Assert.Equal(517, p.Requested);
        Assert.Equal(517, attMtu);
        Assert.Equal(FrameCodec.MaxFrameSize, FrameCodec.GetFrameSize(attMtu));
    }


    [Fact]
    public async Task TheGrantedMtuIsUsedWhenItIsSmaller()
    {
        var p = new FakeMtuPeripheral("p", maxAttMtu: 247);
        Assert.Equal(247, await BleHubClient.NegotiateMtu(p, null, CancellationToken.None));
        Assert.Equal(244, FrameCodec.GetFrameSize(247));
    }


    [Fact]
    public async Task APlatformThatCantBeAskedKeepsItsOwnMtu()
    {
        // Apple negotiates on its own - Shiny reports the payload size it settled on
        var p = new FakeScanPeripheral("p", null, mtu: 182);
        Assert.Equal(185, await BleHubClient.NegotiateMtu(p, null, CancellationToken.None));
    }


    [Fact]
    public async Task AFailedRequestKeepsTheCurrentMtu()
    {
        var p = new FakeMtuPeripheral("p", fail: new InvalidOperationException("busy"));
        Assert.Equal(BleConstants.DefaultAttMtu, await BleHubClient.NegotiateMtu(p, null, CancellationToken.None));
        Assert.Equal(517, p.Requested);
    }
}
