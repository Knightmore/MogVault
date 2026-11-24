using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MogVault.Contract;

namespace MogVault.Store;

public static partial class VaultFile
{
    private static void WriteFixed<T>(ref SpanWriter w, ushort tag, in T value) where T : unmanaged
    {
        w.WriteSectionHeader(tag, Unsafe.SizeOf<T>());
        w.Write(value);
    }

    private static int ProfileSize(CharacterProfile p) =>
        sizeof(long) + sizeof(byte) + sizeof(ulong) + sizeof(ushort) + sizeof(byte) + p.JobLevels.Length * sizeof(short) + sizeof(byte) + p.ActiveQuests.Length * sizeof(ushort);

    private static void WriteProfile(ref SpanWriter w, CharacterProfile p)
    {
        if (p.JobLevels.Length > byte.MaxValue || p.ActiveQuests.Length > byte.MaxValue)
            throw new InvalidOperationException("Too many job levels or quests.");
        w.WriteSectionHeader(ProfileSection, ProfileSize(p));
        w.Write(p.UpdatedTicks);
        w.Write(p.ClassJob);
        w.Write(p.FreeCompanyId);
        w.Write(p.MsqQuestId);
        w.Write((byte)p.JobLevels.Length);
        w.WriteBytes(MemoryMarshal.AsBytes(p.JobLevels.AsSpan()));
        w.Write((byte)p.ActiveQuests.Length);
        w.WriteBytes(MemoryMarshal.AsBytes(p.ActiveQuests.AsSpan()));
    }

    private static CharacterProfile ReadProfile(ref SpanReader s)
    {
        var ticks = s.Read<long>();
        var classJob = s.Read<byte>();
        var fcId = s.Read<ulong>();
        var msq = s.Read<ushort>();
        var levels = MemoryMarshal.Cast<byte, short>(s.ReadBytes(s.Read<byte>() * sizeof(short))).ToArray();
        var quests = MemoryMarshal.Cast<byte, ushort>(s.ReadBytes(s.Read<byte>() * sizeof(ushort))).ToArray();
        return new CharacterProfile(ticks, classJob, fcId, msq, levels, quests);
    }

    private static int DetailedRetainers(CharacterData data) => data.Retainers.Count(r => r.Details is not null);

    private static int RetainerDetailsSize(int count) => sizeof(ushort) + count * (sizeof(ulong) + Unsafe.SizeOf<RetainerDetails>());

    private static void WriteRetainerDetails(ref SpanWriter w, RetainerInfo[] retainers, int detailed)
    {
        w.WriteSectionHeader(RetainerDetailsSection, RetainerDetailsSize(detailed));
        w.Write((ushort)detailed);
        foreach (var r in retainers)
        {
            if (r.Details is not { } d)
                continue;
            w.Write(r.Id);
            w.Write(d);
        }
    }

    private static RetainerInfo[] ReadRetainerDetails(ref SpanReader s, RetainerInfo[] retainers)
    {
        var result = (RetainerInfo[])retainers.Clone();
        var count = s.Read<ushort>();
        for (var i = 0; i < count; i++)
        {
            var id = s.Read<ulong>();
            var details = s.Read<RetainerDetails>();
            var index = Array.FindIndex(result, x => x.Id == id);
            if (index >= 0)
                result[index] = result[index] with { Details = details };
        }

        return result;
    }

    private static int UnavailableRetainers(CharacterData data) => data.Retainers.Count(r => r.Unavailable);

    private static int UnavailableRetainersSize(int count) => sizeof(ushort) + count * sizeof(ulong);

    private static void WriteUnavailableRetainers(ref SpanWriter w, RetainerInfo[] retainers, int unavailable)
    {
        w.WriteSectionHeader(UnavailableRetainersSection, UnavailableRetainersSize(unavailable));
        w.Write((ushort)unavailable);
        foreach (var r in retainers)
            if (r.Unavailable)
                w.Write(r.Id);
    }

    private static RetainerInfo[] ReadUnavailableRetainers(ref SpanReader s, RetainerInfo[] retainers)
    {
        var result = (RetainerInfo[])retainers.Clone();
        var count = s.Read<ushort>();
        for (var i = 0; i < count; i++)
        {
            var id = s.Read<ulong>();
            var index = Array.FindIndex(result, x => x.Id == id);
            if (index >= 0)
                result[index] = result[index] with { Unavailable = true };
        }

        return result;
    }

    private static int CollectionsSize(CollectionsData c) =>
        sizeof(long) + sizeof(byte) + c.Sets.Sum(s => sizeof(byte) + sizeof(ushort) + s.Bits.Length);

    private static void WriteCollections(ref SpanWriter w, CollectionsData collections)
    {
        if (collections.Sets.Length > byte.MaxValue || collections.Sets.Any(s => s.Bits.Length > ushort.MaxValue))
            throw new InvalidOperationException("Too many collection sets or rows.");
        w.WriteSectionHeader(CollectionsSection, CollectionsSize(collections));
        w.Write(collections.UpdatedTicks);
        w.Write((byte)collections.Sets.Length);
        foreach (var set in collections.Sets)
        {
            w.Write(set.Kind);
            w.Write((ushort)set.Bits.Length);
            w.WriteBytes(set.Bits);
        }
    }

    private static CollectionsData ReadCollections(ref SpanReader s)
    {
        var ticks = s.Read<long>();
        var sets = new CollectionSet[s.Read<byte>()];
        for (var i = 0; i < sets.Length; i++)
        {
            var kind = s.Read<CollectionKind>();
            sets[i] = new CollectionSet(kind, s.ReadBytes(s.Read<ushort>()).ToArray());
        }

        return new CollectionsData(ticks, sets);
    }

