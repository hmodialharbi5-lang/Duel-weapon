# DualWieldTShock

TShock 6.1.0 starter project for a server-side simulated dual-wield plugin.

## Commands

- Hold the weapon you want to use as the second weapon, then `/dual on`.
- `/dualoff` - disable dual wield.
- `/dualinfo` - show the saved secondary weapon.

Permission: `dualwield.use`

## Important

This version saves the **currently held item** when you use `/dual on`. It is a **build-ready starter source project**, not a finished projectile-spawning implementation. Terraria's projectile/use-item internals are version-sensitive, so the exact TShock 6.1.0 server DLLs must be used when completing/building the attack hook.

The project targets .NET 9 because TShock 6.1.0 is based on the modern .NET runtime. See the official TShock release information before compiling.

## Build

1. Put the exact TShock 6.1.0 reference files in `references/`:
   - `TShockAPI.dll`
   - `TerrariaApi.Server.dll`
   - `TerrariaServer.exe`
2. Install the .NET 9 SDK.
3. Run:
   `dotnet build -c Release`
4. Copy `bin/Release/net9.0/DualWieldTShock.dll` to the server's `ServerPlugins` folder.


## About the dual attack

The saved secondary weapon is tracked per player, but this source intentionally does **not**
fake a projectile by guessing Terraria internals. A real secondary attack must call the exact
`ItemCheck`/projectile-use path exposed by the TerrariaServer assembly used by the target
TShock 6.1.0 build. The included project therefore compiles as a safe starter but needs those
exact references and API signatures to finish the attack hook.
