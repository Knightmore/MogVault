using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MogVault.Store;

public static partial class VaultFile
{
    public const uint Magic = 0x54564D4D;
    public const byte CurrentVersion = 1;
    public const string Extension = ".mvd";

    public const ushort ProfileSection = 1;
    public const ushort RetainerDetailsSection = 2;
    public const ushort FreeCompanySection = 3;
    public const ushort CollectionsSection = 4;
    public const ushort WorkshopSection = 5;
    public const ushort CreditsSection = 6;
    public const ushort UnavailableRetainersSection = 7;
    public const ushort QuestsSection = 8;
    public const ushort OutfitsSection = 9;
    public const ushort HousesSection = 10;
    public const ushort StatusSection = 11;
    public const ushort MembersSection = 12;
    private const int SectionHeaderSize = sizeof(ushort) + sizeof(uint);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FileHeader
    {
        public uint Magic;
        public byte Version;
        public OwnerKind Kind;
        public ushort WorldId;
        public ulong OwnerId;
        public ushort ContainerCount;
        public ushort RetainerCount;
        public ushort NameLength;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ContainerHeader
    {
        public ulong RetainerId;
        public long LastUpdatedTicks;
        public ushort InventoryType;
        public ushort StackCount;
        public ushort ExtraCount;
        public ushort Reserved;
    }

    public static string FileName(OwnerKind kind, ulong ownerId) =>
        $"{(kind == OwnerKind.FreeCompany ? "fc" : "chr")}_{ownerId:X16}{Extension}";

    public const string BackupFolder = "backups";
    private const string BackupDateFormat = "yyyy-MM-dd";

    public static string BackupName(DateTime localDate) => localDate.ToString(BackupDateFormat, CultureInfo.InvariantCulture) + ".zip";

    public static IEnumerable<string> ExpiredBackups(IEnumerable<string> fileNames, int keep) =>
        fileNames
            .Where(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        DateOnly.TryParseExact(n[..^4], BackupDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .OrderDescending(StringComparer.Ordinal)
            .Skip(Math.Max(1, keep));

    public static void Write(string path, CharacterData data)
    {
        var size = MeasureSize(data);
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            Serialize(data, buffer.AsSpan(0, size));
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1))
            {
                fs.Write(buffer, 0, size);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static CharacterData Read(string path) => Deserialize(File.ReadAllBytes(path));

    public static int MeasureSize(CharacterData data)
    {
        var size = Unsafe.SizeOf<FileHeader>() + Encoding.UTF8.GetByteCount(data.Name);
        foreach (var r in data.Retainers)
            size += sizeof(ulong) + sizeof(ushort) + Encoding.UTF8.GetByteCount(r.Name);
        foreach (var c in data.Containers)
            size += Unsafe.SizeOf<ContainerHeader>() + c.Stacks.Length * Unsafe.SizeOf<Stack>() + c.Extras.Length * Unsafe.SizeOf<StackExtra>();

        size += sizeof(ushort);
        if (data.Profile is { } p)
            size += SectionHeaderSize + ProfileSize(p);
        if (DetailedRetainers(data) is var n and > 0)
            size += SectionHeaderSize + RetainerDetailsSize(n);
        if (data.FreeCompany is not null)
            size += SectionHeaderSize + Unsafe.SizeOf<FreeCompanyProfile>();
        if (data.Collections is { } collections)
            size += SectionHeaderSize + CollectionsSize(collections);
        if (data.Workshop is { } workshop)
            size += SectionHeaderSize + WorkshopSize(workshop);
        if (data.Credits is not null)
            size += SectionHeaderSize + Unsafe.SizeOf<FreeCompanyCredits>();
        if (UnavailableRetainers(data) is var u and > 0)
            size += SectionHeaderSize + UnavailableRetainersSize(u);
        if (data.Quests is { } quests)
            size += SectionHeaderSize + QuestsSize(quests);
        if (data.Outfits is { } outfits)
            size += SectionHeaderSize + OutfitsSize(outfits);
        if (data.Houses is { } houses)
            size += SectionHeaderSize + HousesSize(houses);
        if (data.Status is not null)
            size += SectionHeaderSize + Unsafe.SizeOf<FreeCompanyStatus>();
        if (data.Members is { } members)
            size += SectionHeaderSize + MembersSize(members);
        return size;
    }

    public static void Serialize(CharacterData data, Span<byte> dest)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Big-endian hosts are not supported.");
        if (data.Containers.Length > ushort.MaxValue || data.Retainers.Length > ushort.MaxValue)
            throw new InvalidOperationException("Too many containers or retainers.");

        var w = new SpanWriter(dest);
        w.Write(new FileHeader
        {
            Magic = Magic,
            Version = CurrentVersion,
            Kind = data.Kind,
            WorldId = data.WorldId,
            OwnerId = data.Id,
            ContainerCount = (ushort)data.Containers.Length,
            RetainerCount = (ushort)data.Retainers.Length,
            NameLength = (ushort)Encoding.UTF8.GetByteCount(data.Name),
        });
        w.WriteUtf8(data.Name);

        foreach (var r in data.Retainers)
        {
            w.Write(r.Id);
            w.Write((ushort)Encoding.UTF8.GetByteCount(r.Name));
            w.WriteUtf8(r.Name);
        }

        foreach (var c in data.Containers)
        {
            if (c.Stacks.Length > ushort.MaxValue || c.Extras.Length > ushort.MaxValue)
                throw new InvalidOperationException("Too many stacks in one container.");
            w.Write(new ContainerHeader
            {
                RetainerId = c.RetainerId,
                LastUpdatedTicks = c.LastUpdatedTicks,
                InventoryType = c.InventoryType,
                StackCount = (ushort)c.Stacks.Length,
                ExtraCount = (ushort)c.Extras.Length,
            });
            w.WriteBytes(MemoryMarshal.AsBytes(c.Stacks.AsSpan()));
            w.WriteBytes(MemoryMarshal.AsBytes(c.Extras.AsSpan()));
        }

        var detailed = DetailedRetainers(data);
        var unavailable = UnavailableRetainers(data);
        w.Write((ushort)((data.Profile is null ? 0 : 1) + (detailed > 0 ? 1 : 0) + (data.FreeCompany is null ? 0 : 1) +
                         (data.Collections is null ? 0 : 1) + (data.Workshop is null ? 0 : 1) + (data.Credits is null ? 0 : 1) +
                         (unavailable > 0 ? 1 : 0) + (data.Quests is null ? 0 : 1) + (data.Outfits is null ? 0 : 1) + (data.Houses is null ? 0 : 1) +
                         (data.Status is null ? 0 : 1) + (data.Members is null ? 0 : 1)));

        if (data.Profile is { } p)
            WriteProfile(ref w, p);
        if (detailed > 0)
            WriteRetainerDetails(ref w, data.Retainers, detailed);
        if (data.FreeCompany is { } fc)
            WriteFixed(ref w, FreeCompanySection, fc);
        if (data.Collections is { } collections)
            WriteCollections(ref w, collections);
        if (data.Workshop is { } workshop)
            WriteWorkshop(ref w, workshop);
        if (data.Credits is { } credits)
            WriteFixed(ref w, CreditsSection, credits);
        if (unavailable > 0)
            WriteUnavailableRetainers(ref w, data.Retainers, unavailable);
        if (data.Quests is { } quests)
            WriteQuests(ref w, quests);
        if (data.Outfits is { } outfits)
            WriteOutfits(ref w, outfits);
        if (data.Houses is { } houses)
            WriteHouses(ref w, houses);
        if (data.Status is { } status)
            WriteFixed(ref w, StatusSection, status);
        if (data.Members is { } members)
            WriteMembers(ref w, members);

        if (w.Remaining != 0)
            throw new InvalidOperationException("Size mismatch while serializing.");
    }

    public static CharacterData Deserialize(ReadOnlySpan<byte> src)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Big-endian hosts are not supported.");

        var r = new SpanReader(src);
        var header = r.Read<FileHeader>();
        if (header.Magic != Magic)
            throw new InvalidDataException("Not a MogVault file (bad magic).");

        if (header.Version > CurrentVersion)
            throw new InvalidDataException($"File version {header.Version} is newer than supported version {CurrentVersion}.");
        if (header.Version != CurrentVersion)
            throw new InvalidDataException($"Unknown file version {header.Version}.");

        var data = ReadSections(ref r, ReadBody(ref r, header));
        if (r.Remaining != 0)
            throw new InvalidDataException("Trailing bytes after the last section.");
        return data;
    }

    private static CharacterData ReadBody(ref SpanReader r, FileHeader header)
    {
        if (header.Kind is not (OwnerKind.Character or OwnerKind.FreeCompany))
            throw new InvalidDataException($"Unknown owner kind {(byte)header.Kind}.");

        var name = r.ReadUtf8(header.NameLength);

        var retainers = new RetainerInfo[header.RetainerCount];
        for (var i = 0; i < retainers.Length; i++)
        {
            var id = r.Read<ulong>();
            var len = r.Read<ushort>();
            retainers[i] = new RetainerInfo(id, r.ReadUtf8(len));
        }

        var containers = new ContainerData[header.ContainerCount];
        for (var i = 0; i < containers.Length; i++)
        {
            var ch = r.Read<ContainerHeader>();
            var stacks = MemoryMarshal.Cast<byte, Stack>(r.ReadBytes(ch.StackCount * Unsafe.SizeOf<Stack>())).ToArray();
            var extras = MemoryMarshal.Cast<byte, StackExtra>(r.ReadBytes(ch.ExtraCount * Unsafe.SizeOf<StackExtra>())).ToArray();
            containers[i] = new ContainerData(ch.RetainerId, ch.InventoryType, ch.LastUpdatedTicks, stacks, extras);
        }

        return new CharacterData(header.OwnerId, header.Kind, name, header.WorldId, retainers, containers);
    }

    private static CharacterData ReadSections(ref SpanReader r, CharacterData data)
    {
        var count = r.Read<ushort>();
        for (var i = 0; i < count; i++)
        {
            var tag = r.Read<ushort>();
            var length = r.Read<uint>();
            if (length > r.Remaining)
                throw new InvalidDataException("Section runs past the end of the file.");
            var s = new SpanReader(r.ReadBytes((int)length));
            switch (tag)
            {
                case ProfileSection:
                    data = data with { Profile = ReadProfile(ref s) };
                    break;
                case RetainerDetailsSection:
                    data = data with { Retainers = ReadRetainerDetails(ref s, data.Retainers) };
                    break;
                case FreeCompanySection:
                    data = data with { FreeCompany = s.Read<FreeCompanyProfile>() };
                    break;
                case CollectionsSection:
                    data = data with { Collections = ReadCollections(ref s) };
                    break;
                case WorkshopSection:
                    data = data with { Workshop = ReadWorkshop(ref s) };
                    break;
                case CreditsSection:
                    data = data with { Credits = s.Read<FreeCompanyCredits>() };
                    break;
                case UnavailableRetainersSection:
                    data = data with { Retainers = ReadUnavailableRetainers(ref s, data.Retainers) };
                    break;
                case QuestsSection:
                    data = data with { Quests = ReadQuests(ref s) };
                    break;
                case OutfitsSection:
                    data = data with { Outfits = ReadOutfits(ref s) };
                    break;
                case HousesSection:
                    data = data with { Houses = ReadHouses(ref s) };
                    break;
                case StatusSection:
                    data = data with { Status = s.Read<FreeCompanyStatus>() };
                    break;
                case MembersSection:
                    data = data with { Members = ReadMembers(ref s) };
                    break;
                default:
                    continue;
            }

            if (s.Remaining != 0)
                throw new InvalidDataException($"Section {tag} has {s.Remaining} unread bytes.");
        }

        return data;
    }
}
