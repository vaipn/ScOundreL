import * as anchor from "@anchor-lang/core";
import { BN, Program } from "@anchor-lang/core";
import { expect } from "chai";
import { Scoundrel } from "../target/types/scoundrel";

const U64_MASK = 0xffffffffffffffffn;

type CardCategory = "monster" | "weapon" | "potion";

interface DecodedCard {
  id: number;
  category: CardCategory;
  rank: number;
}

function decodeCard(cardId: number): DecodedCard {
  if (cardId >= 0 && cardId <= 12) {
    return { id: cardId, category: "monster", rank: cardId + 2 };
  }
  if (cardId >= 13 && cardId <= 25) {
    return { id: cardId, category: "monster", rank: cardId - 13 + 2 };
  }
  if (cardId >= 26 && cardId <= 34) {
    return { id: cardId, category: "weapon", rank: cardId - 26 + 2 };
  }
  if (cardId >= 35 && cardId <= 43) {
    return { id: cardId, category: "potion", rank: cardId - 35 + 2 };
  }
  throw new Error(`Invalid card id ${cardId}`);
}

function splitmix64(state: { value: bigint }): bigint {
  state.value = (state.value + 0x9e3779b97f4a7c15n) & U64_MASK;
  let z = state.value;
  z = ((z ^ (z >> 30n)) * 0xbf58476d1ce4e5b9n) & U64_MASK;
  z = ((z ^ (z >> 27n)) * 0x94d049bb133111ebn) & U64_MASK;
  return (z ^ (z >> 31n)) & U64_MASK;
}

function simulateShuffle(rngSeedPlusSlot: bigint): number[] {
  const deck = Array.from({ length: 44 }, (_, i) => i);
  const state = { value: rngSeedPlusSlot & U64_MASK };
  for (let i = 43; i >= 1; i--) {
    const randVal = splitmix64(state);
    // Rust casts `rand_val as usize` (64-bit on sBPF) before `% (i + 1)`
    const j = Number(randVal % BigInt(i + 1));
    const tmp = deck[i];
    deck[i] = deck[j];
    deck[j] = tmp;
  }
  return deck;
}

function findTargetRngState(predicate: (deck: DecodedCard[]) => boolean): bigint {
  for (let k = 1n; k < 500_000n; k++) {
    const raw = simulateShuffle(k);
    const decoded = raw.map(decodeCard);
    if (predicate(decoded)) {
      return k;
    }
  }
  throw new Error("Could not find matching deterministic shuffle seed within search bound");
}

