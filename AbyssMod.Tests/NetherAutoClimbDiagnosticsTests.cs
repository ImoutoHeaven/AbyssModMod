using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherAutoClimbDiagnosticsTests
{
    [Fact]
    public void Structured_diagnostic_sanitizes_untrusted_detail_without_hiding_the_event()
    {
        string line = NetherAutoClimbDiagnostics.Format(
            "toggle-result",
            new("outcome", "rejected"),
            new("reason", "snapshot failed\r\nmissing=model")
        );

        Assert.Equal(
            "[F12][NetherClimb][Diag] event=toggle-result outcome=rejected reason=snapshot_failed_missing_model",
            line
        );
    }

    [Fact]
    public void Structured_diagnostic_bounds_field_count_and_value_length()
    {
        var fields = Enumerable.Range(0, 20)
            .Select(index => new NetherAutoClimbDiagnosticField("field" + index, new string('x', 300)))
            .ToArray();

        string line = NetherAutoClimbDiagnostics.Format("binding", fields);

        Assert.True(line.Length < 1400);
        Assert.Contains("field0=", line);
        Assert.DoesNotContain("field12=", line);
    }
}
