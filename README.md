# MCGalaxy Retro Plugins

Four experimental plugins for an MCGalaxy/ClassiCube server:

| Plugin | Purpose |
| --- | --- |
| **DynmapClassic** | Browser map with surface, top-down, cave, border, player, bot, and ambient-mob layers. |
| **IndevWorldGen** | Runs the original February 23, 2010 Indev generator and imports its maps into MCGalaxy. |
| **AmbientMobs** | Peaceful recreations of Classic 0.30 Survival Test mobs, including spawning, wandering, animation, and per-world controls. |
| **HerobrineGhostMCG** | Rare, private, packet-only Herobrine sightings with the `MHF_Herobrine` skin. |

The checked-in DLLs were built against MCGalaxy 1.9-compatible APIs. Rebuild them against your server's `MCGalaxy_.dll` when APIs differ.

## Installation

1. Stop MCGalaxy and back up its `plugins`, `properties`, `extra/bots`, and `levels` directories.
2. Copy each desired plugin DLL from its directory into the server's top-level `plugins` directory.
3. Copy each plugin's support directory as described below.
4. Start MCGalaxy and check the current log for `Plugin ... loaded`.

Do not place the repository itself inside a live server. Generated maps, renders, settings, and player data are intentionally ignored by Git.

## DynmapClassic

Copy:

```text
plugins/DynmapClassic/DynmapClassic.dll -> <server>/plugins/DynmapClassic.dll
plugins/DynmapClassic/web/              -> <server>/plugins/DynmapClassic/web/
```

Supply a Classic-compatible texture atlas as:

```text
<server>/plugins/DynmapClassic/terrain.png
```

Minecraft texture archives are not distributed here. A `terrain.png` can be extracted from a legally obtained ClassiCube `default.zip` or another compatible texture pack.

The default listener is `http://localhost:48123/`. Set `DYNMAPCLASSIC_PREFIX` before starting MCGalaxy to bind another address, for example `http://192.168.1.20:48123/`. Windows may require an HTTP URL ACL and the selected TCP port must be forwarded separately.

Open `http://HOST:PORT/`. Surface is the default view. The sidebar provides surface, top-down, and cave views plus world-border, player, bot, and mob toggles. Renders are cached under `plugins/DynmapClassic/renders` and refreshed during startup or when a level is added.

AmbientMobs is optional. When loaded, Dynmap discovers its public `DynmapJson` method and enables the mob layer automatically.

## IndevWorldGen

Requirements:

- Java on `PATH`
- A legally obtained Minecraft Indev `in-20100223.jar`

Copy:

```text
plugins/IndevWorldGen/IndevWorldGen.dll          -> <server>/plugins/IndevWorldGen.dll
plugins/IndevWorldGen/IndevGeneratorBridge.jar  -> <server>/plugins/IndevWorldGen/IndevGeneratorBridge.jar
in-20100223.jar                                  -> <server>/plugins/IndevWorldGen/in-20100223.jar
```

The Mojang game JAR is required at runtime but is deliberately not included.

Generate a world with:

```text
/IndevGen [name] [type] [shape] [size] [theme] <seed>
```

- Types: `inland` (alias `classic`), `island`, `floating`, `flat`
- Shapes: `square`, `long`, `deep`
- Sizes: `small`, `normal`, `huge`
- Themes: `normal`, `hell`, `paradise`, `woods`

Example:

```text
/IndevGen skylands floating long normal paradise 12345
```

Generation executes Mojang's archived generator bytecode to preserve its RNG and generation order. Diamond ore is mapped to gold ore. Torches, chests, and mob spawners are imported as level-local custom blocks.

## AmbientMobs

Copy `AmbientMobs.dll` to `<server>/plugins/AmbientMobs.dll`.

The plugin creates its settings files automatically:

```text
plugins/AmbientMobs-worlds.properties
plugins/AmbientMobs-hidden-players.txt
```

Commands:

```text
/mobs
/mobs count
/mobs hide
/mobs show
/mobs on
/mobs off
```

`hide` and `show` are per-player rendering preferences. `on` and `off` control spawning in the current world and require the player to own that realm or have sufficient permissions. Mobs are hidden from MCGalaxy's normal bot list.

## HerobrineGhostMCG

Copy `HerobrineGhostMCG.dll` to `<server>/plugins/HerobrineGhostMCG.dll`.

The plugin creates viewer-specific entities and never saves a bot or changes blocks. Every 15 seconds an eligible isolated player has a 2% chance of a sighting, followed by a 90-minute cooldown. Herobrine appears 25–50 blocks away for four seconds and tracks the viewer with his head and body.

The entity has no tab-list entry or visible name and uses:

```text
https://minotar.net/skin/MHF_Herobrine.png
```

Administrators can test it with:

```text
/HerobrineGhost test
```

## Building

On Windows with .NET Framework 4.x and a JDK installed:

```powershell
.\scripts\build.ps1 -MCGalaxyDll 'C:\path\to\MCGalaxy_.dll'
```

The script compiles all C# plugins and the Java bridge in place. It does not download MCGalaxy, Minecraft, textures, or other proprietary assets.

## Notes

- These plugins target ClassiCube clients and MCGalaxy's CPE entity/environment features.
- Test updates against a backup before replacing a production MCGalaxy build.
- `RENDERING-AUDIT.md` records the rendering behavior used by DynmapClassic.
- Third-party names and game assets remain property of their respective owners.
