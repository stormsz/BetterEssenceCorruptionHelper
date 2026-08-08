This plugin is designed to work with the Exile API in the game Path of Exile. It has been tested and confirmed working on patch 3.29.

Important Notice: This plugin is completely free! There are no payments required or accepted for using this plugin. If you "purchased" it, you have been scammed.

## Building

The project targets `net10.0-windows`, matching ExileApi 329.x.

It resolves `ExileCore.dll` and `GameOffsets.dll` from `$(DepsDir)`, which defaults to `..\..\..` - i.e. the ExileApi root when the plugin sits in `Plugins\Source\<name>`. Set the `CI_LIBS_PATH` property to point somewhere else:

```
dotnet build -c Release -p:CI_LIBS_PATH="D:\path\to\ExileApi"
```

### Note on symlinked/junctioned plugin folders

If you develop outside the ExileApi tree and junction the folder into `Plugins\Source`, be aware that ExileApi's file watcher watches the **junction path**. NTFS junctions do not propagate change notifications from the target, so edits saved through the real path do not trigger a hot reload - touch the file through the `Plugins\Source\...` path instead, or restart ExileApi.
