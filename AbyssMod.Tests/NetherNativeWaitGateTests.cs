using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherNativeWaitGateTests
{
    [Fact]
    public void Missing_native_result_task_waits_for_a_bounded_number_of_main_thread_polls()
    {
        var gate = new NetherNativeWaitGate(maximumMissingPolls: 2);

        Assert.Equal(NetherNativeActionResultKind.Started, gate.AwaitRegistration("result").Kind);
        Assert.Equal(NetherNativeActionResultKind.Started, gate.AwaitRegistration("result").Kind);
        NetherNativeActionResult timeout = gate.AwaitRegistration("result");

        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, timeout.Kind);
        Assert.Contains("timeout", timeout.Detail);
    }

    [Fact]
    public void Observed_task_resets_the_missing_registration_budget()
    {
        var gate = new NetherNativeWaitGate(maximumMissingPolls: 1);

        Assert.Equal(NetherNativeActionResultKind.Started, gate.AwaitRegistration("result").Kind);
        gate.ObserveRegistration();

        Assert.Equal(NetherNativeActionResultKind.Started, gate.AwaitRegistration("result").Kind);
    }
}
