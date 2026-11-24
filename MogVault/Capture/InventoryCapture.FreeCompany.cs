using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MogVault.Contract;
using MogVault.Store;

namespace MogVault.Capture;

internal sealed partial class InventoryCapture
{
    private const int ChestRequestedMaskOffset = 0x36CC;
    private const int ChestPagePendingOffset = 0x36CE;
    private const int ChestGilPendingOffset = 0x36D4;
    private const long ChestPollMs = 250;

    private static readonly InventoryType[] FreeCompanyTypes =
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5, InventoryType.FreeCompanyGil, InventoryType.FreeCompanyCrystals,
    ];

    private const string FreeCompanyAddon = "FreeCompanyChest";
    private const string FreeCompanyWindow = "FreeCompany";
    private const int FreeCompanyExchangeArray = 52;
    private const int FreeCompanyExchangeStrings = 48;
    private const uint GilItemId = 1;
    private const int FreeCompanyStatusArray = 53;
    private const int FreeCompanyStatusStrings = 49;
    private const int FoundedField = 6;
    private const uint DateText = 23;
    private const int SampleDate = 1006430400;
    private const int MaxedPoints = 999999999;
    private readonly List<InventoryType> dirtyFreeCompany = new(7);
    private uint? lastCredits;
    private FreeCompanyStatus? lastStatus;
    private string? lastCreditsOwnerName;
    private int chestSaved;
    private long nextChestPoll;

    private void OnFreeCompanyWindowClosing(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!suspended && !PacketsHooked && Player() is not null && FreeCompany() is { } fc)
            {
                CaptureCredits(fc);
                CaptureStatus(fc);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "MogVault credits capture failed.");
        }
    }

    private void CaptureFreeCompanyProfile(OwnerInfo fc, bool refresh)
    {
        if (ReadFreeCompanyProfile() is not { } fcProfile ||
            !(refresh || lastFreeCompany is not { } last || last with { UpdatedTicks = 0 } != fcProfile with { UpdatedTicks = 0 }))
            return;
        writer.Post(new OwnerCapture(new OwnerUpdate(fc, FreeCompany: fcProfile)));
        lastFreeCompany = fcProfile;
    }

    private void CaptureWorkshop(OwnerInfo fc)
    {
        if (ReadWorkshop() is not { } workshop || workshop.SameAs(lastWorkshop))
            return;
        writer.Post(new OwnerCapture(new OwnerUpdate(fc, Workshop: workshop)));
        lastWorkshop = workshop;
    }

    private void CaptureMembers(OwnerInfo fc, ulong contentId)
    {
        if (ReadMembers(contentId) is not { } members || members.SameAs(lastMembers))
            return;
        writer.Post(new OwnerCapture(new OwnerUpdate(fc, Members: members)));
        lastMembers = members;
    }

    private void CaptureCredits(OwnerInfo freeCompany)
    {
        if (ReadCredits(out var name) is { } credits)
            PostCredits(freeCompany.Name.Length == 0 && name.Length > 0 ? freeCompany with { Name = name } : freeCompany, credits);
    }

    private void PostCredits(OwnerInfo owner, FreeCompanyCredits credits)
    {
        if (credits.Amount == lastCredits && owner.Name == lastCreditsOwnerName)
            return;
        writer.Post(new OwnerCapture(new OwnerUpdate(owner, Credits: credits)));
        lastCredits = credits.Amount;
        lastCreditsOwnerName = owner.Name;
    }

    private void CaptureStatus(OwnerInfo freeCompany)
    {
        if (ReadStatus(freeCompany.Name) is { } status)
            PostStatus(freeCompany, status);
    }

    private void PostStatus(OwnerInfo owner, FreeCompanyStatus status)
    {
        if (lastStatus is { } last && last with { UpdatedTicks = 0 } == status with { UpdatedTicks = 0 })
            return;
        writer.Post(new OwnerCapture(new OwnerUpdate(owner, Status: status)));
        lastStatus = status;
    }

    private void CaptureFreeCompany(List<InventoryType> changed)
    {
        if (!gameGui.GetAddonByName(FreeCompanyAddon).IsVisible || Player() is null || FreeCompany() is not { } owner)
            return;
        foreach (var type in changed)
            CaptureOne(owner, null, type);
    }

    private void CaptureSettledChestTabs()
    {
        if (!gameGui.GetAddonByName(FreeCompanyAddon).IsVisible || ReadChestRequests(out var pending) is not { } mask || mask == 0)
        {
            chestSaved = 0;
            return;
        }

        var todo = mask & ~chestSaved;
        if (todo == 0 || pending || Player() is null || FreeCompany() is not { } owner)
            return;
        for (var i = 0; i < FreeCompanyTypes.Length; i++)
            if ((todo & (1 << i)) != 0 && CaptureOne(owner, null, FreeCompanyTypes[i]))
                chestSaved |= 1 << i;
    }

    private static unsafe WorkshopData? ReadWorkshop()
    {
        var housing = HousingManager.Instance();
        if (housing == null || housing->WorkshopTerritory == null)
            return null;

        var workshop = housing->WorkshopTerritory;
        var vessels = new List<Vessel>(8);
        for (var i = 0; i < workshop->Airship.Data.Length; i++)
        {
            ref var a = ref workshop->Airship.Data[i];
            if (a.RegisterTime != 0 && a.NameString is { Length: > 0 } name)
                vessels.Add(new Vessel(VesselKind.Airship, (byte)i, name, a.RankId, a.RegisterTime, a.ReturnTime));
        }

        for (var i = 0; i < workshop->Submersible.Data.Length; i++)
        {
            ref var s = ref workshop->Submersible.Data[i];
            if (s.RegisterTime != 0 && s.NameString is { Length: > 0 } name)
                vessels.Add(new Vessel(VesselKind.Submersible, (byte)i, name, s.RankId, s.RegisterTime, s.ReturnTime));
        }

        return vessels.Count == 0 ? null : new WorkshopData(DateTime.UtcNow.Ticks, [.. vessels]);
    }

    private static unsafe FreeCompanyCredits? ReadCredits(out string name)
    {
        name = string.Empty;
        var stage = AtkStage.Instance();
        if (stage == null || stage->AtkArrayDataHolder == null)
            return null;
        var array = stage->AtkArrayDataHolder->GetNumberArrayData(FreeCompanyExchangeArray);
        if (array == null || array->IntArray == null || array->AtkArrayData.Size < 10)
            return null;

        var values = array->IntArray;
        if (values[3] != 0 || values[9] < 0 || ReadFreeCompanyProfile() is not { } fc || values[4] != fc.Rank)
            return null;

        var strings = stage->AtkArrayDataHolder->GetStringArrayData(FreeCompanyExchangeStrings);
        if (strings != null && strings->StringArray != null && strings->AtkArrayData.Size > 1)
            name = strings->StringArray[1].ToString();
        return new FreeCompanyCredits(DateTime.UtcNow.Ticks, (uint)values[9]);
    }

    private static unsafe FreeCompanyStatus? ReadStatus(string name)
    {
        var stage = AtkStage.Instance();
        if (stage == null || stage->AtkArrayDataHolder == null)
            return null;
        var holder = stage->AtkArrayDataHolder;
        var header = holder->GetNumberArrayData(FreeCompanyExchangeArray);
        var status = holder->GetNumberArrayData(FreeCompanyStatusArray);
        var names = holder->GetStringArrayData(FreeCompanyExchangeStrings);
        if (header == null || header->IntArray == null || header->AtkArrayData.Size < 10 ||
            status == null || status->IntArray == null || status->AtkArrayData.Size < 11 || (status->IntArray[0] & 1) == 0)
            return null;
        var values = header->IntArray;
        if (values[3] != 0 || ReadFreeCompanyProfile() is not { } fc || values[4] != fc.Rank)
            return null;
        if (name.Length > 0 && (names == null || names->StringArray == null || names->AtkArrayData.Size < 2 || names->StringArray[1].ToString() != name))
            return null;

        var maxed = values[7] >= MaxedPoints;
        var s = status->IntArray;
        return new FreeCompanyStatus(
            DateTime.UtcNow.Ticks, ReadFounded(holder->GetStringArrayData(FreeCompanyStatusStrings)),
            maxed ? 0 : (uint)values[6], maxed ? 0 : (uint)values[7],
            new Standing((byte)s[3], (byte)s[4]), new Standing((byte)s[6], (byte)s[7]), new Standing((byte)s[9], (byte)s[10]));
    }

    private static unsafe long ReadFounded(StringArrayData* strings)
    {
        var text = RaptureTextModule.Instance();
        if (text == null || strings == null || strings->StringArray == null || strings->AtkArrayData.Size <= FoundedField)
            return 0;
        var sample = Numbers(text->FormatAddonText2Int(DateText, SampleDate).ToString());
        var founded = Numbers(strings->StringArray[FoundedField].ToString());
        var sampleDate = DateTimeOffset.FromUnixTimeSeconds(SampleDate).LocalDateTime;
        int[] order = [Array.IndexOf(sample, sampleDate.Year), Array.IndexOf(sample, sampleDate.Month), Array.IndexOf(sample, sampleDate.Day)];
        if (sample.Length != 3 || founded.Length != 3 || order.Distinct().Count() != 3 || order.Contains(-1))
            return 0;
        return DateTime.TryParse($"{founded[order[0]]:D4}-{founded[order[1]]:D2}-{founded[order[2]]:D2}", CultureInfo.InvariantCulture, out var date) ? date.Ticks : 0;
    }

    private static int[] Numbers(string text) => [.. Digits().Matches(text).Select(m => int.TryParse(m.Value, out var n) ? n : -1)];

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    private static unsafe FreeCompanyProfile? ReadFreeCompanyProfile()
    {
        var info = InfoModule.Instance();
        if (info == null)
            return null;
        var fc = (InfoProxyFreeCompany*)info->GetInfoProxyById(InfoProxyId.FreeCompany);
        return fc == null || fc->Id == 0 || fc->GrandCompany == 0
            ? null
            : new FreeCompanyProfile(DateTime.UtcNow.Ticks, fc->Rank, (byte)fc->GrandCompany, fc->TotalMembers);
    }

    private static unsafe MembersData? ReadMembers(ulong contentId)
    {
        var info = InfoModule.Instance();
        if (info == null)
            return null;
        var fc = (InfoProxyFreeCompany*)info->GetInfoProxyById(InfoProxyId.FreeCompany);
        var list = (InfoProxyCommonList*)info->GetInfoProxyById(InfoProxyId.FreeCompanyMember);
        if (fc == null || list == null || fc->TotalMembers == 0 || list->GetEntryCount() != fc->TotalMembers)
            return null;

        var names = new string[fc->TotalMembers];
        var own = false;
        for (var i = 0u; i < names.Length; i++)
        {
            var entry = list->GetEntry(i);
            if (entry == null || entry->NameString is not { Length: > 0 } name)
                return null;
            own |= entry->ContentId == contentId;
            names[i] = name;
        }

        return own ? new MembersData(DateTime.UtcNow.Ticks, [.. names.Order(StringComparer.Ordinal)]) : null;
    }

    private static unsafe int? ReadChestRequests(out bool pending)
    {
        pending = true;
        var manager = (byte*)InventoryManager.Instance();
        if (manager == null)
            return null;
        int mask = *(ushort*)(manager + ChestRequestedMaskOffset);
        if ((mask & ~0x7F) != 0)
            return null;
        pending = *(manager + ChestPagePendingOffset) != 0 || *(manager + ChestGilPendingOffset) != 0;
        return mask;
    }

    private static unsafe OwnerInfo? FreeCompany()
    {
        var info = InfoModule.Instance();
        if (info == null)
            return null;
        var fc = (InfoProxyFreeCompany*)info->GetInfoProxyById(InfoProxyId.FreeCompany);
        return fc == null || fc->Id == 0 ? null : new OwnerInfo(fc->Id, OwnerKind.FreeCompany, fc->NameString, fc->HomeWorldId);
    }
}
