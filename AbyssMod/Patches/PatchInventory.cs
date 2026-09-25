#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace AbyssMod.Patches;

/// <summary>
/// 补丁清单：由程序集扫描而非手工登记得出，因此新增补丁类会自动纳入启动前校验，
/// 不存在“忘记登记导致漏检”的漂移。判定标准与 Harmony 一致：类型或方法带
/// Harmony 属性，或声明了 TargetMethod / TargetMethods 运行时目录解析。
/// </summary>
public static class PatchInventory
{
    private static readonly Lazy<IReadOnlyList<Type>> Scanned = new(ScanPatchClasses);

    /// <summary>全部参与安装的补丁类，含按配置条件安装的那些。</summary>
    public static IReadOnlyList<Type> PatchClasses => Scanned.Value;

    /// <summary>目标由运行时目录解析的补丁类，必须实际调用解析器才能确认当前是否满足约束。</summary>
    public static IReadOnlyList<Type> DynamicTargetClasses { get; } = Scanned
        .Value.Where(HasDynamicTargetProvider)
        .ToArray();

    /// <summary>把 Harmony 注解展开为待校验目标清单（不含运行时目录解析的目标）。</summary>
    public static IReadOnlyList<PatchPreflightPolicy.PatchTargetSpec> AttributeSpecs()
    {
        var specs = new List<PatchPreflightPolicy.PatchTargetSpec>();
        foreach (Type patchClass in PatchClasses)
            AddAttributeSpecs(patchClass, specs);
        return specs;
    }

    private static List<Type> ScanPatchClasses()
    {
        Assembly assembly = typeof(PatchInventory).Assembly;
        return assembly
            .GetTypes()
            // 补丁类通常是 static class，而 static class 的 IsAbstract 为 true，
            // 因此不能用 !IsAbstract 过滤，否则会排除掉全部补丁类。
            .Where(type => type.IsClass)
            .Where(IsHarmonyPatchClass)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsHarmonyPatchClass(Type type) =>
        HasHarmonyAttribute(type)
        || type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
        ).Any(HasHarmonyAttribute)
        || HasDynamicTargetProvider(type);

    /// <summary>
    /// Harmony 只接受静态、无参的 TargetMethod / TargetMethods。
    /// 带参数的同类方法（例如共享辅助类的 TargetMethods(int)）不是目标提供者。
    /// </summary>
    private static bool HasDynamicTargetProvider(Type type) =>
        IsProvider(type.GetMethod(
            "TargetMethods",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        )) || IsProvider(type.GetMethod(
            "TargetMethod",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        ));

    private static bool IsProvider(MethodInfo? method) =>
        method != null && method.GetParameters().Length == 0;

    private static bool HasHarmonyAttribute(ICustomAttributeProvider provider)
    {
        foreach (object attribute in provider.GetCustomAttributes(true))
        {
            Type attributeType = attribute.GetType();
            if (
                attributeType.FullName == "HarmonyLib.HarmonyAttribute"
                || attributeType.BaseType?.FullName == "HarmonyLib.HarmonyAttribute"
            )
            {
                return true;
            }
        }

        return false;
    }

    private static void AddAttributeSpecs(
        Type patchClass,
        List<PatchPreflightPolicy.PatchTargetSpec> specs
    )
    {
        HarmonyTarget classDefault = ReadTargets(patchClass).Aggregate(
            new HarmonyTarget(),
            (accumulated, target) => accumulated.Merge(target)
        );

        int index = 0;
        foreach (
            MethodInfo method in patchClass.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            )
        )
        {
            foreach (HarmonyTarget target in ReadTargets(method))
            {
                index++;
                HarmonyTarget effective = classDefault.Merge(target);
                specs.Add(
                    new PatchPreflightPolicy.PatchTargetSpec(
                        ClassLabel(patchClass),
                        method.Name + "#" + index,
                        effective.DeclaringType ?? string.Empty,
                        effective.MethodName ?? string.Empty,
                        effective.ArgumentTypes
                    )
                );
            }
        }

        // 类级属性自带完整目标、且没有方法级站点时，类级属性本身就是目标声明。
        if (index == 0 && classDefault.HasTarget)
        {
            specs.Add(
                new PatchPreflightPolicy.PatchTargetSpec(
                    ClassLabel(patchClass),
                    "<class-level>",
                    classDefault.DeclaringType ?? string.Empty,
                    classDefault.MethodName ?? string.Empty,
                    classDefault.ArgumentTypes
                )
            );
        }
    }

    private static IEnumerable<HarmonyTarget> ReadTargets(ICustomAttributeProvider provider)
    {
        foreach (object attribute in provider.GetCustomAttributes(true))
        {
            Type attributeType = attribute.GetType();
            if (
                attributeType.FullName != "HarmonyLib.HarmonyAttribute"
                && attributeType.BaseType?.FullName != "HarmonyLib.HarmonyAttribute"
            )
            {
                continue;
            }

            FieldInfo? info = attributeType.GetField("info");
            if (info?.GetValue(attribute) is object harmonyMethod)
                yield return HarmonyTarget.From(harmonyMethod);
        }
    }

    private static string ClassLabel(Type type) =>
        type.DeclaringType == null ? type.Name : type.DeclaringType.Name + "+" + type.Name;

    /// <summary>HarmonyMethod 的只读镜像，避免在策略层直接依赖 HarmonyLib。</summary>
    private readonly struct HarmonyTarget
    {
        private HarmonyTarget(string? declaringType, string? methodName, Type[]? argumentTypes)
        {
            DeclaringType = declaringType;
            MethodName = methodName;
            ArgumentTypes = argumentTypes;
        }

        public string? DeclaringType { get; }
        public string? MethodName { get; }
        public Type[]? ArgumentTypes { get; }
        public bool HasTarget => DeclaringType != null || MethodName != null;

        public static HarmonyTarget From(object harmonyMethod)
        {
            Type type = harmonyMethod.GetType();
            return new HarmonyTarget(
                (type.GetField("declaringType")?.GetValue(harmonyMethod) as Type)?.FullName,
                type.GetField("methodName")?.GetValue(harmonyMethod) as string,
                type.GetField("argumentTypes")?.GetValue(harmonyMethod) as Type[]
            );
        }

        public HarmonyTarget Merge(HarmonyTarget other) =>
            new HarmonyTarget(
                other.DeclaringType ?? DeclaringType,
                other.MethodName ?? MethodName,
                other.ArgumentTypes ?? ArgumentTypes
            );
    }
}
