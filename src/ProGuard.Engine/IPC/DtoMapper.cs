using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace ProGuard.Engine.IPC;

/// <summary>
/// Copies between the stored models (ProGuard.Core.Models) and their wire mirrors (ProGuard.Shared.Contracts) by
/// property name. Enums map by numeric value, so a Core enum and its Shared mirror only need the same members in the
/// same order. Properties that exist on one side only are left alone; tests check that the mirrors are complete.
/// </summary>
public static class DtoMapper
{
    private static readonly ConcurrentDictionary<(Type, Type), (PropertyInfo From, PropertyInfo To)[]> Pairs = new();

    /// <summary>A new <typeparamref name="TTo"/> with every same-named property copied from <paramref name="from"/>.</summary>
    public static TTo Map<TFrom, TTo>(TFrom from) where TTo : new()
    {
        var to = new TTo();
        CopyTo(from, to);
        return to;
    }

    /// <summary>Copies every same-named, writable property from <paramref name="from"/> onto <paramref name="to"/>.</summary>
    public static void CopyTo<TFrom, TTo>(TFrom from, TTo to)
    {
        foreach (var (source, target) in PairsFor(typeof(TFrom), typeof(TTo)))
            target.SetValue(to, Convert(source.GetValue(from), target.PropertyType));
    }

    /// <summary>Names of the readable properties of <paramref name="from"/> that <paramref name="to"/> can't receive.</summary>
    public static string[] Unmapped(Type from, Type to)
    {
        var mapped = PairsFor(from, to).Select(p => p.From.Name).ToHashSet();
        return from.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !mapped.Contains(p.Name))
            .Select(p => p.Name)
            .ToArray();
    }

    private static (PropertyInfo From, PropertyInfo To)[] PairsFor(Type from, Type to) =>
        Pairs.GetOrAdd((from, to), key =>
        {
            var targets = key.Item2.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.SetMethod!.IsPublic)
                .ToDictionary(p => p.Name);
            return key.Item1.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Where(p => targets.TryGetValue(p.Name, out var t) && Compatible(p.PropertyType, t.PropertyType))
                .Select(p => (p, targets[p.Name]))
                .ToArray();
        });

    private static bool Compatible(Type from, Type to)
    {
        var f = Nullable.GetUnderlyingType(from) ?? from;
        var t = Nullable.GetUnderlyingType(to) ?? to;
        if (t.IsAssignableFrom(f)) return true;
        bool IsNumberLike(Type x) => x.IsEnum || x == typeof(int) || x == typeof(long);
        return (f.IsEnum || t.IsEnum) && IsNumberLike(f) && IsNumberLike(t);
    }

    private static object? Convert(object? value, Type to)
    {
        if (value == null) return null;
        var target = Nullable.GetUnderlyingType(to) ?? to;
        if (target.IsInstanceOfType(value)) return value;
        if (target.IsEnum)
        {
            var mapped = Enum.ToObject(target, System.Convert.ToInt64(value));
            if (Enum.IsDefined(target, mapped)) return mapped;
            // A value from a newer or broken client: "not set" where that's allowed, otherwise refuse it.
            return Nullable.GetUnderlyingType(to) != null
                ? null
                : throw new ArgumentException($"{value} is not a valid {target.Name}");
        }
        return System.Convert.ChangeType(value is Enum ? System.Convert.ToInt64(value) : value, target);
    }
}
