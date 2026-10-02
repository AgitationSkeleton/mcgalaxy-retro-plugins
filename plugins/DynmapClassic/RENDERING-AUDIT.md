# DynmapClassic rendering audit

Reference implementation: Dynmap `IsoHDPerspective`, `TexturePackHDShader`,
`CaveHDShader`, the installed Spigot/Poseidon map configurations, and MCGalaxy's
`DefaultSet`, `BlockDefinition`, and `LevelConfig` implementations.

## Projection and visibility

- **Previous surface:** one highest block per X/Z column, projected as hand-drawn
  diamonds. This is not Dynmap rendering and cannot see through glass, water,
  leaves, sprites, or texture alpha.
- **Previous flat:** one highest non-air block per X/Z column. It has the same
  transparency failure.
- **Previous cave:** a top-down column heuristic. Dynmap cave is an isometric
  ray render using `iso_SE_60_lowres` and `CaveHDShader`.
- **Replacement:** all views use an orthographic 3D DDA traversal. Surface uses
  the Dynmap southeast 30-degree basis, cave uses southeast 60-degree, and flat
  uses a vertical basis. Rays enter blocks face-by-face and continue while the
  accumulated alpha remains below Dynmap's completion threshold.

## Materials

- **Previous defaults:** a hand-maintained fallback array disagreed with
  MCGalaxy's `DefaultSet`, causing standard blocks to acquire unrelated textures.
- **Previous faces:** every face used `TopTex`; left/right/front/back/bottom were
  ignored.
- **Previous alpha:** texture alpha and block draw type were ignored.
- **Previous geometry:** custom bounds, slabs, snow, and sprite draw modes were
  treated as full opaque cubes.
- **Replacement:** definitions come from `Level.GetBlockDef`, falling back to
  `DefaultSet.MakeCustomBlock(Block.Convert(id))`. Entered faces select the
  corresponding six-face texture. Texture alpha and draw type control
  front-to-back compositing. Non-full block bounds participate in intersection.
  Sprite blocks sample crossed planes rather than becoming opaque cubes.
- The deployed atlas is sourced from `plugins/DynmapLite/textures.zip`, as
  requested, rather than ClassiCube's current stock `default.zip`.

## Cave shader

- Dynmap hides air, water, logs, leaves, glass, doors, snow and ice for the cave
  shader. A ray becomes eligible only after crossing a non-hidden solid and emits
  when it next enters a hidden/air cell.
- The replacement ports Dynmap's default blue-to-green-to-red height formula and
  its face multipliers: X 224/256, Z 256/256, Y 160/256.

## Lighting and environment

- **Previous backdrop:** hard-coded black/transparent, independent of the world.
- **Replacement:** sky, fog, sunlight and shadow colors are read from each
  level's `LevelConfig`, with ClassiCube defaults when unset. The environment is
  returned in map metadata and drives the viewer backdrop.
- Unloaded maps explicitly load `LevelInfo.GetConfig`; decoding a `.lvl` alone
  does not populate its separate MCGalaxy level-properties file.

## Client presentation

- The current client uses a single Leaflet image overlay rather than Dynmap's
  native tile pyramid. Its pan/zoom behavior is sound, but tile URL layout,
  progressive loading, compass/coordinates, and several stock controls remain
  compatibility work rather than renderer correctness work.
