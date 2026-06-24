using Honua.Collect.Core.Field.Sensors;

namespace Honua.Collect.Core.Tests.Field.Sensors;

public class SensorConnectionStateTests
{
    [Fact]
    public async Task Connect_transitions_through_connecting_to_connected()
    {
        await using var transport = new ReplaySensorTransport();
        var states = new List<SensorConnectionState>();
        transport.StateChanged += (_, s) => states.Add(s);

        Assert.Equal(SensorConnectionState.Disconnected, transport.State);
        await transport.ConnectAsync();

        Assert.Equal([SensorConnectionState.Connecting, SensorConnectionState.Connected], states);
        Assert.Equal(SensorConnectionState.Connected, transport.State);
    }

    [Fact]
    public async Task Disconnect_and_fault_transition_states()
    {
        await using var transport = new ReplaySensorTransport();
        await transport.ConnectAsync();

        transport.Fault();
        Assert.Equal(SensorConnectionState.Faulted, transport.State);

        await transport.DisconnectAsync();
        Assert.Equal(SensorConnectionState.Disconnected, transport.State);
    }

    [Fact]
    public async Task State_changes_propagate_through_the_source()
    {
        await using var transport = new ReplaySensorTransport();
        await using var source = new SensorSource("s", SensorType.Meter, transport);

        var seen = new List<SensorConnectionState>();
        source.StateChanged += (_, s) => seen.Add(s);

        await source.ConnectAsync();
        await source.DisconnectAsync();

        Assert.Equal(
            [SensorConnectionState.Connecting, SensorConnectionState.Connected, SensorConnectionState.Disconnected],
            seen);
    }

    [Fact]
    public async Task Same_state_does_not_re_raise()
    {
        await using var transport = new ReplaySensorTransport();
        await transport.ConnectAsync();

        var raised = 0;
        transport.StateChanged += (_, _) => raised++;
        await transport.ConnectAsync(); // already connected — Connecting==no-op (already connected), Connected==no-op

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Disposed_transport_rejects_emit()
    {
        var transport = new ReplaySensorTransport();
        await transport.ConnectAsync();
        await transport.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => transport.Emit(
            new SensorReading("s", "c", 1, null, DateTimeOffset.UtcNow)));
    }
}
