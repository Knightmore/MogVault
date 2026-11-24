using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MogVault.Store;
using CsPlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;
using QuestRow = Lumina.Excel.Sheets.Quest;

namespace MogVault.Capture;

internal sealed partial class InventoryCapture
{
    private const long ProfilePollMs = 10000;
    private const long ProfileRefreshMs = 300000;
    private const long FreeCompanyGraceMs = 30000;

    private static readonly Lazy<ushort[]> QuestIds = new(() =>
        [.. Underlings.Svc.Data.GetExcelSheet<QuestRow>().Where(q => q.RowId > QuestsData.RowOffset).Select(q => (ushort)(q.RowId - QuestsData.RowOffset))]);

    private long nextProfilePoll;
    private long lastProfilePost;
    private CharacterProfile? lastProfile;
    private ulong lastProfileOwner;
    private FreeCompanyProfile? lastFreeCompany;
    private CollectionsData? lastCollections;
    private bool collectionsDirty = true;
    private WorkshopData? lastWorkshop;
    private MembersData? lastMembers;
    private QuestsData? lastQuests;

    private void OnUnlock(Lumina.Excel.RowRef _) => collectionsDirty = true;

    private void CaptureProfiles(long now)
    {
        if (Player() is not { } owner)
            return;
        var freeCompany = FreeCompany();
        if (freeCompany is null && now - loginAt < FreeCompanyGraceMs)
            return;
        if (ReadProfile(freeCompany?.Id ?? 0) is not { } profile)
            return;

        if (lastProfileOwner != owner.Id)
            ResetOwnerState(owner.Id);

        var refresh = now - lastProfilePost >= ProfileRefreshMs;
        var profileChanged = refresh || !profile.SameAs(lastProfile);
        if (profileChanged)
        {
            writer.Post(new OwnerCapture(new OwnerUpdate(owner, Profile: profile)));
            lastProfile = profile;
            lastProfilePost = now;
        }

        if (sheetsLoaded.IsCompletedSuccessfully)
            CaptureSheetBacked(owner, profileChanged || lastQuests is null);

        if (now - loginAt >= HouseGraceMs)
            CaptureHouses(owner, freeCompany, 0);

        if (freeCompany is { } fc)
            CaptureFreeCompanyProfile(fc, refresh);

        if (collectionsDirty || refresh)
            CaptureCollections(owner);

        if (freeCompany is { } creditsOwner && PacketsHooked)
            CapturePackets(creditsOwner);
        else if (freeCompany is { } windowOwner && gameGui.GetAddonByName(FreeCompanyWindow).IsVisible)
        {
            CaptureCredits(windowOwner);
            CaptureStatus(windowOwner);
        }

        if (freeCompany is { } workshopOwner)
        {
            CaptureWorkshop(workshopOwner);
            CaptureMembers(workshopOwner, owner.Id);
        }
    }

    private void ResetOwnerState(ulong ownerId)
    {
        lastProfileOwner = ownerId;
        lastProfile = null;
        lastFreeCompany = null;
        lastCollections = null;
        lastWorkshop = null;
        lastMembers = null;
        lastCredits = null;
        lastCreditsOwnerName = null;
        lastStatus = null;
        lastQuests = null;
        lastDresser = null;
        lastArmoire = null;
        lastOutfits = null;
        lastHouses = null;
        lastFreeCompanyHouses = null;
        collectionsDirty = true;
    }

    private void CaptureCollections(OwnerInfo owner)
    {
        collectionsDirty = false;
        var read = collections.Read();
        if (read.SameAs(lastCollections))
            return;
        writer.Post(new OwnerCapture(new OwnerUpdate(owner, Collections: read)));
        lastCollections = read;
    }

    private static QuestsData ReadCompletedQuests()
    {
        var ids = QuestIds.Value;
        var bits = new byte[ids.Length == 0 ? 0 : (ids.Max() >> 3) + 1];
        foreach (var id in ids)
            if (QuestManager.IsQuestComplete(id))
                bits[id >> 3] |= (byte)(1 << (id & 7));
        return new QuestsData(DateTime.UtcNow.Ticks, bits);
    }

    private static unsafe CharacterProfile? ReadProfile(ulong freeCompanyId)
    {
        var player = CsPlayerState.Instance();
        if (player == null || !player->IsLoaded)
            return null;

        var msq = MainScenarioQuest();
        var quests = new List<ushort>(30);
        var questManager = QuestManager.Instance();
        if (questManager != null)
            foreach (ref var q in questManager->NormalQuests)
            {
                if (q.QuestId == 0)
                    continue;
                if (!q.IsHidden)
                    quests.Add(q.QuestId);
                if (msq == 0 && IsMainScenario(q.QuestId))
                    msq = q.QuestId;
            }
        quests.Sort();

        return new CharacterProfile(
            DateTime.UtcNow.Ticks, player->CurrentClassJobId, freeCompanyId, msq,
            player->ClassJobLevels.ToArray(), [.. quests]);
    }

    private static bool IsMainScenario(ushort questId) =>
        Underlings.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(0x10000u + questId)
            ?.JournalGenre.ValueNullable?.JournalCategory.ValueNullable?.JournalSection.RowId is 0 or 1;

    private static unsafe ushort MainScenarioQuest()
    {
        var agent = AgentScenarioTree.Instance();
        if (agent == null || agent->Data == null)
            return 0;
        var ids = agent->Data->MainScenarioQuestIds;
        var path = agent->Data->MSQPathIndex;
        return path < 3 && ids[path] != 0 ? ids[path] : ids[3];
    }
}
