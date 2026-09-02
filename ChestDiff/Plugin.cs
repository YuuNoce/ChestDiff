using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ChestDiff.Windows;

namespace ChestDiff;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/chestdiff";
    private const string ChatTag = "ChestDiff";
    private const ushort ChatTagColor = 43;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    public ChestHistoryService HistoryService { get; }
    public WindowSystem WindowSystem { get; } = new("ChestDiff");

    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Normalize();
        HistoryService = new ChestHistoryService(Configuration, GameGui, DataManager);
        mainWindow = new MainWindow(this);

        WindowSystem.AddWindow(mainWindow);
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open ChestDiff. Use /chestdiff export to export visible FC chest history since the configured time.",
        });

        Log.Information($"{PluginInterface.Manifest.Name} loaded.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
    }

    public void ToggleMainUi()
    {
        mainWindow.Toggle();
    }

    public HistoryExportResult ExportSinceConfiguredTime()
    {
        Configuration.Save();
        var from = Configuration.GetExportStartDateTime();
        ThrowIfExportStartTimeIsInFuture(from);
        var history = HistoryService.LoadSince(from);
        var result = ExportHistory(history);
        return result;
    }

    public HistoryLoadResult LoadSinceConfiguredTime()
    {
        Configuration.Save();
        var from = Configuration.GetExportStartDateTime();
        ThrowIfExportStartTimeIsInFuture(from);
        return HistoryService.LoadSince(from);
    }

    public HistoryExportResult ExportHistory(HistoryLoadResult history)
    {
        var result = HistoryService.Export(history, exportSummaryCsv: true, exportDumpFile: false);
        return SaveLastExportTime(result);
    }

    public HistoryExportResult ExportHistory(HistoryLoadResult history, bool exportSummaryCsv, bool exportDumpFile)
    {
        var result = HistoryService.Export(history, exportSummaryCsv, exportDumpFile);
        return SaveLastExportTime(result);
    }

    private HistoryExportResult SaveLastExportTime(HistoryExportResult result)
    {
        try
        {
            Configuration.SetLastExportTime(result.CapturedAt);
            Configuration.Save();
            return result;
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "Failed to save last export time after FC chest history export.");
            return result with
            {
                Warning = $"Export completed, but failed to save last export time: {ex.Message}",
            };
        }
    }

    private static void ThrowIfExportStartTimeIsInFuture(System.DateTimeOffset from)
    {
        var now = System.DateTimeOffset.Now;
        if (from <= now)
        {
            return;
        }

        throw new System.InvalidOperationException($"Start date cannot be in the future. Selected: {from:yyyy-MM-dd HH:mm}; now: {now:yyyy-MM-dd HH:mm}.");
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args))
        {
            ToggleMainUi();
            return;
        }

        if (args.Equals("help", System.StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            return;
        }

        if (args.Equals("export", System.StringComparison.OrdinalIgnoreCase))
        {
            HandleExportCommand();
            return;
        }

        if (args.Equals("dump", System.StringComparison.OrdinalIgnoreCase))
        {
            HandleDumpCommand();
            return;
        }

        if (HandleDiagnosticCommand(args))
        {
            return;
        }

        ChatGui.PrintError("Usage: /chestdiff | /chestdiff export | /chestdiff dump | /chestdiff help", ChatTag);
    }

    private void HandleExportCommand()
    {
        try
        {
            var result = ExportSinceConfiguredTime();
            PrintExportResult(result);
            mainWindow.SetLastResult(result);
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "FC chest history export failed.");
            ChatGui.PrintError($"Export failed: {ex.Message}", ChatTag);
            mainWindow.SetStatus($"Export failed: {ex.Message}");
        }
    }

    private static void PrintExportResult(HistoryExportResult result)
    {
        var paths = result.SummaryCsvPath is not null
            ? $" Summary: {result.SummaryCsvPath}"
            : "";
        paths += result.DumpCsvPath is not null
            ? $" Dump: {result.DumpCsvPath}"
            : "";
        ChatGui.Print($"Exported {result.ExportedEntryCount}/{result.VisibleHistoryRowCount} visible FC chest history rows since {result.From:yyyy-MM-dd HH:mm}.{paths}", ChatTag, ChatTagColor);

        if (!string.IsNullOrWhiteSpace(result.Warning))
        {
            ChatGui.PrintError(result.Warning, ChatTag);
        }
    }

    private void HandleDumpCommand()
    {
        try
        {
            var result = HistoryService.DumpCandidateAddonText();
            ChatGui.Print($"Dumped {result.Count} text nodes. CSV: {result.CsvPath}", ChatTag, ChatTagColor);
            mainWindow.SetStatus($"Dumped {result.Count} text nodes.\nCSV: {result.CsvPath}");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "FC chest text dump failed.");
            ChatGui.PrintError($"Dump failed: {ex.Message}", ChatTag);
            mainWindow.SetStatus($"Dump failed: {ex.Message}");
        }
    }

    // Hidden diagnostic commands kept for addon/array investigation.
    private bool HandleDiagnosticCommand(string args)
    {
        if (args.Equals("addons", System.StringComparison.OrdinalIgnoreCase))
        {
            HandleAddonsCommand();
            return true;
        }

        if (args.Equals("arrays", System.StringComparison.OrdinalIgnoreCase))
        {
            HandleArraysCommand();
            return true;
        }

        return false;
    }

    private void HandleAddonsCommand()
    {
        try
        {
            var result = HistoryService.DumpVisibleAddons();
            ChatGui.Print($"Dumped {result.Count} visible addons. CSV: {result.CsvPath}", ChatTag, ChatTagColor);
            mainWindow.SetStatus($"Dumped {result.Count} visible addons.\nCSV: {result.CsvPath}");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Visible addon dump failed.");
            ChatGui.PrintError($"Addon dump failed: {ex.Message}", ChatTag);
            mainWindow.SetStatus($"Addon dump failed: {ex.Message}");
        }
    }

    private void HandleArraysCommand()
    {
        try
        {
            var result = HistoryService.DumpFreeCompanyArrays();
            ChatGui.Print($"Dumped {result.Count} FreeCompany array entries. CSV: {result.CsvPath}", ChatTag, ChatTagColor);
            mainWindow.SetStatus($"Dumped {result.Count} FreeCompany array entries.\nCSV: {result.CsvPath}");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "FreeCompany array dump failed.");
            ChatGui.PrintError($"Array dump failed: {ex.Message}", ChatTag);
            mainWindow.SetStatus($"Array dump failed: {ex.Message}");
        }
    }

    private static void PrintHelp()
    {
        ChatGui.Print("ChestDiff commands:", ChatTag, ChatTagColor);
        ChatGui.Print("/chestdiff - Open ChestDiff.", ChatTag, ChatTagColor);
        ChatGui.Print("/chestdiff export - Export summary CSV using the configured range start.", ChatTag, ChatTagColor);
        ChatGui.Print("/chestdiff dump - Export raw FC chest history text to CSV.", ChatTag, ChatTagColor);
        ChatGui.Print("/chestdiff help - Show this help.", ChatTag, ChatTagColor);
    }
}
