# VR Block Stacking: Handoff Documentation

**Repository:** https://github.com/euigyuki/vr-block-stacking

**Author:** Derrick Kim

**Successor:** Austin Youngren

**Purpose:** Formal handoff of the Unity simulation environment for the iSAT Strand 1 common-ground data-capture work.

**Status:** Working two-participant VR prototype used to run collaborative block-stacking sessions and capture multimodal interaction data (interaction events, gaze/visibility snapshots, and per-participant speech audio).

This document covers the codebase, build and run instructions, and current asset status. It also records the data outputs the analysis pipeline depends on and the known issues.

---

## 1. Project Overview

A networked VR application in which two participants, each in a separate physical room wearing a Meta Quest headset, share a virtual table and stack colored blocks together while talking to each other. The system records what each participant does, looks at, and says, for offline analysis.

The session has two parts:

- **Host:** runs in the Unity Editor on a small PC (called "GMKtec" in the code). The host is the server and the authority for all game state. Its camera is a fixed Editor view with no head tracking, so it is treated as a non-participant (Player 0) and excluded from gaze logging.
- **Clients:** two Meta Quest headsets, each running a device build. They connect automatically, with no manual code entry.

Networking is peer-to-peer through **Unity Relay**, so the two machines do not need to share a LAN or open any ports. **Unity Netcode for GameObjects (NGO)** carries the game state, and **Vivox** carries voice. The Relay join code is passed through a public **Firebase Realtime Database** so the Quests can find the host with no user input.

Physics note: blocks do **not** use gravity or physical setting. Every block is kinematic and frozen in place. Stacking is a snap-to-top placement routine that runs when a block is released. I built it this way because the gravity-based setting produced constant sync jitter and blocks sinking through the table on the Quest clients. The cost of the fix is that this is not a physics simulation, so anything needing real physical behavior would have to be added.

---

## 2. Environment and Dependencies

| Item | Value |
|---|---|
| Unity Editor version | **6000.3.2f1** (Unity 6.3). The project is version-locked; open it with exactly this version. |
| Render pipeline | Universal Render Pipeline (URP) 17.3.0 |
| Target platform (clients) | Android / Meta Quest, via OpenXR |
| Host platform | Unity Editor (desktop), acts as server |
| Application identifier | `com.DefaultCompany.VRProject1` |
| Product / company name | `VRProject1` / `DefaultCompany` (never customized) |
| Bundle version | 0.1.0 |

Key packages (`Packages/manifest.json`). Hopefully you find these useful when checking for correct version control. 

- `com.unity.netcode.gameobjects` 2.7.0 — networked game state
- `com.unity.services.relay` 1.2.0 — NAT-traversal relay
- `com.unity.services.authentication` 3.6.0 — anonymous sign-in (required before Relay/Vivox)
- `com.unity.services.vivox` 16.10.0 — voice chat
- `com.unity.xr.interaction.toolkit` 3.2.1 — grab/interaction
- `com.unity.xr.openxr` 1.16.0 and `com.unity.xr.management` 4.5.4 — XR runtime
- `com.unity.inputsystem` 1.17.0 — controller input
- `com.unity.render-pipelines.universal` 17.3.0

### External services this project is bound to

These are live external dependencies. Austin either needs access to the existing accounts or has to change the following configurations to his own.

1. **Unity Gaming Services (UGS) project.** `cloudProjectId: 1312219b-d0ae-41fe-a7aa-2625d4c7936a` (in `ProjectSettings/ProjectSettings.asset`). Relay, Authentication, and Vivox all resolve against this UGS project.
2. **Vivox app.** Issuer `24759-vrpro-60023`, server `https://unity.vivox.com/appconfig/24759-vrpro-60023`, domain `mtu1xp.vivox.com` (in `ProjectSettings/Packages/com.unity.services.vivox/Settings.json`). Test Mode is off. Login uses the UGS/anonymous player ID.
3. **Firebase Realtime Database.** URL is hardcoded in `RelayManager.cs`: `https://vrblockstacking-default-rtdb.firebaseio.com/joinCode.json`. The host writes the current Relay join code here, and clients poll it. For anonymous Quests to read and write this key, the database read/write rules have to be open. The join code is public at a known URL. 

---

## 3. Codebase

All gameplay and logging scripts live directly in `Assets/`. There are eleven C# scripts, in three groups.

### 3.1 Networking and session startup

