using Lumina.Excel.Sheets;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Game;

internal static class OutfitCatalog
{
    public const int SlotCount = 11;

    private static readonly Lazy<Dictionary<uint, Outfit>> Outfits = new(Load);

    public static IReadOnlyCollection<Outfit> All => Outfits.Value.Values;

    public static Outfit? Find(uint setId) => Outfits.Value.GetValueOrDefault(setId);

    private static Dictionary<uint, Outfit> Load()
    {
        var items = UnderlingsSvc.Data.GetExcelSheet<Item>();
        var outfits = new Dictionary<uint, Outfit>();
        foreach (var row in UnderlingsSvc.Data.GetExcelSheet<MirageStoreSetItem>())
        {
            uint[] pieces =
            [
                row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId, row.Hands.RowId, row.Legs.RowId,
                row.Feet.RowId, row.Earrings.RowId, row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
            ];
            if (pieces.All(p => p == 0) || items.GetRowOrDefault(row.RowId) is not { } item || item.Name.IsEmpty)
                continue;
            outfits[row.RowId] = new Outfit(row.RowId, item.Name.ExtractText(), item.Icon, pieces);
        }

        return outfits;
    }
}

internal sealed record Outfit(uint SetId, string Name, uint IconId, uint[] Pieces)
{
    public ushort Slots { get; } = (ushort)Pieces.Select((p, i) => p == 0 ? 0 : 1 << i).Sum();

    public int PieceCount => Pieces.Count(p => p != 0);

    public int Owned(ushort slots) => System.Numerics.BitOperations.PopCount((uint)(slots & Slots));

    public IEnumerable<uint> PiecesIn(ushort slots) => Pieces.Where((p, i) => p != 0 && (slots & (1 << i)) != 0);
}
