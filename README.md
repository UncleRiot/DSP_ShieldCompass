# DSP Shield Compass v0.1.1

Source project for Dyson Sphere Program (BepInEx 5, .NET Framework 4.7.2).

## Features

- On/off button in the game and F8 shortcut.
- Movable button and compass arrow: right-click and drag, positions stored in the BepInEx configuration.
- Eight target coordinates in this order: North Pole (90 N), South Pole (90 S), and Equator at 0, 60 E, 120 E, 180, 120 W, 60 W.
- On-screen checklist of all eight targets; click each row to toggle its manually completed state.
- Read-only attempt to detect existing planetary shield generators in the game entity pool near the target coordinates.
- Completed targets are skipped; completed/remaining totals shown on the overlay.
- Manual completion flags persist by planet ID in the BepInEx configuration (not in the game save).

## Build

1. Open `DSPShieldCompass.csproj` in Visual Studio.
2. The configured `GameDir` is `D:\Spiele\Steam\steamapps\common\Dyson Sphere Program`. Change it if needed.
3. Install BepInEx 5 for the game. The project references BepInEx and Unity/DSP assemblies directly from this game installation.
4. Build Release. The output is `bin\Release\net472\DSPShieldCompass.dll` (depending on Visual Studio/MSBuild output settings).
5. Copy the compiled DLL to `BepInEx\plugins\DSPShieldCompass\` for normal loading at startup. For ScriptEngine development/reload testing, follow ScriptEngine's supported DLL-loading instructions instead, and do not keep duplicate copies in both locations.

## Notes and limitations

- **Source only**; no compiled game DLL included.
- The source has not been compiled or tested against the user's local game installation in this environment.
- The 8-position geometry is a chosen *equatorial placement layout*, not a verified guarantee of complete planetary damage protection.
- Automatic detection uses vanilla prototype ID 3008 and 6 degrees matching tolerance. The actual prototype ID and coordinate conversion should be checked in-game. Marking a row manually does not verify the presence of an actual shield generator.
- No automatic factory or shield changes are made.
- Runtime reloading via ScriptEngine has not been verified; assess cleanup/reinitialization before relying on reloads.
