# Void Crew Fleet

A BepInEx mod for **Void Crew** that puts **up to three player ships** in one lobby, or **two teams of up to three
ships** in PvP, for up to **24 players** (8 per ship). Fly the game's own missions as a fleet, take on the **Fleet
Pilgrimage** (more enemies, more objectives), or fight each other in **Fleet Battle**, a team-versus-team PvP mode.

> Everyone in a Fleet room needs the mod. Rooms you host with it are marked as modded `[mod_session]`, as Void Crew's
> modding guidelines ask. With the mod installed you can still join normal rooms.

## Features

### Up to three ships, 24 players
- Rooms hold up to 24 players (the game's own limit is 6), 8 per ship, each ship with its own clone tubes.
- The hub's **ship loadout terminal** gets a **FLEET** panel:
  - **SHIPS: 1 / 2 / 3** (the host's) sets how many ships the game's own missions use (one in a new room; your last
    choice is remembered). With more than 8 players a mission adds ships by itself and evens out the crews. A saved run
    always has one ship.
  - A row for every ship: click one to set it up at the terminal, with its own ship and loadout.
- **The lobby screen** opens for everyone when the host sets two or three ships: **players pick the ship they fly** for
  the whole mission. Crews stay even, and the host can move players and auto-balance. The host then confirms the crews
  (CONFIRM CREWS), and only then can the mission start. **CREWS** on the FLEET panel brings the screen back, and once
  the crews are confirmed it lets the host reopen picking. A player who joins after that goes to the smallest crew.
- On the lobby screen the host can **force each ship's type** (Destroyer, Frigate or Striker) or leave it free. A ship
  with no loadout chosen copies ship 1 (AS SHIP 1) when its type allows it, or else gets its type's standard loadout.
- From the moment you have a ship in the lobby until the mission ends, **your ship** shows as one more row of the
  ship panel in the top left corner, in the HUD's own style: SHIP 2 with its type, or LOST once it is destroyed.
- The ships start side by side, fly freely, and **void-jump together**:
  - the escape vector set on any ship's astral map counts for every ship;
  - each crew charges its own ship's void drive;
  - once **every crewed ship's drive is charged**, pulling any ship's void lever jumps the whole fleet, whoever flies
    it. While a drive isn't charged, the lever says which ship's and nothing starts. A ship nobody crews is carried
    along;
  - the ships arrive side by side.
- In co-op, enemies and the sector's events go for the nearest ship, and every ship is kept inside the play area.
- **Each ship is its own ship.** It has its own:
  - fabricator alloys and biomass;
  - ship voice lines, alarms and sound effects;
  - helm, pilot HUD and third-person camera;
  - target locks and scan reticle;
  - damage markers and pings;
  - power and lights.

  Doing something on one ship doesn't set anything off on the others. In co-op, pings and damage markers are shared
  with every crew.
- **Starting alloys:** with a loadout of its own, a ship starts with its own alloys: its CUSTOM loadout's (Ship
  Customizer), otherwise the game's starting amount. As a copy of ship 1 (AS SHIP 1), it starts with ship 1's.

### Losing a ship (co-op and Fleet Battle)
- **You always respawn on your own ship**, the one you picked in the lobby, wherever you died.
- **When a ship is destroyed, its crew can't be re-cloned for the rest of the mission.** Anyone aboard dies with it. A
  crew member who was elsewhere (aboard another ship) plays on until they die.
- Then they **watch** from a third-person view around the remaining ships: **Q / E** (or the mouse buttons) switch
  ships, the mouse looks around, the wheel zooms.
- The mission carries on with the ships that are left, and the **host decides**: carry on, or **RETURN TO HUB** in the
  escape menu (there once a ship is lost) ends the mission for everyone, to start again. A co-op mission fails when
  every ship is lost; Fleet Battle ends when a team has none left.

### Missions (galaxy map)
- **The game's own missions** can be flown with up to three ships.
- **FLEET PILGRIMAGE**: the Pilgrimage for two or three ships. Enemies come in greater numbers (more with a third ship)
  and every sector holds more objectives, so the crews can split up and work on different objectives at the same time.
- **FLEET BATTLE (PvP)**: two teams of one to three ships each, starting on opposite sides of a large asteroid field
  with salvage to fight over.

### Fleet Battle (PvP)
- **Teams in the lobby:**
  1. When the host picks FLEET BATTLE on the galaxy map, the lobby screen opens for everyone.
     - The host sets **SHIPS PER TEAM** (1 to 3) and **each ship's type**. By default both teams get the same ships
       (BOTH TEAMS: SAME SHIPS).
     - Players **pick a ship**, and with it their team: BLUE has ships 1, 3 and 5, RED has 2, 4 and 6.
     - Teams and crews stay even (an odd count allows one extra), and every ship needs a crew. The host can move
       players, auto-balance and confirms.
  2. Everyone on a team wears a **BLUE TEAM / RED TEAM** badge above their head, and sees their own team and ship
     as a row of the ship panel in the top left corner (BLUE TEAM  BLUE 1, DESTROYER).
  3. At the loadout terminal the FLEET panel lists every ship in team colours. Only a ship's own team can change its
     loadout, Ship Customizer's CUSTOM included. A ship nobody set up starts with its type's standard loadout.
  4. The host starts the match by **sitting in a ready-room seat**, and everyone spawns aboard their ship.

  On the lobby screen:
  - HIDE puts it away for you; TEAMS & RULES on the FLEET panel brings it back.
  - The host's CHANGE MISSION backs out of FLEET BATTLE.
  - LEAVE asks to be pressed again.

  After the teams are confirmed:
  - TEAMS & RULES lets the host reopen team picking (the current teams are kept), and shows the teams and rules to
    everyone else.
  - A player who joins later goes to the smaller team's smallest crew.
- **Ship rules** (with Ship Customizer 0.7.0 or newer): the lobby screen's SHIP RULES tab is where the host decides
  what a CUSTOM loadout may use in the match:
  - the allowed MK tiers (the fabricator may start at the highest one);
  - the allowed categories;
  - single options to ban;
  - a starting-alloys limit.

  Everyone sees the rules. The Customizer greys out what they refuse at the loadout terminal, and the host's game
  removes it from the ships when the match starts. The game's own loadouts are not affected.
- **The arena:**
  - one of the game's asteroid fields, with rocks between the ships to take cover behind;
  - mirrored salvage on each side: supply and generic containers near home, a wreck to search on the flank, and an
    asteroid base, ammo container and VoidFish further out;
  - an alloy supply drop every 5 minutes, the same distance from both ships;
  - no enemy NPCs.
- **Enemies look like enemies**: enemy ship markers (your team's other ships are marked as allies), team-coloured name
  tags (enemies only in sight and within 35 m), and no enemy pings or damage markers.
- **The teams' ships can damage each other** (a team's own ships can't). Every hull has **4x the game's hit points**
  (the host's setting).
- **Destroy every enemy ship to win.** A team with no players left forfeits.
- **Spectators:** while your team has ships left, losing yours doesn't end your match: you watch the rest of it (see
  Losing a ship). Spectators talk among themselves: nobody still fighting hears them, by voice or text chat.
- **Joining a match under way:** while there is room, you play at once: you join the team with fewer active players,
  on its ship with the smallest crew (8 at most), so the teams stay at most one apart. Players reconnecting within their
  3 minutes count as active; the crews of lost ships don't. Players already waiting for a place go first.
- **Lost connection:** your place is held for **3 minutes**. Come back in time and you return to your team and ship.
  Come back later and you play again on your team when it isn't ahead, else you wait for its next free place, watching
  your team's ships.
- **Boarding:**
  - Enemy players can come aboard through an airlock its crew left open, or one their team hacked (below).
  - Aboard the enemy ship you can always use its **System Enhancement panels** and its systems' **power switches**
    (except the central computer's, which is the whole ship's power). Finishing a panel's minigame shuts that system
    down for **120 seconds**, and a power switch turns a turret, shield or any other system off.
  - You can never use its helm, turrets, seats or terminals, enter its **cockpit**, or take its items or its
    **homunculus**, not even with the superior grappling hook. A warning tells you why something is off-limits.
- **Fist fighting aboard:** with empty hands, **hold the right mouse button** to raise your fists into a fighting
  stance, then **left click to punch**, alternating arms (left first). On a gamepad: hold LT, punch with RT (on foot in
  gravity). Five punches knock an enemy out, and you aim where you look, VR included. Everyone around sees your stance
  and punches.
- **Turrets:** B.R.A.I.N. auto-turrets track the enemy ship properly:
  - they aim at its hull and lead its real movement;
  - they hold fire when it's out of their arc.

  Point defenses and auto-turrets also shoot enemy players floating in open space near the ship, but a visible enemy
  ship always comes first. An auto-turret's shots hit a player by chance, more often up close.
- **Proximity voice** is forced on. You hear players on the same ship, or within 50 m in open space.
- **Airlock Hacking Terminal:** a new fabricator item on the Resources tab, made from the Data Shard's model.
  1. Print it and carry it to the **control panel outside an enemy airlock**.
  2. **Hold Fire** on the panel to plug it in. The hack takes **1 minute**, and the defenders can **pull it out** (hold
     Fire on it for 3 seconds) until then.
  3. When it finishes, your whole team can **use every door of that ship for 5 minutes**, including the airlock's door
     buttons and pressurise lever, but not the cockpit doors or the airlock's safety override. A countdown shows on
     screen for both teams.

## Installation
1. Install **BepInEx 5** for Void Crew, with a Thunderstore mod manager (BepInExPack) or by hand.
2. Put `VoidCrewFleet.dll` in `BepInEx\plugins`. With a mod manager, install the zip from the release.
3. Everyone in your Fleet rooms needs a compatible version. If not, the game sends a player back with its "missing
   mods" message, which names the version the room needs.

## Settings (`BepInEx\config\com.captainchunck.voidcrew.fleet.cfg`)

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | Enabled | true | Turn the mod off without removing it. |
| Lobby | MaxPlayers | 24 | Room size for rooms you host (4–24). |
| Lobby | Ships | 1 | How many ships rooms you host start with (1–3; follows the FLEET panel's SHIPS). |
| Two ships | ShipSpacing | 160 | Distance between ships side by side at the start and after a jump (m). |
| Fleet Pilgrimage | EnemyMultiplier | 1.6 | How many more enemies the Fleet Pilgrimage sends with two ships (host); a third ship adds as much again. A change applies from the next mission. |
| PvP | StartDistance | 3600 | How far apart the two teams start (m). |
| PvP | HullMultiplier | 4 | How many times the game's hull hit points the ships have in Fleet Battle (host). |
| PvP | ShipsPerTeam | 1 | Your last ships per team; set it on the lobby screen. |
| PvP | ShipTypes | (empty) | Your last Fleet Battle ship types; set them on the lobby screen. |
| PvP | Arena | auto | Where Fleet Battle is fought: auto, Dz-A, Or-A, Oz-B, Defiance or off (host). |
| PvP | HackTerminalCost | 10 | The hacking terminal's fabricator price in alloys (host). |
| PvP | ShipRules | (empty) | Your last Fleet Battle ship rules; set them on the lobby screen, not here. |
| Diagnostics | VerboseLogging | false | More detail in the BepInEx log. |
| Diagnostics | AutoTest | Off | Developer self-test; leave it Off to play. |

## Progression
Fleet missions, PvP and rooms of more than 6 players **give no XP, rewards or achievements**, as Void Crew's modding
rules ask for mods that change the game:
- Daily and weekly quests aren't used up there; they stay open for a normal mission. Codex entries from data
  shards aren't unlocked there either.
- A mission that starts like that stays so even if players leave.
- Fleet runs aren't saved, because the game's save holds only one ship.

## Compatibility
- **[Ship Customizer](https://github.com/CaptainChunck/VoidCrewShipCustomizer)** (optional):
  - Ship 1 can use its CUSTOM loadout.
  - With Ship Customizer 0.4.0 or newer, ship 2 can have a CUSTOM loadout of its own. Choose SHIP 2 in the FLEET
    panel, then CUSTOM.
  - With Ship Customizer 0.5.1 or newer, ship 2's CUSTOM loadout has its own starting alloys (Fleet 0.2.1 or newer).
  - With Ship Customizer 0.7.0 or newer, CUSTOM loadouts follow Fleet Battle's ship rules (Fleet 0.3.0 or newer).
  - With Ship Customizer 0.9.0 or newer, every ship 2-6 can have a CUSTOM loadout of its own (Fleet 0.4.0 or newer).
    With an older Customizer, ships 3-6 use game loadouts.
  - A Ship Customizer turned off in its own settings counts as not installed.
- **Void Crew VR UI Fix** (optional): the mod's screens use the game's own UI system, so they show in VR.
- Neither is required.

## Status
This is an early release. A host alone has tested:
- co-op with two and three ships: picking crews in the lobby, respawning on your own ship, losing ships, watching
  after your ship is lost, and RETURN TO HUB;
- the Fleet Pilgrimage and the fleet's void jump;
- Ship Customizer's CUSTOM ship 2;
- Fleet Battle 1 v 1 and 2 v 2: lobby teams, ship rules, 4x hulls, factions, a team fighting on after losing a ship,
  spectators and the result;
- the fist-fighting stance and punches.

Not played yet with several real players:
- punches landing on another player;
- turrets shooting players;
- boarding;
- panel sabotage;
- the hacking terminal (plugging in, the hack, pulling it out, door access);
- proximity voice;
- reconnects, and rooms of more than a few players;
- CUSTOM loadouts on ships 3-6 (Ship Customizer 0.9.0) in a mission.

Please report anything odd with your `BepInEx\LogOutput.log`.

## Building from source
- **Requirements:** Visual Studio 2022+ (MSBuild), and Void Crew with BepInEx 5 installed.
- **Build and check:** `powershell -ExecutionPolicy Bypass -File tests\build-and-test.ps1 -VoidCrewDir "<game folder>" -BepInExDir "<BepInEx folder>"`
  builds the plugin and checks every game patch against the installed game's assemblies offline.
- **Package:** `powershell -ExecutionPolicy Bypass -File tools\package.ps1` makes the Thunderstore zip in `dist\`.

## License
GPL-3.0. See [LICENSE](LICENSE).
