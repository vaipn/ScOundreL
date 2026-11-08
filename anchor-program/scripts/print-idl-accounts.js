const i = require("../target/idl/scoundrel.json");
for (const ix of i.instructions) {
  console.log(ix.name, "=>", ix.accounts.map((a) => a.name + (a.writable ? "(w)" : "") + (a.signer ? "(s)" : "")).join(", "));
}
