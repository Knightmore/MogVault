using System.Runtime.InteropServices;

namespace MogVault.Store;

[Flags]
public enum StackFlags : byte
{
    None = 0,
    Collectable = 1,

    HasExtra = 2,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct Stack
{
    public const uint ItemIdMask = 0x000FFFFF;
    public const uint HqBit = 0x80000000;

    public readonly uint ItemIdHq;

    public readonly ushort Qty;
    public readonly byte Slot;
    public readonly StackFlags Flags;

    public Stack(uint itemId, bool hq, ushort qty, byte slot, StackFlags flags = StackFlags.None)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(itemId, ItemIdMask);
        ItemIdHq = itemId | (hq ? HqBit : 0);
        Qty = qty;
        Slot = slot;
        Flags = flags;
    }

    public uint ItemId => ItemIdHq & ItemIdMask;
    public bool IsHq => (ItemIdHq & HqBit) != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct StackExtra
{
    public const int MateriaSlots = 5;
    public const int StainSlots = 2;

    public readonly byte Slot;
    public readonly byte Stain0, Stain1;
    public readonly byte Grade0, Grade1, Grade2, Grade3, Grade4;
    public readonly ushort SpiritbondOrCollectability;
    public readonly ushort Materia0, Materia1, Materia2, Materia3, Materia4;

    public readonly uint Quantity;

    public StackExtra(byte slot, uint quantity, ushort spiritbondOrCollectability, ReadOnlySpan<byte> stains, ReadOnlySpan<ushort> materia, ReadOnlySpan<byte> grades)
    {
        Slot = slot;
        Quantity = quantity;
        SpiritbondOrCollectability = spiritbondOrCollectability;
        stains[..Math.Min(stains.Length, StainSlots)].CopyTo(MemoryMarshal.CreateSpan(ref Stain0, StainSlots));
        materia[..Math.Min(materia.Length, MateriaSlots)].CopyTo(MemoryMarshal.CreateSpan(ref Materia0, MateriaSlots));
        grades[..Math.Min(grades.Length, MateriaSlots)].CopyTo(MemoryMarshal.CreateSpan(ref Grade0, MateriaSlots));
    }

    public byte Stain(int i) => i switch
    {
        0 => Stain0,
        1 => Stain1,
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };

    public ushort Materia(int i) => i switch
    {
        0 => Materia0,
        1 => Materia1,
        2 => Materia2,
        3 => Materia3,
        4 => Materia4,
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };

    public byte Grade(int i) => i switch
    {
        0 => Grade0,
        1 => Grade1,
        2 => Grade2,
        3 => Grade3,
        4 => Grade4,
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };
}

