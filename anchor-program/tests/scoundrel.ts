import * as anchor from "@anchor-lang/core";
import { BN, Program } from "@anchor-lang/core";
import { expect } from "chai";
import { Scoundrel } from "../target/types/scoundrel";

const U64_MASK = 0xffffffffffffffffn;

const DEFAULT_QUEUE = new anchor.web3.PublicKey("Cuj97ggrhhidhbu39TijNVqE74xvKJ69gDervRUXAxGh");
const VRF_PROGRAM_ID = new anchor.web3.PublicKey("Vrf1RNUjXmQGjmQrQLvJHs9SNkvDJEsRVFPkfSQUwGz");

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

function bigintToRandomness32(seed: bigint, chunk1 = 0n, chunk2 = 0n, chunk3 = 0n): number[] {
  const bytes = new Array<number>(32).fill(0);
  const chunks = [seed & U64_MASK, chunk1 & U64_MASK, chunk2 & U64_MASK, chunk3 & U64_MASK];
  for (let c = 0; c < 4; c++) {
    let v = chunks[c];
    for (let b = 0; b < 8; b++) {
      bytes[c * 8 + b] = Number(v & 0xffn);
      v >>= 8n;
    }
  }
  return bytes;
}

function readU64Le(bytes: number[], offset: number): bigint {
  let val = 0n;
  for (let b = 7; b >= 0; b--) {
    val = (val << 8n) | BigInt(bytes[offset + b] & 0xff);
  }
  return val & U64_MASK;
}

function simulateShuffleFromRandomness(randomness: number[]): number[] {
  const deck = Array.from({ length: 44 }, (_, i) => i);
  const s0 = readU64Le(randomness, 0);
  const s1 = readU64Le(randomness, 8);
  const s2 = readU64Le(randomness, 16);
  const s3 = readU64Le(randomness, 24);

  const init =
    (s0 ^
      ((s1 * 0x9e3779b97f4a7c15n) & U64_MASK) ^
      ((s2 * 0xbf58476d1ce4e5b9n) & U64_MASK) ^
      ((s3 * 0x94d049bb133111ebn) & U64_MASK)) &
    U64_MASK;

  const state = { value: init };
  for (let i = 43; i >= 1; i--) {
    const randVal = splitmix64(state);
    const j = Number(randVal % BigInt(i + 1));
    const tmp = deck[i];
    deck[i] = deck[j];
    deck[j] = tmp;
  }
  return deck;
}

function simulateShuffle(rngSeed: bigint): number[] {
  return simulateShuffleFromRandomness(bigintToRandomness32(rngSeed));
}

function findTargetRngState(predicate: (deck: DecodedCard[]) => boolean): bigint {
  for (let k = 1n; k < 500_000n; k++) {
    const raw = simulateShuffle(k);
    const decoded = raw.map(decodeCard);
    if (predicate(decoded)) {
      return k;
    }
  }
  throw new Error("Could not find matching deterministic deck within search window");
}

