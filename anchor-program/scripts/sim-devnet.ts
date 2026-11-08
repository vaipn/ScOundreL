// Read-only devnet diagnostics: simulates (sigVerify=false, nothing is signed or sent) the instructions the
// Unity client sends, using ANY funded pubkey as the player. Usage:
//   npx ts-node scripts/sim-devnet.ts [playerPubkey]
import * as anchor from "@anchor-lang/core";
import { Program } from "@anchor-lang/core";
import fs from "fs";
import os from "os";
import path from "path";

const DEFAULT_QUEUE = new anchor.web3.PublicKey("Cuj97ggrhhidhbu39TijNVqE74xvKJ69gDervRUXAxGh");
const VRF_PROGRAM_ID = new anchor.web3.PublicKey("Vrf1RNUjXmQGjmQrQLvJHs9SNkvDJEsRVFPkfSQUwGz");

async function simulate(conn: anchor.web3.Connection, label: string, ixs: anchor.web3.TransactionInstruction[], payer: anchor.web3.PublicKey) {
  const { blockhash } = await conn.getLatestBlockhash();
  const msg = new anchor.web3.TransactionMessage({ payerKey: payer, recentBlockhash: blockhash, instructions: ixs }).compileToV0Message();
  const res = await conn.simulateTransaction(new anchor.web3.VersionedTransaction(msg), { sigVerify: false, replaceRecentBlockhash: true });
  console.log(`\n=== ${label} ===`);
  console.log("err:", JSON.stringify(res.value.err), " unitsConsumed:", res.value.unitsConsumed);
  for (const l of res.value.logs ?? []) console.log("  " + l);
}

(async () => {
  const conn = new anchor.web3.Connection("https://api.devnet.solana.com", "confirmed");
  const idl = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "target", "idl", "scoundrel.json"), "utf8"));
  const provider = new anchor.AnchorProvider(conn, new anchor.Wallet(anchor.web3.Keypair.generate()), {});
  const program = new Program(idl, provider) as Program<any>;

  const keyfile = path.join(os.homedir(), ".config", "solana", "id.json");
  const player = new anchor.web3.PublicKey(process.argv[2] ?? anchor.web3.Keypair.fromSecretKey(Uint8Array.from(JSON.parse(fs.readFileSync(keyfile, "utf8")))).publicKey.toBase58());
  const session = anchor.web3.Keypair.generate().publicKey;
  const [run] = anchor.web3.PublicKey.findProgramAddressSync([Buffer.from("dungeon_run"), player.toBuffer()], program.programId);

  console.log("player:", player.toBase58(), " run PDA:", run.toBase58());
  const acct = await conn.getAccountInfo(run);
  console.log("existing run account:", acct ? `${acct.data.length} bytes, owner ${acct.owner.toBase58()}` : "none");

  const startIx = await program.methods
    .startRun(42)
    .accountsPartial({ player, sessionSigner: session, dungeonRun: run, oracleQueue: DEFAULT_QUEUE, vrfProgram: VRF_PROGRAM_ID })
    .instruction();
  await simulate(conn, "start_run (VRF request)", [startIx], player);
})().catch((e) => { console.error(e); process.exit(1); });
