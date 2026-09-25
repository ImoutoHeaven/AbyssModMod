using System;
using System.Reflection;
using AbyssMod.Patches;
using Xunit;

namespace AbyssMod.Tests;

/// <summary>
/// 启动前目标校验的纯逻辑测试：单个目标的解析规则（类型缺失、方法缺失、重载不唯一、
/// 精确参数匹配）以及批量校验的失败聚合与去重统计。不需要 BepInEx 或游戏程序集。
/// </summary>
public class PatchPreflightPolicyTests
{
    private static readonly Assembly[] SearchAssemblies = { typeof(string).Assembly };

    // 必须在目标类型上直接声明：解析使用 DeclaredOnly，继承来的成员查不到。
    private static readonly string DeclaredType = typeof(DateTime).FullName!;
    private const string DeclaredMethod = nameof(DateTime.AddDays);

    private static PatchPreflightPolicy.PatchTargetSpec Spec(
        string declaringType,
        string method,
        params Type[] argumentTypes
    ) =>
        new(
            "TestPatch",
            "site",
            declaringType,
            method,
            argumentTypes.Length == 0 ? null : argumentTypes
        );

    [Fact]
    public void Resolve_returns_unique_method_for_unambiguous_name()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(DeclaredType, DeclaredMethod, typeof(double))
        );

        Assert.True(resolution.Ok);
        Assert.Equal(DeclaredMethod, resolution.Target!.Name);
    }

    [Fact]
    public void Resolve_reports_missing_type_with_the_declaring_name()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec("No.Such.Type", "Whatever")
        );

        Assert.False(resolution.Ok);
        Assert.Equal("missing-type:No.Such.Type", resolution.Failure);
    }

    [Fact]
    public void Resolve_reports_missing_method()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(DeclaredType, "NoSuchMethod")
        );

        Assert.False(resolution.Ok);
        Assert.StartsWith("missing-method:", resolution.Failure);
    }

    [Fact]
    public void Resolve_reports_ambiguity_when_overloads_are_not_disambiguated()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(typeof(int).FullName!, nameof(int.Parse))
        );

        Assert.False(resolution.Ok);
        Assert.StartsWith("ambiguous-target:", resolution.Failure);
    }

    [Fact]
    public void Resolve_uses_argument_types_to_pick_one_overload()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(typeof(int).FullName!, nameof(int.Parse), typeof(string))
        );

        Assert.True(resolution.Ok);
        Assert.Single(resolution.Target!.GetParameters());
        Assert.Equal(typeof(string), resolution.Target.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Resolve_reports_missing_method_when_argument_types_match_nothing()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(typeof(int).FullName!, nameof(int.Parse), typeof(Exception), typeof(Guid))
        );

        Assert.False(resolution.Ok);
        Assert.StartsWith("missing-method:", resolution.Failure);
    }

    [Fact]
    public void Resolve_reports_missing_type_for_empty_spec()    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            new PatchPreflightPolicy.PatchTargetSpec("TestPatch", "site", string.Empty, string.Empty)
        );

        Assert.False(resolution.Ok);
        Assert.StartsWith("missing-type:", resolution.Failure);
    }

    [Fact]
    public void Check_aggregates_every_failure_in_input_order()
    {
        var report = PatchPreflightPolicy.Check(
            new[]
            {
                Spec("No.Such.Type", "First"),
                Spec(DeclaredType, DeclaredMethod, typeof(double)),
                Spec(DeclaredType, "NoSuchMethod"),
                Spec("Also.Missing", "Last"),
            },
            SearchAssemblies
        );

        Assert.False(report.Ok);
        Assert.Equal(4, report.SiteCount);
        Assert.Equal(3, report.Failures.Count);
        Assert.StartsWith("TestPatch::site => missing-type:No.Such.Type", report.Failures[0]);
        Assert.StartsWith("TestPatch::site => missing-method:", report.Failures[1]);
        Assert.StartsWith("TestPatch::site => missing-type:Also.Missing", report.Failures[2]);
        Assert.Single(report.Targets);
    }

    [Fact]
    public void Check_deduplicates_repeated_targets()
    {
        var report = PatchPreflightPolicy.Check(
            new[]
            {
                Spec(DeclaredType, DeclaredMethod, typeof(double)),
                Spec(DeclaredType, DeclaredMethod, typeof(double)),
                Spec(typeof(object).FullName!, nameof(object.ToString)),
            },
            SearchAssemblies
        );

        Assert.True(report.Ok);
        Assert.Equal(3, report.SiteCount);
        Assert.Equal(2, report.Targets.Count);
    }

    [Fact]
    public void CheckResolvedLikeTargets_flags_null_entries_by_index()
    {
        MethodInfo valid = typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes)!;

        var failures = PatchPreflightPolicy.CheckResolvedLikeTargets(
            "DynamicPatch",
            new MethodBase[] { valid, null!, valid }
        );

        Assert.Single(failures);
        Assert.Equal("DynamicPatch[2] => null-target", failures[0]);
    }

    [Fact]
    public void CheckResolvedLikeTargets_accepts_a_fully_resolved_sequence()
    {
        MethodInfo valid = typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes)!;

        var failures = PatchPreflightPolicy.CheckResolvedLikeTargets(
            "DynamicPatch",
            new MethodBase[] { valid, valid }
        );

        Assert.Empty(failures);
    }
}
