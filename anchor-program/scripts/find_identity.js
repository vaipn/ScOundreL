const anchor = require("@coral-xyz/anchor");
const prog = new anchor.web3.PublicKey("7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx");
const [pda, bump] = anchor.web3.PublicKey.findProgramAddressSync([Buffer.from("identity")], prog);
console.log("Identity PDA:", pda.toBase58(), "bump:", bump);

