using FFXIVClientStructs.FFXIV.Client.Game;
using MogVault.Store;

namespace MogVault.Capture;

internal sealed partial class InventoryCapture
{
    private const long HouseGraceMs = 10000;
    private House[]? lastHouses;
    private House[]? lastFreeCompanyHouses;
    private ulong lastIndoor;

    private void TrackHouseEntry()
    {
        var inside = CurrentHouse();
        if (inside == lastIndoor)
            return;
        if (inside != 0 && (Player() is not { } owner || !CaptureHouses(owner, FreeCompany(), inside)))
            return;
        lastIndoor = inside;
    }

    private bool CaptureHouses(OwnerInfo owner, OwnerInfo? freeCompany, ulong entered)
    {
        var ticks = DateTime.UtcNow.Ticks;
        var personal = OwnedHouses(entered, ticks, HouseType.PersonalEstate, HouseType.PrivateChambers, HouseType.Apartment);
        var company = OwnedHouses(entered, ticks, HouseType.FreeCompanyEstate);
        PostHouses(owner, personal, ref lastHouses, ticks);
        if (freeCompany is { } fc)
            PostHouses(fc, company, ref lastFreeCompanyHouses, ticks);
        return freeCompany is not null || !Array.Exists(company, h => h.EnteredTicks != 0);
    }

    private void PostHouses(OwnerInfo owner, House[] houses, ref House[]? last, long ticks)
    {
        var entered = Array.Exists(houses, h => h.EnteredTicks != 0);
        if (!entered && last is not null && houses.AsSpan().SequenceEqual(last))
            return;
        writer.Post(new OwnerCapture(new OwnerUpdate(owner, Houses: new HousesData(ticks, houses))));
        last = Array.ConvertAll(houses, h => h with { EnteredTicks = 0 });
    }

    private static House[] OwnedHouses(ulong entered, long ticks, params HouseType[] types) =>
    [
        .. types.Select(type => (Type: type, Id: HousingManager.GetOwnedHouseId((EstateType)type)))
            .Where(h => h.Id.Id != ulong.MaxValue && h.Id.TerritoryTypeId != 0)
            .Select(h => new House(h.Type, h.Id, h.Id == entered ? ticks : 0)),
    ];

    private static unsafe ulong CurrentHouse()
    {
        var housing = HousingManager.Instance();
        return housing == null || housing->IndoorTerritory == null ? 0 : housing->IndoorTerritory->HouseId;
    }
}
