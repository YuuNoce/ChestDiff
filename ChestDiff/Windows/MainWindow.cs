using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ChestDiff.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private int fromYear;
    private int fromMonth;
    private int fromDay;
    private int fromHour;
    private int fromMinute;
    private int exportStartMode;
    private bool showInWindow;
    private bool exportCsv;
    private bool exportDumpFile;
    private string outputDirectory = "";
    private string statusText = "";
    private string settingsStatusText = "";
    private HistoryLoadResult? lastHistory;

    public MainWindow(Plugin plugin)
        : base("ChestDiff###ChestDiffMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 220),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        fromYear = plugin.Configuration.FromYear;
        fromMonth = plugin.Configuration.FromMonth;
        fromDay = plugin.Configuration.FromDay;
        fromHour = plugin.Configuration.FromHour;
        fromMinute = plugin.Configuration.FromMinute;
        exportStartMode = plugin.Configuration.ExportStartMode;
        showInWindow = plugin.Configuration.ShowInWindow;
        exportCsv = plugin.Configuration.ExportCsv;
        exportDumpFile = plugin.Configuration.ExportDumpFile;
        outputDirectory = string.IsNullOrWhiteSpace(plugin.Configuration.OutputDirectory)
            ? plugin.HistoryService.DefaultOutputDirectory
            : plugin.Configuration.OutputDirectory;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var historyLogOpen = plugin.HistoryService.IsHistoryLogOpen();
        var hasLastExport = plugin.Configuration.TryGetLastExportTime(out var lastExportAt);

        ImGui.Text("CSV start date");
        if (ImGui.RadioButton("Since last export", exportStartMode == Configuration.StartModeLastExport))
        {
            exportStartMode = Configuration.StartModeLastExport;
        }

        ImGui.SameLine();
        ImGui.TextDisabled(hasLastExport ? lastExportAt.ToString("yyyy-MM-dd HH:mm") : "(no previous export)");

        if (ImGui.RadioButton("Custom date/time", exportStartMode == Configuration.StartModeCustomDateTime))
        {
            exportStartMode = Configuration.StartModeCustomDateTime;
        }

        ImGui.BeginDisabled(exportStartMode != Configuration.StartModeCustomDateTime);
        DrawDateTimeInputs();
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Text("Actions");
        ImGui.Checkbox("Show in window", ref showInWindow);
        ImGui.SameLine();
        ImGui.Checkbox("Export CSV", ref exportCsv);
        ImGui.Checkbox("Export dump file", ref exportDumpFile);
        ImGui.SameLine();
        ImGui.TextDisabled("(optional)");

        var exportsFile = exportCsv || exportDumpFile;
        var hasAction = showInWindow || exportsFile;
        var canRun = historyLogOpen
            && hasAction
            && (exportStartMode != Configuration.StartModeLastExport || hasLastExport);

        ImGui.Spacing();
        ImGui.Text("Output directory");
        ImGui.BeginDisabled(!exportsFile);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##ChestDiffOutputDirectory", ref outputDirectory, 500);

        if (ImGui.Button("Use default"))
        {
            outputDirectory = plugin.HistoryService.DefaultOutputDirectory;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Copy path"))
        {
            CopyOutputPath();
        }

        ImGui.SameLine();
        if (ImGui.Button("Save settings"))
        {
            SaveSettings();
            settingsStatusText = "Settings saved.";
        }
        ImGui.SameLine();
        ImGui.TextDisabled(settingsStatusText);

        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.86f, 0.25f, 1.0f), "! Open the Free Company Chest Log before exporting.");
        ImGui.TextDisabled(historyLogOpen ? "Status: FreeCompanyChestLog is open." : "Status: FreeCompanyChestLog is not open.");
        ImGui.Spacing();
        ImGui.BeginDisabled(!canRun);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.45f, 0.85f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.55f, 1.0f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.08f, 0.35f, 0.70f, 1.0f));
        if (ImGui.Button("Show / Export", new Vector2(220, 34)))
        {
            SaveSettings();
            RunShowExport();
        }

        ImGui.PopStyleColor(3);
        ImGui.EndDisabled();

        if (!historyLogOpen)
        {
            ImGui.TextDisabled("Export is disabled until the Free Company Chest Log is open.");
        }
        else if (exportStartMode == Configuration.StartModeLastExport && !hasLastExport)
        {
            ImGui.TextDisabled("Select Custom date/time for the first export.");
        }
        else if (!hasAction)
        {
            ImGui.TextDisabled("Select Show in window, Export CSV, Export dump file, or a combination.");
        }

        ImGui.Spacing();
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(outputDirectory)
            ? $"Output: {plugin.HistoryService.DefaultOutputDirectory}"
            : $"Output: {outputDirectory.Trim()}");

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(statusText);
        }

        if (lastHistory is not null)
        {
            DrawHistoryPreview(lastHistory);
        }
    }

    public void SetLastResult(HistoryExportResult result)
    {
        SetLastResult(result, showPreview: false);
    }

    private void SetLastResult(HistoryExportResult result, bool showPreview)
    {
        LoadFromConfig();
        statusText = FormatExportStatus(result, showPreview);
    }

    public void SetStatus(string status)
    {
        statusText = status;
    }

    private void SaveSettings()
    {
        plugin.Configuration.FromYear = fromYear;
        plugin.Configuration.FromMonth = fromMonth;
        plugin.Configuration.FromDay = fromDay;
        plugin.Configuration.FromHour = fromHour;
        plugin.Configuration.FromMinute = fromMinute;
        plugin.Configuration.ExportStartMode = exportStartMode;
        plugin.Configuration.ShowInWindow = showInWindow;
        plugin.Configuration.ExportCsv = exportCsv;
        plugin.Configuration.ExportDumpFile = exportDumpFile;
        plugin.Configuration.OutputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? "" : outputDirectory.Trim();
        plugin.Configuration.Save();
        LoadFromConfig();
    }

    private void CopyOutputPath()
    {
        var path = string.IsNullOrWhiteSpace(outputDirectory)
            ? plugin.HistoryService.DefaultOutputDirectory
            : outputDirectory.Trim();

        ImGui.SetClipboardText(path);
        settingsStatusText = "Path copied.";
    }

    private void DrawDateTimeInputs()
    {
        ImGui.SetNextItemWidth(72);
        ImGui.InputInt("##ChestDiffFromYear", ref fromYear, 0);
        ImGui.SameLine();
        ImGui.Text("/");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(46);
        ImGui.InputInt("##ChestDiffFromMonth", ref fromMonth, 0);
        ImGui.SameLine();
        ImGui.Text("/");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(46);
        ImGui.InputInt("##ChestDiffFromDay", ref fromDay, 0);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(46);
        ImGui.InputInt("##ChestDiffFromHour", ref fromHour, 0);
        ImGui.SameLine();
        ImGui.Text(":");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(46);
        ImGui.InputInt("##ChestDiffFromMinute", ref fromMinute, 0);
        ImGui.SameLine();
        ImGui.TextDisabled("YYYY/MM/DD HH:mm");
    }

    private void RunShowExport()
    {
        try
        {
            settingsStatusText = "";
            var history = plugin.LoadSinceConfiguredTime();
            HistoryExportResult? exportResult = null;

            if (exportCsv || exportDumpFile)
            {
                exportResult = plugin.ExportHistory(history, exportCsv, exportDumpFile);
            }

            lastHistory = showInWindow ? history : null;

            if (exportResult is not null)
            {
                SetLastResult(exportResult, showInWindow);
            }
            else
            {
                LoadFromConfig();
                statusText = "";
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "FC chest history export failed.");
            statusText = $"Show/export failed: {ex.Message}";
            Plugin.ChatGui.PrintError(statusText, "ChestDiff");
        }
    }

    private static void DrawHistoryPreview(HistoryLoadResult history)
    {
        ImGui.Separator();
        ImGui.Text($"Preview: {history.ExportedEntryCount}/{history.VisibleHistoryRowCount} rows since {history.From:yyyy-MM-dd HH:mm}");

        if (!ImGui.BeginTabBar("ChestDiffSummaryTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("All"))
        {
            DrawSummaryTable(history.Summaries);
            ImGui.EndTabItem();
        }

        foreach (var tabSummary in history.TabSummaries)
        {
            if (ImGui.BeginTabItem($"Tab {tabSummary.ChestTab}"))
            {
                DrawSummaryTable(tabSummary.Summaries);
                ImGui.EndTabItem();
            }
        }

        ImGui.EndTabBar();
    }

    private static void DrawSummaryTable(IReadOnlyList<HistorySummaryEntry> summaries)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("ChestDiffSummaryTable", 5, flags, new Vector2(0, 220)))
        {
            return;
        }

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Player");
        ImGui.TableSetupColumn("Deposit");
        ImGui.TableSetupColumn("Withdraw");
        ImGui.TableSetupColumn("Net");
        ImGui.TableHeadersRow();

        if (summaries.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled("No summary rows.");
            ImGui.EndTable();
            return;
        }

        foreach (var summary in summaries)
        {
            ImGui.TableNextRow();
            TableText(summary.ItemName);
            TableText(summary.PlayerName);
            TableText(summary.Deposited.ToString(CultureInfo.InvariantCulture));
            TableText(summary.Withdrawn.ToString(CultureInfo.InvariantCulture));
            TableText(summary.Net.ToString(CultureInfo.InvariantCulture));
        }

        ImGui.EndTable();
    }

    private static void TableText(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private static string FormatExportStatus(HistoryExportResult result, bool showPreview)
    {
        var lines = new List<string>();

        if (!showPreview)
        {
            lines.Add($"Exported {result.ExportedEntryCount}/{result.VisibleHistoryRowCount} rows since {result.From:yyyy-MM-dd HH:mm}.");
        }

        if (result.SummaryCsvPath is not null)
        {
            lines.Add($"Summary: {result.SummaryCsvPath}");
        }

        if (result.DumpCsvPath is not null)
        {
            lines.Add($"Dump: {result.DumpCsvPath}");
        }

        if (!string.IsNullOrWhiteSpace(result.Warning))
        {
            lines.Add($"Warning: {result.Warning}");
        }

        return string.Join("\n", lines);
    }
}
