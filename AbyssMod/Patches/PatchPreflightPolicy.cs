#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AbyssMod.Patches;

/// <summary>
/// 启动前补丁目标校验：对目标清单逐个解析并聚合失败项。纯逻辑，只依赖
/// System.Reflection，不引用 BepInEx / Harmony / 游戏程序集，可直接编入测试项目验证。
/// 清单本身由补丁层枚举（见 <see cref="PatchInventory"/>），因此不会漏掉补丁类。
/// </summary>
public static class PatchPreflightPolicy
{
    public const string MissingType = "missing-type";
    public const string MissingMethod = "missing-method";
    public const string AmbiguousTarget = "ambiguous-target";
    public const string NullTarget = "null-target";
    public const string TargetError = "target-error";

    /// <summary>一个待校验的补丁目标：声明类型 + 方法名 + 可选精确参数类型。</summary>
    public readonly struct PatchTargetSpec
    {
        public PatchTargetSpec(
            string patchClass,
            string site,
            string declaringTypeName,
            string methodName,
            Type[]? argumentTypes = null
        )
        {
            PatchClass = patchClass;
            Site = site;
            DeclaringTypeName = declaringTypeName;
            MethodName = methodName;
            ArgumentTypes = argumentTypes;
        }

        public string PatchClass { get; }
        public string Site { get; }
        public string DeclaringTypeName { get; }
        public string MethodName { get; }
        public Type[]? ArgumentTypes { get; }

        public string Label => PatchClass + "::" + Site;
    }

    /// <summary>个体解析结果：成功给出唯一目标，失败给出原因。</summary>
    public readonly struct SiteResolution
    {
        internal SiteResolution(MethodBase? target, string? failure)
        {
            Target = target;
            Failure = failure;
        }

        public MethodBase? Target { get; }
        public string? Failure { get; }
        public bool Ok => Failure == null;
    }

    /// <summary>整体校验报告：失败为空即通过，同时给出统计信息用于启动日志。</summary>
    public sealed class Report
    {
        internal Report(int siteCount, IReadOnlyList<MethodBase> targets, IReadOnlyList<string> failures)
        {
            SiteCount = siteCount;
            Targets = targets;
            Failures = failures;
        }

        public int SiteCount { get; }
        public IReadOnlyList<MethodBase> Targets { get; }
        public IReadOnlyList<string> Failures { get; }
        public bool Ok => Failures.Count == 0;
    }

    public static Report Check(
        IEnumerable<PatchTargetSpec> specs,
        IEnumerable<Assembly> searchAssemblies
    )
    {
        Assembly[] assemblies = searchAssemblies.ToArray();
        var failures = new List<string>();
        var targets = new List<MethodBase>();
        int siteCount = 0;

        foreach (PatchTargetSpec spec in specs)
        {
            siteCount++;
            SiteResolution resolution = Resolve(assemblies, spec);
            if (!resolution.Ok)
            {
                failures.Add(spec.Label + " => " + resolution.Failure);
                continue;
            }

            if (!targets.Contains(resolution.Target!))
                targets.Add(resolution.Target!);
        }

        return new Report(siteCount, targets, failures);
    }

    /// <summary>解析单个目标：类型缺失、方法缺失或重载不唯一都视为失败。</summary>
    public static SiteResolution Resolve(IEnumerable<Assembly> searchAssemblies, PatchTargetSpec spec)
    {
        if (string.IsNullOrEmpty(spec.DeclaringTypeName) || string.IsNullOrEmpty(spec.MethodName))
        {
            return new SiteResolution(
                null,
                MissingType + ":" + (spec.DeclaringTypeName ?? "?") + "::" + (spec.MethodName ?? "?")
            );
        }

        Type? declaringType = FindType(searchAssemblies, spec.DeclaringTypeName);
        if (declaringType == null)
            return new SiteResolution(null, MissingType + ":" + spec.DeclaringTypeName);

        MethodInfo[] candidates = declaringType
            .GetMethods(
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly
            )
            .Where(method => method.Name == spec.MethodName)
            .Where(
                method =>
                    spec.ArgumentTypes == null
                    || Parameters(method).SequenceEqual(spec.ArgumentTypes)
            )
            .ToArray();

        if (candidates.Length == 0)
            return new SiteResolution(null, MissingMethod + ":" + Describe(spec));

        if (candidates.Length > 1)
            return new SiteResolution(
                null,
                AmbiguousTarget + ":" + Describe(spec) + ":candidates=" + candidates.Length
            );

        return new SiteResolution(candidates[0], null);
    }

    /// <summary>校验运行时目录解析出的目标集合（TargetMethods / TargetMethod 的产物）。</summary>
    public static List<string> CheckResolvedLikeTargets(
        string label,
        IEnumerable<MethodBase?> targets
    )
    {
        var failures = new List<string>();
        int index = 0;
        foreach (MethodBase? target in targets)
        {
            index++;
            if (target == null)
                failures.Add(label + "[" + index + "] => " + NullTarget);
        }

        return failures;
    }

    public static string Describe(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner }
            ? inner.GetType().Name + ":" + inner.Message
            : exception.GetType().Name + ":" + exception.Message;

    private static string Describe(PatchTargetSpec spec) =>
        spec.DeclaringTypeName
        + "::"
        + spec.MethodName
        + (spec.ArgumentTypes == null
            ? string.Empty
            : "(" + string.Join(", ", spec.ArgumentTypes.Select(t => t.FullName)) + ")");

    private static Type? FindType(IEnumerable<Assembly> assemblies, string fullName)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type? type;
            try
            {
                type = assembly.GetType(fullName, false, false);
            }
            catch (Exception)
            {
                // 单个损坏或不完整的 interop 程序集不应中断精确查找。
                continue;
            }

            if (type != null)
                return type;
        }

        return null;
    }

    private static Type[] Parameters(MethodInfo method) =>
        method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
}
