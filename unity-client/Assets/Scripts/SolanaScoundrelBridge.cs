using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Solana.Unity.Programs;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.SDK;
using Solana.Unity.Wallet;
using UnityEngine;

namespace Scoundrel
{
    public class SolanaScoundrelBridge : MonoBehaviour
    {
        // Exact 8-byte Anchor instruction & account discriminators from target/idl/scoundrel.json
        private static readonly byte[] DiscStartRun = { 72, 212, 1, 91, 61, 186, 2, 52 };
        private static readonly byte[] DiscCallbackStartRun = { 191, 41, 73, 72, 165, 139, 12, 72 };
        private static readonly byte[] DiscBuyItem = { 80, 82, 193, 201, 216, 27, 70, 184 };
        private static readonly byte[] DiscUseMarketplaceItem = { 27, 247, 19, 150, 134, 232, 181, 186 };
        private static readonly byte[] DiscFleeRoom = { 67, 216, 47, 229, 176, 138, 203, 97 };
        private static readonly byte[] DiscUseEpicPotion = { 80, 220, 203, 172, 136, 53, 36, 231 };
        private static readonly byte[] DiscResolveCard = { 7, 249, 238, 138, 126, 35, 54, 92 };
        private static readonly byte[] DiscClaimSpoils = { 132, 55, 110, 6, 235, 211, 149, 45 };
        private static readonly byte[] DiscClaimGoldTokens = { 168, 50, 230, 68, 223, 73, 86, 21 };
        private static readonly byte[] DiscCloseRun = { 31, 61, 130, 76, 74, 147, 171, 204 };
        private static readonly byte[] DiscDungeonRunAccount = { 105, 118, 160, 69, 75, 241, 13, 36 };

        private static readonly byte[] DungeonRunSeedPrefix = Encoding.UTF8.GetBytes("dungeon_run");
        private static readonly byte[] ProgramIdentitySeedPrefix = Encoding.UTF8.GetBytes("identity");
        private static readonly byte[] GoldMintSeedPrefix = Encoding.UTF8.GetBytes("scoundrel_gold_mint");

        private const string BurnerWalletPrefsKey = "Scoundrel_DevnetBurnerPrivateKeyBase64";

        // MagicBlock VRF Program & Default Queue Constants (from ephemeral-vrf-sdk 0.17.3)
        public const string DefaultVrfQueueAddress = "Cuj97ggrhhidhbu39TijNVqE74xvKJ69gDervRUXAxGh";
        public const string VrfProgramAddress = "Vrf1RNUjXmQGjmQrQLvJHs9SNkvDJEsRVFPkfSQUwGz";
        public const string SlotHashesSysvarAddress = "SysvarS1otHashes111111111111111111111111111";
        public const string SplTokenProgramAddress = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
        public const string AssociatedTokenProgramAddress = "ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL";

        private const int ConfirmationMaxAttempts = 60;
        private const int ConfirmationPollDelayMs = 250;
        private const int VrfFulfillmentPollAttempts = 8;
        private const int VrfFulfillmentPollDelayMs = 500;

        [Header("Solana Network & Mode")]
        [SerializeField] private bool offlineSimulationMode = true;
        [SerializeField] private string rpcUrl = "https://api.devnet.solana.com";
        [SerializeField] private string programId = "7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx";

        public bool OfflineSimulationMode
        {
            get => offlineSimulationMode;
            set => offlineSimulationMode = value;
        }

        public string RpcUrl => rpcUrl;
        public string ProgramId => programId;
        public string ActiveSessionPubkey { get; private set; } = string.Empty;
        public string ActivePlayerPubkey { get; private set; } = string.Empty;
        public string DungeonRunPdaAddress { get; private set; } = string.Empty;
        public string GoldMintPdaAddress { get; private set; } = string.Empty;
        public string PlayerGoldAtaAddress { get; private set; } = string.Empty;
        public double LastKnownSolBalance { get; private set; }
        public ulong LastKnownMintedGoldBalance { get; private set; }
        public bool HasConnectedWallet => ResolveConnectedPlayerAccount() != null;
        public string LastStatusMessage { get; private set; } = "Session Key Ready (Local VRF Sim)";
        public string LastTransactionSignature { get; private set; } = string.Empty;
        public int SettledTransactionCount { get; private set; }

        public event Action<string> OnBridgeStatusChanged;
        public event Action OnWalletStateChanged;

        private readonly SemaphoreSlim _txMutex = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _lifecycleCts;

        private Account _localSimPlayerAccount;
        private Account _overridePlayerAccount;
        private Account _sessionSigner;
        private PublicKey _dungeonRunPda;
        private IRpcClient _cachedRpcClient;
        private string _cachedRpcUrl;
        private bool _hasActiveOnChainSession;
        private int _runEpoch;

        private void Awake()
        {
            _lifecycleCts = new CancellationTokenSource();
            EnsureLocalKeysInitialized();
        }

        private void OnDestroy()
        {
            if (_lifecycleCts != null)
            {
                _lifecycleCts.Cancel();
                _lifecycleCts.Dispose();
                _lifecycleCts = null;
            }
        }

        /// <summary>
        /// Optional programmatic player keypair override for headless Devnet/Localnet runs when Web3.Wallet is not used.
        /// </summary>
        public void SetPlayerAccount(Account playerAccount)
        {
            _overridePlayerAccount = playerAccount;
            if (playerAccount != null && TryGetProgramPublicKey(out PublicKey progKey))
            {
                ActivePlayerPubkey = playerAccount.PublicKey.Key;
                if (TryDeriveDungeonRunPda(playerAccount.PublicKey, progKey, out PublicKey pda, out _))
                {
                    _dungeonRunPda = pda;
                    DungeonRunPdaAddress = pda.Key;
                }
                if (TryDeriveGoldMintPda(progKey, out PublicKey goldMint, out _))
                {
                    GoldMintPdaAddress = goldMint.Key;
                    if (TryDeriveAssociatedTokenAddress(playerAccount.PublicKey, goldMint, out PublicKey ata))
                    {
                        PlayerGoldAtaAddress = ata.Key;
                    }
                }
            }
            OnWalletStateChanged?.Invoke();
        }

        public void SetNetworkMode(bool offlineSim)
        {
            offlineSimulationMode = offlineSim;
            if (offlineSim)
            {
                EmitStatus("Session Key Ready • Local VRF Sim");
            }
            else if (HasConnectedWallet)
            {
                EmitStatus($"Devnet Live • Wallet ({ShortKey(ActivePlayerPubkey)})");
                _ = RefreshBalancesAsync();
            }
            else
            {
                EmitStatus("Devnet Mode • Connect Wallet to Sign");
            }
            OnWalletStateChanged?.Invoke();
        }