describe("scoundrel", () => {
  const provider = anchor.AnchorProvider.env();
  anchor.setProvider(provider);

  const program = anchor.workspace.scoundrel as Program<Scoundrel>;
  const { Keypair, SystemProgram, PublicKey, Transaction, LAMPORTS_PER_SOL } = anchor.web3;

  function deriveDungeonRunPda(playerPubkey: anchor.web3.PublicKey): anchor.web3.PublicKey {
    const [pda] = PublicKey.findProgramAddressSync(
      [Buffer.from("dungeon_run"), playerPubkey.toBuffer()],
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
    extraRandomnessChunks: [bigint, bigint, bigint] = [0n, 0n, 0n]
  ): Promise<{
    player: anchor.web3.Keypair;
    sessionSigner: anchor.web3.Keypair;
    dungeonRun: anchor.web3.PublicKey;
    randomness: number[];
    expectedDeck: DecodedCard[];
  }> {
    const randomness = bigintToRandomness32(
      targetRngState,
      extraRandomnessChunks[0],
      extraRandomnessChunks[1],
      extraRandomnessChunks[2]
    );
    const expectedRaw = simulateShuffleFromRandomness(randomness);
    const expectedDeck = expectedRaw.map(decodeCard);

    const player = Keypair.generate();
    const sessionSigner = Keypair.generate();
    // Only fund player; start_run automatically tops up sessionSigner with 0.005 SOL!
    await fundKeypairs(player);

    const dungeonRun = deriveDungeonRunPda(player.publicKey);

    await program.methods
      .startRun(42)
      .accountsPartial({
        player: player.publicKey,
        sessionSigner: sessionSigner.publicKey,
        dungeonRun,
        oracleQueue: DEFAULT_QUEUE,
        vrfProgram: VRF_PROGRAM_ID,
      })
      .signers([player])
      .rpc();

    const awaitingState = await program.account.dungeonRun.fetch(dungeonRun);
    expect(awaitingState.runStatus).to.equal(3); // RUN_STATUS_AWAITING_VRF

    await program.methods
      .callbackStartRun(randomness)
      .accountsPartial({
        vrfProgramIdentity: player.publicKey,
        dungeonRun,
      })
      .signers([player])
      .rpc();

    const activeState = await program.account.dungeonRun.fetch(dungeonRun);
    expect(activeState.runStatus).to.equal(0); // RUN_STATUS_ACTIVE
    expect(Array.from(activeState.vrfSeed)).to.deep.equal(randomness);

    const roomIds = activeState.room.map((c) => (c === null ? -1 : c));
    expect(roomIds).to.deep.equal(expectedRaw.slice(0, 4));

    return { player, sessionSigner, dungeonRun, randomness, expectedDeck };
  }

  it("1. Initializes via MagicBlock VRF (start_run -> callback_start_run), auto-funds session_signer, and handles flee_room", async () => {
    const player = Keypair.generate();
    const sessionSigner = Keypair.generate();
    const rogueSigner = Keypair.generate();
    // Do NOT pre-fund sessionSigner so we verify start_run's 0.005 SOL CPI transfer
    await fundKeypairs(player, rogueSigner);

    const dungeonRun = deriveDungeonRunPda(player.publicKey);

    await program.methods
      .startRun(7)
      .accountsPartial({
        player: player.publicKey,
        sessionSigner: sessionSigner.publicKey,
        dungeonRun,
        oracleQueue: DEFAULT_QUEUE,
        vrfProgram: VRF_PROGRAM_ID,
      })
      .signers([player])
      .rpc();

    const sessionBalance = await provider.connection.getBalance(sessionSigner.publicKey);
    expect(sessionBalance).to.equal(5_000_000);

    const awaiting = await program.account.dungeonRun.fetch(dungeonRun);
    expect(awaiting.runStatus).to.equal(3); // RUN_STATUS_AWAITING_VRF

    // Actions while awaiting VRF must be rejected with RunNotActive
    let preVrfRejected = false;
    try {
      await program.methods
        .fleeRoom()
        .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
        .signers([sessionSigner])
        .rpc();
    } catch (err: any) {
      preVrfRejected = true;
      expect(String(err)).to.include("RunNotActive");
    }
    expect(preVrfRejected).to.be.true;

    // Unauthorized callback signer must be rejected
    const vrfBytes = bigintToRandomness32(42n, 101n, 202n, 303n);
    let rogueVrfRejected = false;
    try {
      await program.methods
        .callbackStartRun(vrfBytes)
        .accountsPartial({
          vrfProgramIdentity: rogueSigner.publicKey,
          dungeonRun,
        })
        .signers([rogueSigner])
        .rpc();
    } catch (err: any) {
      rogueVrfRejected = true;
      expect(String(err)).to.include("UnauthorizedVrfCallback");
    }
    expect(rogueVrfRejected).to.be.true;

    // Authorized VRF fulfillment transitions run_status to Active (0) and deals 4 cards
    await program.methods
      .callbackStartRun(vrfBytes)
      .accountsPartial({
        vrfProgramIdentity: player.publicKey,
        dungeonRun,
      })
      .signers([player])
      .rpc();

    // Duplicate callback must be rejected (idempotency)
    let duplicateCallbackRejected = false;
    try {
      await program.methods
        .callbackStartRun(vrfBytes)
        .accountsPartial({
          vrfProgramIdentity: player.publicKey,
          dungeonRun,
        })
        .signers([player])
        .rpc();
    } catch (err: any) {
      duplicateCallbackRejected = true;
      expect(String(err)).to.include("NotAwaitingVrf");
    }
    expect(duplicateCallbackRejected).to.be.true;

    const initial = await program.account.dungeonRun.fetch(dungeonRun);
    expect(initial.hpTenths).to.equal(200);
    expect(initial.maxHpTenths).to.equal(200);
    expect(initial.deckLen).to.equal(40);
    expect(initial.deckHead).to.equal(4);
    expect(initial.runStatus).to.equal(0);
    expect(Array.from(initial.vrfSeed)).to.deep.equal(vrfBytes);

    // Verify all 44 cards (4 room + 40 remaining deck) form a valid permutation of 0..43
    const initialRoom = initial.room.map((x) => x as number);
    expect(initialRoom.every((c) => c !== null && c >= 0 && c <= 43)).to.be.true;
    const remainingDeck: number[] = [];
    for (let i = 0; i < initial.deckLen; i++) {
      remainingDeck.push(initial.deck[(initial.deckHead + i) % 44]);
    }
    const allCardsSorted = [...initialRoom, ...remainingDeck].sort((a, b) => a - b);
    expect(allCardsSorted).to.deep.equal(Array.from({ length: 44 }, (_, i) => i));

    // Unauthorized rogue signer must be rejected on gameplay turns
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

    // Ephemeral sessionSigner flees room using its auto-funded 0.005 SOL without player popup
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

    await program.methods
      .resolveCard(0, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    let state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.equippedWeaponRank).to.equal(weapon.rank);
    expect(state.weaponCeiling).to.equal(14);

    await program.methods
      .resolveCard(1, true)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    const expectedChip1 = Math.max(0, monster1.rank - weapon.rank) * 10;
    expect(state.hpTenths).to.equal(200 - expectedChip1);
    expect(state.weaponCeiling).to.equal(monster1.rank);

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

    await program.methods
      .resolveCard(higherSlot, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.hpTenths).to.equal(Math.max(0, hpAfterRoom1 - higherMonster.rank * 10));
    expect(state.weaponCeiling).to.equal(monster2.rank);
  });

  it("3. Enforces 2nd Heart potion discard rule (0 heal on second potion in same room)", async () => {
    const targetK = findTargetRngState((d) => {
      if (d[0].category !== "monster" || d[0].rank < 11) return false;
      if (d[1].category !== "potion" || d[1].rank > 6) return false;
      if (d[2].category !== "potion") return false;
      return d[0].rank > d[1].rank + 2;
    });

    const { sessionSigner, dungeonRun, expectedDeck } = await startDeterministicRun(targetK);
    const m = expectedDeck[0];
    const p1 = expectedDeck[1];

    await program.methods
      .resolveCard(0, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    let state = await program.account.dungeonRun.fetch(dungeonRun);
    const hpAfterMonster = 200 - m.rank * 10;
    expect(state.hpTenths).to.equal(hpAfterMonster);

    await program.methods
      .resolveCard(1, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    const hpAfterFirstPotion = Math.min(200, hpAfterMonster + p1.rank * 10);
    expect(state.hpTenths).to.equal(hpAfterFirstPotion);
    expect(state.potionUsedThisRoom).to.be.true;
    expect(state.firstPotionHealedTenths).to.equal(p1.rank * 10);

    await program.methods
      .resolveCard(2, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.hpTenths).to.equal(hpAfterFirstPotion);
    expect(state.firstPotionHealedTenths).to.equal(p1.rank * 10);
  });

  it("4. Claims Medium Spoils Epic Potion and heals exact 10% echo across 3 consecutive rooms", async () => {
    interface RoomPlan {
      steps: { slot: number; useWeapon: boolean }[];
      survivingCard: number;
      nextHp: number;
      nextWeapon: number;
      nextCeiling: number;
      damage: number;
    }

    function planNonPotionRoom(
      roomSlots: (number | null)[],
      hpTenths: number,
      weaponRank: number,
      weaponCeiling: number
    ): RoomPlan | null {
      for (let skipSlot = 0; skipSlot < 4; skipSlot++) {
        const pickSlots = [0, 1, 2, 3].filter((s) => s !== skipSlot);
        if (pickSlots.some((s) => roomSlots[s] === null || decodeCard(roomSlots[s]!).category === "potion")) {
          continue;
        }
        const perms = [
          [pickSlots[0], pickSlots[1], pickSlots[2]],
          [pickSlots[0], pickSlots[2], pickSlots[1]],
          [pickSlots[1], pickSlots[0], pickSlots[2]],
          [pickSlots[1], pickSlots[2], pickSlots[0]],
          [pickSlots[2], pickSlots[0], pickSlots[1]],
          [pickSlots[2], pickSlots[1], pickSlots[0]],
        ];

        for (const order of perms) {
          let w = weaponRank;
          let ceil = weaponCeiling;
          let dmg = 0;
          const steps: { slot: number; useWeapon: boolean }[] = [];

          for (const slot of order) {
            const c = decodeCard(roomSlots[slot]!);
            if (c.category === "weapon") {
              w = c.rank;
              ceil = 14;
              steps.push({ slot, useWeapon: false });
            } else if (c.category === "monster") {
              if (w > 0 && c.rank <= ceil) {
                dmg += Math.max(0, c.rank - w) * 10;
                ceil = c.rank;
                steps.push({ slot, useWeapon: true });
              } else {
                dmg += c.rank * 10;
                steps.push({ slot, useWeapon: false });
              }
            }
          }

          if (hpTenths - dmg >= 30) {
            return {
              steps,
              survivingCard: roomSlots[skipSlot]!,
              nextHp: hpTenths - dmg,
              nextWeapon: w,
              nextCeiling: ceil,
              damage: dmg,
            };
          }
        }
      }
      return null;
    }

    const targetK = findTargetRngState((d) => {
      if (d[0].category !== "monster" || d[0].rank < 7 || d[0].rank > 13) return false;
      if (d[1].category !== "weapon") return false;
      if (d[2].category !== "potion") return false;

      if (d[4].category !== "monster" || d[4].rank < 9 || d[4].rank > 13) return false;
      if (d[5].category !== "potion" || d[5].rank >= d[4].rank - 1) return false;
      if (d[6].category !== "weapon" || d[6].rank < 7) return false;

      const echo = d[5].rank;
      const r2StartHp = Math.min(200, 200 - d[0].rank * 10 + d[2].rank * 10);
      const r2AfterMonster = r2StartHp - d[4].rank * 10;
      if (r2AfterMonster <= 0) return false;
      const r2EndHp = Math.min(200, r2AfterMonster + d[5].rank * 10 + echo);
      if (r2EndHp >= 195) return false;

      const room3: (number | null)[] = [d[7].id, d[8].id, d[9].id, d[3].id];
      const p3 = planNonPotionRoom(room3, r2EndHp, d[6].rank, 14);
      if (!p3) return false;
      const r3EndHp = Math.min(200, p3.nextHp + echo);

      const room4: (number | null)[] = [d[10].id, d[11].id, d[12].id, p3.survivingCard];
      const p4 = planNonPotionRoom(room4, r3EndHp, p3.nextWeapon, p3.nextCeiling);
      if (!p4) return false;

      return true;
    });

    const { sessionSigner, dungeonRun, expectedDeck } = await startDeterministicRun(targetK);

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
    expect(state.pendingSpoilsThreat).to.equal(expectedDeck[0].rank + 5);
    expect(state.pendingSpoilsThreat).to.be.within(12, 24);

    await program.methods
      .claimSpoils(1)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.epicPotionsInventory).to.equal(1);

    await program.methods
      .useEpicPotion()
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.epicPotionsInventory).to.equal(0);
    expect(state.epicPotionBuffRoomsLeft).to.equal(3);

    const r2Monster = expectedDeck[4];
    const r2Potion = expectedDeck[5];
    const echoPerRoom = r2Potion.rank;

    await program.methods
      .resolveCard(0, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    const hpBeforePotion = (await program.account.dungeonRun.fetch(dungeonRun)).hpTenths;

    await program.methods
      .resolveCard(1, false)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    const hpAfterDirectPotion = Math.min(200, hpBeforePotion + r2Potion.rank * 10);
    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.hpTenths).to.equal(hpAfterDirectPotion);

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

    const r3 = await executePlannedNonPotionRoom();
    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(r3.hpAfter).to.equal(Math.min(200, r3.hpBefore - r3.damage + echoPerRoom));
    expect(state.epicPotionBuffRoomsLeft).to.equal(1);

    await program.methods
      .claimSpoils(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    const r4 = await executePlannedNonPotionRoom();
    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(r4.hpAfter).to.equal(Math.min(200, r4.hpBefore - r4.damage + echoPerRoom));
    expect(state.epicPotionBuffRoomsLeft).to.equal(0);
  });

  it("5. Claims High Threat Spoils (T >= 25) and enforces disabled Legendary Weapon gate", async () => {
    const targetK = findTargetRngState((d) => {
      if (d[0].category !== "monster" || d[1].category !== "monster") return false;
      const sumRanks = d[0].rank + d[1].rank;
      if (sumRanks < 15 || sumRanks > 19) return false;
      if (d[2].category !== "weapon" || d[2].rank < 7) return false;
      if (d[4].category !== "monster" || d[4].rank > 6) return false;
      if (d[5].category !== "monster" || d[5].rank <= d[4].rank || d[5].rank > 9) return false;
      return true;
    });

    const { sessionSigner, dungeonRun, expectedDeck } = await startDeterministicRun(targetK);

    let state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.hasLegendarySkin).to.be.false;
    expect(state.legendaryForgedEdgeCharges).to.equal(0);

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

    state = await program.account.dungeonRun.fetch(dungeonRun);
    const expectedThreat = expectedDeck[0].rank + 5 + (expectedDeck[1].rank + 5);
    expect(state.pendingSpoilsThreat).to.equal(expectedThreat);
    expect(state.pendingSpoilsThreat).to.be.greaterThanOrEqual(25);

    let legendaryBlocked = false;
    try {
      await program.methods
        .claimSpoils(2)
        .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
        .signers([sessionSigner])
        .rpc();
    } catch (err: any) {
      legendaryBlocked = true;
      expect(String(err)).to.include("InvalidSpoilsChoice");
    }
    expect(legendaryBlocked).to.be.true;

    await program.methods
      .claimSpoils(1)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.epicPotionsInventory).to.equal(1);
    expect(state.unclaimedGold).to.equal(25);
    expect(state.hasLegendarySkin).to.be.false;
    expect(state.legendaryForgedEdgeCharges).to.equal(0);

    await program.methods
      .resolveCard(0, true)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.weaponCeiling).to.equal(expectedDeck[4].rank);

    let ceilingRejected = false;
    try {
      await program.methods
        .resolveCard(1, true)
        .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
        .signers([sessionSigner])
        .rpc();
    } catch (err: any) {
      ceilingRejected = true;
      expect(String(err)).to.include("WeaponCeilingExceeded");
    }
    expect(ceilingRejected).to.be.true;
  });

  it("6. Verifies Mini Marketplace (Ofe 60g & Rusty Shield 45g), 3-room buff countdown, and VRF combat rolls", async () => {
    // Find a deck where:
    // Room 1 (0..3): two monsters whose sum of ranks is 15..18 (High Threat >= 25), plus a Heart potion >= 8 to heal back up
    // Room 2 (4..6): non-lethal cards (e.g. weapon/potion/low monster) so we can clear 3 rooms and test combat rolls
    const targetK = findTargetRngState((d) => {
      if (d[0].category !== "monster" || d[1].category !== "monster") return false;
      const sum1 = d[0].rank + d[1].rank;
      if (sum1 < 15 || sum1 > 17) return false;
      if (d[2].category !== "potion" || d[2].rank < 9) return false;
      // Room 2 cards (4, 5, 6): slot 0 = monster (rank 4..8), slot 1 = monster (rank 4..8), slot 2 = weapon or potion
      if (d[4].category !== "monster" || d[4].rank < 4 || d[4].rank > 7) return false;
      if (d[5].category !== "monster" || d[5].rank < 4 || d[5].rank > 7) return false;
      if (d[6].category === "monster") return false;
      // Room 3 cards (7, 8, 9): at least 2 non-monsters or low monsters so we can clear Room 3 & Room 4 easily
      const r3Monsters = [d[7], d[8], d[9]].filter((c) => c.category === "monster");
      if (r3Monsters.reduce((acc, c) => acc + c.rank, 0) > 10) return false;
      const r4Monsters = [d[10], d[11], d[12]].filter((c) => c.category === "monster");
      if (r4Monsters.reduce((acc, c) => acc + c.rank, 0) > 10) return false;
      return true;
    });

    const { sessionSigner, dungeonRun } = await startDeterministicRun(targetK);

    // Clear Room 1 for High Threat Spoils (+85 Gold)
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

    // Claim +85 Gold (Choice 0)
    await program.methods
      .claimSpoils(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    let state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.unclaimedGold).to.equal(85);

    // Buy Rusty Shield (item_id = 1, 45 Gold) -> 40 Gold remaining
    await program.methods
      .buyItem(1)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.unclaimedGold).to.equal(40);
    expect(state.rustyShieldInventory).to.equal(1);

    // Buying Ofe (60 Gold) with only 40 Gold must fail with InsufficientGold
    let insufficientFailed = false;
    try {
      await program.methods
        .buyItem(0)
        .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
        .signers([sessionSigner])
        .rpc();
    } catch (err: any) {
      insufficientFailed = true;
      expect(String(err)).to.include("InsufficientGold");
    }
    expect(insufficientFailed).to.be.true;

    // Activate Rusty Shield (`useMarketplaceItem(1)`) -> sets rustyShieldBuffRoomsLeft = 3
    await program.methods
      .useMarketplaceItem(1)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.rustyShieldInventory).to.equal(0);
    expect(state.rustyShieldBuffRoomsLeft).to.equal(3);

    // Clear Room 2 (slots 0, 1, 2) -> fights two monsters barehanded with Rusty Shield active (increments combatRngNonce)
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

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.combatRngNonce.toNumber()).to.equal(2); // 2 Rusty Shield VRF rolls executed
    expect(state.rustyShieldBuffRoomsLeft).to.equal(2); // Decremented 3 -> 2 at end of Room 2!

    // Claim Room 2 Spoils (+40 Gold from Medium Threat) -> 40 + 40 = 80 Gold!
    await program.methods
      .claimSpoils(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.unclaimedGold).to.be.greaterThanOrEqual(60);

    // Now purchase Ofe Charm (item_id = 0, 60 Gold) and activate it (`ofeBuffRoomsLeft = 3`)
    await program.methods
      .buyItem(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();
    await program.methods
      .useMarketplaceItem(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.ofeInventory).to.equal(0);
    expect(state.ofeBuffRoomsLeft).to.equal(3);

    // Clear Room 3 (slots 0, 1, 2) -> verifies both Ofe (3 -> 2) and Rusty Shield (2 -> 1) tick down!
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

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.ofeBuffRoomsLeft).to.equal(2);
    expect(state.rustyShieldBuffRoomsLeft).to.equal(1);

    await program.methods
      .claimSpoils(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    // Clear Room 4 (slots 0, 1, 2) -> Ofe ticks 2 -> 1, Rusty Shield ticks 1 -> 0 (expires!)
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

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.ofeBuffRoomsLeft).to.equal(1);
    expect(state.rustyShieldBuffRoomsLeft).to.equal(0);
  });

  it("7. Mints accumulated unclaimed_gold into real SPL Gold tokens in the player's Associated Token Account", async () => {
    const SPL_TOKEN_PROGRAM_ID = new PublicKey("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA");
    const ASSOCIATED_TOKEN_PROGRAM_ID = new PublicKey("ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL");

    const targetK = findTargetRngState((d) => {
      if (d[0].category !== "monster" || d[1].category !== "monster") return false;
      const sum1 = d[0].rank + d[1].rank;
      if (sum1 < 15 || sum1 > 17) return false;
      return d[2].category === "potion" && d[2].rank >= 8;
    });

    const { player, sessionSigner, dungeonRun } = await startDeterministicRun(targetK);

    // Clear Room 1 and claim High Threat Spoils (+85 Gold)
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
    await program.methods
      .claimSpoils(0)
      .accountsPartial({ signer: sessionSigner.publicKey, dungeonRun })
      .signers([sessionSigner])
      .rpc();

    let state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.unclaimedGold).to.equal(85);

    const [goldMint] = PublicKey.findProgramAddressSync(
      [Buffer.from("scoundrel_gold_mint")],
      program.programId
    );
    const [playerGoldAta] = PublicKey.findProgramAddressSync(
      [player.publicKey.toBuffer(), SPL_TOKEN_PROGRAM_ID.toBuffer(), goldMint.toBuffer()],
      ASSOCIATED_TOKEN_PROGRAM_ID
    );

    await program.methods
      .claimGoldTokens()
      .accountsPartial({
        payer: player.publicKey,
        player: player.publicKey,
        dungeonRun,
        goldMint,
        playerGoldAta,
        tokenProgram: SPL_TOKEN_PROGRAM_ID,
        associatedTokenProgram: ASSOCIATED_TOKEN_PROGRAM_ID,
      })
      .signers([player])
      .rpc();

    state = await program.account.dungeonRun.fetch(dungeonRun);
    expect(state.unclaimedGold).to.equal(0);

    const tokenBalance = await provider.connection.getTokenAccountBalance(playerGoldAta);
    expect(tokenBalance.value.amount).to.equal("85");
    expect(tokenBalance.value.decimals).to.equal(0);

    // Calling claimGoldTokens again with 0 unclaimed_gold must fail with NoGoldToClaim
    let duplicateClaimFailed = false;
    try {
      await program.methods
        .claimGoldTokens()
        .accountsPartial({
          payer: player.publicKey,
          player: player.publicKey,
          dungeonRun,
          goldMint,
          playerGoldAta,
          tokenProgram: SPL_TOKEN_PROGRAM_ID,
          associatedTokenProgram: ASSOCIATED_TOKEN_PROGRAM_ID,
        })
        .signers([player])
        .rpc();
    } catch (err: any) {
      duplicateClaimFailed = true;
      expect(String(err)).to.include("NoGoldToClaim");
    }
    expect(duplicateClaimFailed).to.be.true;
  });
});
