# Two Point Museum VR

A VR mod for Two Point Museum: your whole museum on a table in front of you,
like a model you can pick up, move, scale and turn. Point the laser at the
game's menus to click, and pick exhibits up and put them down by hand.

Part of [Diorama VR](https://dioramavr.app). Install it with the Diorama VR
installer; it sets everything up and puts the game back to normal when you
uninstall. You need a Windows PC, a SteamVR headset and the game on Steam.

## This repository

Each release's source, published by the release workflow, and the files the
installer and the site read:

- `data/index.json`: every release (version, maturity, tested game builds,
  download, SHA-256) and the known issues.
- `data/game.json`: the game's description, controls and tutorial.
- `src/`: the plugin (BepInEx 6, IL2CPP). It builds against the Diorama VR
  Unity core at the version pinned in `Directory.Build.props`
  (`DvrCoreVersion`) and needs your own copy of the game's DLLs; the release
  zips are the supported way to install it.

Not affiliated with Two Point Studios or SEGA. Two Point Museum is theirs.

## Licence

MIT, see `LICENSE`.
