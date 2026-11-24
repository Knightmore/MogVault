using MogVault.Contract;

namespace MogVault.Store;

public static class ContainerKinds
{
    public const ushort CurrencyType = 2000;
    public const ushort GlamourDresser = 60000;
    public const ushort Armoire = 60001;

    public static ContainerType Categorize(ushort inventoryType) => inventoryType switch
    {
        <= 3 => ContainerType.Bag,
        1000 => ContainerType.Equipped,
        CurrencyType => ContainerType.Currency,
        2001 => ContainerType.Crystals,
        >= 3200 and <= 3500 => ContainerType.Armoury,
        4000 or 4001 => ContainerType.Saddlebag,
        4100 or 4101 => ContainerType.PremiumSaddlebag,
        >= 10000 and <= 10006 => ContainerType.RetainerBag,
        11000 => ContainerType.RetainerEquipped,
        12000 => ContainerType.RetainerGil,
        12001 => ContainerType.RetainerCrystals,
        12002 => ContainerType.RetainerMarket,
        >= 20000 and <= 20004 => ContainerType.FreeCompanyChest,
        22000 => ContainerType.FreeCompanyGil,
        22001 => ContainerType.FreeCompanyCrystals,
        GlamourDresser => ContainerType.GlamourDresser,
        Armoire => ContainerType.Armoire,
        _ => ContainerType.Unknown,
    };

    public static bool IsRetainer(ContainerType type) => type is
        ContainerType.RetainerBag or ContainerType.RetainerEquipped or ContainerType.RetainerGil or
        ContainerType.RetainerCrystals or ContainerType.RetainerMarket;

    public static bool IsFreeCompany(ContainerType type) => type is
        ContainerType.FreeCompanyChest or ContainerType.FreeCompanyGil or ContainerType.FreeCompanyCrystals;

    public static bool BelongsTo(OwnerKind owner, ContainerData container) =>
        IsRetainer(container.Type) == (container.RetainerId != 0) &&
        IsFreeCompany(container.Type) == (owner == OwnerKind.FreeCompany);
}

