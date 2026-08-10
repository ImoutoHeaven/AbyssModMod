using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodeDiagnosticAuditTests
{
    [Fact]
    public void Detailed_code_audit_is_silent_when_logging_is_disabled()
    {
        string? audit = NetherCodeDiagnosticAudit.Format(
            detailedLogging: false,
            new[] { new NetherCodeMasterAudit(10001, 1, 2, 3, 4, 5, 6, 7, "asset", 8, "level", "scope", "target", "effect") }
        );

        Assert.Null(audit);
    }

    [Fact]
    public void Detailed_code_audit_is_bounded_and_contains_only_mapping_identifiers()
    {
        var rows = new NetherCodeMasterAudit[10];
        for (int index = 0; index < rows.Length; index++)
        {
            rows[index] = new NetherCodeMasterAudit(
                10000 + index,
                category: 2,
                effectType: 1,
                effectParameter1: 300 + index,
                effectParameter2: 400 + index,
                effectParameter3: 500 + index,
                rarity: 3,
                power: 20,
                assetId: "asset_" + index,
                abilityId: 700 + index,
                effectLevelType: "Level",
                scopeType: "Self",
                targetType: "Enemy",
                abilityEffectType: "Damage"
            );
        }

        string? audit = NetherCodeDiagnosticAudit.Format(detailedLogging: true, rows);

        Assert.NotNull(audit);
        Assert.Contains("id=10000", audit);
        Assert.Contains("category=2", audit);
        Assert.Contains("effectType=1", audit);
        Assert.Contains("p1=300", audit);
        Assert.Contains("abilityId=700", audit);
        Assert.DoesNotContain("id=10008", audit);
        Assert.DoesNotContain("name=", audit);
        Assert.DoesNotContain("description=", audit);
    }
}
