# ChestDiff

ChestDiff is a minimal Dalamud plugin that reads the visible Free Company chest history screen, filters rows after a configured date/time, and summarizes deposits and withdrawals by item and actor.

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

- Open the Free Company Chest Log in game before showing or exporting.
- The date/time is selected manually in the plugin UI.
- The plugin UI can show the summary in the window, export the summary CSV, export a dump file, or combine those actions.
- The summary view shows All and Tab 1-5 summaries with `Item`, `Actor`, `Deposit`, `Withdraw`, and `Net` columns.
- The summary CSV aggregates deposited, withdrawn, and net quantity by item and actor.
- The optional dump file keeps raw parsed rows for troubleshooting.
- `item_id` is resolved by matching the parsed item name against the Item sheet when possible.
- CSV files are stored under the plugin config directory's `exports` folder by default.

## Building

```text
dotnet build ChestDiff.slnx
```