    private static int WorkshopSize(WorkshopData w) =>
        sizeof(long) + sizeof(byte) + w.Vessels.Sum(v => 3 * sizeof(byte) + 2 * sizeof(uint) + sizeof(byte) + Encoding.UTF8.GetByteCount(v.Name));

    private static void WriteWorkshop(ref SpanWriter w, WorkshopData workshop)
    {
        if (workshop.Vessels.Length > byte.MaxValue || workshop.Vessels.Any(v => Encoding.UTF8.GetByteCount(v.Name) > byte.MaxValue))
            throw new InvalidOperationException("Too many vessels or vessel name too long.");
        w.WriteSectionHeader(WorkshopSection, WorkshopSize(workshop));
        w.Write(workshop.UpdatedTicks);
        w.Write((byte)workshop.Vessels.Length);
        foreach (var v in workshop.Vessels)
        {
            w.Write(v.Kind);
            w.Write(v.Slot);
            w.Write(v.Rank);
            w.Write(v.RegisterTime);
            w.Write(v.ReturnTime);
            w.Write((byte)Encoding.UTF8.GetByteCount(v.Name));
            w.WriteUtf8(v.Name);
        }
    }

    private static WorkshopData ReadWorkshop(ref SpanReader s)
    {
        var ticks = s.Read<long>();
        var vessels = new Vessel[s.Read<byte>()];
        for (var i = 0; i < vessels.Length; i++)
        {
            var kind = s.Read<VesselKind>();
            var slot = s.Read<byte>();
            var rank = s.Read<byte>();
            var registered = s.Read<uint>();
            var returns = s.Read<uint>();
            vessels[i] = new Vessel(kind, slot, s.ReadUtf8(s.Read<byte>()), rank, registered, returns);
        }

        return new WorkshopData(ticks, vessels);
    }

    private static int MembersSize(MembersData m) =>
        sizeof(long) + sizeof(ushort) + m.Names.Sum(n => sizeof(byte) + Encoding.UTF8.GetByteCount(n));

    private static void WriteMembers(ref SpanWriter w, MembersData members)
    {
        if (members.Names.Length > ushort.MaxValue || members.Names.Any(n => Encoding.UTF8.GetByteCount(n) > byte.MaxValue))
            throw new InvalidOperationException("Too many members or member name too long.");
        w.WriteSectionHeader(MembersSection, MembersSize(members));
        w.Write(members.UpdatedTicks);
        w.Write((ushort)members.Names.Length);
        foreach (var name in members.Names)
        {
            w.Write((byte)Encoding.UTF8.GetByteCount(name));
            w.WriteUtf8(name);
        }
    }

    private static MembersData ReadMembers(ref SpanReader s)
    {
        var ticks = s.Read<long>();
        var names = new string[s.Read<ushort>()];
        for (var i = 0; i < names.Length; i++)
            names[i] = s.ReadUtf8(s.Read<byte>());
        return new MembersData(ticks, names);
    }

    private static int QuestsSize(QuestsData q) => sizeof(long) + sizeof(ushort) + q.Bits.Length;

    private static void WriteQuests(ref SpanWriter w, QuestsData quests)
    {
        if (quests.Bits.Length > ushort.MaxValue)
            throw new InvalidOperationException("Too many quests.");
        w.WriteSectionHeader(QuestsSection, QuestsSize(quests));
        w.Write(quests.UpdatedTicks);
        w.Write((ushort)quests.Bits.Length);
        w.WriteBytes(quests.Bits);
    }

    private static QuestsData ReadQuests(ref SpanReader s) => new(s.Read<long>(), s.ReadBytes(s.Read<ushort>()).ToArray());

    private static int OutfitsSize(OutfitData o) => sizeof(long) + sizeof(ushort) + o.Sets.Length * Unsafe.SizeOf<OutfitPieces>();

    private static void WriteOutfits(ref SpanWriter w, OutfitData outfits)
    {
        if (outfits.Sets.Length > ushort.MaxValue)
            throw new InvalidOperationException("Too many outfits.");
        w.WriteSectionHeader(OutfitsSection, OutfitsSize(outfits));
        w.Write(outfits.UpdatedTicks);
        w.Write((ushort)outfits.Sets.Length);
        w.WriteBytes(MemoryMarshal.AsBytes(outfits.Sets.AsSpan()));
    }

    private static OutfitData ReadOutfits(ref SpanReader s)
    {
        var ticks = s.Read<long>();
        var count = s.Read<ushort>();
        return new OutfitData(ticks, MemoryMarshal.Cast<byte, OutfitPieces>(s.ReadBytes(count * Unsafe.SizeOf<OutfitPieces>())).ToArray());
    }

    private static int HousesSize(HousesData h) => sizeof(long) + sizeof(byte) + h.Houses.Length * Unsafe.SizeOf<House>();

    private static void WriteHouses(ref SpanWriter w, HousesData houses)
    {
        if (houses.Houses.Length > byte.MaxValue)
            throw new InvalidOperationException("Too many houses.");
        w.WriteSectionHeader(HousesSection, HousesSize(houses));
        w.Write(houses.UpdatedTicks);
        w.Write((byte)houses.Houses.Length);
        w.WriteBytes(MemoryMarshal.AsBytes(houses.Houses.AsSpan()));
    }

    private static HousesData ReadHouses(ref SpanReader s)
    {
        var ticks = s.Read<long>();
        var count = s.Read<byte>();
        return new HousesData(ticks, MemoryMarshal.Cast<byte, House>(s.ReadBytes(count * Unsafe.SizeOf<House>())).ToArray());
    }
}