describe("scoundrel", () => {
  const provider = anchor.AnchorProvider.env();
  anchor.setProvider(provider);

  const program = anchor.workspace.scoundrel as Program<Scoundrel>;
  const { Keypair, PublicKey, SystemProgram, Transaction, LAMPORTS_PER_SOL } = anchor.web3;

  function deriveDungeonRunPda(player: anchor.web3.PublicKey): anchor.web3.PublicKey {
    const [pda] = PublicKey.findProgramAddressSync(
      [Buffer.from("dungeon_run"), player.toBuffer()],
      program.programId
    );
    return pda;
  }

  async function fundKeypairs(...keypairs: anchor.web3.Keypair[]): Promise<void> {
    const tx = new Transaction();
    for (const kp of keypairs) {
      tx.add(
        SystemProgram.transfer({
          fromPubkey: provider.wallet.publicKey,
          toPubkey: kp.publicKey,
          lamports: Math.floor(0.1 * LAMPORTS_PER_SOL),
        })
      );
    }
    await provider.sendAndConfirm(tx);
  }

  async function startDeterministicRun(
    targetRngState: bigint,
    hasLegendaryNft = false
  ): Promise<{
    player: anchor.web3.Keypair;
    sessionSigner: anchor.web3.Keypair;
    dungeonRun: anchor.web3.PublicKey;
    expectedDeck: DecodedCard[];
  }> {
    const expectedRaw = simulateShuffle(targetRngState);
    const expectedDeck = expectedRaw.map(decodeCard);

    for (let attempt = 0; attempt < 8; attempt++) {
      const player = Keypair.generate();
      const sessionSigner = Keypair.generate();
      await fundKeypairs(player, sessionSigner);

      const dungeonRun = deriveDungeonRunPda(player.publicKey);
      const currentSlot = await provider.connection.getSlot("processed");
      // Adjust for potential slot increment when sending transaction if prior attempt observed an offset
      const slotGuess = BigInt(currentSlot + (attempt % 2));
      const seed = (targetRngState - slotGuess) & U64_MASK;

      await program.methods
        .startRun(new BN(seed.toString()), hasLegendaryNft)
        .accountsPartial({
          player: player.publicKey,
          sessionSigner: sessionSigner.publicKey,
          dungeonRun,
          systemProgram: SystemProgram.programId,
        })
        .signers([player])
        .rpc();

      const state = await program.account.dungeonRun.fetch(dungeonRun);
      const roomIds = state.room.map((c) => (c === null ? -1 : c));
      if (
        roomIds[0] === expectedRaw[0] &&
        roomIds[1] === expectedRaw[1] &&
        roomIds[2] === expectedRaw[2] &&
        roomIds[3] === expectedRaw[3]
      ) {
        return { player, sessionSigner, dungeonRun, expectedDeck };
      }
    }

    throw new Error("Failed to align slot for deterministic run setup after retries");
  }

  it("1. Initializes 44-card Scoundrel deck, enforces Session Key auth, and handles flee_room ring-buffer", async () => {
    const player = Keypair.generate();
    const sessionSigner = Keypair.generate();
    const rogueSigner = Keypair.generate();
    await fundKeypairs(player, sessionSigner, rogueSigner);

    const dungeonRun = deriveDungeonRunPda(player.publicKey);

    await program.methods
      .startRun(new BN(42), false)
      .accountsPartial({
        player: player.publicKey,
        sessionSigner: sessionSigner.publicKey,
        dungeonRun,
        systemProgram: SystemProgram.programId,
      })
      .signers([player])
      .rpc();

    const initial = await program.account.dungeonRun.fetch(dungeonRun);
    expect(initial.hpTenths).to.equal(200);
    expect(initial.maxHpTenths).to.equal(200);
    expect(initial.deckLen).to.equal(40);
    expect(initial.deckHead).to.equal(4);
    expect(initial.runStatus).to.equal(0);

    // Verify all 44 cards (4 room + 40 remaining deck) form a valid permutation of 0..43
    const initialRoom = initial.room.map((x) => x as number);
    expect(initialRoom.every((c) => c !== null && c >= 0 && c <= 43)).to.be.true;
    const remainingDeck: number[] = [];
    for (let i = 0; i < initial.deckLen; i++) {
      remainingDeck.push(initial.deck[(initial.deckHead + i) % 44]);
    }
    const allCardsSorted = [...initialRoom, ...remainingDeck].sort((a, b) => a - b);
    expect(allCardsSorted).to.deep.equal(Array.from({ length: 44 }, (_, i) => i));

    // Unauthorized rogue signer must be rejected
    let rogueFailed = false;
    try {
      await program.methods
        .fleeRoom()
        .accountsPartial({
          signer: rogueSigner.publicKey,
          dungeonRun,
        })
        .signers([rogueSigner])
        .rpc();
    } catch (err: any) {
      rogueFailed = true;
      expect(String(err)).to.include("UnauthorizedSigner");
    }
    expect(rogueFailed).to.be.true;

    // Ephemeral sessionSigner flees room without player popup
    await program.methods
      .fleeRoom()
      .accountsPartial({
        signer: sessionSigner.publicKey,
        dungeonRun,
      })
      .signers([sessionSigner])
      .rpc();

    const afterFlee = await program.account.dungeonRun.fetch(dungeonRun);
    expect(afterFlee.fledLastRoom).to.be.true;
    expect(afterFlee.deckLen).to.equal(40);
    expect(afterFlee.pendingSpoilsThreat).to.equal(0);

    // Verify initial 4 room cards were pushed to the exact bottom of the ring-buffer deck
    const bottomFour = [36, 37, 38, 39].map(
      (offset) => afterFlee.deck[(afterFlee.deckHead + offset) % 44]
    );
    expect(bottomFour).to.deep.equal(initialRoom);

    // Consecutive flee must fail
    let secondFleeFailed = false;
    try {
      await program.methods
        .fleeRoom()
        .accountsPartial({
          signer: sessionSigner.publicKey,
          dungeonRun,
        })
        .signers([sessionSigner])
        .rpc();
    } catch (err: any) {
      secondFleeFailed = true;
      expect(String(err)).to.include("CannotFleeConsecutiveRooms");
    }
    expect(secondFleeFailed).to.be.true;
  });

  it("2. Verifies <= weapon degradation, WeaponCeilingExceeded, and barehanded vs. weapon damage", async () => {
    // Find a deck where:
    // Room 1 (0..3): slot 0 = Weapon (rank 6..9), slot 1 = Monster rank M (7..9), slot 2 = Monster SAME rank M, slot 3 = any
    // Room 2 draw (4..6): at least one Monster with rank > M (<= 14)
    const targetK = findTargetRngState((d) => {
      const w = d[0];
      const m1 = d[1];
      const m2 = d[2];
      if (w.category !== "weapon" || w.rank < 6 || w.rank > 8) return false;
      if (m1.category !== "monster" || m2.category !== "monster") return false;
      if (m1.rank !== m2.rank || m1.rank < 7 || m1.rank > 9) return false;
      return [d[4], d[5], d[6]].some((c) => c.category === "monster" && c.rank > m1.rank);
    });

    const { sessionSigner, dungeonRun, expectedDeck } = await startDeterministicRun(targetK);
    const weapon = expectedDeck[0];
    const monster1 = expectedDeck[1];
    const monster2 = expectedDeck[2];

    // 1) Equip Diamond weapon from slot 0
    await program.methods
      .resolveCard(0, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    let state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.equippedWeaponRank).to.equal(weapon.rank);
    expect(state.weaponCeiling).to.equal(14);

    // 2) Slay Monster 1 (rank M <= 14) with weapon
    await program.methods
      .resolveCard(1, true)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    const expectedChip1 = Math.max(0, monster1.rank - weapon.rank) * 10;
    expect(state.hpTenths).to.equal(200 - expectedChip1);
    expect(state.weaponCeiling).to.equal(monster1.rank);

    // 3) Slay Monster 2 of EQUAL rank M using `<=` rule (monster_rank == weapon_ceiling succeeds!)
    await program.methods
      .resolveCard(2, true)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    const expectedChip2 = Math.max(0, monster2.rank - weapon.rank) * 10;
    const hpAfterRoom1 = 200 - expectedChip1 - expectedChip2;
    expect(state.hpTenths).to.equal(hpAfterRoom1);
    expect(state.weaponCeiling).to.equal(monster2.rank);
    expect(state.pendingSpoilsThreat).to.be.greaterThan(0);

    // Claim Room 1 spoils (choice 0 = Gold) to deal Room 2 cards
    await program.methods
      .claimSpoils(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    const higherSlot = state.room.findIndex(
      (c) => c !== null && decodeCard(c).category === "monster" && decodeCard(c).rank > monster2.rank
    );
    expect(higherSlot).to.be.greaterThanOrEqual(0);
    const higherMonster = decodeCard(state.room[higherSlot] as number);

    // 4) Attempting to use weapon on monster_rank > weapon_ceiling MUST fail
    let ceilingFailed = false;
    try {
      await program.methods
        .resolveCard(higherSlot, true)
        .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
        .signers([sessionSigner])
        .rpc();
    } catch (err: any) {
      ceilingFailed = true;
      expect(String(err)).to.include("WeaponCeilingExceeded");
    }
    expect(ceilingFailed).to.be.true;

    // 5) Fighting the same higher-rank monster barehanded (`use_weapon = false`) takes full rank*10 damage
    await program.methods
      .resolveCard(higherSlot, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.hpTenths).to.equal(Math.max(0, hpAfterRoom1 - higherMonster.rank * 10));
    expect(state.weaponCeiling).to.equal(monster2.rank); // Weapon ceiling untouched on barehanded hit
  });

  it("3. Enforces 2nd Heart potion discard rule (0 heal on second potion in same room)", async () => {
    // Room 1: slot 0 = Monster (rank 10..12), slot 1 = Potion (rank 3..5), slot 2 = Potion (rank 4..8)
    const targetK = findTargetRngState((d) => {
      return (
        d[0].category === "monster" &&
        d[0].rank >= 10 &&
        d[0].rank <= 12 &&
        d[1].category === "potion" &&
        d[1].rank >= 3 &&
        d[1].rank <= 5 &&
        d[2].category === "potion" &&
        d[2].rank >= 4
      );
    });

    const { sessionSigner, dungeonRun, expectedDeck } = await startDeterministicRun(targetK);
    const monster = expectedDeck[0];
    const potion1 = expectedDeck[1];

    // Take barehanded damage first so HP has plenty of missing health
    await program.methods
      .resolveCard(0, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    let state = await program.account.dungeonRun.fetch(dungeonRun);
    const damagedHp = 200 - monster.rank * 10;
    expect(state.hpTenths).to.equal(damagedHp);

    // First potion heals normally
    await program.methods
      .resolveCard(1, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    const hpAfterFirstPotion = Math.min(200, damagedHp + potion1.rank * 10);
    expect(state.hpTenths).to.equal(hpAfterFirstPotion);
    expect(state.potionUsedThisRoom).to.be.true;
    expect(state.firstPotionHealedTenths).to.equal(potion1.rank * 10);

    // Second potion in the SAME room must heal 0!
    await program.methods
      .resolveCard(2, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.hpTenths).to.equal(hpAfterFirstPotion);
    expect(state.firstPotionHealedTenths).to.equal(potion1.rank * 10);
  });

  it("4. Claims Medium Spoils Epic Potion and heals exact 10% echo across 3 consecutive rooms", async () => {
    // Helper to pick an ordered sequence of 3 non-potion slot indices for a room:
    // equips highest weapon first (if better than current ceiling/rank), then fights monsters in descending rank order!
    function planNonPotionRoom(
      roomCards: (number | null)[],
      startHp: number,
      startWepRank: number,
      startWepCeiling: number
    ): { steps: { slot: number; useWeapon: boolean }[]; endHp: number; damage: number } | null {
      const nonPotionSlots = [0, 1, 2, 3].filter(
        (s) => roomCards[s] !== null && decodeCard(roomCards[s] as number).category !== "potion"
      );
      if (nonPotionSlots.length < 3) return null;
      const chosen = nonPotionSlots.slice(0, 3);

      // Order: weapons first (ascending so best ends up equipped if multiple, unless monsters need fresh ceiling),
      // actually: best single weapon first, then monsters in DESCENDING rank order, then any leftover weapons!
      const weapons = chosen
        .filter((s) => decodeCard(roomCards[s] as number).category === "weapon")
        .sort((a, b) => decodeCard(roomCards[b] as number).rank - decodeCard(roomCards[a] as number));
      const monsters = chosen
        .filter((s) => decodeCard(roomCards[s] as number).category === "monster")
        .sort((a, b) => decodeCard(roomCards[b] as number).rank - decodeCard(roomCards[a] as number));

      const orderedSlots =
        weapons.length > 0 ? [weapons[0], ...monsters, ...weapons.slice(1)] : [...monsters];

      let hp = startHp;
      let wepRank = startWepRank;
      let wepCeiling = startWepCeiling;
      let totalDamage = 0;
      const steps: { slot: number; useWeapon: boolean }[] = [];

      for (const slot of orderedSlots) {
        const card = decodeCard(roomCards[slot] as number);
        if (card.category === "weapon") {
          wepRank = card.rank;
          wepCeiling = 14;
          steps.push({ slot, useWeapon: false });
        } else {
          const useWeapon = wepRank > 0 && card.rank <= wepCeiling;
          const dmg = useWeapon ? Math.max(0, card.rank - wepRank) * 10 : card.rank * 10;
          if (useWeapon) wepCeiling = card.rank;
          hp -= dmg;
          totalDamage += dmg;
          if (hp <= 0) return null;
          steps.push({ slot, useWeapon });
        }
      }
      return { steps, endHp: hp, damage: totalDamage };
    }

    const targetK = findTargetRngState((d) => {
      // Room 1 (resolve slots 0, 1, 2): d[0] = Monster(7..11) barehanded -> threat 12..16 (Medium),
      // d[1], d[2] = non-monsters
      if (d[0].category !== "monster" || d[0].rank < 7 || d[0].rank > 11) return false;
      if (d[1].category === "monster" || d[2].category === "monster") return false;

      let hp = 200 - d[0].rank * 10;
      let wepRank = 0;
      let wepCeiling = 0;
      let potionUsedR1 = false;
      for (const idx of [1, 2]) {
        if (d[idx].category === "potion" && !potionUsedR1) {
          hp = Math.min(200, hp + d[idx].rank * 10);
          potionUsedR1 = true;
        } else if (d[idx].category === "weapon") {
          wepRank = d[idx].rank;
          wepCeiling = 14;
        }
      }

      // Room 2: slots [d[4], d[5], d[6], d[3]]
      // Require d[4] = Monster(6..10) barehanded, d[5] = Potion(rank 3..7, fractional tenths echo!), d[6] = Weapon(>= 7)
      if (d[4].category !== "monster" || d[4].rank < 6 || d[4].rank > 10) return false;
      if (d[5].category !== "potion" || d[5].rank < 3 || d[5].rank > 7) return false;
      if (d[6].category !== "weapon" || d[6].rank < 7) return false;

      hp -= d[4].rank * 10;
      if (hp <= 0) return false;
      hp = Math.min(200, hp + d[5].rank * 10);
      if (hp >= 185 || hp < 90) return false; // Plenty of HP and headroom below 200 for echo #1!
      const echo = d[5].rank;
      hp = Math.min(200, hp + echo);
      wepRank = d[6].rank;
      wepCeiling = 14;

      // Simulate Room 3: slots [d[7].id, d[8].id, d[9].id, d[3].id]
      const room3: (number | null)[] = [d[7].id, d[8].id, d[9].id, d[3].id];
      const plan3 = planNonPotionRoom(room3, hp, wepRank, wepCeiling);
      if (!plan3 || plan3.endHp >= 190) return false;
      let hpAfter3 = Math.min(200, plan3.endHp + echo);

      // Remaining carryover from Room 3 + d[10..12]
      const resolvedSet3 = new Set(plan3.steps.map((s) => s.slot));
      const room4: (number | null)[] = [0, 1, 2, 3].map((s) => (resolvedSet3.has(s) ? null : room3[s]));
      let nextIdx = 10;
      for (let s = 0; s < 4; s++) {
        if (room4[s] === null) room4[s] = d[nextIdx++].id;
      }

      // Track weapon state after Room 3
      let r3Wep = wepRank;
      let r3Ceil = wepCeiling;
      for (const st of plan3.steps) {
        const c = decodeCard(room3[st.slot] as number);
        if (c.category === "weapon") {
          r3Wep = c.rank;
          r3Ceil = 14;
        } else if (st.useWeapon) {
          r3Ceil = c.rank;
        }
      }

      const plan4 = planNonPotionRoom(room4, hpAfter3, r3Wep, r3Ceil);
      return plan4 !== null && plan4.endHp < 190;
    });

    const { sessionSigner, dungeonRun, expectedDeck } = await startDeterministicRun(targetK);
    const r1Monster = expectedDeck[0];
    const r2Potion = expectedDeck[5];
    const echoPerRoom = Math.floor((r2Potion.rank * 10) / 10);

    // --- ROOM 1: Generate Medium Threat (12 <= T <= 24) ---
    await program.methods
      .resolveCard(0, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();
    await program.methods
      .resolveCard(1, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();
    await program.methods
      .resolveCard(2, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    let state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.pendingSpoilsThreat).to.equal(r1Monster.rank + 5);
    expect(state.pendingSpoilsThreat).to.be.within(12, 24);

    // Claim Medium Spoils Choice 1 -> +1 Epic Health Potion
    await program.methods
      .claimSpoils(1)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.epicPotionsInventory).to.equal(1);

    // --- ROOM 2: Activate Epic Health Potion, drink Heart, equip Weapon, and verify Echo #1 ---
    await program.methods
      .useEpicPotion()
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.epicPotionsInventory).to.equal(0);
    expect(state.epicPotionBuffRoomsLeft).to.equal(3);

    await program.methods
      .resolveCard(0, false) // d[4] Monster barehanded
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();
    const hpBeforePotion = (await program.account.dungeonRun.fetch(dungeonRun)).hpTenths;

    await program.methods
      .resolveCard(1, false) // d[5] Heart Potion
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    const hpAfterDirectPotion = Math.min(200, hpBeforePotion + r2Potion.rank * 10);
    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.hpTenths).to.equal(hpAfterDirectPotion);

    // Resolve 3rd card of Room 2 (d[6] Weapon >= 7) -> triggers Room 2 completion + Echo #1!
    await program.methods
      .resolveCard(2, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.hpTenths).to.equal(Math.min(200, hpAfterDirectPotion + echoPerRoom));
    expect(state.epicPotionBuffRoomsLeft).to.equal(2);

    await program.methods
      .claimSpoils(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    async function executePlannedNonPotionRoom(): Promise<{ hpBefore: number; damage: number; hpAfter: number }> {
      const s = await program.account.dungeonRun.fetch(dungeonRun);
      const plan = planNonPotionRoom(
        s.room,
        s.hpTenths,
        s.equippedWeaponRank,
        s.weaponCeiling
      );
      expect(plan).to.not.be.null;
      for (const step of plan!.steps) {
        await program.methods
          .resolveCard(step.slot, step.useWeapon)
          .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
          .signers([sessionSigner])
          .rpc();
      }
      const after = (await program.account.dungeonRun.fetch(dungeonRun)).hpTenths;
      return { hpBefore: s.hpTenths, damage: plan!.damage, hpAfter: after };
    }

    // --- ROOM 3: No potions drunk -> Echo #2 heals exact `echoPerRoom` tenths ---
    const r3 = await executePlannedNonPotionRoom();
    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(r3.hpAfter).to.equal(Math.min(200, r3.hpBefore - r3.damage + echoPerRoom));
    expect(state.epicPotionBuffRoomsLeft).to.equal(1);

    await program.methods
      .claimSpoils(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    // --- ROOM 4: No potions drunk -> Echo #3 heals exact `echoPerRoom` tenths & expires buff ---
    const r4 = await executePlannedNonPotionRoom();
    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(r4.hpAfter).to.equal(Math.min(200, r4.hpBefore - r4.damage + echoPerRoom));
    expect(state.epicPotionBuffRoomsLeft).to.equal(0);
  });

  it("5. Claims High Threat Spoils (T >= 25), emits LegendarySkinUnlocked, and allows 1-time weapon ceiling bypass", async () => {
    const targetK = findTargetRngState((d) => {
      if (d[0].category !== "monster" || d[1].category !== "monster") return false;
      const sumRanks = d[0].rank + d[1].rank;
      if (sumRanks < 15 || sumRanks > 19) return false; // Threat = sumRanks + 10 >= 25, Damage = sumRanks*10 <= 190 < 200!
      if (d[2].category !== "weapon" || d[2].rank < 7) return false;
      if (d[4].category !== "monster" || d[4].rank > 6) return false;
      if (d[5].category !== "monster" || d[5].rank <= d[4].rank || d[5].rank > 9) return false;
      return true;
    });

    const { sessionSigner, dungeonRun, expectedDeck } = await startDeterministicRun(targetK);

    // Fight two monsters barehanded for High Threat (T >= 26) + equip strong weapon (rank >= 8)
    await program.methods
      .resolveCard(0, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();
    await program.methods
      .resolveCard(1, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();
    await program.methods
      .resolveCard(2, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    let state = await program.account.dungeonRun.fetch(dungeonRun);
    const expectedThreat = expectedDeck[0].rank + 5 + (expectedDeck[1].rank + 5);
    expect(state.pendingSpoilsThreat).to.equal(expectedThreat);
    expect(state.pendingSpoilsThreat).to.be.greaterThanOrEqual(25);

    // Claim High Spoils Choice 2 -> Unlock Legendary Weapon Skin + 1 Forged Edge charge
    let unlockedEventPlayer: string | null = null;
    let unlockedEventThreat = 0;
    const listenerId = program.addEventListener("legendarySkinUnlocked", (event: any) => {
      unlockedEventPlayer = event.player.toBase58();
      unlockedEventThreat = event.threatScore;
    });

    try {
      await program.methods
        .claimSpoils(2)
        .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
        .signers([sessionSigner])
        .rpc();
    } finally {
      await program.removeEventListener(listenerId);
    }

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.hasLegendarySkin).to.be.true;
    expect(state.legendaryForgedEdgeCharges).to.equal(1);
    if (unlockedEventPlayer !== null) {
      expect(unlockedEventPlayer).to.equal(state.player.toBase58());
      expect(unlockedEventThreat).to.equal(expectedThreat);
    }

    // Room 2: Slot 0 is low monster (rank 3..5) -> degrades weaponCeiling to 3..5
    await program.methods
      .resolveCard(0, true)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.weaponCeiling).to.equal(expectedDeck[4].rank);
    expect(state.legendaryForgedEdgeCharges).to.equal(1);

    // Slot 1 is high monster (rank 7..9 > weaponCeiling 3..5) -> SUCCEEDS via Legendary Forged Edge charge!
    await program.methods
      .resolveCard(1, true)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.legendaryForgedEdgeCharges).to.equal(0);
    expect(state.weaponCeiling).to.equal(expectedDeck[5].rank);
  });
});
