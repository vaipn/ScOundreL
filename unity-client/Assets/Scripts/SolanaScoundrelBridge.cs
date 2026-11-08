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
        private static readonly byte[] DiscPrepareSessionVault = { 5, 4, 224, 190, 207, 17, 233, 245 };
        private static readonly byte[] DiscCloseRun = { 31, 61, 130, 76, 74, 147, 171, 204 };

        private static readonly byte[] DiscDungeonRunAccount = { 105, 118, 160, 69, 75, 241, 13, 36 };
        private static readonly byte[] DiscPlayerProfileAccount = { 82, 226, 99, 87, 164, 130, 181, 80 };
        private static readonly byte[] DiscLeaderboardAccount = { 247, 186, 238, 243, 194, 30, 9, 36 };

        private static readonly byte[] DungeonRunSeedPrefix = Encoding.UTF8.GetBytes("dungeon_run");
        private static readonly byte[] ProfileSeedPrefix = Encoding.UTF8.GetBytes("profile");
        private static readonly byte[] LeaderboardSeedPrefix = Encoding.UTF8.GetBytes("leaderboard");
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
        private const int VrfFulfillmentPollAttempts = 50;
        private const int VrfFulfillmentPollDelayMs = 800;

        [Header("Solana Network & Mode")]
        [SerializeField] private bool offlineSimulationMode = true;
        [SerializeField] private bool allowLocalnetVrfFallback = false;
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
        public string PlayerProfilePdaAddress { get; private set; } = string.Empty;
        public string LeaderboardPdaAddress { get; private set; } = string.Empty;
        public string GoldMintPdaAddress { get; private set; } = string.Empty;
        public string PlayerGoldAtaAddress { get; private set; } = string.Empty;

        public double LastKnownSolBalance { get; private set; }
        public ulong LastKnownMintedGoldBalance { get; private set; }

        public void AdjustMintedBalance(long delta)
        {
            if (delta < 0)
            {
                ulong sub = (ulong)(-delta);
                LastKnownMintedGoldBalance = LastKnownMintedGoldBalance >= sub ? LastKnownMintedGoldBalance - sub : 0UL;
            }
            else
            {
                LastKnownMintedGoldBalance += (ulong)delta;
            }
            OnWalletStateChanged?.Invoke();
        }
        public bool HasConnectedWallet => ResolveConnectedPlayerAccount() != null;
        public bool HasActiveOnChainSession => _hasActiveOnChainSession;
        public string LastStatusMessage { get; private set; } = "Session Key Ready (Local VRF Sim)";
        public string LastTransactionSignature { get; private set; } = string.Empty;
        public int SettledTransactionCount { get; private set; }

        public PlayerProfileData CurrentProfile { get; private set; } = new PlayerProfileData();
        public LeaderboardData CurrentLeaderboard { get; private set; } = new LeaderboardData();

        public event Action<string> OnBridgeStatusChanged;
        public event Action OnWalletStateChanged;
        public event Action<PlayerProfileData> OnPlayerProfileChanged;
        public event Action<LeaderboardData> OnLeaderboardChanged;

        private readonly SemaphoreSlim _txMutex = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _lifecycleCts;

        private Account _localSimPlayerAccount;
        private Account _overridePlayerAccount;
        private Account _sessionSigner;
        private PublicKey _dungeonRunPda;
        private PublicKey _playerProfilePda;
        private PublicKey _leaderboardPda;
        private PublicKey _goldMintPda;
        private PublicKey _playerGoldAta;

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

        public void SetPlayerAccount(Account playerAccount)
        {
            _overridePlayerAccount = IsWeb3WalletAccount(playerAccount) ? null : playerAccount;
            if (playerAccount != null)
            {
                UpdateDerivedAddressesForPlayer(playerAccount.PublicKey);
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
                _ = RefreshOnChainStatsAsync();
            }
            else
            {
                EmitStatus("Devnet Mode • Connect Wallet to Sign");
            }
            OnWalletStateChanged?.Invoke();
        }

        private const string Web3PrefabResourcePath = "SolanaUnitySDK/[WalletController]";
        private const int WalletLoginTimeoutSeconds = 180;

        private static bool EnsureWeb3Instance()
        {
            if (Web3.Instance != null) return true;

            GameObject prefab = Resources.Load<GameObject>(Web3PrefabResourcePath);
            if (prefab == null)
            {
                Debug.LogError($"[SolanaScoundrelBridge] Missing Resources/{Web3PrefabResourcePath}.prefab; wallet adapter unavailable.");
                return false;
            }

            GameObject go = Instantiate(prefab);
            go.name = "[WalletController]";
            DontDestroyOnLoad(go);
            return Web3.Instance != null;
        }

        private static bool IsWeb3WalletAccount(Account account)
        {
            return account != null
                && Web3.Wallet != null
                && Web3.Account != null
                && Web3.Account.PublicKey.Equals(account.PublicKey);
        }

        private static readonly HashSet<int> WiredWalletButtonIds = new HashSet<int>();
        private static int _wiredExitButtonId;
        private static Action _activePickerExitCallback;

        private static async Task<bool> MonitorAndWireWebGlPickerAsync(Task<Account> loginTask)
        {
            var cancelledTcs = new TaskCompletionSource<bool>();
            _activePickerExitCallback = () => cancelledTcs.TrySetResult(true);

            try
            {
                while (!loginTask.IsCompleted && !cancelledTcs.Task.IsCompleted)
                {
                    GameObject uiRoot = SolanaWalletAdapterWebGL.WalletAdapterUI;
                    if (uiRoot != null && uiRoot.activeInHierarchy)
                    {
                        WalletAdapterButton[] walletButtons = uiRoot.GetComponentsInChildren<WalletAdapterButton>(true);
                        if (walletButtons != null)
                        {
                            foreach (WalletAdapterButton wb in walletButtons)
                            {
                                if (wb == null) continue;
                                int id = wb.GetInstanceID();
                                if (WiredWalletButtonIds.Add(id))
                                {
                                    var btn = wb.GetComponent<UnityEngine.UI.Button>();
                                    if (btn != null)
                                    {
                                        btn.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                                        btn.onClick.AddListener(wb.OnSelected);
                                    }
                                }
                            }
                        }

                        Transform exitTr = uiRoot.transform.Find("WalletAdapterScreen/Exit Button");
                        if (exitTr != null)
                        {
                            var exitBtn = exitTr.GetComponent<UnityEngine.UI.Button>();
                            if (exitBtn != null && _wiredExitButtonId != exitBtn.GetInstanceID())
                            {
                                _wiredExitButtonId = exitBtn.GetInstanceID();
                                exitBtn.onClick.AddListener(() => _activePickerExitCallback?.Invoke());
                            }
                        }
                    }

                    await Task.Delay(50);
                }

                return cancelledTcs.Task.IsCompleted;
            }
            finally
            {
                _activePickerExitCallback = null;
            }
        }

        public async Task<bool> ConnectWalletAdapterAsync()
        {
            bool isWebGlPlayer = Application.platform == RuntimePlatform.WebGLPlayer;
            try
            {
                if (!EnsureWeb3Instance())
                {
                    EmitStatus("Wallet Adapter Unavailable • Use Devnet Burner");
                    return false;
                }

                EmitStatus("Connecting Solana Wallet Adapter...");

                if (isWebGlPlayer)
                {
                    await SolanaWalletAdapterWebGL.IsXnft();
                    SolanaWalletAdapterWebGL.WalletSpecs[] wallets = SolanaWalletAdapterWebGL.Wallets;
                    bool anyInstalled = false;
                    if (wallets != null)
                    {
                        foreach (SolanaWalletAdapterWebGL.WalletSpecs w in wallets)
                        {
                            if (w != null && w.installed)
                            {
                                anyInstalled = true;
                                break;
                            }
                        }
                    }

                    if (!anyInstalled)
                    {
                        EmitStatus("No Solana Wallet Detected • Install Phantom, Solflare or Backpack & Reload");
                        return false;
                    }
                }

                Task<Account> loginTask = Web3.Instance.LoginWalletAdapter();
                Task<bool> pickerCancelTask = isWebGlPlayer
                    ? MonitorAndWireWebGlPickerAsync(loginTask)
                    : new TaskCompletionSource<bool>().Task;
                Task finished = await Task.WhenAny(loginTask, pickerCancelTask, Task.Delay(TimeSpan.FromSeconds(WalletLoginTimeoutSeconds)));
                if (finished == pickerCancelTask && await pickerCancelTask)
                {
                    EmitStatus("Wallet Connection Cancelled");
                    return false;
                }
                if (finished != loginTask)
                {
                    EmitStatus("Wallet Connection Timed Out • Try Again");
                    return false;
                }

                Account acct = await loginTask;
                if (acct == null)
                {
                    EmitStatus("Wallet Connection Cancelled");
                    return false;
                }

                _overridePlayerAccount = null;
                offlineSimulationMode = false;
                UpdateDerivedAddressesForPlayer(acct.PublicKey);
                EmitStatus($"Wallet Connected • {ShortKey(acct.PublicKey.Key)} (Devnet)");
                OnWalletStateChanged?.Invoke();
                await RefreshBalancesAsync();
                await RefreshOnChainStatsAsync();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] ConnectWalletAdapterAsync failed: {ex.Message}");

                if (!isWebGlPlayer)
                {
                    Account burner = LoadOrCreateDevnetBurnerAccount();
                    SetPlayerAccount(burner);
                    offlineSimulationMode = false;
                    EmitStatus($"Devnet Wallet Active • {ShortKey(burner.PublicKey.Key)}");
                    await RefreshBalancesAsync();
                    await RefreshOnChainStatsAsync();
                    return true;
                }

                EmitStatus(ex.Message.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Wallet Connection Cancelled"
                    : "Wallet Connection Failed • Use Devnet Burner or Retry");
                return false;
            }
        }

        public async Task<bool> ConnectDevnetBurnerWalletAsync(bool requestAirdropIfLow = false)
        {
            Account burner = LoadOrCreateDevnetBurnerAccount();
            SetPlayerAccount(burner);
            offlineSimulationMode = false;
            EmitStatus($"Devnet Burner Ready • {ShortKey(burner.PublicKey.Key)}");
            await RefreshBalancesAsync();
            await RefreshOnChainStatsAsync();

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
                // Transient RPC query ignore
            }

            OnWalletStateChanged?.Invoke();
        }

        public async Task RefreshOnChainStatsAsync()
        {
            if (offlineSimulationMode) return;

            IRpcClient rpc = GetOrCreateRpcClient();
            if (rpc == null) return;

            try
            {
                if (!string.IsNullOrEmpty(PlayerProfilePdaAddress))
                {
                    var profRes = await rpc.GetAccountInfoAsync(PlayerProfilePdaAddress, Commitment.Confirmed, BinaryEncoding.Base64);
                    var data = profRes?.Result?.Value?.Data;
                    if (data != null && data.Count > 0 && !string.IsNullOrEmpty(data[0]))
                    {
                        byte[] raw = Convert.FromBase64String(data[0]);
                        PlayerProfileData prof = DeserializePlayerProfile(raw);
                        if (prof != null)
                        {
                            CurrentProfile = prof;
                            OnPlayerProfileChanged?.Invoke(prof);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(LeaderboardPdaAddress))
                {
                    var leadRes = await rpc.GetAccountInfoAsync(LeaderboardPdaAddress, Commitment.Confirmed, BinaryEncoding.Base64);
                    var data = leadRes?.Result?.Value?.Data;
                    if (data != null && data.Count > 0 && !string.IsNullOrEmpty(data[0]))
                    {
                        byte[] raw = Convert.FromBase64String(data[0]);
                        LeaderboardData lead = DeserializeLeaderboard(raw);
                        if (lead != null)
                        {
                            CurrentLeaderboard = lead;
                            OnLeaderboardChanged?.Invoke(lead);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] RefreshOnChainStatsAsync warning: {ex.Message}");
            }
        }

        public void OpenExplorerForAddress(string pubkey)
        {
            if (string.IsNullOrEmpty(pubkey)) return;
            Application.OpenURL($"https://explorer.solana.com/address/{pubkey}?cluster=devnet");
        }

        public void OpenExplorerForTx(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return;
            Application.OpenURL($"https://explorer.solana.com/tx/{signature}?cluster=devnet");
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
            catch (Exception) { }

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
                if (TryDerivePlayerProfilePda(playerPubKey, progKey, out PublicKey profilePda, out _))
                {
                    _playerProfilePda = profilePda;
                    PlayerProfilePdaAddress = profilePda.Key;
                }
                if (TryDeriveLeaderboardPda(progKey, out PublicKey leadPda, out _))
                {
                    _leaderboardPda = leadPda;
                    LeaderboardPdaAddress = leadPda.Key;
                }
                if (TryDeriveGoldMintPda(progKey, out PublicKey goldMint, out _))
                {
                    _goldMintPda = goldMint;
                    GoldMintPdaAddress = goldMint.Key;
                    if (TryDeriveAssociatedTokenAddress(playerPubKey, goldMint, out PublicKey ata))
                    {
                        _playerGoldAta = ata;
                        PlayerGoldAtaAddress = ata.Key;
                    }
                }
            }
        }

        #region Fire-and-Forget Dispatchers

        public void DispatchStartRun(byte clientSeed, Action<DungeonRunState> onChainStateFetched = null)
        {
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

            _sessionSigner = new Account();
            Account turnSessionSigner = _sessionSigner;
            ActiveSessionPubkey = turnSessionSigner.PublicKey.Key;

            Account connectedPlayer = ResolveConnectedPlayerAccount();
            Account effectivePlayer = connectedPlayer ?? EnsureLocalSimPlayerAccount();
            ActivePlayerPubkey = effectivePlayer.PublicKey.Key;

            if (!TryGetProgramPublicKey(out PublicKey programPubKey))
            {
                _hasActiveOnChainSession = false;
                EmitStatus("Invalid Program ID • Fallback Local Sim");
                return false;
            }

            UpdateDerivedAddressesForPlayer(effectivePlayer.PublicKey);

            if (offlineSimulationMode)
            {
                _hasActiveOnChainSession = false;
                SettledTransactionCount++;
                EmitStatus("MagicBlock VRF Seeded • Local Sim (start_run)");
                return true;
            }

            if (connectedPlayer == null)
            {
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
                    _hasActiveOnChainSession = false;
                    EmitStatus("RPC Unavailable • Local Sim Fallback");
                    return true;
                }

                // Balance review before prompting Phantom (Rule W009)
                var balRes = await rpc.GetBalanceAsync(connectedPlayer.PublicKey.Key, Commitment.Confirmed);
                ulong balanceLamports = balRes?.Result?.Value ?? 0UL;
                if (balanceLamports < 12_000_000UL) // Need ~0.012 SOL for session key topup + rent + fees
                {
                    _hasActiveOnChainSession = false;
                    EmitStatus("Need ≥ 0.015 Devnet SOL • Click Devnet Burner or Fund Wallet");
                    return false;
                }

                EmitStatus("Requesting MagicBlock VRF & Ephemeral Session Key...");

                var blockhashRes = await rpc.GetLatestBlockHashAsync(Commitment.Confirmed);
                string recentBlockhash = blockhashRes?.Result?.Value?.Blockhash;
                if (string.IsNullOrEmpty(recentBlockhash))
                {
                    _hasActiveOnChainSession = false;
                    EmitStatus("RPC Offline • Local Sim Fallback");
                    return true;
                }

                // Anchor's start_run uses init_if_needed: if the account already exists, it is reused and all 202 bytes
                // are reset in-place. Prepending close_run in the same transaction causes 'Allocate: account already in use'
                // simulation reverts in Phantom. Single start_run instruction ensures clean simulation.
                var instructions = new List<TransactionInstruction>(1)
                {
                    BuildStartRunInstruction(
                        connectedPlayer.PublicKey,
                        turnSessionSigner.PublicKey,
                        _dungeonRunPda,
                        _playerProfilePda,
                        programPubKey,
                        clientSeed)
                };

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
                    _hasActiveOnChainSession = false;
                    EmitStatus("Wallet Signature Cancelled");
                    return false;
                }

                string signature = await SendAndConfirmTransactionAsync(rpc, signedTx, ct);
                if (string.IsNullOrEmpty(signature))
                {
                    _hasActiveOnChainSession = false;
                    EmitStatus("start_run Failed on Devnet");
                    return false;
                }

                LastTransactionSignature = signature;
                _hasActiveOnChainSession = true;
                SettledTransactionCount++;
                EmitStatus("Awaiting MagicBlock VRF Oracle Proof...");

                // Poll for MagicBlock VRF oracle fulfillment (run_status 3 -> 0)
                DungeonRunState onChainState = null;
                bool isLocalValidator = !string.IsNullOrEmpty(rpcUrl) &&
                    (rpcUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                     rpcUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase));
                int maxVrfPolls = (isLocalValidator || allowLocalnetVrfFallback) ? 4 : VrfFulfillmentPollAttempts;

                for (int poll = 0; poll < maxVrfPolls; poll++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        onChainState = await FetchAndDeserializeDungeonRunAsync(rpc, _dungeonRunPda, ct);
                        if (onChainState != null && onChainState.Status != RunStatus.AwaitingVrf)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[SolanaScoundrelBridge] VRF poll {poll + 1}/{maxVrfPolls} transient error: {ex.Message}");
                    }
                    await Task.Delay(VrfFulfillmentPollDelayMs, ct);
                }

                if (onChainState == null || onChainState.Status == RunStatus.AwaitingVrf)
                {
                    Debug.LogWarning("[SolanaScoundrelBridge] MagicBlock VRF callback still pending; continuing with local optimistic session.");
                    _hasActiveOnChainSession = false;
                    EmitStatus("VRF Pending • Local Sim Active");
                    return true;
                }

                EmitStatus($"VRF Confirmed • Verified Proof ({onChainState.FormatVrfSeedShort()})");

                if (currentEpoch == _runEpoch && this != null)
                {
                    onChainStateFetched?.Invoke(onChainState);
                }

                _ = RefreshBalancesAsync();
                _ = RefreshOnChainStatsAsync();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] start_run exception: {ex.Message}");
                _hasActiveOnChainSession = false;
                EmitStatus("RPC Error • Local Sim Fallback (start_run)");
                return false;
            }
            finally
            {
                _txMutex.Release();
            }
        }

        public async Task<bool> PrepareSessionVaultAsync(ulong allowance = 500)
        {
            EnsureLocalKeysInitialized();
            Account connectedPlayer = ResolveConnectedPlayerAccount();
            if (connectedPlayer == null || !TryGetProgramPublicKey(out PublicKey programPubKey))
            {
                EmitStatus("Connect Wallet to Prepare Session Vault");
                return false;
            }

            UpdateDerivedAddressesForPlayer(connectedPlayer.PublicKey);
            CancellationToken ct = _lifecycleCts != null ? _lifecycleCts.Token : CancellationToken.None;

            await _txMutex.WaitAsync(ct);
            try
            {
                IRpcClient rpc = GetOrCreateRpcClient();
                if (rpc == null) return false;

                var blockhashRes = await rpc.GetLatestBlockHashAsync(Commitment.Confirmed);
                string recentBlockhash = blockhashRes?.Result?.Value?.Blockhash;
                if (string.IsNullOrEmpty(recentBlockhash)) return false;

                TransactionInstruction ix = BuildPrepareSessionVaultInstruction(
                    connectedPlayer.PublicKey,
                    _sessionSigner.PublicKey,
                    _dungeonRunPda,
                    _goldMintPda,
                    _playerGoldAta,
                    programPubKey,
                    allowance);

                var tx = new Transaction
                {
                    RecentBlockHash = recentBlockhash,
                    FeePayer = connectedPlayer.PublicKey,
                    Instructions = new List<TransactionInstruction>(1) { ix },
                    Signatures = new List<SignaturePubKeyPair>()
                };

                Transaction signedTx = await SignWithPlayerAsync(tx, connectedPlayer);
                if (signedTx == null) return false;

                EmitStatus("Approving Ephemeral Session Vault Delegate...");
                string sig = await SendAndConfirmTransactionAsync(rpc, signedTx, ct);
                if (string.IsNullOrEmpty(sig))
                {
                    EmitStatus("Session Vault Setup Failed");
                    return false;
                }

                LastTransactionSignature = sig;
                SettledTransactionCount++;
                EmitStatus($"Session Vault Approved ({allowance} SPL Gold Cap)");
                await RefreshBalancesAsync();
                return true;
            }
            finally
            {
                _txMutex.Release();
            }
        }

        public Task<bool> BuyItemAsync(byte itemId)
        {
            string itemLabel = itemId == 0 ? "Ofe Charm" : (itemId == 1 ? "Rusty Shield" : "Epic Potion");
            Account playerAcct = ResolveConnectedPlayerAccount() ?? EnsureLocalSimPlayerAccount();

            return ExecuteSessionGameplayInstructionAsync(
                $"buy_item ({itemLabel})",
                (signer, pda, prog) => BuildBuyItemInstruction(
                    signer,
                    playerAcct.PublicKey,
                    pda,
                    _playerProfilePda,
                    _goldMintPda,
                    _playerGoldAta,
                    prog,
                    itemId));
        }

        public Task<bool> UseMarketplaceItemAsync(byte itemId)
        {
            string itemLabel = itemId == 0 ? "Ofe Charm" : (itemId == 1 ? "Rusty Shield" : "Epic Potion");
            return ExecuteSessionGameplayInstructionAsync(
                $"use_item ({itemLabel})",
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
            Account playerAcct = ResolveConnectedPlayerAccount() ?? EnsureLocalSimPlayerAccount();

            return ExecuteSessionGameplayInstructionAsync(
                label,
                (signer, pda, prog) => BuildResolveCardInstruction(
                    signer,
                    playerAcct.PublicKey,
                    pda,
                    _playerProfilePda,
                    _leaderboardPda,
                    prog,
                    slotIndex,
                    useWeapon));
        }

        public Task<bool> ClaimSpoilsAsync(byte choice)
        {
            string label = $"claim_spoils #{choice + 1}";
            Account playerAcct = ResolveConnectedPlayerAccount() ?? EnsureLocalSimPlayerAccount();

            return ExecuteSessionGameplayInstructionAsync(
                label,
                (signer, pda, prog) => BuildClaimSpoilsInstruction(
                    signer,
                    playerAcct.PublicKey,
                    pda,
                    _playerProfilePda,
                    prog,
                    choice));
        }

        public async Task<bool> ClaimGoldTokensAsync(uint expectedGoldAmount = 0)
        {
            EnsureLocalKeysInitialized();

            if (offlineSimulationMode)
            {
                if (expectedGoldAmount > 0)
                {
                    LastKnownMintedGoldBalance += expectedGoldAmount;
                }
                SettledTransactionCount++;
                EmitStatus($"SPL Gold Minted • +{expectedGoldAmount} GOLD (Local Sim)");
                OnWalletStateChanged?.Invoke();
                return true;
            }

            Account connectedPlayer = ResolveConnectedPlayerAccount();
            if (connectedPlayer == null || !TryGetProgramPublicKey(out PublicKey programPubKey))
            {
                EmitStatus("Connect Wallet to Mint SPL Gold on Devnet");
                return false;
            }

            UpdateDerivedAddressesForPlayer(connectedPlayer.PublicKey);

            // Pre-validation: ensure active on-chain session exists to avoid Phantom simulation revert
            if (!_hasActiveOnChainSession || _dungeonRunPda == null)
            {
                EmitStatus("No active on-chain run • Deal a VRF run first");
                return false;
            }

            CancellationToken ct = _lifecycleCts != null ? _lifecycleCts.Token : CancellationToken.None;
            await _txMutex.WaitAsync(ct);
            try
            {
                IRpcClient rpc = GetOrCreateRpcClient();
                if (rpc == null) return false;

                // Pre-check on-chain DungeonRun state before requesting wallet signature (Rule W009)
                var runAcctRes = await rpc.GetAccountInfoAsync(_dungeonRunPda.Key, Commitment.Confirmed, BinaryEncoding.Base64);
                var runData = runAcctRes?.Result?.Value?.Data;
                if (runData == null || runData.Count == 0 || string.IsNullOrEmpty(runData[0]))
                {
                    EmitStatus("On-chain run not found • Start an On-Chain VRF Run first");
                    return false;
                }

                DungeonRunState liveRun = DeserializeDungeonRun(Convert.FromBase64String(runData[0]));
                if (liveRun == null || liveRun.UnclaimedGold == 0)
                {
                    EmitStatus("No unclaimed gold on-chain to mint");
                    return false;
                }

                var blockhashRes = await rpc.GetLatestBlockHashAsync(Commitment.Confirmed);
                string recentBlockhash = blockhashRes?.Result?.Value?.Blockhash;
                if (string.IsNullOrEmpty(recentBlockhash))
                {
                    EmitStatus("RPC Delay • Could not fetch blockhash");
                    return false;
                }

                TransactionInstruction ix = BuildClaimGoldTokensInstruction(
                    connectedPlayer.PublicKey,
                    connectedPlayer.PublicKey,
                    _sessionSigner.PublicKey,
                    _dungeonRunPda,
                    _playerProfilePda,
                    _goldMintPda,
                    _playerGoldAta,
                    programPubKey);

                var tx = new Transaction
                {
                    RecentBlockHash = recentBlockhash,
                    FeePayer = connectedPlayer.PublicKey,
                    Instructions = new List<TransactionInstruction>(1) { ix },
                    Signatures = new List<SignaturePubKeyPair>()
                };

                Transaction signedTx = await SignWithPlayerAsync(tx, connectedPlayer);
                if (signedTx == null)
                {
                    EmitStatus("Mint Signature Cancelled");
                    return false;
                }

                EmitStatus("Minting SPL Gold Tokens to ATA...");
                string sig = await SendAndConfirmTransactionAsync(rpc, signedTx, ct);
                if (string.IsNullOrEmpty(sig))
                {
                    EmitStatus("SPL Gold Mint Transaction Failed");
                    return false;
                }

                LastTransactionSignature = sig;
                SettledTransactionCount++;
                EmitStatus($"SPL Gold Minted to ATA ({ShortKey(sig)})");
                await RefreshBalancesAsync();
                await RefreshOnChainStatsAsync();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SolanaScoundrelBridge] claim_gold_tokens exception: {ex.Message}");
                EmitStatus("Mint Error • Check Devnet Explorer");
                return false;
            }
            finally
            {
                _txMutex.Release();
            }
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
                _hasActiveOnChainSession = false;
                return true;
            }

            UpdateDerivedAddressesForPlayer(connectedPlayer.PublicKey);
            CancellationToken ct = _lifecycleCts != null ? _lifecycleCts.Token : CancellationToken.None;

            await _txMutex.WaitAsync(ct);
            try
            {
                IRpcClient rpc = GetOrCreateRpcClient();
                if (rpc == null) return false;

                var blockhashRes = await rpc.GetLatestBlockHashAsync(Commitment.Confirmed);
                string recentBlockhash = blockhashRes?.Result?.Value?.Blockhash;
                if (string.IsNullOrEmpty(recentBlockhash)) return false;

                TransactionInstruction ix = BuildCloseRunInstruction(connectedPlayer.PublicKey, _dungeonRunPda, programPubKey);
                var tx = new Transaction
                {
                    RecentBlockHash = recentBlockhash,
                    FeePayer = connectedPlayer.PublicKey,
                    Instructions = new List<TransactionInstruction>(1) { ix },
                    Signatures = new List<SignaturePubKeyPair>()
                };

                Transaction signedTx = await SignWithPlayerAsync(tx, connectedPlayer);
                if (signedTx == null) return false;

                string sig = await SendAndConfirmTransactionAsync(rpc, signedTx, ct);
                if (string.IsNullOrEmpty(sig)) return false;

                LastTransactionSignature = sig;
                _hasActiveOnChainSession = false;
                SettledTransactionCount++;
                EmitStatus($"Run Closed • Rent Reclaimed ({ShortKey(sig)})");
                await RefreshBalancesAsync();
                return true;
            }
            finally
            {
                _txMutex.Release();
            }
        }

        #endregion

        #region Anchor Instruction Builders

        public static bool TryDeriveDungeonRunPda(PublicKey player, PublicKey prog, out PublicKey pda, out byte bump) =>
            PublicKey.TryFindProgramAddress(new[] { DungeonRunSeedPrefix, player.KeyBytes }, prog, out pda, out bump);

        public static bool TryDerivePlayerProfilePda(PublicKey player, PublicKey prog, out PublicKey pda, out byte bump) =>
            PublicKey.TryFindProgramAddress(new[] { ProfileSeedPrefix, player.KeyBytes }, prog, out pda, out bump);

        public static bool TryDeriveLeaderboardPda(PublicKey prog, out PublicKey pda, out byte bump) =>
            PublicKey.TryFindProgramAddress(new[] { LeaderboardSeedPrefix }, prog, out pda, out bump);

        public static bool TryDeriveProgramIdentityPda(PublicKey prog, out PublicKey pda, out byte bump) =>
            PublicKey.TryFindProgramAddress(new[] { ProgramIdentitySeedPrefix }, prog, out pda, out bump);

        public static bool TryDeriveGoldMintPda(PublicKey prog, out PublicKey pda, out byte bump) =>
            PublicKey.TryFindProgramAddress(new[] { GoldMintSeedPrefix }, prog, out pda, out bump);

        public static bool TryDeriveAssociatedTokenAddress(PublicKey wallet, PublicKey mint, out PublicKey ata)
        {
            ata = null;
            if (wallet == null || mint == null) return false;
            PublicKey tokenProg = new PublicKey(SplTokenProgramAddress);
            PublicKey ataProg = new PublicKey(AssociatedTokenProgramAddress);
            return PublicKey.TryFindProgramAddress(new[] { wallet.KeyBytes, tokenProg.KeyBytes, mint.KeyBytes }, ataProg, out ata, out _);
        }

        public static TransactionInstruction BuildStartRunInstruction(
            PublicKey player,
            PublicKey sessionSigner,
            PublicKey dungeonRunPda,
            PublicKey playerProfilePda,
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
                Keys = new List<AccountMeta>(9)
                {
                    AccountMeta.Writable(player, true),
                    AccountMeta.Writable(sessionSigner, false),
                    AccountMeta.Writable(dungeonRunPda, false),
                    AccountMeta.Writable(playerProfilePda, false),
                    AccountMeta.Writable(oracleQueue, false),
                    AccountMeta.ReadOnly(vrfProgram, false),
                    AccountMeta.ReadOnly(programIdentity, false),
                    AccountMeta.ReadOnly(slotHashes, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildPrepareSessionVaultInstruction(
            PublicKey player,
            PublicKey sessionSigner,
            PublicKey dungeonRunPda,
            PublicKey goldMintPda,
            PublicKey playerGoldAta,
            PublicKey programPubKey,
            ulong allowance)
        {
            byte[] data = new byte[16];
            Buffer.BlockCopy(DiscPrepareSessionVault, 0, data, 0, 8);
            byte[] allowanceBytes = BitConverter.GetBytes(allowance);
            Buffer.BlockCopy(allowanceBytes, 0, data, 8, 8);

            PublicKey tokenProgram = new PublicKey(SplTokenProgramAddress);
            PublicKey ataProgram = new PublicKey(AssociatedTokenProgramAddress);

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(8)
                {
                    AccountMeta.Writable(player, true),
                    AccountMeta.ReadOnly(sessionSigner, false),
                    AccountMeta.ReadOnly(dungeonRunPda, false),
                    AccountMeta.Writable(goldMintPda, false),
                    AccountMeta.Writable(playerGoldAta, false),
                    AccountMeta.ReadOnly(tokenProgram, false),
                    AccountMeta.ReadOnly(ataProgram, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildBuyItemInstruction(
            PublicKey signer,
            PublicKey player,
            PublicKey dungeonRunPda,
            PublicKey playerProfilePda,
            PublicKey goldMintPda,
            PublicKey playerGoldAta,
            PublicKey programPubKey,
            byte itemId)
        {
            byte[] data = new byte[9];
            Buffer.BlockCopy(DiscBuyItem, 0, data, 0, 8);
            data[8] = itemId;

            PublicKey tokenProgram = new PublicKey(SplTokenProgramAddress);

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(8)
                {
                    AccountMeta.Writable(signer, true),
                    AccountMeta.ReadOnly(player, false),
                    AccountMeta.Writable(dungeonRunPda, false),
                    AccountMeta.Writable(playerProfilePda, false),
                    AccountMeta.Writable(goldMintPda, false),
                    AccountMeta.Writable(playerGoldAta, false),
                    AccountMeta.ReadOnly(tokenProgram, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildResolveCardInstruction(
            PublicKey signer,
            PublicKey player,
            PublicKey dungeonRunPda,
            PublicKey playerProfilePda,
            PublicKey leaderboardPda,
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
                Keys = new List<AccountMeta>(6)
                {
                    AccountMeta.Writable(signer, true),
                    AccountMeta.ReadOnly(player, false),
                    AccountMeta.Writable(dungeonRunPda, false),
                    AccountMeta.Writable(playerProfilePda, false),
                    AccountMeta.Writable(leaderboardPda, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildClaimSpoilsInstruction(
            PublicKey signer,
            PublicKey player,
            PublicKey dungeonRunPda,
            PublicKey playerProfilePda,
            PublicKey programPubKey,
            byte choice)
        {
            byte[] data = new byte[9];
            Buffer.BlockCopy(DiscClaimSpoils, 0, data, 0, 8);
            data[8] = choice;

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(5)
                {
                    AccountMeta.Writable(signer, true),
                    AccountMeta.ReadOnly(player, false),
                    AccountMeta.Writable(dungeonRunPda, false),
                    AccountMeta.Writable(playerProfilePda, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
                },
                Data = data
            };
        }

        public static TransactionInstruction BuildClaimGoldTokensInstruction(
            PublicKey payer,
            PublicKey player,
            PublicKey sessionSigner,
            PublicKey dungeonRunPda,
            PublicKey playerProfilePda,
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
                Keys = new List<AccountMeta>(10)
                {
                    AccountMeta.Writable(payer, true),
                    AccountMeta.ReadOnly(player, false),
                    AccountMeta.ReadOnly(sessionSigner, false),
                    AccountMeta.Writable(dungeonRunPda, false),
                    AccountMeta.Writable(playerProfilePda, false),
                    AccountMeta.Writable(goldMintPda, false),
                    AccountMeta.Writable(playerGoldAta, false),
                    AccountMeta.ReadOnly(tokenProgram, false),
                    AccountMeta.ReadOnly(ataProgram, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
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

        #endregion

        #region Borsh Deserializers with Strict Account Validation (Rule W011)

        public static DungeonRunState DeserializeDungeonRun(byte[] data)
        {
            if (data == null || data.Length < 193) return null;

            for (int i = 0; i < 8; i++)
            {
                if (data[i] != DiscDungeonRunAccount[i]) return null;
            }

            int offset = 8;
            byte[] playerBytes = new byte[32];
            Buffer.BlockCopy(data, offset, playerBytes, 0, 32);
            offset += 32;

            byte[] sessionBytes = new byte[32];
            Buffer.BlockCopy(data, offset, sessionBytes, 0, 32);
            offset += 32;

            _ = data[offset++]; // bump

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
                if (offset >= data.Length) return null;
                byte tag = data[offset++];
                if (tag == 0) state.Room[slot] = -1;
                else if (tag == 1 && offset < data.Length) state.Room[slot] = data[offset++];
                else return null;
            }

            if (offset + 70 > data.Length) return null;

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

        public static PlayerProfileData DeserializePlayerProfile(byte[] data)
        {
            if (data == null || data.Length < 128) return null;

            for (int i = 0; i < 8; i++)
            {
                if (data[i] != DiscPlayerProfileAccount[i]) return null;
            }

            int offset = 8;
            byte[] playerBytes = new byte[32];
            Buffer.BlockCopy(data, offset, playerBytes, 0, 32);
            offset += 32;

            _ = data[offset++]; // bump

            var profile = new PlayerProfileData
            {
                PlayerPubkey = new PublicKey(playerBytes).Key,
                HasCompletedRun = data[offset++] != 0,
                RunsStarted = ReadUInt32LE(data, ref offset),
                Victories = ReadUInt32LE(data, ref offset),
                Defeats = ReadUInt32LE(data, ref offset),
                BestScore = ReadInt32LE(data, ref offset),
                MonstersSlain = ReadUInt32LE(data, ref offset),
                TotalGoldEarned = ReadUInt64LE(data, ref offset),
                TotalGoldMinted = ReadUInt64LE(data, ref offset),
                TotalGoldSpent = ReadUInt64LE(data, ref offset),
                TotalVrfRequests = ReadUInt32LE(data, ref offset),
                TotalVrfCombatRolls = ReadUInt64LE(data, ref offset),
                LastVrfSeed = new byte[32]
            };

            Buffer.BlockCopy(data, offset, profile.LastVrfSeed, 0, 32);
            return profile;
        }

        public static LeaderboardData DeserializeLeaderboard(byte[] data)
        {
            if (data == null || data.Length < 568) return null;

            for (int i = 0; i < 8; i++)
            {
                if (data[i] != DiscLeaderboardAccount[i]) return null;
            }

            int offset = 8;
            _ = data[offset++]; // bump
            byte entryCount = data[offset++];
            ulong totalRuns = ReadUInt64LE(data, ref offset);

            var leaderboard = new LeaderboardData
            {
                EntryCount = entryCount,
                TotalRunsCompleted = totalRuns,
                Entries = new List<LeaderboardEntryData>()
            };

            int count = Mathf.Min(10, (int)entryCount);
            for (int i = 0; i < 10; i++)
            {
                byte[] playerBytes = new byte[32];
                Buffer.BlockCopy(data, offset, playerBytes, 0, 32);
                offset += 32;

                int score = ReadInt32LE(data, ref offset);
                ushort hp = ReadUInt16LE(data, ref offset);
                bool vic = data[offset++] != 0;

                byte[] prefix = new byte[8];
                Buffer.BlockCopy(data, offset, prefix, 0, 8);
                offset += 8;

                long ts = ReadInt64LE(data, ref offset);

                if (i < count)
                {
                    leaderboard.Entries.Add(new LeaderboardEntryData
                    {
                        PlayerPubkey = new PublicKey(playerBytes).Key,
                        Score = score,
                        HpTenths = hp,
                        Victory = vic,
                        VrfSeedPrefix = prefix,
                        Timestamp = ts
                    });
                }
            }

            return leaderboard;
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
                SettledTransactionCount++;
                EmitStatus($"Session Key Signed • {actionLabel} (Local Sim)");
                return true;
            }

            if (!TryGetProgramPublicKey(out PublicKey programPubKey)) return false;

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
                if (string.IsNullOrEmpty(recentBlockhash))
                {
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

        public void MarkActiveOnChainSession(bool active) => _hasActiveOnChainSession = active;
        public void SetStatusMessage(string message) => EmitStatus(message);

        public async Task<DungeonRunState> FetchCurrentDungeonRunAsync(CancellationToken ct = default)
        {
            IRpcClient rpc = GetOrCreateRpcClient();
            if (rpc == null || _dungeonRunPda == null) return null;
            return await FetchAndDeserializeDungeonRunAsync(rpc, _dungeonRunPda, ct);
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
            return null;
        }

        private async Task<Transaction> SignWithPlayerAsync(Transaction tx, Account playerAccount)
        {
            if (IsWeb3WalletAccount(playerAccount))
            {
                try
                {
                    return await Web3.Wallet.SignTransaction(tx);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SolanaScoundrelBridge] Wallet signature rejected: {ex.Message}");
                    return null;
                }
            }

            if (_overridePlayerAccount != null && playerAccount == _overridePlayerAccount)
            {
                tx.Sign(_overridePlayerAccount);
                return tx;
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
                    if (info.Error != null) return null;
                    string confirmation = info.ConfirmationStatus;
                    if (confirmation == "confirmed" || confirmation == "finalized" || (info.Confirmations.HasValue && info.Confirmations.Value > 0))
                    {
                        return signature;
                    }
                }
                await Task.Delay(ConfirmationPollDelayMs, ct);
            }

            return null;
        }

        private Account ResolveConnectedPlayerAccount()
        {
            if (_overridePlayerAccount != null) return _overridePlayerAccount;
            if (Web3.Account != null) return Web3.Account;
            if (Web3.Wallet != null && Web3.Wallet.Account != null) return Web3.Wallet.Account;
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

            if (_dungeonRunPda == null)
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
            if (string.IsNullOrWhiteSpace(programId) || !PublicKey.IsValid(programId, false)) return false;
            try
            {
                key = new PublicKey(programId);
                return true;
            }
            catch
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

        public static string ShortKey(string base58)
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

        private static long ReadInt64LE(byte[] buffer, ref int offset)
        {
            return unchecked((long)ReadUInt64LE(buffer, ref offset));
        }

        #endregion
    }
}
