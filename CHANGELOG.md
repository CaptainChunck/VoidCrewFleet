# Changelog

## 0.4.1
Fixes from a full code review of 0.4.0. Everyone in a room needs 0.4.1: the network protocol changed.

**Losing ships, respawns and spectators**
- A player whose ship was lost is never re-cloned, also once only one ship is left, after reconnecting or from the
  spawn queue.
- Other players' deaths are seen correctly everywhere: dead spectators near their body are no longer heard, their chat
  stays among spectators, turrets stop shooting floating corpses and punches can't target them.
- A spectator's HUD stays hidden after the escape menu closes, and the view starts with the ships' outsides shown.
- FORCE RESPAWN is off once your ship is lost.
- A lost ship's wreck is always removed, also when the host changes during its explosion.
- RETURN TO HUB also shows for a host who joined after the loss.
- The clone tube screen keeps working when ship 1 is lost, and shows the biomass of the ship whose tube you're in.
- A lost ship's power-recovery hint and voice events are cleaned up.

**Co-op fleets**
- Ships following a void jump get the game's "entered the void" event: their galaxy map, sockets and collisions update.
- Void jumps work when another player than the host flies a ship, and a ship nobody crews no longer blocks a jump.
- Enemies, scouts and the sector's events go for the nearest ship instead of the host's, and every ship is kept in the
  play area. "Ship arrived" sector events fire once per jump, not once per ship.
- Relic and weapon bonuses that depend on the ship (alloys, power, breakers, speed) read their own ship.

