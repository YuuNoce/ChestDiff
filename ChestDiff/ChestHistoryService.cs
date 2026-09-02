using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.NativeWrapper;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace ChestDiff;

public sealed partial class ChestHistoryService
{
    private const int HistoryStringStartIndex = 70;
    private const int HistoryStringStride = 5;
    private const int HistoryNumberStartIndex = 356;
    private const int HistoryNumberStride = 3;
    private const string HistoryAddonName = "FreeCompanyChestLog";

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private Dictionary<string, uint>? itemIdsByName;

    public ChestHistoryService(Configuration configuration, IGameGui gameGui, IDataManager dataManager)
    {
        this.configuration = configuration;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
    }

    public string DefaultOutputDirectory => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "exports");

    public HistoryExportResult ExportSince(DateTimeOffset from)
    {
        return Export(LoadSince(from), exportSummaryCsv: true, exportDumpFile: false);
    }

    public HistoryLoadResult LoadSince(DateTimeOffset from)
    {
        var capturedAt = DateTimeOffset.Now;
        var allEntries = ReadHistoryEntriesFromArrays(capturedAt);
        var entries = allEntries
            .Where(entry => entry.Timestamp >= from)
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.PlayerName, StringComparer.Ordinal)
            .ThenBy(entry => entry.ItemId)
            .ThenBy(entry => entry.ItemName, StringComparer.Ordinal)
            .ToList();

        return new HistoryLoadResult(from, capturedAt, allEntries.Count, entries.Count, entries, BuildSummaries(entries), BuildTabSummaries(entries));
    }

    public HistoryExportResult Export(HistoryLoadResult history, bool exportSummaryCsv, bool exportDumpFile)
    {
        var summaryCsvPath = exportSummaryCsv
            ? WriteSummaryCsv(history.From, history.CapturedAt, history.Summaries)
            : null;
        var dumpCsvPath = exportDumpFile
            ? WriteDumpCsv(history.From, history.CapturedAt, history.Entries)
            : null;

        return new HistoryExportResult(history.From, history.CapturedAt, history.VisibleHistoryRowCount, history.ExportedEntryCount, summaryCsvPath, dumpCsvPath);
    }

    public IReadOnlyList<string> ReadVisibleRows()
    {
        var capturedAt = DateTimeOffset.Now;
        return ReadHistoryEntriesFromArrays(capturedAt)
            .Select(entry => $"{entry.Timestamp:yyyy-MM-dd HH:mm},{entry.ActionKind},{entry.Action},{entry.PlayerName},{FormatChestTab(entry.ChestTab)},{entry.ChestLocation},{entry.ItemName},{entry.ItemId},{entry.Quantity}")
            .ToList();
    }

    public DebugDumpResult DumpCandidateAddonText()
    {
        var addon = FindVisibleHistoryAddon();
        if (addon.IsNull || !addon.IsVisible || addon.Address == IntPtr.Zero)
        {
            throw new InvalidOperationException("FC chest history addon was not found. Run /chestdiff addons while the history screen is open, then set Addon names to the matching addon name.");
        }

        unsafe
        {
            var unit = (AtkUnitBase*)addon.Address;
            var dumpEntries = CollectAddonText(unit);
            var path = WriteAddonTextDumpCsv(unit->NameString, dumpEntries);
            return new DebugDumpResult(path, dumpEntries.Count);
        }
    }

    public DebugDumpResult DumpVisibleAddons()
    {
        unsafe
        {
            var stage = AtkStage.Instance();
            if (stage is null || stage->RaptureAtkUnitManager is null)
            {
                throw new InvalidOperationException("AtkStage or RaptureAtkUnitManager was not available.");
            }

            var addons = new List<VisibleAddonSnapshot>();
            AddUnitsFromList(stage->RaptureAtkUnitManager->AllLoadedUnitsList, addons);
            var path = WriteVisibleAddonDumpCsv(addons);
            return new DebugDumpResult(path, addons.Count);
        }
    }

    public DebugDumpResult DumpFreeCompanyArrays()
    {
        unsafe
        {
            var stage = AtkStage.Instance();
            if (stage is null)
            {
                throw new InvalidOperationException("AtkStage was not available.");
            }

            var entries = new List<ArrayDumpEntry>();

            foreach (var arrayType in Enum.GetValues<StringArrayType>().Where(type => type.ToString().Contains("FreeCompany", StringComparison.Ordinal)))
            {
                CollectStringArray(stage->GetStringArrayData(arrayType), (int)arrayType, arrayType.ToString(), entries);
            }

            foreach (var arrayType in Enum.GetValues<NumberArrayType>().Where(type => type.ToString().Contains("FreeCompany", StringComparison.Ordinal)))
            {
                CollectNumberArray(stage->GetNumberArrayData(arrayType), (int)arrayType, arrayType.ToString(), entries);
            }

            var path = WriteArrayDumpCsv(entries);
            return new DebugDumpResult(path, entries.Count);
        }
    }

    public string GetOutputDirectory()
    {
        if (string.IsNullOrWhiteSpace(configuration.OutputDirectory))
        {
            return DefaultOutputDirectory;
        }

        var configured = configuration.OutputDirectory.Trim();
        return Path.IsPathFullyQualified(configured)
            ? configured
            : Path.Combine(DefaultOutputDirectory, configured);
    }

    public bool IsHistoryLogOpen()
    {
        var addon = gameGui.GetAddonByName(HistoryAddonName);
        return !addon.IsNull && addon.IsVisible && addon.Address != IntPtr.Zero;
    }

    private IReadOnlyList<HistoryEntry> ReadHistoryEntriesFromArrays(DateTimeOffset capturedAt)
    {
        if (!IsHistoryLogOpen())
        {
            throw new InvalidOperationException("FC chest history addon was not found. Open the FC chest history screen before running /chestdiff export.");
        }

        unsafe
        {
            var stage = AtkStage.Instance();
            if (stage is null)
            {
                throw new InvalidOperationException("AtkStage was not available.");
            }

            var stringArray = stage->GetStringArrayData(StringArrayType.FreeCompanyChest);
            var numberArray = stage->GetNumberArrayData(NumberArrayType.FreeCompanyChest);
            if (stringArray is null || numberArray is null || stringArray->ManagedStringArray is null || numberArray->IntArray is null)
            {
                throw new InvalidOperationException("FreeCompanyChest history arrays were not available. Open the FC chest history screen, then run /chestdiff arrays to verify array 55/59.");
            }

            var strings = stringArray->ManagedSpan;
            var numbers = numberArray->Span;
            var entries = new List<HistoryEntry>();

            for (var stringIndex = HistoryStringStartIndex;
                 stringIndex + 4 < strings.Length;
                 stringIndex += HistoryStringStride)
            {
                var rowIndex = (stringIndex - HistoryStringStartIndex) / HistoryStringStride;
                var numberIndex = HistoryNumberStartIndex + (rowIndex * HistoryNumberStride);
                if (numberIndex + 2 >= numbers.Length)
                {
                    break;
                }

                var playerName = CleanDisplayText(strings[stringIndex].ToString());
                var rawChestLocation = strings[stringIndex + 1].ToString();
                var chestTab = ParseChestTab(rawChestLocation);
                var chestLocation = CleanDisplayText(rawChestLocation);
                var rawItemText = CleanText(strings[stringIndex + 2].ToString());
                var itemName = CleanItemText(rawItemText);
                var timestampText = CleanDisplayText(strings[stringIndex + 4].ToString());

                if (!TryParseTimestamp(timestampText, capturedAt, out var timestamp))
                {
                    if (entries.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                var actionKind = numbers[numberIndex];
                var itemId = numbers[numberIndex + 1] > 0 ? (uint)numbers[numberIndex + 1] : 0;
                var quantity = numbers[numberIndex + 2];
                if (quantity <= 0)
                {
                    quantity = TryParseQuantity(strings[stringIndex + 3].ToString(), out var parsedQuantity)
                        ? parsedQuantity
                        : 0;
                }

                if (string.IsNullOrWhiteSpace(playerName) && string.IsNullOrWhiteSpace(itemName) && quantity <= 0)
                {
                    continue;
                }

                entries.Add(new HistoryEntry(
                    timestamp,
                    actionKind,
                    ActionFromKind(actionKind),
                    playerName,
                    chestTab,
                    chestLocation,
                    itemName,
                    itemId,
                    quantity,
                    rawItemText));
            }

            return entries;
        }
    }

    private AtkUnitBasePtr FindVisibleHistoryAddon()
    {
        var addon = gameGui.GetAddonByName(HistoryAddonName);
        return !addon.IsNull && addon.IsVisible
            ? addon
            : default;
    }

    private unsafe static void AddUnitsFromList(AtkUnitList list, List<VisibleAddonSnapshot> addons)
    {
        var entries = list.Entries;
        var count = Math.Min(list.Count, entries.Length);
        for (var index = 0; index < count; index++)
        {
            var unit = entries[index].Value;
            if (unit is null || !unit->IsVisible)
            {
                continue;
            }

            var name = unit->NameString;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            addons.Add(new VisibleAddonSnapshot(name, unit->Id, unit->X, unit->Y, unit->GetScaledWidth(false), unit->GetScaledHeight(false)));
        }
    }

    private unsafe static void CollectTextNodes(AtkResNode* node, List<TextNodeSnapshot> results, int depth)
    {
        if (node is null || depth > 64)
        {
            return;
        }

        for (var current = node; current is not null; current = current->NextSiblingNode)
        {
            if (!current->IsVisible())
            {
                continue;
            }

            if (current->Type == NodeType.Text)
            {
                var textNode = current->GetAsAtkTextNode();
                if (textNode is not null)
                {
                    var text = textNode->NodeText.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        results.Add(new TextNodeSnapshot(current->NodeId, current->ScreenX, current->ScreenY, text));
                    }
                }
            }

            if (current->Type == NodeType.Component)
            {
                var componentNode = current->GetAsAtkComponentNode();
                var component = componentNode is null ? null : componentNode->Component;
                var componentRoot = component is null ? null : component->AtkResNode;
                if (componentRoot is not null)
                {
                    CollectTextNodes(componentRoot, results, depth + 1);
                }
            }

            if (current->ChildNode is not null)
            {
                CollectTextNodes(current->ChildNode, results, depth + 1);
            }
        }
    }

    private unsafe static IReadOnlyList<AddonTextDumpEntry> CollectAddonText(AtkUnitBase* unit)
    {
        var entries = new List<AddonTextDumpEntry>();
        CollectTextNodes(unit->RootNode, entries, depth: 0);
        CollectListLabels(unit->RootNode, entries, depth: 0);
        CollectAtkValues(unit, entries);
        return entries;
    }

    private unsafe static void CollectTextNodes(AtkResNode* node, List<AddonTextDumpEntry> results, int depth)
    {
        if (node is null || depth > 64)
        {
            return;
        }

        for (var current = node; current is not null; current = current->NextSiblingNode)
        {
            if (!current->IsVisible())
            {
                continue;
            }

            if (current->Type == NodeType.Text)
            {
                var textNode = current->GetAsAtkTextNode();
                AddDumpEntry(results, "TextNode", current->NodeId, -1, current->ScreenX, current->ScreenY, textNode is null ? "" : textNode->NodeText.ToString());
            }

            if (current->Type == NodeType.Component)
            {
                var componentNode = current->GetAsAtkComponentNode();
                var component = componentNode is null ? null : componentNode->Component;
                var componentRoot = component is null ? null : component->AtkResNode;
                if (componentRoot is not null)
                {
                    CollectTextNodes(componentRoot, results, depth + 1);
                }
            }

            if (current->ChildNode is not null)
            {
                CollectTextNodes(current->ChildNode, results, depth + 1);
            }
        }
    }

    private unsafe static void CollectListLabels(AtkResNode* node, List<AddonTextDumpEntry> results, int depth)
    {
        if (node is null || depth > 64)
        {
            return;
        }

        for (var current = node; current is not null; current = current->NextSiblingNode)
        {
            if (!current->IsVisible())
            {
                continue;
            }

            if (current->Type == NodeType.Component)
            {
                var list = current->GetAsAtkComponentList();
                if (list is not null)
                {
                    CollectListLabels(current, list, results);
                }

                var componentNode = current->GetAsAtkComponentNode();
                var component = componentNode is null ? null : componentNode->Component;
                var componentRoot = component is null ? null : component->AtkResNode;
                if (componentRoot is not null)
                {
                    CollectListLabels(componentRoot, results, depth + 1);
                }
            }

            if (current->ChildNode is not null)
            {
                CollectListLabels(current->ChildNode, results, depth + 1);
            }
        }
    }

    private unsafe static void CollectListLabels(AtkResNode* ownerNode, AtkComponentList* list, List<AddonTextDumpEntry> results)
    {
        var listLength = Math.Clamp(list->ListLength, 0, 1000);

        if (list->ItemLabels is not null)
        {
            for (var index = 0; index < listLength; index++)
            {
                AddDumpEntry(results, "List.ItemLabels", ownerNode->NodeId, index, ownerNode->ScreenX, ownerNode->ScreenY, list->ItemLabels[index].ToString());
            }
        }

        if (list->ItemRendererList is not null)
        {
            var rendererCount = Math.Clamp(list->AllocatedItemRendererListLength, 0, 1000);
            for (var index = 0; index < rendererCount; index++)
            {
                AddDumpEntry(results, "List.RendererList.Label", ownerNode->NodeId, index, ownerNode->ScreenX, ownerNode->ScreenY, list->ItemRendererList[index].Label.ToString());
            }
        }

        for (var index = 0; index < listLength; index++)
        {
            AddDumpEntry(results, "List.GetItemLabel", ownerNode->NodeId, index, ownerNode->ScreenX, ownerNode->ScreenY, list->GetItemLabel(index).ToString());
        }
    }

    private unsafe static void CollectAtkValues(AtkUnitBase* unit, List<AddonTextDumpEntry> results)
    {
        if (unit->AtkValues is null)
        {
            return;
        }

        var count = Math.Clamp((int)unit->AtkValuesCount, 0, 512);
        for (var index = 0; index < count; index++)
        {
            var value = unit->AtkValues[index];
            AddDumpEntry(results, $"AtkValue.{value.Type}", 0, index, 0, 0, value.GetValueAsString());
        }
    }

    private unsafe static void CollectStringArray(StringArrayData* array, int arrayType, string arrayName, List<ArrayDumpEntry> results)
    {
        if (array is null)
        {
            return;
        }

        AddArrayEntry(results, "String.Meta", arrayType, arrayName, -1, FormatStringArrayMeta(array));

        if (array->StringArray is not null)
        {
            var span = array->Span;
            var count = Math.Min(span.Length, 4096);
            for (var index = 0; index < count; index++)
            {
                AddArrayEntry(results, "String", arrayType, arrayName, index, span[index].ToString());
            }
        }

        if (array->ManagedStringArray is not null)
        {
            var span = array->ManagedSpan;
            var count = Math.Min(span.Length, 4096);
            for (var index = 0; index < count; index++)
            {
                AddArrayEntry(results, "ManagedString", arrayType, arrayName, index, span[index].ToString());
            }
        }
    }

    private unsafe static void CollectNumberArray(NumberArrayData* array, int arrayType, string arrayName, List<ArrayDumpEntry> results)
    {
        if (array is null)
        {
            return;
        }

        AddArrayEntry(results, "Number.Meta", arrayType, arrayName, -1, FormatNumberArrayMeta(array));

        if (array->IntArray is null)
        {
            return;
        }

        var span = array->Span;
        var count = Math.Min(span.Length, 4096);
        for (var index = 0; index < count; index++)
        {
            var value = span[index];
            if (value != 0)
            {
                AddArrayEntry(results, "Number", arrayType, arrayName, index, value.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private unsafe static string FormatStringArrayMeta(StringArrayData* array)
    {
        return $"size={array->Size}; subscribed={FormatSubscribedAddons(array->SubscribedAddons)}; update_state={array->UpdateState}; ref_count={array->RefCount}";
    }

    private unsafe static string FormatNumberArrayMeta(NumberArrayData* array)
    {
        return $"size={array->Size}; subscribed={FormatSubscribedAddons(array->SubscribedAddons)}; update_state={array->UpdateState}; ref_count={array->RefCount}";
    }

    private static string FormatSubscribedAddons(Span<byte> subscribedAddons)
    {
        return string.Join('|', subscribedAddons.ToArray().Where(id => id != 0));
    }

    private static void AddDumpEntry(List<AddonTextDumpEntry> results, string source, uint nodeId, int index, float screenX, float screenY, string text)
    {
        text = CleanText(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        results.Add(new AddonTextDumpEntry(source, nodeId, index, screenX, screenY, text));
    }

    private static void AddArrayEntry(List<ArrayDumpEntry> results, string source, int arrayType, string arrayName, int index, string value)
    {
        value = CleanText(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        results.Add(new ArrayDumpEntry(source, arrayType, arrayName, index, value));
    }

    private HistoryEntry ParseEntry(string rawText, DateTimeOffset capturedAt)
    {
        var timestamp = TryParseTimestamp(rawText, capturedAt, out var parsedTimestamp)
            ? parsedTimestamp
            : DateTimeOffset.MinValue;
        var action = DetectAction(rawText);
        var quantity = TryParseQuantity(rawText, out var parsedQuantity) ? parsedQuantity : 0;
        var itemName = GuessItemName(rawText);
        var itemId = ResolveItemId(itemName);

        return new HistoryEntry(timestamp, 0, action, "", 0, "", itemName, itemId, quantity, rawText);
    }

    private string WriteDumpCsv(DateTimeOffset from, DateTimeOffset capturedAt, IReadOnlyList<HistoryEntry> entries)
    {
        var outputDirectory = GetOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var path = Path.Combine(outputDirectory, $"fc_chest_dump_from_{from:yyyyMMdd_HHmm}_to_{capturedAt:yyyyMMdd_HHmmss}.csv");
        var builder = new StringBuilder();
        builder.AppendLine("requested_from_time,captured_at,timestamp,action_kind,action,player,chest_tab,chest_location,item_name,quantity,raw_item_text");

        foreach (var entry in entries)
        {
            builder.Append(Escape(FormatCsvDateTime(from))).Append(',');
            builder.Append(Escape(FormatCsvDateTime(capturedAt))).Append(',');
            builder.Append(Escape(entry.Timestamp == DateTimeOffset.MinValue ? "" : FormatCsvDateTime(entry.Timestamp))).Append(',');
            builder.Append(entry.ActionKind.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Escape(entry.Action)).Append(',');
            builder.Append(Escape(entry.PlayerName)).Append(',');
            builder.Append(Escape(FormatChestTab(entry.ChestTab))).Append(',');
            builder.Append(Escape(entry.ChestLocation)).Append(',');
            builder.Append(Escape(entry.ItemName)).Append(',');
            builder.Append(entry.Quantity.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.AppendLine(Escape(entry.RawItemText));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private string WriteSummaryCsv(DateTimeOffset from, DateTimeOffset capturedAt, IReadOnlyList<HistorySummaryEntry> summaries)
    {
        var outputDirectory = GetOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var path = Path.Combine(outputDirectory, $"fc_chest_summary_from_{from:yyyyMMdd_HHmm}_to_{capturedAt:yyyyMMdd_HHmmss}.csv");
        var builder = new StringBuilder();
        builder.AppendLine("requested_from_time,captured_at,item_name,player,deposited_quantity,withdrawn_quantity,net_quantity");

        foreach (var summary in summaries)
        {
            builder.Append(Escape(FormatCsvDateTime(from))).Append(',');
            builder.Append(Escape(FormatCsvDateTime(capturedAt))).Append(',');
            builder.Append(Escape(summary.ItemName)).Append(',');
            builder.Append(Escape(summary.PlayerName)).Append(',');
            builder.Append(summary.Deposited.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(summary.Withdrawn.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.AppendLine(summary.Net.ToString(CultureInfo.InvariantCulture));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static IReadOnlyList<HistorySummaryEntry> BuildSummaries(IReadOnlyList<HistoryEntry> entries)
    {
        return entries
            .Where(IsSummaryEntry)
            .GroupBy(entry => new SummaryKey(entry.PlayerName, GetSummaryItemId(entry), GetSummaryItemName(entry)), SummaryKeyComparer.Instance)
            .Select(group =>
            {
                var deposited = group
                    .Where(IsSummaryDeposit)
                    .Sum(entry => entry.Quantity);
                var withdrawn = group
                    .Where(IsSummaryWithdraw)
                    .Sum(entry => entry.Quantity);
                var sample = group.First();
                return new HistorySummaryEntry(
                    GetSummaryItemName(sample),
                    sample.PlayerName,
                    deposited,
                    withdrawn,
                    group.Sum(GetSummaryNetQuantity));
            })
            .OrderBy(summary => summary.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => summary.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<HistoryTabSummary> BuildTabSummaries(IReadOnlyList<HistoryEntry> entries)
    {
        return Enumerable.Range(1, 5)
            .Select(chestTab => new HistoryTabSummary(
                chestTab,
                BuildSummaries(entries.Where(entry => entry.ChestTab == chestTab).ToList())))
            .ToList();
    }

    private static bool IsSummaryEntry(HistoryEntry entry)
    {
        if (entry.Quantity <= 0)
        {
            return false;
        }

        if (IsGilAction(entry))
        {
            return true;
        }

        return (entry.Action == "deposit" || entry.Action == "withdraw")
            && (!string.IsNullOrWhiteSpace(entry.ItemName) || entry.ItemId != 0);
    }

    private static bool IsSummaryDeposit(HistoryEntry entry)
    {
        return entry.Action == "deposit"
            || entry.Action == "gil_deposit"
            || (entry.Action == "gil" && DetectGilAction(entry) == "deposit");
    }

    private static bool IsSummaryWithdraw(HistoryEntry entry)
    {
        return entry.Action == "withdraw"
            || entry.Action == "gil_withdraw"
            || (entry.Action == "gil" && DetectGilAction(entry) == "withdraw");
    }

    private static int GetSummaryNetQuantity(HistoryEntry entry)
    {
        if (entry.Action == "withdraw" || entry.Action == "gil_withdraw")
        {
            return -entry.Quantity;
        }

        if (entry.Action == "gil")
        {
            return DetectGilAction(entry) == "withdraw"
                ? -entry.Quantity
                : entry.Quantity;
        }

        return entry.Quantity;
    }

    private static uint GetSummaryItemId(HistoryEntry entry)
    {
        return IsGilAction(entry) ? 0 : entry.ItemId;
    }

    private static string GetSummaryItemName(HistoryEntry entry)
    {
        return IsGilAction(entry) && string.IsNullOrWhiteSpace(entry.ItemName)
            ? "gil"
            : entry.ItemName;
    }

    private static bool IsGilAction(HistoryEntry entry)
    {
        return entry.Action == "gil"
            || entry.Action == "gil_deposit"
            || entry.Action == "gil_withdraw";
    }

    private static string DetectGilAction(HistoryEntry entry)
    {
        return DetectAction($"{entry.RawItemText} {entry.ChestLocation}");
    }

    private string WriteAddonTextDumpCsv(string addonName, IReadOnlyList<AddonTextDumpEntry> entries)
    {
        var outputDirectory = GetOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var path = Path.Combine(outputDirectory, $"fc_chest_text_dump_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.csv");
        var builder = new StringBuilder();
        builder.AppendLine("addon_name,source,node_id,index,screen_x,screen_y,text");

        foreach (var entry in entries
            .OrderBy(entry => entry.Source, StringComparer.Ordinal)
            .ThenBy(entry => entry.ScreenY)
            .ThenBy(entry => entry.ScreenX)
            .ThenBy(entry => entry.Index))
        {
            builder.Append(Escape(addonName)).Append(',');
            builder.Append(Escape(entry.Source)).Append(',');
            builder.Append(entry.NodeId.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(entry.Index.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(entry.ScreenX.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(entry.ScreenY.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.AppendLine(Escape(entry.Text));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private string WriteVisibleAddonDumpCsv(IReadOnlyList<VisibleAddonSnapshot> addons)
    {
        var outputDirectory = GetOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var path = Path.Combine(outputDirectory, $"visible_addons_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.csv");
        var builder = new StringBuilder();
        builder.AppendLine("name,id,x,y,width,height");

        foreach (var addon in addons.OrderBy(addon => addon.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(Escape(addon.Name)).Append(',');
            builder.Append(addon.Id.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(addon.X.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(addon.Y.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(addon.Width.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.AppendLine(addon.Height.ToString(CultureInfo.InvariantCulture));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private string WriteArrayDumpCsv(IReadOnlyList<ArrayDumpEntry> entries)
    {
        var outputDirectory = GetOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var path = Path.Combine(outputDirectory, $"fc_array_dump_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.csv");
        var builder = new StringBuilder();
        builder.AppendLine("source,array_type,array_name,index,value");

        foreach (var entry in entries
            .OrderBy(entry => entry.ArrayName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Source, StringComparer.Ordinal)
            .ThenBy(entry => entry.Index))
        {
            builder.Append(Escape(entry.Source)).Append(',');
            builder.Append(entry.ArrayType.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Escape(entry.ArrayName)).Append(',');
            builder.Append(entry.Index.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.AppendLine(Escape(entry.Value));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private uint ResolveItemId(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return 0;
        }

        itemIdsByName ??= BuildItemNameIndex();
        return itemIdsByName.GetValueOrDefault(itemName.Trim());
    }

    private Dictionary<string, uint> BuildItemNameIndex()
    {
        var sheet = dataManager.GetExcelSheet<Item>();
        var index = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in sheet)
        {
            var name = item.Name.ToString().Trim();
            if (string.IsNullOrWhiteSpace(name) || index.ContainsKey(name))
            {
                continue;
            }

            index[name] = item.RowId;
        }

        return index;
    }

    private static bool LooksLikeHistoryRow(string text)
    {
        return ContainsDate(text)
            && (ContainsAction(text) || QuantityRegex().IsMatch(text) || text.Contains("x", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsDate(string text)
    {
        return DateRegex().IsMatch(text);
    }

    private static bool ContainsAction(string text)
    {
        return text.Contains("入庫", StringComparison.Ordinal)
            || text.Contains("出庫", StringComparison.Ordinal)
            || text.Contains("入れ", StringComparison.Ordinal)
            || text.Contains("取り出", StringComparison.Ordinal)
            || text.Contains("引き出", StringComparison.Ordinal)
            || text.Contains("預け", StringComparison.Ordinal)
            || text.Contains("deposit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("withdraw", StringComparison.OrdinalIgnoreCase)
            || text.Contains("removed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("entrusted", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectAction(string text)
    {
        if (text.Contains("出庫", StringComparison.Ordinal)
            || text.Contains("取り出", StringComparison.Ordinal)
            || text.Contains("引き出", StringComparison.Ordinal)
            || text.Contains("withdraw", StringComparison.OrdinalIgnoreCase)
            || text.Contains("removed", StringComparison.OrdinalIgnoreCase))
        {
            return "withdraw";
        }

        if (text.Contains("入庫", StringComparison.Ordinal)
            || text.Contains("入れ", StringComparison.Ordinal)
            || text.Contains("預け", StringComparison.Ordinal)
            || text.Contains("deposit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("entrusted", StringComparison.OrdinalIgnoreCase))
        {
            return "deposit";
        }

        return "";
    }

    private static string ActionFromKind(int actionKind)
    {
        return actionKind switch
        {
            9 => "deposit",
            10 => "withdraw",
            1 => "gil_deposit",
            2 => "gil_withdraw",
            _ => "",
        };
    }

    private static bool TryParseTimestamp(string text, DateTimeOffset capturedAt, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var match = DateRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var dateText = match.Value;
        var formats = new[]
        {
            "yyyy/MM/dd HH:mm",
            "yyyy/M/d H:mm",
            "yyyy-MM-dd HH:mm",
            "yyyy-M-d H:mm",
            "MM/dd/yyyy HH:mm",
            "M/d/yyyy H:mm",
            "MM/dd/yyyy h:mm tt",
            "M/d/yyyy h:mm tt",
            "MM/dd HH:mm",
            "M/d H:mm",
        };

        if (DateTime.TryParseExact(dateText, formats, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var local))
        {
            if (local.Year == 1)
            {
                local = new DateTime(capturedAt.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
            }

            timestamp = new DateTimeOffset(local, capturedAt.Offset);
            return true;
        }

        if (DateTime.TryParse(dateText, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out local))
        {
            timestamp = new DateTimeOffset(local, capturedAt.Offset);
            return true;
        }

        return false;
    }

    private static bool TryParseQuantity(string text, out int quantity)
    {
        quantity = 0;
        var match = QuantityRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups["quantity"].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity);
    }

    private static string GuessItemName(string text)
    {
        var cleaned = DateRegex().Replace(text, " ");
        cleaned = QuantityRegex().Replace(cleaned, " ");
        cleaned = ActionWordsRegex().Replace(cleaned, " ");
        cleaned = NoiseRegex().Replace(cleaned, " ");
        cleaned = CleanText(cleaned);

        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "";
        }

        return parts.OrderByDescending(part => part.Length).First();
    }

    private static string CleanDisplayText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsControl(character) || character == '\uFFFD' || IsPrivateUseCharacter(character))
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(character);
        }

        return CleanText(builder.ToString());
    }

    private static int ParseChestTab(string text)
    {
        foreach (var character in text)
        {
            if (character is >= '\uE08F' and <= '\uE098')
            {
                return character - '\uE08F';
            }
        }

        return 0;
    }

    private static string FormatChestTab(int chestTab)
    {
        return chestTab > 0
            ? chestTab.ToString(CultureInfo.InvariantCulture)
            : "";
    }

    private static string CleanItemText(string text)
    {
        var cleaned = CleanDisplayText(text);
        var payloadEnd = cleaned.LastIndexOf('&');
        if (payloadEnd >= 0 && payloadEnd + 1 < cleaned.Length)
        {
            cleaned = cleaned[(payloadEnd + 1)..];
        }

        var parts = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part is not "H" and not "I" and not "%" and not "&")
            .ToList();

        while (parts.Count > 0 && parts[^1] is "H" or "I" or "%" or "&")
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return CleanText(string.Join(' ', parts));
    }

    private static bool IsPrivateUseCharacter(char character)
    {
        return character is >= '\uE000' and <= '\uF8FF';
    }

    private static string CleanText(string text)
    {
        return WhitespaceRegex().Replace(text.Replace('\u00a0', ' '), " ").Trim();
    }

    private static string FormatCsvDateTime(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
    {
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    [GeneratedRegex(@"\b(?:(?:20\d{2})[/-]\d{1,2}[/-]\d{1,2}|\d{1,2}[/-]\d{1,2}(?:[/-](?:20\d{2}))?)\s+\d{1,2}:\d{2}(?:\s*[AP]M)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(?:x|\u00d7|\*)\s*(?<quantity>[0-9][0-9,]*)|(?<quantity>[0-9][0-9,]*)\s*(?:個|こ)", RegexOptions.IgnoreCase)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"入庫|出庫|入れました|取り出しました|取り出し|引き出し|預けました|deposited|withdrew|withdrawn|removed|entrusted", RegexOptions.IgnoreCase)]
    private static partial Regex ActionWordsRegex();

    [GeneratedRegex(@"カンパニーチェスト|フリーカンパニー|Free Company Chest|Company Chest|チェスト|履歴|History", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

public sealed record HistoryExportResult(DateTimeOffset From, DateTimeOffset CapturedAt, int VisibleHistoryRowCount, int ExportedEntryCount, string? SummaryCsvPath, string? DumpCsvPath)
{
    public string? Warning { get; init; }
}

public sealed record HistoryLoadResult(DateTimeOffset From, DateTimeOffset CapturedAt, int VisibleHistoryRowCount, int ExportedEntryCount, IReadOnlyList<HistoryEntry> Entries, IReadOnlyList<HistorySummaryEntry> Summaries, IReadOnlyList<HistoryTabSummary> TabSummaries);

public sealed record DebugDumpResult(string CsvPath, int Count);

public sealed record HistoryEntry(DateTimeOffset Timestamp, int ActionKind, string Action, string PlayerName, int ChestTab, string ChestLocation, string ItemName, uint ItemId, int Quantity, string RawItemText);

public sealed record HistorySummaryEntry(string ItemName, string PlayerName, int Deposited, int Withdrawn, int Net);

public sealed record HistoryTabSummary(int ChestTab, IReadOnlyList<HistorySummaryEntry> Summaries);

internal sealed record TextNodeSnapshot(uint NodeId, float ScreenX, float ScreenY, string Text);

internal sealed record AddonTextDumpEntry(string Source, uint NodeId, int Index, float ScreenX, float ScreenY, string Text);

internal sealed record VisibleAddonSnapshot(string Name, uint Id, short X, short Y, float Width, float Height);

internal sealed record ArrayDumpEntry(string Source, int ArrayType, string ArrayName, int Index, string Value);

internal sealed record ItemKey(uint ItemId, string ItemName);

internal sealed record SummaryKey(string PlayerName, uint ItemId, string ItemName);

internal sealed class SummaryKeyComparer : IEqualityComparer<SummaryKey>
{
    public static readonly SummaryKeyComparer Instance = new();

    public bool Equals(SummaryKey? x, SummaryKey? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return string.Equals(x.PlayerName, y.PlayerName, StringComparison.OrdinalIgnoreCase)
            && ItemKeyComparer.Instance.Equals(new ItemKey(x.ItemId, x.ItemName), new ItemKey(y.ItemId, y.ItemName));
    }

    public int GetHashCode(SummaryKey obj)
    {
        var hash = new HashCode();
        hash.Add(obj.PlayerName, StringComparer.OrdinalIgnoreCase);
        hash.Add(ItemKeyComparer.Instance.GetHashCode(new ItemKey(obj.ItemId, obj.ItemName)));
        return hash.ToHashCode();
    }
}

internal sealed class ItemKeyComparer : IEqualityComparer<ItemKey>
{
    public static readonly ItemKeyComparer Instance = new();

    public bool Equals(ItemKey? x, ItemKey? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        if (x.ItemId != 0 || y.ItemId != 0)
        {
            return x.ItemId == y.ItemId;
        }

        return string.Equals(x.ItemName, y.ItemName, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(ItemKey obj)
    {
        return obj.ItemId != 0
            ? obj.ItemId.GetHashCode()
            : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ItemName);
    }
}
