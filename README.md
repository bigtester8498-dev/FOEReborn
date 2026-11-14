# FOE Reborn

Launcher and download page of **FOE Reborn**, a fan project based on *Fallout Equestria: Remains*.

**Download:** https://bigtester8498-dev.github.io/FOEReborn/

The launcher (Windows 10/11, one exe, nothing to install):

- installs the game and updates it, downloading only the parts that changed;
- checks and repairs the game files;
- manages mods: enable, disable, load order, a whole group of mods with one box;
- has a mod catalogue: install, update and remove mods, one at a time or a whole group at once;
- updates itself.

Put `FOEReLauncher.exe` into an empty folder of its own and run it. The game is installed beside it.

## Building

```
dotnet build launcher/FOEReLauncher.sln -c Release
dotnet test launcher/FOEReLauncher.sln -c Release
```

The tests need Python on PATH.