**Lobby**
- **Your team and ship** show as one more row of the ship panel in the top left corner, built from the HUD's own art
  (the diamond with the ship icon in your team's colour, the bar plate and the value plate): BLUE TEAM  BLUE 2 with the
  ship's type, or SHIP 2 in co-op, and LOST once it is destroyed. It shows from the moment you have a ship in the lobby
  until the mission ends, and hides with the HUD.
- Only the host can change the ship count. Saved runs (one ship) don't open the crew screen or hold the start.
- Back in the hub, the host no longer places everyone "as already confirmed" for a moment.
- A hidden lobby screen comes back when the host switches between co-op and Fleet Battle.
- The loadout terminal opens on your own ship each time you use it.
- The Fleet Pilgrimage's two-ship minimum is no longer saved as your default ship count.
- A player who becomes host in the hub tells their Ship Customizer which ships use CUSTOM.
- When a mission adds ships for a big room, the crews are evened out and no ship takes more than 8.

**Fleet Battle**
- **Players who join a match under way play at once while there is room**, on the team with fewer active players and
  its ship with the smallest crew (8 at most), so the teams stay at most one apart. Players reconnecting within their 3
  minutes count as active; the crews of lost ships don't. Before, a match only had as many places per team as its
  larger team had at the start.
- After a host change, ships aren't moved mid-match and supply drops keep their schedule.
- Free places go to waiting players fairly: their own team first, then one at a time to the team with fewer active
  players. A newcomer can't take a place ahead of players already waiting. A waiting player without a team sees and
  hears no team's ships, talks only with other waiting players without a team, and gets Defeat on the end screen.
- Twin and quad auto-turrets hit players as hard as intended, and a piercing round hits an enemy ship once.
- Punches, enemy power switches, panel sabotage and the hacking terminal are checked properly by the host (or the
  ship's pilot): a modified game can't punch from afar or too fast, switch enemy power from anywhere, or sabotage or hack
  while dead or off the ship. Each hacking terminal works once.

**Rooms and other mods**
- A Fleet player who becomes host of a normal room no longer makes it require Fleet.
- A new host keeps the room's size.
- A Ship Customizer turned off in its own settings counts as not installed.
- Codex entries from data shards aren't unlocked where progression is paused, and every player agrees on the pause.
- Fleet's reconnect ID is a hash of the Steam ID (not the ID itself), published in Fleet rooms only.

**Performance**
- The plugin's periodic work is spread over frames instead of running all at once 4 times a second (smoother in VR).
- Less work per frame in proximity voice, turrets, factions, the spectator view, the hacking terminal, ship sounds and
  the lobby screen.

## 0.4.0
Fleets of up to three ships, and Fleet Battle with up to three ships per team. Everyone in a room needs 0.4.0: the
network protocol changed.

**Ships and players**
- Up to **24 players** per room (8 per ship).
- **Co-op: up to three ships.** The FLEET panel's SHIPS button sets how many ships the game's own missions use (one in
  a new room; the host's last choice is remembered). The Fleet Pilgrimage has two or three.
- The FLEET panel lists every ship: click one to set it up at the terminal.
- **Players pick the ship they fly** on the lobby screen, which opens for everyone when the host sets two or three
  ships. Crews stay even; the host can move players and auto-balance, and confirms the crews (CONFIRM CREWS) before the
  mission can start. CREWS on the FLEET panel brings the screen back.
- **Ship types:** the host can force each ship's type (Destroyer, Frigate or Striker) on the lobby screen, or leave it
  free. A ship whose loadout isn't of that type gets the type's standard loadout.
- A ship with no loadout chosen copies ship 1, as before (in co-op, when its type allows it).
- **Void jumps:** the escape vector set on any ship counts for every ship, and once every ship's void drive is charged,
  any ship's lever jumps the whole fleet. While a drive isn't charged, the lever says which ship's and nothing starts.
  During a jump, any ship's lever acts for the fleet.
- **Everyone always respawns on their own ship**, the one picked in the lobby.
- **A destroyed ship's crew can't be re-cloned for the rest of the mission** (co-op and Fleet Battle). Anyone aboard
  dies with it; a crew member aboard another ship plays on until they die. Then they watch the remaining ships from a
  third-person view: Q / E (or the mouse buttons) switch ships, the mouse looks around and the wheel zooms.
- **The mission goes on while any ship is left** (before, losing a ship could end it). Once a ship is lost, the host's
  escape menu has **RETURN TO HUB**, which ends the mission for everyone so they can start again.
- The Fleet Pilgrimage's extra enemies grow with a third ship.
- Respawn tubes keep their numbers when a ship is lost, so nobody is re-cloned on the wrong ship.

**Fleet Battle**
- **One to three ships per team**, set by the host on the lobby screen. BLUE has ships 1, 3 and 5; RED has 2, 4 and 6.
- **The host forces each ship's type.** By default both teams get the same ships (BOTH TEAMS: SAME SHIPS).
- Players pick a ship, and with it their team. Teams and crews stay even, and every ship needs a crew before the host
  can confirm.
- **Hulls have 4x the game's hit points** (PvP > HullMultiplier, the host's setting).
- A team's own ships are friendly to each other: they can't damage each other, and their crews may use anything aboard
  each other's ships.
- **A team loses when it has no ships left.**
- **Spectators:** the crew of a destroyed ship watches the rest of the match, as in co-op.
- **Reconnects:** a player who loses their connection keeps their place for 3 minutes, and returns to their team and
  ship. After that their place goes to someone waiting, or anyone joining. A player who comes back to a full team waits
  for the next free place, watching their own team's ships.
- Spectators talk among themselves: nobody still fighting hears them, by voice or text chat.
- Teammates' ships are marked as allies, enemy ships as enemies.
- Late joiners and returning players are placed by account, so a reconnect is recognised.
- No ship copies another in Fleet Battle: a ship nobody set up starts with its type's standard loadout.
- **Fist fighting:** with empty hands, hold the right mouse button to raise your fists into a fighting stance, then
  left click to punch, alternating arms. On a gamepad: hold LT, punch with RT. Everyone around sees the stance and the
  punches. V no longer punches: it is the game's salute key, so every punch also saluted (the PunchKey setting is gone).

**Ship Customizer**
- With Ship Customizer 0.9.0 or newer, every ship can have a CUSTOM loadout of its own. With older versions, ship 2 can
  (as before), and ships 3-6 use game loadouts.

## 0.3.1
Fixes from a full code review. Everyone in a room needs 0.3.1: the network protocol changed, and the game now tells a
player with another version which one the room needs.

**Rooms and missions**
- TWO SHIPS is off in a new room (the host's last choice is remembered). Before, every room planned two ships, so
  ordinary missions gave no progression.
- Activating FLEET BATTLE again no longer reopens confirmed teams for everyone.
- Choosing the saved run ends a fleet mission choice.
- Clients show the host's ready-room countdown in Fleet Battle.
- A mission falls back to one ship when a game update broke a patch two ships need.
- A player who had hosted an earlier room no longer brings its crews along when they become host.

**Progression and saves**
- Daily and weekly quests finished in a mission without progression are no longer used up for nothing.
- Quest and achievement tracking stays off there.
- The pause holds until the hub, even if players leave.
- Fleet runs aren't autosaved: a saved fleet run came back as a one-ship run that earned progression.

**Co-op with two ships**
- A lost ship's crew is re-cloned aboard the other ship without being left on a black screen with no HUD.
- The HUD's hull, power and breakers, the target locks and the markers follow the ship you're on.
- Sector twists and hazards (ship modifiers, scorching pulses, module disablers, interference, black holes) act on
  every ship alike, not on whichever ship each player was on.
- Ship 2 without a homunculus of its own no longer takes ship 1's loadout homunculus.
- After the host leaves, the new host no longer spawns fewer Fleet Pilgrimage enemies.

**Fleet Battle**
- The lobby screen:
  - blocks the game's controls behind it;
  - its team lists scroll;
  - LEAVE asks to be pressed again;
  - it has HIDE / CLOSE, and the host's CHANGE MISSION;
  - TEAMS & RULES shows the teams and rules to non-hosts;
  - it steps aside for the game's own menus and the Steam Deck cursor.
- A player who joins the hub after the teams are confirmed gets a team.
- Match result:
  - A team that empties after the host leaves still forfeits.
  - When both ships die, the first loss decides, with one result for everyone.
- Shots:
  - Shots on an enemy shield do the same damage whoever fires them, with the target-lock bonus.
  - A piercing round stops at the enemy shield or hull.
- Fire no longer punches while you're using a button, lever, panel or item.
- Auto-turret shots hit players by chance, more often up close. Two turrets no longer knock a boarder out in about a
  second.
- Hacking and sabotage:
  - A hack or a sabotage can no longer be lost when two happen at once.
  - The hacking terminal finds an airlock's outside, and puts enemies out of a cockpit, more reliably.
  - An airlock already being hacked says so.
- Boarding:
  - The superior grappling hook can't take the enemy ship's items.
  - The enemy's central computer power switch (the whole ship's power) is off-limits.
  - Warnings say why something is off-limits.
- Performance: proximity voice, carrying a hacking terminal and the countdowns do far less work every frame.
- The ship rules tab redraws at once, and non-hosts can't press its controls.

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
