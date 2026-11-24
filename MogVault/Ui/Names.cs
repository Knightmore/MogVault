using MogVault.Store;
using Style = MogVault.Ui.NameGenerator.Style;

namespace MogVault.Ui;

internal static class Names
{
    private static Aliases? cache;

    public static bool Hidden => Plugin.P?.Config.HideNames == true;

    public static long Key(Snapshot snapshot) => HashCode.Combine(snapshot.Version, Hidden, Plugin.P?.Config.NameSeed);

    public static string Owner(Snapshot snapshot, CharacterData owner) =>
        Hidden && Get(snapshot).Names.TryGetValue((owner.Id, 0), out var alias) ? alias : owner.Name;

    public static string Retainer(Snapshot snapshot, ulong retainerId, string name) =>
        Hidden && Get(snapshot).Names.TryGetValue((retainerId, 0), out var alias) ? alias : name;

    public static string Vessel(Snapshot snapshot, CharacterData freeCompany, Vessel vessel) =>
        Hidden && Get(snapshot).Names.TryGetValue((freeCompany.Id, VesselKey(vessel)), out var alias) ? alias : vessel.Name;

    private static Aliases Get(Snapshot snapshot)
    {
        var seed = Plugin.P?.Config.NameSeed ?? 0;
        if (Volatile.Read(ref cache) is { } cached && cached.Version == snapshot.Version && cached.Seed == seed)
            return cached;
        var built = Build(snapshot, seed);
        Volatile.Write(ref cache, built);
        return built;
    }

    private static int VesselKey(Vessel v) => 1 + (int)v.Kind * 16 + v.Slot;

    private static Aliases Build(Snapshot snapshot, ulong seed)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<(ulong, int), string>();
        foreach (var owner in snapshot.Characters.OrderBy(c => c.Id))
            names[(owner.Id, 0)] = NameGenerator.Next(seed, owner.Id, owner.Kind == OwnerKind.FreeCompany ? Style.Company : Style.Full, used);
        foreach (var retainer in snapshot.Characters.SelectMany(c => c.Retainers).OrderBy(r => r.Id))
            names[(retainer.Id, 0)] = NameGenerator.Next(seed, retainer.Id, Style.Single, used);
        foreach (var fc in snapshot.Characters.Where(c => c.Workshop is not null).OrderBy(c => c.Id))
        foreach (var vessel in fc.Workshop!.Vessels)
            names[(fc.Id, VesselKey(vessel))] = NameGenerator.Next(seed, fc.Id ^ ((ulong)VesselKey(vessel) << 56), Style.Single, used);
        return new Aliases(snapshot.Version, seed, names);
    }

    private sealed record Aliases(long Version, ulong Seed, Dictionary<(ulong, int), string> Names);
}

