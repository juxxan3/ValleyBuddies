# 🌟 Valley Buddies — v5.7.2 (`Juxxan.ValleyBuddies`)

**Bring Pelican Town villagers to physical life in Stardew Valley 1.6+.**  
Companions, living spouses, real farm chores, tactical cave combat, 18 interactive micro-events, 20 native cutscenes, ambient group scenes, a 4-layer Living Town simulation, cozy sleepovers, starlit camping, full Co-op sync, and a public C# API (`IValleyAgencyApi`) for modders.

📥 **Download the playable mod on Nexus Mods:** [Valley Buddies v5.7.2 on Nexus Mods (#55767)](https://www.nexusmods.com/stardewvalley/mods/55767)

---

## ✨ Key Features (v5.7.2 Gold Stable Master)

### 🔌 100% Standalone + Dynamic AliveNpcs Bridge (1.8.1 & 1.8.2+)
- **Zero Required Dependencies:** Works completely standalone out of the box—recruit any eligible villager via hotkey (`LeftShift + E`, fully rebindable), post-dialogue invite prompts, or reverse porch invitations.
- **Hot-Swappable AI Bridge:** Automatically links with **AliveNpcs** if installed (with live toggle in GMCM) via isolated Harmony instances and dynamic reflection supporting both legacy (`<= 1.8.0`) and modern (`1.8.1` / `1.8.2+`) `GroupConversationDirector` signatures.

### 🏠 Living Home & Post-Marriage Spouse Routines
- **Morning Kiss & Homemade Breakfast:** Tender morning kisses (`06:00–07:00 AM`) followed by walking to the kitchen to cook and deliver homemade breakfast (`07:00–08:30 AM`)—or leaving it in the fridge with an affectionate note—plus learning new recipes by watching you cook.
- **30-Minute Household Life Blocks:** Spouses wash dishes (with real steam and soap bubbles), tidy up the house, read by the window, or relax in their custom spouse room.
- **Vanilla Workday Announcements:** On days when your spouse has a town job (e.g., Penny, Harvey, Maru), they announce it warmly around `08:00 AM` and yield smoothly to their official schedule.

### 🌾 Real Farm Assistance & Greenhouse Chores
- **Smart Watering & Animal Care:** Waters tilled crops, indoor flower pots, and pet water bowls; visits barns and coops to pet animals for real daily friendship points.
- **Crop & Greenhouse Harvesting:** Harvests ripe crops (protecting flowers within 5 tiles of Bee Houses and prompting before harvesting farm/greenhouse flowers), preserves `ColoredObject` tints, and deposits items into chests (including **Big Chests**) or hands them directly to you.
- **Lumberjack, Mining & Obstacle Clearing:** Chops wood/trees with configurable filters, breaks rocks, and clears blocking weeds, twigs, or small stones along their path using real 1.6 tool animations.

### 🗡️ Tactical Cave & Mine Expeditions (Caves 2.0)
- **97 Custom NPC Profiles & 6 Archetypes:** Explicit profiles for all **36 Vanilla**, **17 Stardew Valley Expanded (SVE)**, and **44 Ridgeside Village** NPCs (plus dynamic classification for other custom mods) across *Vanguard*, *Miner/Artisan*, *Support/Healer*, *Technical/Arcane*, *Cautious/Fortune*, and *Child Protection* (children wait safely at the entrance).
- **4 Tactical Stances & Survival AI:** Switch between *Balanced*, *Ladder Hunter*, *Guard*, and *Stay Back*—featuring ladder/shaft spotting, bomb evasion, percentage-based healing, exhaustion recovery breaks, and dynamic lanterns on dark floors.

### 🎬 Universal Cutscenes, 18 Micro-Events & 4-Layer Living Town
- **20 Native Cutscenes + Ambient Group Scenes:** Powered by `StardewValley.Event` with zero save-file pollution—including Golden Sunset, Rainy Night, Winter Snow, and multi-actor ambient valley scenes—backed by **97 custom character voice packs**.
- **18 Interactive Micro-Events & Pacing Director:** Expressive portrait moments (*Burned Breakfast, Kitchen Taste Test, Firefly Wish, Wildflower Crown, Snowball Play, Stargazing Wish, Shared Umbrella*, and more) with interactive choices, buffs, and *Relaxed / Balanced / Lively* presets in GMCM—plus **10 discoverable Easter Eggs**.
- **4-Layer Living Town Simulation:** Multi-turn neighbor banter, spontaneous personal chatter, contextual pass-by greetings, and staged town vignettes with optional localized chat echo.
- **Picnic 2.0, Hangouts & Nightlife:** Persistent checkered picnic blankets with snacks, reverse porch invitations (`4+` hearts), fishing together, Bathhouse Spa swimming, Saloon/Museum/Beach hangouts, tucked-in bed sleepovers with `Zzz` emotes, outdoor camping with campfires, and morning coffee thank-you letters.

### 🌐 Host-Authoritative Multiplayer Co-op & 11 Languages
- **Full Co-op Sync:** Atomic invite arbitration, cross-owner job protection, independent multi-spouse routines across Farmhouse and Cabins, synced cave tactics/healing, and clean disconnect cleanup.
- **1,600 Localized Keys (11 Languages):** 100% key and token parity across `en`, `es`, `zh`, `de`, `fr`, `it`, `ja`, `ko`, `pt`, `ru`, and `default.json`.

---

## 🏗️ Level 3 Modular Architecture

Valley Buddies is structured into **127 C# files** organized into clean, decoupled layers:

```text
├── AgencyCore/                  # Reusable core contracts, runtime, registries & public API
│   ├── Contracts/               # INpcNavigator, IFarmWorldService, INpcJobHandler, IValleyAgencyApi
│   ├── Runtime/                 # JobExecutionContext, JobHandlerRegistry, ExternalTaskRegistry, ValleyAgencyApi
│   └── Models/JobStates/        # Strongly-typed job states (zero untyped dictionary keys)
├── Handlers/                    # 24 decoupled plugin handlers (Farm/, Social/, External/)
├── Execution/                   # Core lifecycle, human A* pathfinding, caves, camping & Spouse/ (4 partials)
├── Bridge/                      # Explicit Harmony patches (zero PatchAll) & AliveNpcs bridge
├── Systems/                     # LivingEvents (18 events, cutscenes, voice packs), Living Town, animations & mail
├── Multiplayer/                 # Host-authoritative Co-op sync messages & snapshot state
├── Config/                      # Modular GMCM Quick-Start dashboard + 5 themed sub-pages
├── Utilities/                   # Localization (I18n), bubble sanitization & safety helpers
├── assets/                      # JSON data packs (voices, banter, chatter, vignettes, ambient cutscenes)
└── i18n/                        # 11 official languages × 1,600 keys