        public async Task<bool> ConnectWalletAdapterAsync()
        {
            try
            {
                EmitStatus("Connecting Solana Wallet Adapter...");
                Account acct = null;
                if (Web3.Instance != null)
                {
                    acct = await Web3.Instance.LoginWalletAdapter();
                }

                if (acct == null)
                {
                    // Fallback to in-game wallet or Devnet burner if Wallet Adapter plugin is unavailable in Editor
                    acct = LoadOrCreateDevnetBurnerAccount();
                }

                if (acct != null)
                {
                    _overridePlayerAccount = acct;
                    offlineSimulationMode = false;
                    UpdateDerivedAddressesForPlayer(acct.PublicKey);
                    EmitStatus($"Wallet Connected • {ShortKey(acct.PublicKey.Key)} (Devnet)");
                    OnWalletStateChanged?.Invoke();
                    await RefreshBalancesAsync();
                    return true;
                }

                EmitStatus("Wallet Adapter Connection Cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] ConnectWalletAdapterAsync fallback: {ex.Message}");
                Account burner = LoadOrCreateDevnetBurnerAccount();
                SetPlayerAccount(burner);
                offlineSimulationMode = false;
                EmitStatus($"Devnet Wallet Active • {ShortKey(burner.PublicKey.Key)}");
                await RefreshBalancesAsync();
                return true;
            }
        }

        public async Task<bool> ConnectDevnetBurnerWalletAsync(bool requestAirdropIfLow = false)
        {
            Account burner = LoadOrCreateDevnetBurnerAccount();
            SetPlayerAccount(burner);
            offlineSimulationMode = false;
            EmitStatus($"Devnet Burner Ready • {ShortKey(burner.PublicKey.Key)}");
            await RefreshBalancesAsync();

            if (requestAirdropIfLow && LastKnownSolBalance < 0.05)
            {
                await RequestDevnetAirdropAsync(1_000_000_000UL);
            }

            return true;
        }

        public async Task<bool> RequestDevnetAirdropAsync(ulong lamports = 1_000_000_000UL)
        {
            EnsureLocalKeysInitialized();
            Account targetAcct = ResolveConnectedPlayerAccount() ?? LoadOrCreateDevnetBurnerAccount();
            SetPlayerAccount(targetAcct);

            IRpcClient rpc = GetOrCreateRpcClient();
            if (rpc == null)
            {
                EmitStatus("RPC Unavailable • Cannot Request Airdrop");
                return false;
            }

            try
            {
                EmitStatus($"Requesting {lamports / 1_000_000_000.0:0.0} SOL Devnet Airdrop...");
                var res = await rpc.RequestAirdropAsync(targetAcct.PublicKey.Key, lamports, Commitment.Confirmed);
                if (res != null && res.WasSuccessful && !string.IsNullOrEmpty(res.Result))
                {
                    LastTransactionSignature = res.Result;
                    await Task.Delay(1200);
                    await RefreshBalancesAsync();
                    EmitStatus($"Devnet Airdrop Confirmed ({ShortKey(res.Result)})");
                    return true;
                }

                EmitStatus($"Devnet Faucet Rate-Limited • Fund {ShortKey(targetAcct.PublicKey.Key)} at faucet.solana.com");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] RequestDevnetAirdropAsync warning: {ex.Message}");
                EmitStatus($"Devnet Faucet Busy • Fund {ShortKey(targetAcct.PublicKey.Key)}");
                return false;
            }
        }

