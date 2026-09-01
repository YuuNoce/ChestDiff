# ChestDiff

ChestDiff is a minimal Dalamud plugin that reads the visible Free Company chest history screen, filters rows after a configured date/time, and shows or exports them.

## Commands

```text
/chestdiff
/chestdiff export
/chestdiff dump
/chestdiff help
```

## Advanced Commands

```text
/chestdiff addons
/chestdiff arrays
```

## Notes

- Open the FC chest history screen in game before exporting.
- The date/time is selected manually in the plugin UI.
- The plugin UI can show the summary in the window, export the summary CSV, export a dump file, or combine those actions.
- The detail CSV keeps `raw_text` because the in-game history text is the primary source.
- The summary CSV aggregates deposited, withdrawn, and net quantity by item.
- `item_id` is resolved by matching the parsed item name against the Item sheet when possible.
- CSV files are stored under the plugin config directory's `exports` folder by default.

## Building

```text
dotnet build ChestDiff.slnx
```
