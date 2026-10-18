# Void Crew Fleet

A BepInEx mod for **Void Crew** that puts **two player ships** in one lobby, for up to **16 players** (8 per ship).
Fly the game's own missions with two ships, take on the **Fleet Pilgrimage** (more enemies, more objectives), or fight
each other in **Fleet Battle**, a crew-versus-crew PvP mode.

> Everyone in a Fleet room needs the mod. Rooms you host with it are marked as modded `[mod_session]`, as Void Crew's
> modding guidelines ask. With the mod installed you can still join normal rooms.

## Features

### Two ships, 16 players
- Rooms hold up to 16 players (the game's own limit is 6). Players are split evenly between the ships, each ship with
  its own clone tubes.
- The hub's **ship loadout terminal** gets a **FLEET** panel. Use it to turn **TWO SHIPS** on, and to choose
  **SHIP 1** and **SHIP 2**, each with its own ship and loadout.
- The two ships start side by side, both fly freely, and they void-jump together.
- **Each ship is its own ship.** It has its own:
  - fabricator alloys and biomass;
  - ship voice lines, alarms and sound effects;
  - helm, pilot HUD and third-person camera;
  - target locks and scan reticle;
  - damage markers and pings;
  - power and lights.

  Doing something on one ship doesn't set anything off on the other. In co-op, pings and damage markers are shared with
  both crews.

### Missions (galaxy map)
- **The game's own missions** can be flown with two ships.
- **FLEET PILGRIMAGE**: the Pilgrimage for two ships. Enemies come in greater numbers and every sector holds more
  objectives, so the two crews can split up and work on different objectives at the same time.
- **FLEET BATTLE (PvP)**: two crews, one ship each, starting on opposite sides of an asteroid field.

### Fleet Battle (PvP)
- **Team screen** before anyone spawns:
  - players join BLUE or RED;
  - teams stay balanced (an odd count allows one extra);
  - the host can move players, auto-balance, and must confirm the teams.
- **Enemies look like enemies**: enemy ship markers, red name tags (only in sight and within 35 m), and no enemy pings
  or damage markers.
- **The ships can damage each other.** Destroy the enemy ship to win, and a team with no players left forfeits.
- **Boarding:**
  - Enemy players can come aboard only through an open airlock.
  - They can't open your airlocks or use anything on your ship, except its **System Enhancement panels**.
  - An enemy who completes a panel's minigame shuts that system down for **120 seconds**.
- **Fighting aboard:** punch enemy players with **V**, or with **Fire** when your hands are empty. Five punches knock
  one out, and it aims where you look, VR included.
- **Turrets vs. players:** point defenses and B.R.A.I.N. auto-turrets shoot enemy players floating in open space near
  the ship.
- **Proximity voice** is forced on. You hear players on the same ship, or within 50 m in open space.
- **Airlock Breaching Charge:** a new fabricator item on the Resources tab, made from the Hull Repair Plate's model.
  1. Print it and carry it to the **outer door of an enemy airlock**.
  2. **Hold Fire** on the door to attach it.
  3. **Hold Fire** on the charge to arm it. It beeps, then blows after 6 seconds.
  4. The blast forces **both airlock doors open** and **vents the enemy ship's air**. The doors stay jammed open for
     30 seconds, then the crew can close them again.

  Until it blows, the defending crew can **pull it off** (hold Fire on it for 3 seconds). Opening the airlock knocks it
  off too.

## Installation
1. Install **BepInEx 5** for Void Crew, with a Thunderstore mod manager (BepInExPack) or by hand.
2. Put `VoidCrewFleet.dll` in `BepInEx\plugins`. With a mod manager, install the zip from the release.
3. Everyone who plays in your Fleet rooms needs the same version.

## Settings (`BepInEx\config\com.captainchunck.voidcrew.fleet.cfg`)

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | Enabled | true | Turn the mod off without removing it. |
| Lobby | MaxPlayers | 16 | Room size for rooms you host (4–16). |
| Two ships | ShipSpacing | 160 | Distance between the two ships at the start and after a jump (m). |
| Fleet Pilgrimage | EnemyMultiplier | 1.6 | How many more enemies the Fleet Pilgrimage sends (host). |
| PvP | StartDistance | 3600 | How far apart the PvP ships start (m). |
| PvP | PunchKey | V | The punch key (Fire with empty hands also punches). |
| PvP | BreachChargeCost | 10 | The breaching charge's fabricator price in alloys (host). |
| Diagnostics | VerboseLogging | false | More detail in the BepInEx log. |
| Diagnostics | AutoTest | Off | Developer self-test; leave it Off to play. |

## Progression
Two-ship missions, PvP and rooms of more than 6 players **give no XP, rewards or achievements**, as Void Crew's modding
rules ask for mods that change the game.

## Compatibility
- **[Ship Customizer](https://github.com/CaptainChunck/VoidCrewShipCustomizer)** (optional):
  - Ship 1 can use its CUSTOM loadout.
  - With Ship Customizer 0.4.0 or newer, ship 2 can have a CUSTOM loadout of its own. Choose SHIP 2 in the FLEET
    panel, then CUSTOM.
- **Void Crew VR UI Fix** (optional): the mod's screens use the game's own UI system, so they show in VR.
- Neither is required.

## Status
This is an early release. A host alone has tested co-op two ships, the Fleet Pilgrimage, Ship Customizer's CUSTOM
ship 2, and the PvP setup: teams, factions, airlock forcing and venting. The PvP features that need two real players
haven't been played yet:
- punching;
- turrets shooting players;
- boarding;
- panel sabotage;
- the breaching charge's attach, arm and defuse;
- proximity voice.

Please report anything odd with your `BepInEx\LogOutput.log`.

## Building from source
- **Requirements:** Visual Studio 2022+ (MSBuild), and Void Crew with BepInEx 5 installed.
- **Build and check:** `powershell -ExecutionPolicy Bypass -File tests\build-and-test.ps1 -VoidCrewDir "<game folder>" -BepInExDir "<BepInEx folder>"`
  builds the plugin and checks every game patch against the installed game's assemblies offline.
- **Package:** `powershell -ExecutionPolicy Bypass -File tools\package.ps1` makes the Thunderstore zip in `dist\`.

## License
GPL-3.0. See [LICENSE](LICENSE).