**`RelayManager.cs`**  is the core of the connection flow.
- Host path (`StartHostWithRelay`): anonymous sign-in, create a Relay allocation for 2 connections, get a join code, write it to Firebase, set the transport to the Relay allocation over `wss`, start the NGO host.
- Client path (`StartClientWithRelay`): anonymous sign-in, read the join code from Firebase (retries up to 10 times, 2 s apart, since the host may not have written it yet), join the allocation, start the NGO client.


**`NetworkUI.cs`** drives startup and picks host vs. client by build type: `#if UNITY_EDITOR` starts the host, otherwise it starts the client. Displays the join code and status text and triggers the Vivox channel join once the session is up. 

**`VivoxManager.cs`** initializes Vivox, logs in with the UGS player ID, and joins a single audio-only group channel named `dpip_session`. Everyone in that channel hears each other. It runs independently of `SpeechLogger`: Vivox handles live two-way voice, while `SpeechLogger` records the local mic to disk for transcription. 

### 3.2 Blocks and scoring

 Most of the interaction logic lives in **`StackableBlock.cs`**.
- Grab/release via XR Interaction Toolkit, with ownership transfer to the grabbing client, a grab debounce, and dual-grab prevention (a static `_heldByPlayer` map stops one player from holding two blocks and cancels the conflicting selection).
- While a block is held locally, the owner streams its transform to the server about 30 times per second, and the server rebroadcasts it to the other client.
- On release, `TryFindSnapTarget` looks for the nearest other block within `snapRadius`, computes the clean on-top position (target top + half block height), and walks upward until it finds a clear slot. The server then slides the block into place (`_isSnapping`) and freezes it. If there is no nearby block, it freezes exactly where released. No gravity is ever applied.
- Emits grab/release events to `InteractionLogger`, tagged with the current score.

**`StackingArea.cs`** tracks blocks inside a table zone and, every 0.1 s, computes the height of the tallest tower using downward/upward box casts, ignoring blocks that are held or still moving. Publishes a "Live Score" to a TMP text.

**`ScoreManager.cs`** maintains a separate server-authoritative `NetworkVariable<int>` count that increments/decrements as blocks report stacked/unstacked via `StackableBlock.OnStackedStateChanged`.



### 3.3 Data capture 

**`InteractionLogger.cs`** appends every interaction event to a per-session JSON file and rewrites the file on each event so nothing is lost on a crash. Each event has a wall-clock timestamp, `Time.time` game time, player ID, block name, action (`grabbed`, `released`, `recording_started`, `recording_stopped`, `audio_sync`), position, and score. Output: `session_{yyyyMMdd_HHmmss}.json`, to the **Desktop** in the Editor (i.e. on the host PC) and to `persistentDataPath` on device.

For **`GazeLogger.cs`**, every 5 seconds, for each connected client (Player 0 / host excluded), records a snapshot of: head position, gaze direction, which blocks are visible (raycast from the head within a 90-degree cone and 1.5 m, unobstructed), which blocks are within 1.5 m regardless of line of sight, and the full stack configuration (for each block, which block it is resting on).  Output: `gaze_{yyyyMMdd_HHmmss}.json` next to the interaction log.

For **`PlayerHeadReporter.cs`**, every 5 seconds client's head position and forward vector are sent to the server via `GazeLogger.ReportHeadStateServerRpc`. Its interval has to match `GazeLogger.SnapshotInterval`.

**`SpeechLogger.cs`**  records the local microphone to a 16 kHz mono 16-bit WAV in `persistentDataPath`, Whisper-ready. Output: `speech_{clientId}_{yyyyMMdd_HHmmss}.wav` on each Quest.

**`AnimateHandOnInput.cs`**  reads controller trigger/grip values and drives the hand `Animator` parameters `Trigger` and `Grip`.

---

## 4. Build and Run Instructions

### 4.1 Host (Unity Editor, on the mini-PC)

1. Open the project in Unity **6000.3.2f1**.
2. Confirm the UGS project is linked and Relay/Auth/Vivox are enabled: Edit > Project Settings > Services. If this machine/account is not a member of UGS project `1312219b-...`, either get added to it or relink to a new one .
3. Open `Assets/Scenes/Main VR Scene.unity`. This is the production scene.
4. Press Play. `NetworkUI` allocates the Relay session, starts the host, writes the join code to Firebase, and joins the Vivox channel. The join code appears on the host UI and in the Console.

