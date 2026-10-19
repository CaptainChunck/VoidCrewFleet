# Changelog

## 0.3.0
- **Fleet Battle ship rules.** The lobby screen has a SHIP RULES tab where the host decides what a CUSTOM loadout
  (Ship Customizer) may use in the match:
  - which MK tiers may be fitted (MK I, II and/or III); the fabricator may start at the highest allowed tier;
  - which categories (weapons, defense, support modules, ammo, crates, homunculi and so on);
  - single options to ban;
  - how many starting alloys a CUSTOM loadout may set (no limit, the game's default, or a cap).

  Everyone sees the rules, in one line on the lobby screen and on the tab. Ship Customizer 0.7.0 or newer greys out
  what they refuse at the loadout terminal and, on the host, removes it from the ships when the match starts. The
  game's own loadouts are not affected. The host's last rules come back in their next Fleet Battle lobby.
- **Looks like the game.** Fleet's screens are built from the game's own style sheets and art:
  - The FLEET section of the loadout terminal uses the terminal's own parts: its header and decorative line, its
    LOADOUTS rows (gold for the ship being set up, a team-colour marker in Fleet Battle) and its small button.
  - The Fleet Battle lobby screen looks like one of the hub's terminals. It has the terminal background with its
    meander bars, section headers over the decorative lines, RESOURCES-style rows, the game's buttons, and CONFIRM
    TEAMS as the big hex button where SELECT sits on the loadout terminal.
  - In a match, the punch and hacking-terminal prompts show their key in a key-cap box like the game's prompts. The
    airlock hack countdowns use the HUD's bold text and colours.
- **Fixed:** the loadout terminal's team colour in Fleet Battle never showed, because it sat under the terminal's
  background picture. It now tints the terminal blue or red.
- The FLEET section's team-match button is now **TEAMS & RULES**. The host uses it to reopen the lobby screen; the
  current teams are kept.
- FleetApi: ShipRulesActive, IsTierAllowed, FabricatorMaxLevel, IsCategoryAllowed, IsItemAllowed, MaxStartAlloys and
  ShipRulesChanged, for Ship Customizer.

## 0.2.1
- **Ship 2's own starting alloys.** When ship 2 has a loadout of its own, it starts with its own alloys:
  - with Ship Customizer 0.5.1 or newer, the alloys set on its CUSTOM loadout;
  - otherwise, the game's own starting amount.

  As a copy of ship 1 (AS SHIP 1), it still starts with ship 1's amount. Until now ship 2 always started with ship 1's
  amount, even when ship 1 was a CUSTOM loadout with its own starting alloys.
- **FleetApi.ReadsShip2Alloys** tells Ship Customizer that Fleet does this, so Ship Customizer offers starting alloys
  for ship 2's CUSTOM loadout.

## 0.2.0
- **Teams are picked in the lobby.**
  - When the host picks FLEET BATTLE, the team screen opens for everyone. Team badges show above players' heads.
  - At the loadout terminal, the BLUE and RED ships are in team colours and only a ship's own team can change it.
  - The host starts the match by sitting in the ready room, and everyone spawns on their team's ship.
- **FleetApi** for other plugins: Ship Customizer 0.5.0 uses it to make the other team's CUSTOM view-only.
- **A real arena:**
  - Fleet Battle is fought in one of the game's asteroid fields, with rocks for cover;
  - mirrored salvage on both sides (containers, a wreck, an asteroid base, a VoidFish);
  - an alloy supply drop every 5 minutes;
  - no enemy NPCs.
- **B.R.A.I.N. auto-turrets against the enemy ship:**
  - they aim at its hull and lead its real movement;
  - they only fire while on target (they used to keep firing into space at the edge of their arc);
  - the enemy's modules and shields no longer count as cover;
  - a visible ship always beats an enemy player in space.
- **Shields fixed with two ships:** each ship's shield segments were left at the world origin (80 m away in co-op,
  1.8 km in PvP). They now sit on their ship.
- **Airlock Hacking Terminal replaces the breaching charge.** Plug it into the control panel outside an enemy airlock.
  After a one-minute hack, your team can use that ship's doors for five minutes (not the cockpit's). Countdowns show on
  screen, and the defenders can pull it out while it hacks.
- **Aboard the enemy ship:**
  - its systems' power switches are always usable;
  - the helm, turrets, seats, terminals and the homunculus stay off-limits;
  - enemies can't get into the cockpit.
- Fixed an error in clone-tube healing when a player loads in while someone is in a clone tube.

## 0.1.1
- **Punching works in VR.** It aims where you look (the game's own interaction ray, which the VR UI Fix points with
  your head) instead of the camera's mouse-look direction.
- **Fire punches too** (mouse button or VR controller), with empty hands and an enemy in reach. V still works.
- **Hub mission panel:** with two ships it names both slots (SHIP 1 / SHIP 2, or BLUE / RED in Fleet Battle), and it
  shows FLEET PILGRIMAGE or FLEET BATTLE instead of STANDARD.
- **End of a PvP match:** fixed an error from proximity voice when a player's character was already gone.

## 0.1.0

First release.

### Two ships
- Two player ships per lobby, up to 16 players (8 per ship), split evenly. Each ship has its own clone tubes.
- The **FLEET** panel on the hub's loadout terminal: turn two ships on and pick each ship's loadout. Ship Customizer
  CUSTOM works for ship 1, and with Ship Customizer 0.4.0+ for ship 2 too.
- The ships start side by side, fly freely and void-jump together.

### Each ship kept apart
- Its own fabricator alloys and biomass, blueprints and homunculus.
- Its own ship voice lines, alarms, music cues, shakes and sounds.
- Its own helm, pilot HUD, third-person camera, boosters, target locks and scan reticle.
- Its own damage markers, pings, power and lights.

### Missions
- The game's own missions with two ships.
- **FLEET PILGRIMAGE**: more enemies, and more objectives per sector.
- **FLEET BATTLE**: PvP.

### Fleet Battle (PvP)
- **Teams:** a balanced team screen, which the host confirms.
- **Combat:**
  - enemy markers and red name tags;
  - ship-versus-ship damage, with a win or a forfeit;
  - punching (V);
  - point defenses and auto-turrets shoot enemy players in space.
- **Boarding:**
  - enemies board only through open airlocks;
  - enemies can't use your ship, except its System Enhancement panels;
  - an enemy who completes a panel shuts that system down for 120 s.
- **Voice:** proximity voice is forced on.
- **Airlock Breaching Charge:** a new fabricator item (PvP). Attach it to an enemy airlock and arm it. The blast forces
  both doors open and vents the ship. The defenders can pull it off.

### Rules
- Progression is paused in fleet missions and in rooms of more than 6 players.
- Rooms you host are marked `[mod_session]`.
