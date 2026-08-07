using System.Collections.Generic;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherDetailedAuditLoggerTests
{
    [Fact]
    public void Disabled_detailed_logging_is_silent_even_when_a_poll_repeats()
    {
        var entries = new List<string>();
        var logger = new NetherDetailedAuditLogger(entries.Add);

        for (int tick = 0; tick < 100; tick++)
        {
            logger.Emit(
                enabled: false,
                NetherDetailedAuditKind.Task,
                "native-parent-pending",
                new NetherDetailedAuditField("status", "pending")
            );
        }

        Assert.Empty(entries);
    }

    [Fact]
    public void Structured_snapshot_log_is_bounded_and_excludes_sensitive_display_fields()
    {
        var entries = new List<string>();
        var logger = new NetherDetailedAuditLogger(entries.Add);

        bool emitted = logger.Emit(
            enabled: true,
            NetherDetailedAuditKind.Snapshot,
            "nether:7:map:12:floor:34",
            new NetherDetailedAuditField("netherId", "7"),
            new NetherDetailedAuditField("mapId", "12"),
            new NetherDetailedAuditField("floorId", "34"),
            new NetherDetailedAuditField("hpPermille", "700"),
            new NetherDetailedAuditField("erosion", "42"),
            new NetherDetailedAuditField("tickets", "1"),
            new NetherDetailedAuditField("keys", "2"),
            new NetherDetailedAuditField("gold", "99"),
            new NetherDetailedAuditField("codeHash", "code-a"),
            new NetherDetailedAuditField("name", "must-not-appear")
        );

        Assert.True(emitted);
        Assert.Single(entries);
        Assert.Contains("audit=snapshot", entries[0]);
        Assert.Contains("mapId=12", entries[0]);
        Assert.Contains("codeHash=code-a", entries[0]);
        Assert.DoesNotContain("name=", entries[0]);
        Assert.DoesNotContain("must-not-appear", entries[0]);
    }

    [Fact]
    public void Repeated_poll_event_is_deduplicated_and_each_kind_has_a_hard_bound()
    {
        var entries = new List<string>();
        var logger = new NetherDetailedAuditLogger(entries.Add);

        for (int tick = 0; tick < 100; tick++)
        {
            logger.Emit(
                enabled: true,
                NetherDetailedAuditKind.Task,
                "floor-parent-pending",
                new NetherDetailedAuditField("terminal", "pending")
            );
        }
        for (int index = 0; index < NetherDetailedAuditLogger.MaximumEntriesPerKind + 5; index++)
        {
            logger.Emit(
                enabled: true,
                NetherDetailedAuditKind.Route,
                "candidate:" + index,
                new NetherDetailedAuditField("floorId", index.ToString())
            );
        }

        Assert.Equal(1 + NetherDetailedAuditLogger.MaximumEntriesPerKind, entries.Count);
        Assert.Single(entries.FindAll(entry => entry.Contains("audit=task")));
        Assert.Equal(
            NetherDetailedAuditLogger.MaximumEntriesPerKind,
            entries.FindAll(entry => entry.Contains("audit=route")).Count
        );
    }

    [Fact]
    public void Every_required_audit_family_is_structured_without_unbounded_detail()
    {
        var entries = new List<string>();
        var logger = new NetherDetailedAuditLogger(entries.Add);
        NetherDetailedAuditKind[] kinds =
        {
            NetherDetailedAuditKind.Snapshot,
            NetherDetailedAuditKind.Route,
            NetherDetailedAuditKind.Interactive,
            NetherDetailedAuditKind.Battle,
            NetherDetailedAuditKind.F11,
            NetherDetailedAuditKind.Lease,
            NetherDetailedAuditKind.Checkpoint,
            NetherDetailedAuditKind.Native,
            NetherDetailedAuditKind.Task,
            NetherDetailedAuditKind.Reconcile,
        };

        for (int index = 0; index < kinds.Length; index++)
        {
            logger.Emit(
                enabled: true,
                kinds[index],
                "event:" + index,
                new NetherDetailedAuditField("detail", new string('x', 300))
            );
        }

        Assert.Equal(kinds.Length, entries.Count);
        Assert.All(entries, entry => Assert.True(entry.Length <= 256));
        Assert.Contains(entries, entry => entry.Contains("audit=lease"));
        Assert.Contains(entries, entry => entry.Contains("audit=reconcile"));
    }
}
