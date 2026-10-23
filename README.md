This plugin is designed to work with the Exile API in the game Path of Exile. It has been tested and confirmed working on patch 3.29.

Important Notice: This plugin is completely free! There are no payments required or accepted for using this plugin. If you "purchased" it, you have been scammed.

## Requirements

- Prices come from the **Ninja Price** plugin (`NinjaPrice.GetBaseItemTypeValue` bridge). Without it, tracking works and prices show as unavailable.

## Development

```
dotnet build BetterEssenceCorruptionHelper.csproj -p:CI_LIBS_PATH=<ExileApi-Compiled folder>
dotnet run --project tests/BetterEssenceCorruptionHelper.Tests -p:CI_LIBS_PATH=<ExileApi-Compiled folder>
```

`deploy.ps1` builds and copies the plugin into the newest local HUD (`-Source` for a source deploy, `-DryRun` to preview).