### 4.2 Clients (Meta Quest)

1. File > Build Settings: platform Android; confirm the only enabled scene is `Main VR Scene`.
2. Under XR Plug-in Management, confirm OpenXR is enabled for Android with the Meta Quest feature set.
3. Build and Run to each Quest, or install the committed APK, `VRBlockStacking_Relay_v1.apk`, which is the last known-good build.
4. Grant microphone permission on each Quest (required for `SpeechLogger`; Vivox voice also needs it)
 
5. Launch on both Quests. Each one reads the join code from Firebase and connects automatically, then voice connects after the network session is up. Start the host first so the join code exists when the clients poll.

### 4.3 Collecting data after a session

- Host logs (interaction and gaze) are written to the **host PC**: `session_*.json` and `gaze_*.json`.
- Per-participant audio is on each Quest; pull it over ADB.
- To align modalities: every log shares `gameTime` (`Time.time`), and the `audio_sync` events in `session_*.json` map each recording's elapsed time to game time.

---

## 5. Current Asset Status

Located under `Assets/`:

- **Scenes** (`Assets/Scenes/`): `Main VR Scene.unity` is the production scene and the only one enabled in the build.
- **Blocks:** prefabs `BlackBlock`, `BlueBlock`, `GreenBlock`, `RedBlock`, with matching materials (`Black.mat`, `Blue.mat`, `Green.mat`, `Red.mat`) and a shared `BlockPhysics.physicMaterial`. Blocks are tagged `Block` and sit on a dedicated blocks layer referenced by the `blockLayer` masks in the scripts. Confirm these tag/layer assignments if you clone blocks.
- **Hands:** `Animated Hands/` assets plus `AnimateHandOnInput.cs`.
- **UI / networking assets:** `JoinButton.prefab`, `NetworkUI`, and `DefaultNetworkPrefabs.asset` (the NGO network-prefab registry; any new networked object has to be registered here).
- **Input:** `InputSystem_Actions.inputactions`.
- **`VRBlockStacking_Relay_v1.apk`:** last known-good Quest build (~52 MB), committed to the repo.

---

## 6. Known Issues, Limitations, and Open TODOs

1. **Two scoring systems that measure different quantities.** `StackingArea` (tallest-tower height, computed locally and not networked) and `ScoreManager` (networked count of stacked blocks) coexist. `InteractionLogger` logs the former. Decide which is authoritative and remove or reconcile the other. As it stands, the "score" in the logs means "height of the tallest tower at the moment of the event."

2. **Firebase database is a public, hardcoded, open endpoint.** The join code is public at a known URL. 


3. **No physics.** Blocks are kinematic and frozen, and stacking is snap-based. Any future work needing real physical interaction (toppling, weight, collisions between falling blocks) needs to start from scratch.


4. **Project identity never set.** Company/product are still `DefaultCompany`/`VRProject1`, which is where the package name `com.DefaultCompany.VRProject1` comes from. 
---

## 7. Suggested First Steps

1. Get added to the UGS project (`1312219b-...`) and the Vivox app, or relink to your own and re-enable Relay, Auth, and Vivox.
2. Open in Unity 6000.3.2f1, open `Main VR Scene`, press Play, and confirm a join code is written to Firebase and shown on screen.
3. Build to one Quest, grant mic permission, and confirm it connects and that `session_*.json`, `gaze_*.json`, and a `speech_*.wav` are produced.

---

## 8. Planned Next Direction

The direction I was moving toward, and a natural next step for this environment, is a voice-controlled variant aimed more directly at grounding and reference in cohort dialogue.

- **Labeled blocks and named regions.** Divide the table into alphabetized regions (A, B, C, and so on) and label each block with a number. Every object and every location then has a stable, speakable identifier. That turns vague spatial reference ("that one, over there") into explicit reference ("block 4 into region B"), which is far easier to ground and to score against.
- **Voice-driven placement.** Let a participant move a block into a region by voice command instead of by hand ("put block 4 in region B"). This moves the task from manual manipulation toward spoken instruction, which is the kind of interaction iSAT Strand 1 studies.
- **Why it fits the data-capture goal.** The current logs already record what each participant does, sees, and says. A fixed reference vocabulary (numbered blocks, lettered regions) lines the language in the speech logs up with the actions in the interaction logs, so a spoken command can be checked directly against the block movement it produced. That is the alignment you need to tell whether the two participants actually reached a shared understanding.


