using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PathLab.Core;

/// <summary>
/// Discovers every IPathfinder compiled into the carrier.
/// Adding a module = copy its folder into /Algorithms and rebuild; no carrier edit.
/// </summary>
public static class AlgorithmHost
{
    public static IReadOnlyList<IPathfinder> All { get; } = Discover();

    /// <summary>
    /// Creates an independent instance of the same module type.
    /// Each robot owns one, so a module may keep per-robot state between searches.
    /// Falls back to the prototype if construction fails.
    /// </summary>
    public static IPathfinder Create(IPathfinder prototype)
    {
        if (prototype.GetType().GetConstructor(Type.EmptyTypes) is null) return prototype;

        try
        {
            return (IPathfinder)Activator.CreateInstance(prototype.GetType())!;
        }
        catch (Exception)
        {
            return prototype;
        }
    }

    private static List<IPathfinder> Discover()
    {
        var found = new List<IPathfinder>();

        foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IPathfinder).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;

            try
            {
                if (Activator.CreateInstance(type) is IPathfinder pathfinder)
                    found.Add(pathfinder);
            }
            catch (Exception)
            {
                // A broken module must not take the whole carrier down.
            }
        }

        return found
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
