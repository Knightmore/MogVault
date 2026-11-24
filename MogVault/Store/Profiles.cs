using System.Runtime.InteropServices;
using MogVault.Contract;

namespace MogVault.Store;

public sealed record CharacterProfile(
    long UpdatedTicks, byte ClassJob, ulong FreeCompanyId, ushort MsqQuestId, short[] JobLevels, ushort[] ActiveQuests)
{
    public bool SameAs(CharacterProfile? other) =>
        other is not null && ClassJob == other.ClassJob && FreeCompanyId == other.FreeCompanyId && MsqQuestId == other.MsqQuestId &&
        JobLevels.AsSpan().SequenceEqual(other.JobLevels) && ActiveQuests.AsSpan().SequenceEqual(other.ActiveQuests);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct RetainerDetails(
    long UpdatedTicks, byte ClassJob, byte Level, byte Town, byte ItemCount, byte MarketItemCount, ushort VentureId, uint VentureComplete, uint MarketExpire);

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct FreeCompanyProfile(long UpdatedTicks, byte Rank, byte GrandCompany, ushort TotalMembers);

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct FreeCompanyCredits(long UpdatedTicks, uint Amount);

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct Standing(byte Level, byte Percent)
{
    public Standing Over(Standing old) => Level == 0 ? old : this;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct FreeCompanyStatus(
    long UpdatedTicks, long FoundedTicks, uint RankPoints, uint RankPointsNeeded, Standing Maelstrom, Standing TwinAdder, Standing ImmortalFlames)
{
    public Standing For(byte grandCompany) => grandCompany switch { 1 => Maelstrom, 2 => TwinAdder, _ => ImmortalFlames };

    public static FreeCompanyStatus Partial(long updatedTicks, uint rankPoints, uint rankPointsNeeded, byte grandCompany, Standing standing) =>
        new(updatedTicks, 0, rankPoints, rankPointsNeeded,
            grandCompany == 1 ? standing : default, grandCompany == 2 ? standing : default, grandCompany == 3 ? standing : default);

    public FreeCompanyStatus Over(FreeCompanyStatus old) => this with
    {
        FoundedTicks = FoundedTicks > 0 ? FoundedTicks : old.FoundedTicks,
        Maelstrom = Maelstrom.Over(old.Maelstrom),
        TwinAdder = TwinAdder.Over(old.TwinAdder),
        ImmortalFlames = ImmortalFlames.Over(old.ImmortalFlames),
    };
}

public sealed record MembersData(long UpdatedTicks, string[] Names)
{
    public bool SameAs(MembersData? other) => other is not null && Names.AsSpan().SequenceEqual(other.Names);
}

public sealed record CollectionSet(CollectionKind Kind, byte[] Bits)
{
    public bool Has(uint rowId) => (rowId >> 3) < (uint)Bits.Length && (Bits[rowId >> 3] & (1 << (int)(rowId & 7))) != 0;
}

public sealed record CollectionsData(long UpdatedTicks, CollectionSet[] Sets)
{
    public CollectionSet? Get(CollectionKind kind) => Array.Find(Sets, s => s.Kind == kind);

    public bool SameAs(CollectionsData? other) =>
        other is not null && Sets.Length == other.Sets.Length &&
        Sets.Zip(other.Sets).All(p => p.First.Kind == p.Second.Kind && p.First.Bits.AsSpan().SequenceEqual(p.Second.Bits));
}

public sealed record QuestsData(long UpdatedTicks, byte[] Bits)
{
    public const uint RowOffset = 0x10000;

    public bool Has(uint questId)
    {
        var id = questId & 0xFFFF;
        return (id >> 3) < (uint)Bits.Length && (Bits[id >> 3] & (1 << (int)(id & 7))) != 0;
    }

    public uint[] Completed()
    {
        var rows = new List<uint>();
        for (var id = 0u; id < Bits.Length * 8u; id++)
            if (Has(id))
                rows.Add(RowOffset + id);
        return [.. rows];
    }

    public bool SameAs(QuestsData? other) => other is not null && Bits.AsSpan().SequenceEqual(other.Bits);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct OutfitPieces(uint SetId, ushort Slots);

public sealed record OutfitData(long UpdatedTicks, OutfitPieces[] Sets)
{
    public ushort SlotsOf(uint setId)
    {
        foreach (var set in Sets)
            if (set.SetId == setId)
                return set.Slots;
        return 0;
    }

    public bool SameAs(OutfitData? other) => other is not null && Sets.AsSpan().SequenceEqual(other.Sets);
}

public sealed record Vessel(VesselKind Kind, byte Slot, string Name, byte Rank, uint RegisterTime, uint ReturnTime);

public sealed record WorkshopData(long UpdatedTicks, Vessel[] Vessels)
{
    public bool SameAs(WorkshopData? other) => other is not null && Vessels.SequenceEqual(other.Vessels);
}


public enum HouseType : byte
{
    FreeCompanyEstate = 0,
    PrivateChambers = 1,
    PersonalEstate = 2,
    Apartment = 6,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct House(HouseType Type, ulong Id, long EnteredTicks);

public sealed record HousesData(long UpdatedTicks, House[] Houses)
{
    public long LastEntered(House house)
    {
        foreach (var known in Houses)
            if (known.Type == house.Type && known.Id == house.Id)
                return known.EnteredTicks;
        return 0;
    }

    public HousesData MergedWith(HousesData? known) =>
        known is null ? this : this with { Houses = Array.ConvertAll(Houses, h => h with { EnteredTicks = Math.Max(h.EnteredTicks, known.LastEntered(h)) }) };
}