        public async Task RefreshBalancesAsync()
        {
            EnsureLocalKeysInitialized();
            Account player = ResolveConnectedPlayerAccount() ?? EnsureLocalSimPlayerAccount();
            UpdateDerivedAddressesForPlayer(player.PublicKey);

            if (offlineSimulationMode)
            {
                OnWalletStateChanged?.Invoke();
                return;
            }

            IRpcClient rpc = GetOrCreateRpcClient();
            if (rpc == null) return;

            try
            {
                var balRes = await rpc.GetBalanceAsync(player.PublicKey.Key, Commitment.Confirmed);
                if (balRes != null && balRes.WasSuccessful && balRes.Result != null)
                {
                    LastKnownSolBalance = balRes.Result.Value / 1_000_000_000.0;
                }

                if (!string.IsNullOrEmpty(PlayerGoldAtaAddress))
                {
                    var tokRes = await rpc.GetTokenAccountBalanceAsync(PlayerGoldAtaAddress, Commitment.Confirmed);
                    if (tokRes != null && tokRes.WasSuccessful && tokRes.Result?.Value != null)
                    {
                        if (ulong.TryParse(tokRes.Result.Value.Amount, out ulong parsedGold))
                        {
                            LastKnownMintedGoldBalance = parsedGold;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore transient RPC polling errors
            }

            OnWalletStateChanged?.Invoke();
        }

        private Account LoadOrCreateDevnetBurnerAccount()
        {
            try
            {
                if (PlayerPrefs.HasKey(BurnerWalletPrefsKey))
                {
                    string b64 = PlayerPrefs.GetString(BurnerWalletPrefsKey, string.Empty);
                    if (!string.IsNullOrEmpty(b64))
                    {
                        byte[] raw = Convert.FromBase64String(b64);
                        if (raw.Length == 64)
                        {
                            byte[] pub = new byte[32];
                            Buffer.BlockCopy(raw, 32, pub, 0, 32);
                            return new Account(raw, pub);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Regenerate below if corrupted
            }

            Account fresh = new Account();
            if (fresh.PrivateKey?.KeyBytes != null && fresh.PrivateKey.KeyBytes.Length == 64)
            {
                PlayerPrefs.SetString(BurnerWalletPrefsKey, Convert.ToBase64String(fresh.PrivateKey.KeyBytes));
                PlayerPrefs.Save();
            }
            return fresh;
        }

        private void UpdateDerivedAddressesForPlayer(PublicKey playerPubKey)
        {
            if (playerPubKey == null) return;
            ActivePlayerPubkey = playerPubKey.Key;
            if (TryGetProgramPublicKey(out PublicKey progKey))
            {
                if (TryDeriveDungeonRunPda(playerPubKey, progKey, out PublicKey pda, out _))
                {
                    _dungeonRunPda = pda;
                    DungeonRunPdaAddress = pda.Key;
                }
                if (TryDeriveGoldMintPda(progKey, out PublicKey goldMint, out _))
                {
                    GoldMintPdaAddress = goldMint.Key;
                    if (TryDeriveAssociatedTokenAddress(playerPubKey, goldMint, out PublicKey ata))
                    {
                        PlayerGoldAtaAddress = ata.Key;
                    }
                }
            }
        }

        #region Fire-and-Forget Dispatchers (Called by ScoundrelGameManager)

        public void DispatchStartRun(byte clientSeed, Action<DungeonRunState> onChainStateFetched = null)
        {
            ObserveAsync(StartRunAsync(clientSeed, onChainStateFetched), "start_run");
        }

        public void DispatchStartRun(ulong seed, bool hasLegendaryNft, Action<DungeonRunState> onChainStateFetched = null)
        {
            byte clientSeed = (byte)((seed & 0xFFUL) == 0 ? 42 : (seed & 0xFFUL));
            ObserveAsync(StartRunAsync(clientSeed, onChainStateFetched), "start_run");
        }

        public void DispatchBuyItem(byte itemId)
        {
            ObserveAsync(BuyItemAsync(itemId), "buy_item");
        }

        public void DispatchUseMarketplaceItem(byte itemId)
        {
            ObserveAsync(UseMarketplaceItemAsync(itemId), "use_marketplace_item");
        }

        public void DispatchFleeRoom()
        {
            ObserveAsync(FleeRoomAsync(), "flee_room");
        }

        public void DispatchUseEpicPotion()
        {
            ObserveAsync(UseEpicPotionAsync(), "use_epic_potion");
        }

        public void DispatchResolveCard(byte slotIndex, bool useWeapon)
        {
            ObserveAsync(ResolveCardAsync(slotIndex, useWeapon), "resolve_card");
        }

        public void DispatchClaimSpoils(byte choice)
        {
            ObserveAsync(ClaimSpoilsAsync(choice), "claim_spoils");
        }

        public void DispatchClaimGoldTokens(uint expectedGoldAmount = 0)
        {
            ObserveAsync(ClaimGoldTokensAsync(expectedGoldAmount), "claim_gold_tokens");
        }

        public void DispatchCloseRun()
        {
            ObserveAsync(CloseRunAsync(), "close_run");
        }

        #endregion

        #region Async Solana Instruction Methods

        public async Task<bool> StartRunAsync(byte clientSeed, Action<DungeonRunState> onChainStateFetched = null)
        {
            int currentEpoch = ++_runEpoch;

            // Synchronously rotate the ephemeral session key and derive PDA BEFORE the first await
            // so callers reading ActiveSessionPubkey / DungeonRunPdaAddress right after DispatchStartRun see fresh keys.
            _sessionSigner = new Account();
            Account turnSessionSigner = _sessionSigner;
            ActiveSessionPubkey = turnSessionSigner.PublicKey.Key;

            Account connectedPlayer = ResolveConnectedPlayerAccount();
            Account effectivePlayer = connectedPlayer ?? EnsureLocalSimPlayerAccount();
            ActivePlayerPubkey = effectivePlayer.PublicKey.Key;

            if (!TryGetProgramPublicKey(out PublicKey programPubKey))
            {
                _hasActiveOnChainSession = false;
                EmitStatus("Invalid Program ID • Fallback Local Sim (start_run)");
                return false;
            }

            if (TryDeriveDungeonRunPda(effectivePlayer.PublicKey, programPubKey, out PublicKey derivedPda, out _))
            {
                _dungeonRunPda = derivedPda;
                DungeonRunPdaAddress = derivedPda.Key;
            }

            if (offlineSimulationMode)
            {
                _hasActiveOnChainSession = false;
                SettledTransactionCount++;
                EmitStatus("MagicBlock VRF Seeded • Local Sim (start_run)");
                return true;
            }

            if (connectedPlayer == null)
            {
                Debug.LogWarning("[SolanaScoundrelBridge] OfflineSimulationMode is false, but no live Solana Wallet/Account is connected. Falling back to local optimistic simulation.");
                _hasActiveOnChainSession = false;
                SettledTransactionCount++;
                EmitStatus("No Wallet Connected • Local Sim Fallback (start_run)");
                return true;
            }

            CancellationToken ct = _lifecycleCts != null ? _lifecycleCts.Token : CancellationToken.None;
            await _txMutex.WaitAsync(ct);
            try
            {
                IRpcClient rpc = GetOrCreateRpcClient();
                if (rpc == null)
                {
                    Debug.LogWarning("[SolanaScoundrelBridge] Could not create RPC client. Falling back to local simulation.");
                    _hasActiveOnChainSession = false;
                    SettledTransactionCount++;
                    EmitStatus("RPC Unavailable • Local Sim Fallback (start_run)");
                    return true;
                }

                EmitStatus("Requesting MagicBlock VRF & Session Key (start_run)...");

                var blockhashRes = await rpc.GetLatestBlockHashAsync(Commitment.Confirmed);
                string recentBlockhash = blockhashRes?.Result?.Value?.Blockhash;
                if (blockhashRes == null || !blockhashRes.WasSuccessful || string.IsNullOrEmpty(recentBlockhash))
                {
                    Debug.LogWarning($"[SolanaScoundrelBridge] Failed to fetch recent blockhash ({blockhashRes?.Reason}). Falling back to local simulation.");
                    _hasActiveOnChainSession = false;
                    SettledTransactionCount++;
                    EmitStatus("RPC Offline • Local Sim Fallback (start_run)");
                    return true;
                }

                var instructions = new List<TransactionInstruction>(2);

                // If a previous dungeon_run PDA already exists and is still Active or AwaitingVrf,
                // close it first so start_run's init_if_needed can re-initialize cleanly.
                var existingAcctRes = await rpc.GetAccountInfoAsync(derivedPda.Key, Commitment.Confirmed, BinaryEncoding.Base64);
                AccountInfo existingInfo = existingAcctRes?.Result?.Value;
                if (existingInfo != null && existingInfo.Lamports > 0 && existingInfo.Owner == programPubKey.Key)
                {
                    DungeonRunState existingState = null;
                    if (existingInfo.Data != null && existingInfo.Data.Count > 0 && !string.IsNullOrEmpty(existingInfo.Data[0]))
                    {
                        existingState = DeserializeDungeonRun(Convert.FromBase64String(existingInfo.Data[0]));
                    }

                    if (existingState == null || existingState.Status == RunStatus.Active || existingState.Status == RunStatus.AwaitingVrf)
                    {
                        instructions.Add(BuildCloseRunInstruction(connectedPlayer.PublicKey, derivedPda, programPubKey));
                    }
                }

                // start_run automatically transfers 0.005 SOL (5,000,000 lamports) from player to session_signer
                // and invokes MagicBlock VRF request_randomness CPI
                instructions.Add(BuildStartRunInstruction(
                    connectedPlayer.PublicKey,
                    turnSessionSigner.PublicKey,
                    derivedPda,
                    programPubKey,
                    clientSeed));

                var tx = new Transaction
                {
                    RecentBlockHash = recentBlockhash,
                    FeePayer = connectedPlayer.PublicKey,
                    Instructions = instructions,
                    Signatures = new List<SignaturePubKeyPair>()
                };

                Transaction signedTx = await SignWithPlayerAsync(tx, connectedPlayer);
                if (signedTx == null)
                {
                    Debug.LogWarning("[SolanaScoundrelBridge] Player signature was cancelled or unavailable. Falling back to local simulation.");
                    _hasActiveOnChainSession = false;
                    EmitStatus("Signature Declined • Local Sim Fallback");
                    return false;
                }

                string signature = await SendAndConfirmTransactionAsync(rpc, signedTx, ct);
                if (string.IsNullOrEmpty(signature))
                {
                    _hasActiveOnChainSession = false;
                    EmitStatus("start_run Failed • Local Sim Fallback");
                    return false;
                }

                LastTransactionSignature = signature;
                _hasActiveOnChainSession = true;
                SettledTransactionCount++;
                EmitStatus("Awaiting MagicBlock VRF Oracle...");

                // Poll for MagicBlock VRF oracle callback (run_status transitioning from 3 -> 0) for up to ~4s
                DungeonRunState onChainState = null;
                for (int poll = 0; poll < VrfFulfillmentPollAttempts; poll++)
                {
                    ct.ThrowIfCancellationRequested();
                    onChainState = await FetchAndDeserializeDungeonRunAsync(rpc, derivedPda, ct);
                    if (onChainState != null && onChainState.Status != RunStatus.AwaitingVrf)
                    {
                        break;
                    }
                    await Task.Delay(VrfFulfillmentPollDelayMs, ct);
                }

                // Fallback: if VRF Oracle has not fulfilled within 4s (e.g. localnet or unserviced queue),
                // fulfill callback_start_run directly using the funded session_signer and 32 cryptographic bytes.
                if (onChainState == null || onChainState.Status == RunStatus.AwaitingVrf)
                {
                    EmitStatus("Fulfilling VRF Callback (Fallback Oracle)...");
                    byte[] fallbackRandomness = new byte[32];
                    RandomNumberGenerator.Fill(fallbackRandomness);

                    var cbBlockhashRes = await rpc.GetLatestBlockHashAsync(Commitment.Confirmed);
                    string cbBlockhash = cbBlockhashRes?.Result?.Value?.Blockhash;
                    if (!string.IsNullOrEmpty(cbBlockhash))
                    {
                        TransactionInstruction cbIx = BuildCallbackStartRunInstruction(
                            turnSessionSigner.PublicKey,
                            derivedPda,
                            programPubKey,
                            fallbackRandomness);

                        var cbTx = new Transaction
                        {
                            RecentBlockHash = cbBlockhash,
                            FeePayer = turnSessionSigner.PublicKey,
                            Instructions = new List<TransactionInstruction>(1) { cbIx },
                            Signatures = new List<SignaturePubKeyPair>()
                        };
                        cbTx.Sign(turnSessionSigner);

                        string cbSig = await SendAndConfirmTransactionAsync(rpc, cbTx, ct);
                        if (!string.IsNullOrEmpty(cbSig))
                        {
                            LastTransactionSignature = cbSig;
                            SettledTransactionCount++;
                            onChainState = await FetchAndDeserializeDungeonRunAsync(rpc, derivedPda, ct);
                        }
                    }
                }

                EmitStatus($"MagicBlock VRF Active • Session ({ShortKey(turnSessionSigner.PublicKey.Key)})");

                if (onChainState != null && currentEpoch == _runEpoch && this != null)
                {
                    onChainStateFetched?.Invoke(onChainState);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] start_run exception: {ex.Message}. Falling back to local simulation.");
                _hasActiveOnChainSession = false;
                EmitStatus("RPC Error • Local Sim Fallback (start_run)");
                return false;
            }
            finally
            {
                _txMutex.Release();
            }
        }

        public Task<bool> BuyItemAsync(byte itemId)
        {
            string itemLabel = itemId == 0 ? "buy_item (Ofe Charm)" : "buy_item (Rusty Shield)";
            return ExecuteSessionGameplayInstructionAsync(
                itemLabel,
                (signer, pda, prog) => BuildBuyItemInstruction(signer, pda, prog, itemId));
        }

        public Task<bool> UseMarketplaceItemAsync(byte itemId)
        {
            string itemLabel = itemId == 0 ? "use_item (Ofe Charm)" : "use_item (Rusty Shield)";
            return ExecuteSessionGameplayInstructionAsync(
                itemLabel,
                (signer, pda, prog) => BuildUseMarketplaceItemInstruction(signer, pda, prog, itemId));
        }

        public Task<bool> FleeRoomAsync()
        {
            return ExecuteSessionGameplayInstructionAsync(
                "flee_room",
                (signer, pda, prog) => BuildFleeRoomInstruction(signer, pda, prog));
        }

        public Task<bool> UseEpicPotionAsync()
        {
            return ExecuteSessionGameplayInstructionAsync(
                "use_epic_potion",
                (signer, pda, prog) => BuildUseEpicPotionInstruction(signer, pda, prog));
        }

        public Task<bool> ResolveCardAsync(byte slotIndex, bool useWeapon)
        {
            string label = $"resolve_card #{slotIndex + 1}";
            return ExecuteSessionGameplayInstructionAsync(
                label,
                (signer, pda, prog) => BuildResolveCardInstruction(signer, pda, prog, slotIndex, useWeapon));
        }

        public Task<bool> ClaimSpoilsAsync(byte choice)
        {
            string label = $"claim_spoils #{choice + 1}";
            return ExecuteSessionGameplayInstructionAsync(
                label,
                (signer, pda, prog) => BuildClaimSpoilsInstruction(signer, pda, prog, choice));
        }

        public async Task<bool> ClaimGoldTokensAsync(uint simulatedOrExpectedAmount = 0)
        {
            EnsureLocalKeysInitialized();

            if (offlineSimulationMode)
            {
                if (simulatedOrExpectedAmount > 0)
                {
                    LastKnownMintedGoldBalance += simulatedOrExpectedAmount;
                }
                SettledTransactionCount++;
                EmitStatus($"SPL Gold Minted • +{simulatedOrExpectedAmount} GOLD (Local Sim)");
                OnWalletStateChanged?.Invoke();
                return true;
            }

            Account connectedPlayer = ResolveConnectedPlayerAccount();
            if (connectedPlayer == null || !TryGetProgramPublicKey(out PublicKey programPubKey))
            {
                if (simulatedOrExpectedAmount > 0)
                {
                    LastKnownMintedGoldBalance += simulatedOrExpectedAmount;
                }
                SettledTransactionCount++;
                EmitStatus($"SPL Gold Minted • +{simulatedOrExpectedAmount} GOLD (Sim Fallback)");
                OnWalletStateChanged?.Invoke();
                return true;
            }

            if (!TryDeriveDungeonRunPda(connectedPlayer.PublicKey, programPubKey, out PublicKey pda, out _) ||
                !TryDeriveGoldMintPda(programPubKey, out PublicKey goldMint, out _) ||
                !TryDeriveAssociatedTokenAddress(connectedPlayer.PublicKey, goldMint, out PublicKey playerAta))
            {
                return false;
            }

            // Use session_signer if active and funded, otherwise sign with connectedPlayer
            bool useSessionSigner = _hasActiveOnChainSession && _sessionSigner != null;
            Account feePayerAccount = useSessionSigner ? _sessionSigner : connectedPlayer;

            CancellationToken ct = _lifecycleCts != null ? _lifecycleCts.Token : CancellationToken.None;
            await _txMutex.WaitAsync(ct);
            try
            {
                IRpcClient rpc = GetOrCreateRpcClient();
                if (rpc == null)
                {
                    return false;
                }

                var blockhashRes = await rpc.GetLatestBlockHashAsync(Commitment.Confirmed);
                string recentBlockhash = blockhashRes?.Result?.Value?.Blockhash;
                if (blockhashRes == null || !blockhashRes.WasSuccessful || string.IsNullOrEmpty(recentBlockhash))
                {
                    EmitStatus("RPC Offline • Could Not Mint SPL Gold");
                    return false;
                }

                TransactionInstruction ix = BuildClaimGoldTokensInstruction(
                    feePayerAccount.PublicKey,
                    connectedPlayer.PublicKey,
                    pda,
                    goldMint,
                    playerAta,
                    programPubKey);

                var tx = new Transaction
                {
                    RecentBlockHash = recentBlockhash,
                    FeePayer = feePayerAccount.PublicKey,
                    Instructions = new List<TransactionInstruction>(1) { ix },
                    Signatures = new List<SignaturePubKeyPair>()
                };

                Transaction signedTx;
                if (useSessionSigner)
                {
                    tx.Sign(feePayerAccount);
                    signedTx = tx;
                }
                else
                {
                    signedTx = await SignWithPlayerAsync(tx, connectedPlayer);
                }

                if (signedTx == null)
                {
                    return false;
                }

                EmitStatus("Minting SPL Gold Tokens to ATA...");
                string sig = await SendAndConfirmTransactionAsync(rpc, signedTx, ct);
                if (string.IsNullOrEmpty(sig))
                {
                    EmitStatus("SPL Gold Mint Failed on Devnet");
                    return false;
                }

                LastTransactionSignature = sig;
                SettledTransactionCount++;
                EmitStatus($"SPL Gold Minted to ATA ({ShortKey(sig)})");
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] claim_gold_tokens exception: {ex.Message}");
                return false;
            }
            finally
            {
                _txMutex.Release();
            }

            await RefreshBalancesAsync();
            return true;
        }

        public async Task<bool> CloseRunAsync()
        {
            EnsureLocalKeysInitialized();

            if (offlineSimulationMode)
            {
                _hasActiveOnChainSession = false;
                SettledTransactionCount++;
                EmitStatus("Run Closed • Local Sim (close_run)");
                return true;
            }

            Account connectedPlayer = ResolveConnectedPlayerAccount();
            if (connectedPlayer == null || !TryGetProgramPublicKey(out PublicKey programPubKey))
            {
                Debug.LogWarning("[SolanaScoundrelBridge] Cannot close_run online without a connected player wallet; marking closed locally.");
                _hasActiveOnChainSession = false;
                SettledTransactionCount++;
                EmitStatus("Run Closed • Local Sim Fallback (close_run)");
                return true;
            }

            if (!TryDeriveDungeonRunPda(connectedPlayer.PublicKey, programPubKey, out PublicKey pda, out _))
            {
                return false;
            }

            CancellationToken ct = _lifecycleCts != null ? _lifecycleCts.Token : CancellationToken.None;
            await _txMutex.WaitAsync(ct);
            try
            {
                IRpcClient rpc = GetOrCreateRpcClient();
                if (rpc == null)
                {
                    _hasActiveOnChainSession = false;
                    return false;
                }

                var blockhashRes = await rpc.GetLatestBlockHashAsync(Commitment.Confirmed);
                string recentBlockhash = blockhashRes?.Result?.Value?.Blockhash;
                if (blockhashRes == null || !blockhashRes.WasSuccessful || string.IsNullOrEmpty(recentBlockhash))
                {
                    Debug.LogWarning($"[SolanaScoundrelBridge] close_run failed to fetch blockhash: {blockhashRes?.Reason}");
                    return false;
                }

                var instructions = new List<TransactionInstruction>(2);

                // Automatically mint any remaining unclaimed_gold before closing the DungeonRun PDA
                var acctRes = await rpc.GetAccountInfoAsync(pda.Key, Commitment.Confirmed, BinaryEncoding.Base64);
                var dataList = acctRes?.Result?.Value?.Data;
                if (dataList != null && dataList.Count > 0 && !string.IsNullOrEmpty(dataList[0]))
                {
                    DungeonRunState liveState = DeserializeDungeonRun(Convert.FromBase64String(dataList[0]));
                    if (liveState != null && liveState.UnclaimedGold > 0 &&
                        TryDeriveGoldMintPda(programPubKey, out PublicKey goldMint, out _) &&
                        TryDeriveAssociatedTokenAddress(connectedPlayer.PublicKey, goldMint, out PublicKey playerAta))
                    {
                        instructions.Add(BuildClaimGoldTokensInstruction(
                            connectedPlayer.PublicKey,
                            connectedPlayer.PublicKey,
                            pda,
                            goldMint,
                            playerAta,
                            programPubKey));
                    }
                }

                instructions.Add(BuildCloseRunInstruction(connectedPlayer.PublicKey, pda, programPubKey));

                var tx = new Transaction
                {
                    RecentBlockHash = recentBlockhash,
                    FeePayer = connectedPlayer.PublicKey,
                    Instructions = instructions,
                    Signatures = new List<SignaturePubKeyPair>()
                };

                Transaction signedTx = await SignWithPlayerAsync(tx, connectedPlayer);
                if (signedTx == null)
                {
                    return false;
                }

                string sig = await SendAndConfirmTransactionAsync(rpc, signedTx, ct);
                if (string.IsNullOrEmpty(sig))
                {
                    return false;
                }

                LastTransactionSignature = sig;
                _hasActiveOnChainSession = false;
                SettledTransactionCount++;
                EmitStatus($"Run Closed • Gold Minted & Rent Reclaimed ({ShortKey(sig)})");
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] close_run exception: {ex.Message}");
                return false;
            }
            finally
            {
                _txMutex.Release();
            }
        }

        #endregion

        #region Anchor Instruction Builders & Borsh Account Deserializer

        public static bool TryDeriveDungeonRunPda(
            PublicKey playerPubKey,
            PublicKey programPubKey,
            out PublicKey dungeonRunPda,
            out byte bump)
        {
            if (playerPubKey == null || programPubKey == null)
            {
                dungeonRunPda = null;
                bump = 0;
                return false;
            }

            return PublicKey.TryFindProgramAddress(
                new[] { DungeonRunSeedPrefix, playerPubKey.KeyBytes },
                programPubKey,
                out dungeonRunPda,
                out bump);
        }

        public static bool TryDeriveProgramIdentityPda(
            PublicKey programPubKey,
            out PublicKey programIdentityPda,
            out byte bump)
        {
            if (programPubKey == null)
            {
                programIdentityPda = null;
                bump = 0;
                return false;
            }

            return PublicKey.TryFindProgramAddress(
                new[] { ProgramIdentitySeedPrefix },
                programPubKey,
                out programIdentityPda,
                out bump);
        }

        public static bool TryDeriveGoldMintPda(
            PublicKey programPubKey,
            out PublicKey goldMintPda,
            out byte bump)
        {
            if (programPubKey == null)
            {
                goldMintPda = null;
                bump = 0;
                return false;
            }

            return PublicKey.TryFindProgramAddress(
                new[] { GoldMintSeedPrefix },
                programPubKey,
                out goldMintPda,
                out bump);
        }

        public static bool TryDeriveAssociatedTokenAddress(
            PublicKey walletPubKey,
            PublicKey mintPubKey,
            out PublicKey ataPubKey)
        {
            ataPubKey = null;
            if (walletPubKey == null || mintPubKey == null)
            {
                return false;
            }

            PublicKey tokenProgram = new PublicKey(SplTokenProgramAddress);
            PublicKey ataProgram = new PublicKey(AssociatedTokenProgramAddress);
            return PublicKey.TryFindProgramAddress(
                new[] { walletPubKey.KeyBytes, tokenProgram.KeyBytes, mintPubKey.KeyBytes },
                ataProgram,
                out ataPubKey,
                out _);
        }

        public static TransactionInstruction BuildClaimGoldTokensInstruction(
            PublicKey payer,
            PublicKey player,
            PublicKey dungeonRunPda,
            PublicKey goldMintPda,
            PublicKey playerGoldAta,
            PublicKey programPubKey)
        {
            byte[] data = new byte[8];
            Buffer.BlockCopy(DiscClaimGoldTokens, 0, data, 0, 8);

            PublicKey tokenProgram = new PublicKey(SplTokenProgramAddress);
            PublicKey ataProgram = new PublicKey(AssociatedTokenProgramAddress);

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(8)
                {
                    AccountMeta.Writable(payer, true),
                    AccountMeta.ReadOnly(player, false),
                    AccountMeta.Writable(dungeonRunPda, false),
                    AccountMeta.Writable(goldMintPda, false),
                    AccountMeta.Writable(playerGoldAta, false),
                    AccountMeta.ReadOnly(tokenProgram, false),
                    AccountMeta.ReadOnly(ataProgram, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildStartRunInstruction(
            PublicKey player,
            PublicKey sessionSigner,
            PublicKey dungeonRunPda,
            PublicKey programPubKey,
            byte clientSeed)
        {
            byte[] data = new byte[9];
            Buffer.BlockCopy(DiscStartRun, 0, data, 0, 8);
            data[8] = clientSeed;

            PublicKey oracleQueue = new PublicKey(DefaultVrfQueueAddress);
            PublicKey vrfProgram = new PublicKey(VrfProgramAddress);
            PublicKey slotHashes = new PublicKey(SlotHashesSysvarAddress);
            TryDeriveProgramIdentityPda(programPubKey, out PublicKey programIdentity, out _);

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(8)
                {
                    AccountMeta.Writable(player, true),
                    AccountMeta.Writable(sessionSigner, false),
                    AccountMeta.Writable(dungeonRunPda, false),
                    AccountMeta.Writable(oracleQueue, false),
                    AccountMeta.ReadOnly(vrfProgram, false),
                    AccountMeta.ReadOnly(programIdentity, false),
                    AccountMeta.ReadOnly(slotHashes, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildCallbackStartRunInstruction(
            PublicKey vrfIdentityOrFallbackSigner,
            PublicKey dungeonRunPda,
            PublicKey programPubKey,
            byte[] randomness32)
        {
            byte[] data = new byte[40];
            Buffer.BlockCopy(DiscCallbackStartRun, 0, data, 0, 8);
            if (randomness32 != null && randomness32.Length >= 32)
            {
                Buffer.BlockCopy(randomness32, 0, data, 8, 32);
            }

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(2)
                {
                    AccountMeta.ReadOnly(vrfIdentityOrFallbackSigner, true),
                    AccountMeta.Writable(dungeonRunPda, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildBuyItemInstruction(
            PublicKey signer,
            PublicKey dungeonRunPda,
            PublicKey programPubKey,
            byte itemId)
        {
            byte[] data = new byte[9];
            Buffer.BlockCopy(DiscBuyItem, 0, data, 0, 8);
            data[8] = itemId;

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(2)
                {
                    AccountMeta.Writable(signer, true),
                    AccountMeta.Writable(dungeonRunPda, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildUseMarketplaceItemInstruction(
            PublicKey signer,
            PublicKey dungeonRunPda,
            PublicKey programPubKey,
            byte itemId)
        {
            byte[] data = new byte[9];
            Buffer.BlockCopy(DiscUseMarketplaceItem, 0, data, 0, 8);
            data[8] = itemId;

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(2)
                {
                    AccountMeta.Writable(signer, true),
                    AccountMeta.Writable(dungeonRunPda, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildFleeRoomInstruction(
            PublicKey signer,
            PublicKey dungeonRunPda,
            PublicKey programPubKey)
        {
            byte[] data = new byte[8];
            Buffer.BlockCopy(DiscFleeRoom, 0, data, 0, 8);

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(2)
                {
                    AccountMeta.Writable(signer, true),
                    AccountMeta.Writable(dungeonRunPda, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildUseEpicPotionInstruction(
            PublicKey signer,
            PublicKey dungeonRunPda,
            PublicKey programPubKey)
        {
            byte[] data = new byte[8];
            Buffer.BlockCopy(DiscUseEpicPotion, 0, data, 0, 8);

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(2)
                {
                    AccountMeta.Writable(signer, true),
                    AccountMeta.Writable(dungeonRunPda, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildResolveCardInstruction(
            PublicKey signer,
            PublicKey dungeonRunPda,
            PublicKey programPubKey,
            byte slotIndex,
            bool useWeapon)
        {
            byte[] data = new byte[10];
            Buffer.BlockCopy(DiscResolveCard, 0, data, 0, 8);
            data[8] = slotIndex;
            data[9] = useWeapon ? (byte)1 : (byte)0;

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(2)
                {
                    AccountMeta.Writable(signer, true),
                    AccountMeta.Writable(dungeonRunPda, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildClaimSpoilsInstruction(
            PublicKey signer,
            PublicKey dungeonRunPda,
            PublicKey programPubKey,
            byte choice)
        {
            byte[] data = new byte[9];
            Buffer.BlockCopy(DiscClaimSpoils, 0, data, 0, 8);
            data[8] = choice;

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(2)
                {
                    AccountMeta.Writable(signer, true),
                    AccountMeta.Writable(dungeonRunPda, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildCloseRunInstruction(
            PublicKey player,
            PublicKey dungeonRunPda,
            PublicKey programPubKey)
        {
            byte[] data = new byte[8];
            Buffer.BlockCopy(DiscCloseRun, 0, data, 0, 8);

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(2)
                {
                    AccountMeta.Writable(player, true),
                    AccountMeta.Writable(dungeonRunPda, false)
                },
                Data = data
            };
        }

        /// <summary>
        /// Deserializes an Anchor Borsh-encoded DungeonRun account buffer into Unity's DungeonRunState.
        /// Verifies the 8-byte DungeonRun discriminator and parses variable-width Option&lt;u8&gt;[4] room slots,
        /// MagicBlock VRF seed + nonce, and Mini Marketplace inventories/buffs.
        /// </summary>
        public static DungeonRunState DeserializeDungeonRun(byte[] data)
        {
            // Minimum possible Borsh byte length (when all 4 room Option<u8> slots are None = 4 bytes):
            // 8 (disc) + 32 (player) + 32 (session) + 1 (bump) + 44 (deck) + 2 (head/len) + 4 (room min) + 71 (scalar tail) = 194 bytes
            if (data == null || data.Length < 194)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] DungeonRun buffer too short ({data?.Length ?? 0} bytes).");
                return null;
            }

            for (int i = 0; i < 8; i++)
            {
                if (data[i] != DiscDungeonRunAccount[i])
                {
                    Debug.LogWarning("[SolanaScoundrelBridge] DungeonRun account discriminator mismatch.");
                    return null;
                }
            }

            int offset = 8;

            byte[] playerBytes = new byte[32];
            Buffer.BlockCopy(data, offset, playerBytes, 0, 32);
            offset += 32;

            byte[] sessionBytes = new byte[32];
            Buffer.BlockCopy(data, offset, sessionBytes, 0, 32);
            offset += 32;

            // Consume bump byte right after session_signer
            _ = data[offset++];

            var state = new DungeonRunState
            {
                PlayerPubkey = new PublicKey(playerBytes).Key,
                SessionSignerPubkey = new PublicKey(sessionBytes).Key,
                Deck = new byte[ScoundrelCardDecoder.DeckSize],
                Room = new int[4] { -1, -1, -1, -1 },
                VrfSeed = new byte[32]
            };

            Buffer.BlockCopy(data, offset, state.Deck, 0, ScoundrelCardDecoder.DeckSize);
            offset += ScoundrelCardDecoder.DeckSize;

            state.DeckHead = data[offset++];
            state.DeckLen = data[offset++];

            for (int slot = 0; slot < 4; slot++)
            {
                if (offset >= data.Length)
                {
                    Debug.LogWarning("[SolanaScoundrelBridge] Unexpected EOF while parsing DungeonRun.room Option<u8>.");
                    return null;
                }

                byte optionTag = data[offset++];
                if (optionTag == 0)
                {
                    state.Room[slot] = -1;
                }
                else if (optionTag == 1)
                {
                    if (offset >= data.Length)
                    {
                        Debug.LogWarning("[SolanaScoundrelBridge] Unexpected EOF reading Some(u8) in DungeonRun.room.");
                        return null;
                    }
                    state.Room[slot] = data[offset++];
                }
                else
                {
                    Debug.LogWarning($"[SolanaScoundrelBridge] Invalid Borsh Option<u8> tag {optionTag} at slot {slot}.");
                    return null;
                }
            }

            // Remaining fixed-width scalar tail is 71 bytes
            if (offset + 71 > data.Length)
            {
                Debug.LogWarning("[SolanaScoundrelBridge] DungeonRun buffer truncated before VRF/Marketplace scalar tail.");
                return null;
            }

            state.CardsResolvedInRoom = data[offset++];
            state.HpTenths = ReadUInt16LE(data, ref offset);
            state.MaxHpTenths = ReadUInt16LE(data, ref offset);
            state.EquippedWeaponRank = data[offset++];
            state.EquippedWeaponCardId = (state.EquippedWeaponRank >= 2 && state.EquippedWeaponRank <= 10)
                ? (state.EquippedWeaponRank + 24)
                : -1;
            state.WeaponCeiling = data[offset++];
            state.LegendaryForgedEdgeCharges = data[offset++];
            state.HasLegendarySkin = data[offset++] != 0;
            state.PotionUsedThisRoom = data[offset++] != 0;
            state.FirstPotionHealedTenths = ReadUInt16LE(data, ref offset);
            state.EpicPotionsInventory = data[offset++];
            state.EpicPotionBuffRoomsLeft = data[offset++];
            state.FledLastRoom = data[offset++] != 0;
            state.RoomThreatAccumulator = data[offset++];
            state.PendingSpoilsThreat = data[offset++];
            state.UnclaimedGold = ReadUInt32LE(data, ref offset);
            state.Score = ReadInt32LE(data, ref offset);

            byte rawStatus = data[offset++];
            state.Status = rawStatus <= (byte)RunStatus.AwaitingVrf ? (RunStatus)rawStatus : RunStatus.Active;

            Buffer.BlockCopy(data, offset, state.VrfSeed, 0, 32);
            offset += 32;

            state.CombatRngNonce = ReadUInt64LE(data, ref offset);
            state.OfeInventory = data[offset++];
            state.OfeBuffRoomsLeft = data[offset++];
            state.RustyShieldInventory = data[offset++];
            state.RustyShieldBuffRoomsLeft = data[offset++];

            int activeInRoom = state.ActiveCardsInRoomCount();
            int equippedCount = state.EquippedWeaponRank > 0 ? 1 : 0;
            state.DiscardCount = Mathf.Max(0, ScoundrelCardDecoder.DeckSize - state.DeckLen - activeInRoom - equippedCount);

            return state;
        }

        #endregion

        #region Internal Session Signing & RPC Helpers

        private async Task<bool> ExecuteSessionGameplayInstructionAsync(
            string actionLabel,
            Func<PublicKey, PublicKey, PublicKey, TransactionInstruction> instructionFactory)
        {
            EnsureLocalKeysInitialized();

            if (offlineSimulationMode)
            {
                SettledTransactionCount++;
                EmitStatus($"Session Key Signed • {actionLabel}");
                return true;
            }

            if (!_hasActiveOnChainSession || _sessionSigner == null || _dungeonRunPda == null)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] Online {actionLabel} invoked without an active on-chain session; simulating locally.");
                SettledTransactionCount++;
                EmitStatus($"Session Key Signed • {actionLabel} (Local Sim)");
                return true;
            }

            if (!TryGetProgramPublicKey(out PublicKey programPubKey))
            {
                return false;
            }

            Account signerSnapshot = _sessionSigner;
            PublicKey pdaSnapshot = _dungeonRunPda;
            CancellationToken ct = _lifecycleCts != null ? _lifecycleCts.Token : CancellationToken.None;

            await _txMutex.WaitAsync(ct);
            try
            {
                IRpcClient rpc = GetOrCreateRpcClient();
                if (rpc == null)
                {
                    SettledTransactionCount++;
                    EmitStatus($"RPC Unavailable • {actionLabel} (Local Sim)");
                    return true;
                }

                var blockhashRes = await rpc.GetLatestBlockHashAsync(Commitment.Confirmed);
                string recentBlockhash = blockhashRes?.Result?.Value?.Blockhash;
                if (blockhashRes == null || !blockhashRes.WasSuccessful || string.IsNullOrEmpty(recentBlockhash))
                {
                    Debug.LogWarning($"[SolanaScoundrelBridge] {actionLabel} blockhash lookup failed: {blockhashRes?.Reason}");
                    EmitStatus($"RPC Delay • {actionLabel} Optimistic");
                    return false;
                }

                TransactionInstruction ix = instructionFactory(signerSnapshot.PublicKey, pdaSnapshot, programPubKey);
                var tx = new Transaction
                {
                    RecentBlockHash = recentBlockhash,
                    FeePayer = signerSnapshot.PublicKey,
                    Instructions = new List<TransactionInstruction>(1) { ix },
                    Signatures = new List<SignaturePubKeyPair>()
                };

                // Zero wallet popups: signed strictly in-memory with the authorized ephemeral session keypair
                tx.Sign(signerSnapshot);

                string sig = await SendAndConfirmTransactionAsync(rpc, tx, ct);
                if (string.IsNullOrEmpty(sig))
                {
                    EmitStatus($"Tx Rejected • {actionLabel}");
                    return false;
                }

                LastTransactionSignature = sig;
                SettledTransactionCount++;
                EmitStatus($"Session Key Settled • {actionLabel} ({ShortKey(sig)})");
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] {actionLabel} exception: {ex.Message}");
                EmitStatus($"RPC Warning • {actionLabel} Optimistic");
                return false;
            }
            finally
            {
                _txMutex.Release();
            }
        }

        private async Task<DungeonRunState> FetchAndDeserializeDungeonRunAsync(
            IRpcClient rpc,
            PublicKey dungeonRunPda,
            CancellationToken ct)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var acctRes = await rpc.GetAccountInfoAsync(dungeonRunPda.Key, Commitment.Confirmed, BinaryEncoding.Base64);
                List<string> dataList = acctRes?.Result?.Value?.Data;
                if (acctRes != null && acctRes.WasSuccessful && dataList != null && dataList.Count > 0 && !string.IsNullOrEmpty(dataList[0]))
                {
                    byte[] rawBytes = Convert.FromBase64String(dataList[0]);
                    return DeserializeDungeonRun(rawBytes);
                }

                await Task.Delay(200, ct);
            }

            Debug.LogWarning("[SolanaScoundrelBridge] Confirmed start_run, but GetAccountInfoAsync returned empty data after retries.");
            return null;
        }

        private async Task<Transaction> SignWithPlayerAsync(Transaction tx, Account playerAccount)
        {
            if (_overridePlayerAccount != null && playerAccount == _overridePlayerAccount)
            {
                tx.Sign(_overridePlayerAccount);
                return tx;
            }

            if (Web3.Wallet != null && Web3.Account != null && Web3.Account.PublicKey.Equals(playerAccount.PublicKey))
            {
                return await Web3.Wallet.SignTransaction(tx);
            }

            if (playerAccount?.PrivateKey != null)
            {
                tx.Sign(playerAccount);
                return tx;
            }

            return null;
        }

        private static async Task<string> SendAndConfirmTransactionAsync(
            IRpcClient rpc,
            Transaction signedTx,
            CancellationToken ct)
        {
            byte[] serialized = signedTx.Serialize();
            RequestResult<string> sendRes = await rpc.SendTransactionAsync(serialized, false, Commitment.Confirmed);
            if (sendRes == null || !sendRes.WasSuccessful || string.IsNullOrEmpty(sendRes.Result))
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] SendTransactionAsync failed: {sendRes?.Reason}");
                return null;
            }

            string signature = sendRes.Result;
            var sigQuery = new List<string>(1) { signature };

            for (int i = 0; i < ConfirmationMaxAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                var statusRes = await rpc.GetSignatureStatusesAsync(sigQuery, true);
                var statuses = statusRes?.Result?.Value;
                if (statuses != null && statuses.Count > 0 && statuses[0] != null)
                {
                    SignatureStatusInfo info = statuses[0];
                    if (info.Error != null)
                    {
                        Debug.LogWarning($"[SolanaScoundrelBridge] Transaction {signature} failed on-chain.");
                        return null;
                    }

                    string confirmation = info.ConfirmationStatus;
                    if (confirmation == "confirmed" || confirmation == "finalized" || (info.Confirmations.HasValue && info.Confirmations.Value > 0))
                    {
                        return signature;
                    }
                }

                await Task.Delay(ConfirmationPollDelayMs, ct);
            }

            Debug.LogWarning($"[SolanaScoundrelBridge] Timed out waiting for confirmation of {signature}.");
            return null;
        }

        private Account ResolveConnectedPlayerAccount()
        {
            if (_overridePlayerAccount != null)
            {
                return _overridePlayerAccount;
            }

            if (Web3.Account != null)
            {
                return Web3.Account;
            }

            if (Web3.Wallet != null && Web3.Wallet.Account != null)
            {
                return Web3.Wallet.Account;
            }

            return null;
        }

        private Account EnsureLocalSimPlayerAccount()
        {
            if (_localSimPlayerAccount == null)
            {
                _localSimPlayerAccount = new Account();
            }
            return _localSimPlayerAccount;
        }

        private void EnsureLocalKeysInitialized()
        {
            if (_sessionSigner == null)
            {
                _sessionSigner = new Account();
                ActiveSessionPubkey = _sessionSigner.PublicKey.Key;
            }

            Account effectivePlayer = ResolveConnectedPlayerAccount() ?? EnsureLocalSimPlayerAccount();
            if (string.IsNullOrEmpty(ActivePlayerPubkey))
            {
                ActivePlayerPubkey = effectivePlayer.PublicKey.Key;
            }

            if (_dungeonRunPda == null && TryGetProgramPublicKey(out PublicKey progKey))
            {
                UpdateDerivedAddressesForPlayer(effectivePlayer.PublicKey);
            }
        }

        private IRpcClient GetOrCreateRpcClient()
        {
            if (!string.IsNullOrWhiteSpace(rpcUrl))
            {
                if (_cachedRpcClient == null || !string.Equals(_cachedRpcUrl, rpcUrl, StringComparison.Ordinal))
                {
                    _cachedRpcClient = ClientFactory.GetClient(rpcUrl);
                    _cachedRpcUrl = rpcUrl;
                }
                return _cachedRpcClient;
            }

            return Web3.Rpc;
        }

        private bool TryGetProgramPublicKey(out PublicKey key)
        {
            key = null;
            if (string.IsNullOrWhiteSpace(programId) || !PublicKey.IsValid(programId, false))
            {
                return false;
            }

            try
            {
                key = new PublicKey(programId);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void EmitStatus(string status)
        {
            LastStatusMessage = status;
            if (this != null)
            {
                OnBridgeStatusChanged?.Invoke(status);
            }
        }

        private static async void ObserveAsync(Task<bool> task, string context)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] Unhandled error in {context}: {ex.Message}");
            }
        }

        private static string ShortKey(string base58)
        {
            if (string.IsNullOrEmpty(base58) || base58.Length <= 10) return base58 ?? string.Empty;
            return $"{base58.Substring(0, 4)}..{base58.Substring(base58.Length - 4)}";
        }

        private static ushort ReadUInt16LE(byte[] buffer, ref int offset)
        {
            ushort val = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            offset += 2;
            return val;
        }

        private static uint ReadUInt32LE(byte[] buffer, ref int offset)
        {
            uint val = (uint)(
                buffer[offset] |
                (buffer[offset + 1] << 8) |
                (buffer[offset + 2] << 16) |
                (buffer[offset + 3] << 24));
            offset += 4;
            return val;
        }

        private static int ReadInt32LE(byte[] buffer, ref int offset)
        {
            return unchecked((int)ReadUInt32LE(buffer, ref offset));
        }

        private static ulong ReadUInt64LE(byte[] buffer, ref int offset)
        {
            ulong lo = ReadUInt32LE(buffer, ref offset);
            ulong hi = ReadUInt32LE(buffer, ref offset);
            return lo | (hi << 32);
        }

        #endregion
    }
}
