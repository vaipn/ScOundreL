# ⚔️ ScOundreL: On-Chain Dungeon Crawler on Solana

[![Solana Devnet](https://img.shields.io/badge/Solana-Devnet-14F195?logo=solana&logoColor=white)](https://explorer.solana.com/address/7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx?cluster=devnet)
[![MagicBlock VRF](https://img.shields.io/badge/MagicBlock-VRF%20v0.17-7B2BF9)](https://docs.magicblock.gg/pages/randomness/)
[![Unity 6](https://img.shields.io/badge/Unity-6000.0.64f1%20URP-000000?logo=unity&logoColor=white)](https://unity.com/)
[![Anchor](https://img.shields.io/badge/Anchor-0.30.1-blue)](https://www.anchor-lang.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**ScOundreL** is a tactical single-player roguelike card crawler built for the [Solana Hackathon](https://events.mlh.com/events/15200-solana-hackathon), combining tactile tabletop visuals in **Unity 6 WebGL** with complete on-chain game state, **MagicBlock Verifiable Randomness (VRF)**, **Ephemeral Session Keys**, **SPL Token Gold minting**, persistent **Player Profiles**, and a global **Top-10 Leaderboard**.

---

## 🎮 Playable Demo

* **Live Itch.io WebGL Build:** Compatible with modern desktop browsers (Chrome, Brave, Firefox, Edge).
* **Supported Wallets:** Phantom, Solflare, Backpack, plus an instant **Devnet Burner Wallet** for frictionless playtesting.
* **Cluster:** Solana Devnet.

---

## 🏛️ Architecture & On-Chain Design

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│                             UNITY 6 WEBGL CLIENT                                 │
│  Tactile 3D Tabletop • Balatro Tilt Shaders • Dynamic Camera • SFX/BGM Pipeline  │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │ JSON-RPC & JSLib Wallet Adapter
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│                    ANCHOR PROGRAM ON SOLANA DEVNET                              │
│            Program ID: 7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx              │
├──────────────────────────┬───────────────────────────┬───────────────────────────┤
│    1. MagicBlock VRF     │  2. Ephemeral Session     │  3. SPL Gold Mint & ATA   │
│   Provably fair deck     │   Keypair authorized to   │   On-chain currency mint  │
│   shuffle via oracle CPI │   sign turn transactions  │   and bounded merchant    │
│   and callback verification│   popup-free (0 lag)   │   delegate burns          │
├──────────────────────────┴───────────────────────────┴───────────────────────────┤
│    4. Player Profile PDA                             5. Global Leaderboard PDA   │
│   Lifetime runs, victories, slain, scores           Top-10 ranked runs on Devnet │
└──────────────────────────────────────────────────────────────────────────────────┘
```

---

## ⚡ Core Solana Features

### 1. Provably Fair MagicBlock VRF (Verifiable Random Function)
* **CPI Randomness Request:** When starting a run (`start_run`), the program initiates a Cross-Program Invocation to MagicBlock's default VRF oracle queue (`Cuj97ggrhhidhbu39TijNVqE74xvKJ69gDervRUXAxGh`).
* **Authenticated Oracle Callback:** The MagicBlock oracle node fulfills randomness via `callback_start_run`, verifying that the caller matches the official VRF program.
* **On-Chain Cryptographic Shuffle:** The 32-byte VRF seed is committed to the player's `DungeonRun` PDA. The program runs a Fisher-Yates shuffle directly on-chain using the verified seed, guaranteeing zero front-running and tamper-proof dungeon decks.

### 2. Ephemeral Session Keys (Popup-Free Gameplay)
* **Single Initial Signature:** The player signs once with Phantom/Solflare to approve `start_run` and fund an ephemeral session keypair with min lamports (`0.01 SOL`).
* **Zero-Interruption Gameplay:** Turn-by-turn combat (`resolve_card`), room avoidance (`flee_room`), potion usage (`use_epic_potion`), and merchant purchases (`use_marketplace_item`) are signed exclusively by the ephemeral key in the background.
* **Security Constraints:** The program enforces that only the delegated session key registered inside the active `DungeonRun` account is authorized to sign turns on behalf of the player.

### 3. SPL Gold Token Economy & Merchant Delegation
* **Canonical Mint PDA:** Derived on-chain via seeds `[b"scoundrel_gold_mint"]`.
* **Play-to-Mint:** Gold accumulated by defeating monsters and resolving dungeon rooms can be minted directly to the player's Associated Token Account (ATA) on Devnet (`claim_gold_tokens`).
* **Session-Delegated Merchant Spending:** Players can prepare a bounded token delegate allowance (`prepare_session_vault`), allowing the ephemeral session key to burn/spend SPL Gold tokens popup-free when purchasing consumables (Health Potions, Oil of Slipperiness, Rusty Shields).

### 4. Persistent Player Profile PDA
* **Seeds:** `[b"profile", player_pubkey]`.
* **Tracked Metrics:** Lifetime runs started, total victories, defeats, highest score, total monsters slain, total SPL Gold minted/spent, and cumulative VRF requests.
* **Automatic Synchronization:** Updated atomically on-chain on game start, combat resolution, and run completion.

### 5. Global Top-10 Leaderboard PDA
* **Seeds:** `[b"leaderboard"]`.
* **Sorted On-Chain Ranking:** Stores the top 10 runs on Devnet ordered by `(victory, score, remaining_hp)`.
* **Verified Proof:** Each entry stores the player's pubkey, final score, remaining HP, victory state, timestamp, and an 8-byte prefix of the verified MagicBlock VRF seed.

---

## 📜 Devnet Deployments & Identifiers

| Component | Address / Identifier | Explorer Link |
| :--- | :--- | :--- |
| **Program ID** | `7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx` | [Solana Explorer](https://explorer.solana.com/address/7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx?cluster=devnet) |
| **SPL Gold Mint PDA** | `aGiTr2glCPZwd3Avbvssx7UFyreDhC33YKvArG76CYg` | [Solana Explorer](https://explorer.solana.com/address/aGiTr2glCPZwd3Avbvssx7UFyreDhC33YKvArG76CYg?cluster=devnet) |
| **Global Leaderboard PDA** | `26xV31rUfxwJov8C162oU71x7x9v6J49c5hX55y87P1h` | [Solana Explorer](https://explorer.solana.com/address/26xV31rUfxwJov8C162oU71x7x9v6J49c5hX55y87P1h?cluster=devnet) |
| **MagicBlock VRF Queue** | `Cuj97ggrhhidhbu39TijNVqE74xvKJ69gDervRUXAxGh` | [Solana Explorer](https://explorer.solana.com/address/Cuj97ggrhhidhbu39TijNVqE74xvKJ69gDervRUXAxGh?cluster=devnet) |
| **MagicBlock VRF Program** | `Vrf1RNUjXmQGjmQrQLvJHs9SNkvDJEsRVFPkfSQUwGz` | [Solana Explorer](https://explorer.solana.com/address/Vrf1RNUjXmQGjmQrQLvJHs9SNkvDJEsRVFPkfSQUwGz?cluster=devnet) |

---

## 🃏 Game Rules (Standard Scoundrel)

* **Deck:** Standard 52-card deck with Face cards removed (values 2–10, Aces = 14), plus Aces & Face cards for special dungeon events (44 cards total).
* **Suits:**
  * ♠️ **Spades & ♣️ Clubs:** Monsters (damage equal to card rank).
  * ♦️ **Diamonds:** Weapons (equip to attack monsters up to weapon rank; subsequent attacks must be against equal or weaker monsters).
  * ♥️ **Hearts:** Potions (restore HP equal to card rank; only one potion can heal per room).
* **The Room:** 4 cards dealt at a time. The player must resolve at least 3 cards before advancing to the next room, or choose to Avoid/Flee the room (cannot flee twice consecutively).
* **Victory:** Clear all 44 cards in the dungeon.
* **Defeat:** Player HP drops to 0.

---

## 🛠️ Local Development & Build Guide

### 1. Anchor Smart Contract

#### Prerequisites
* [Rust](https://www.rust-lang.org/) `1.79+`
* [Solana CLI](https://docs.solana.com/cli/install-solana-cli-tools) `1.18+`
* [Anchor](https://www.anchor-lang.com/docs/installation) `0.30.1`

#### Build & Test
```bash
cd anchor-program

# Build BPF binary
anchor build

# Run comprehensive test suite
anchor test --skip-local-validator
```

---

### 2. Unity 6 WebGL Client

#### Prerequisites
* **Unity 6 (6000.0.64f1)** with WebGL Build Support.
* Standard modern browser with WebAssembly and WebGL 2.0 support.

#### Building for Itch.io
The project includes an automated Editor builder that enforces itch.io-safe delivery (disabling Gzip chunking to prevent connection drops on large WebAssembly binaries):
1. Open Unity Editor with project directory `./unity-client`.
2. In the top menu, click **Scoundrel > Build WebGL for itch.io**.
3. The build compiles IL2CPP and outputs an itch-ready archive to `Scoundrel-WebGL-itch.zip`.

---

## 🛡️ Security & Integrity

* **Borsh Binary Protocol:** All on-chain accounts (`DungeonRun`, `PlayerProfile`, `Leaderboard`) implement deterministic Borsh layout serialization with Anchor discriminators.
* **Checked Arithmetic:** All health, score, and token arithmetic on-chain use `saturating_add`, `saturating_sub`, or checked math to avoid underflow/overflow vulnerabilities.
* **Rent Reclamation:** When closing a run (`close_run`), remaining account lamports are safely returned to the player wallet.

---

## 🏆 Hackathon Submission Notes

Built for the **Major League Hacking (MLH) Solana Hackathon**.
* **GitHub Repository:** [ScOundreL](https://github.com/vaipn/ScOundreL)
* **Cluster:** Solana Devnet
* **Live Program:** Verified on Solana Explorer Devnet.
