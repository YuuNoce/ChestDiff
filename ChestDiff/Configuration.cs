using System;
using System.Globalization;
using Dalamud.Configuration;

namespace ChestDiff;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int StartModeLastExport = 0;
    public const int StartModeCustomDateTime = 1;

    public int Version { get; set; } = 0;
    public string OutputDirectory { get; set; } = "";
    public string AddonNames { get; set; } = "";
    public bool ShowInWindow { get; set; } = true;
    public bool ExportCsv { get; set; } = true;
    public bool ExportDumpFile { get; set; } = false;
    public int ExportStartMode { get; set; } = StartModeCustomDateTime;
    public string LastExportAt { get; set; } = "";
    public int FromYear { get; set; } = DateTimeOffset.Now.Year;
    public int FromMonth { get; set; } = DateTimeOffset.Now.Month;
    public int FromDay { get; set; } = DateTimeOffset.Now.Day;
    public int FromHour { get; set; } = 0;
    public int FromMinute { get; set; } = 0;

    public DateTimeOffset GetFromDateTime()
    {
        var year = Math.Clamp(FromYear, 2020, 2100);
        var month = Math.Clamp(FromMonth, 1, 12);
        var day = Math.Clamp(FromDay, 1, DateTime.DaysInMonth(year, month));
        var hour = Math.Clamp(FromHour, 0, 23);
        var minute = Math.Clamp(FromMinute, 0, 59);
        return new DateTimeOffset(year, month, day, hour, minute, 0, DateTimeOffset.Now.Offset);
    }

    public bool TryGetLastExportTime(out DateTimeOffset lastExportAt)
    {
        return DateTimeOffset.TryParse(LastExportAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastExportAt);
    }

    public DateTimeOffset GetExportStartDateTime()
    {
        if (ExportStartMode == StartModeLastExport)
        {
            if (TryGetLastExportTime(out var lastExportAt))
            {
                return lastExportAt;
            }

            throw new InvalidOperationException("No previous export time is saved. Select Custom date/time for the first export.");
        }

        return GetFromDateTime();
    }

    public void SetLastExportTime(DateTimeOffset lastExportAt)
    {
        LastExportAt = lastExportAt.ToString("O", CultureInfo.InvariantCulture);
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(AddonNames)
            && IsKnownAddonName(OutputDirectory))
        {
            AddonNames = OutputDirectory.Trim();
            OutputDirectory = "";
        }
    }

    public void Save()
    {
        Normalize();
        var from = GetFromDateTime();
        ExportStartMode = ExportStartMode == StartModeLastExport
            ? StartModeLastExport
            : StartModeCustomDateTime;
        FromYear = from.Year;
        FromMonth = from.Month;
        FromDay = from.Day;
        FromHour = from.Hour;
        FromMinute = from.Minute;
        OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? "" : OutputDirectory.Trim();
        AddonNames = "";
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    private static bool IsKnownAddonName(string value)
    {
        return value.Equals("FreeCompanyChestLog", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FreeCompanyChest", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FreeCompanyChestHistory", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FreeCompanyLog", StringComparison.OrdinalIgnoreCase);
    }
}
