using System;
using System.Collections.Generic;
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
        private static readonly byte[] DiscFleeRoom = { 67, 216, 47, 229, 176, 138, 203, 97 };
        private static readonly byte[] DiscUseEpicPotion = { 80, 220, 203, 172, 136, 53, 36, 231 };
        private static readonly byte[] DiscResolveCard = { 7, 249, 238, 138, 126, 35, 54, 92 };
        private static readonly byte[] DiscClaimSpoils = { 132, 55, 110, 6, 235, 211, 149, 45 };
        private static readonly byte[] DiscCloseRun = { 31, 61, 130, 76, 74, 147, 171, 204 };
        private static readonly byte[] DiscDungeonRunAccount = { 105, 118, 160, 69, 75, 241, 13, 36 };

        private static readonly byte[] DungeonRunSeedPrefix = Encoding.UTF8.GetBytes("dungeon_run");

        // 0.0025 SOL covers 0-byte system account rent-exemption (890,880 lamports) + ~320 turn signatures
        private const ulong SessionFundingLamports = 2_500_000UL;
        private const int ConfirmationMaxAttempts = 60;
        private const int ConfirmationPollDelayMs = 250;

        [Header("Solana Network & Mode")]
        [SerializeField] private bool offlineSimulationMode = true;
        [SerializeField] private string rpcUrl = "https://api.devnet.solana.com";
        [SerializeField] private string programId = "7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx";

        public bool OfflineSimulationMode
        {
            get => offlineSimulationMode;
            set => offlineSimulationMode = value;
        }

        public string ActiveSessionPubkey { get; private set; } = string.Empty;
        public string ActivePlayerPubkey { get; private set; } = string.Empty;
        public string DungeonRunPdaAddress { get; private set; } = string.Empty;
        public string LastStatusMessage { get; private set; } = "Session Key Ready (Local Optimistic)";
        public string LastTransactionSignature { get; private set; } = string.Empty;
        public int SettledTransactionCount { get; private set; }

        public event Action<string> OnBridgeStatusChanged;

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
            }
        }

        #region Fire-and-Forget Dispatchers (Called by ScoundrelGameManager)

        public void DispatchStartRun(ulong seed, bool hasLegendaryNft, Action<DungeonRunState> onChainStateFetched = null)
        {
            ObserveAsync(StartRunAsync(seed, hasLegendaryNft, onChainStateFetched), "start_run");
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

        public void DispatchCloseRun()
        {
            ObserveAsync(CloseRunAsync(), "close_run");
        }

        #endregion

        #region Async Solana Instruction Methods

        public async Task<bool> StartRunAsync(ulong seed, bool hasLegendaryNft, Action<DungeonRunState> onChainStateFetched = null)
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
                EmitStatus("Session Key Active • Local Sim (start_run)");
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

                EmitStatus("Authorizing Ephemeral Session Key (start_run)...");

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

                var instructions = new List<TransactionInstruction>(3);

                // If a previous dungeon_run PDA already exists for this player, close it first to reclaim rent & re-init cleanly
                var existingAcctRes = await rpc.GetAccountInfoAsync(derivedPda.Key, Commitment.Confirmed, BinaryEncoding.Base64);
                AccountInfo existingInfo = existingAcctRes?.Result?.Value;
                if (existingInfo != null && existingInfo.Lamports > 0 && existingInfo.Owner == programPubKey.Key)
                {
                    instructions.Add(BuildCloseRunInstruction(connectedPlayer.PublicKey, derivedPda, programPubKey));
                }

                // Fund the fresh ephemeral session key so all subsequent room actions require zero wallet popups
                instructions.Add(SystemProgram.Transfer(
                    connectedPlayer.PublicKey,
                    turnSessionSigner.PublicKey,
                    SessionFundingLamports));

                instructions.Add(BuildStartRunInstruction(
                    connectedPlayer.PublicKey,
                    turnSessionSigner.PublicKey,
                    derivedPda,
                    programPubKey,
                    seed,
                    hasLegendaryNft));

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
                EmitStatus($"Session Key Active • On-Chain ({ShortKey(turnSessionSigner.PublicKey.Key)})");

                // Fetch confirmed DungeonRun PDA account and reconcile slot-salted deck & initial 4-card room
                DungeonRunState onChainState = await FetchAndDeserializeDungeonRunAsync(rpc, derivedPda, ct);
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

                var tx = new Transaction
                {
                    RecentBlockHash = recentBlockhash,
                    FeePayer = connectedPlayer.PublicKey,
                    Instructions = new List<TransactionInstruction>
                    {
                        BuildCloseRunInstruction(connectedPlayer.PublicKey, pda, programPubKey)
                    },
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
                EmitStatus($"Run Closed • Rent Reclaimed ({ShortKey(sig)})");
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

        public static TransactionInstruction BuildStartRunInstruction(
            PublicKey player,
            PublicKey sessionSigner,
            PublicKey dungeonRunPda,
            PublicKey programPubKey,
            ulong seed,
            bool hasLegendaryNft)
        {
            byte[] data = new byte[17];
            Buffer.BlockCopy(DiscStartRun, 0, data, 0, 8);
            WriteUInt64LE(data, 8, seed);
            data[16] = hasLegendaryNft ? (byte)1 : (byte)0;

            return new TransactionInstruction
            {
                ProgramId = programPubKey.KeyBytes,
                Keys = new List<AccountMeta>(4)
                {
                    AccountMeta.Writable(player, true),
                    AccountMeta.ReadOnly(sessionSigner, false),
                    AccountMeta.Writable(dungeonRunPda, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
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
        /// Verifies the 8-byte DungeonRun discriminator and parses variable-width Option&lt;u8&gt;[4] room slots.
        /// </summary>
        public static DungeonRunState DeserializeDungeonRun(byte[] data)
        {
            // Minimum possible Borsh byte length (when all 4 room Option<u8> slots are None = 4 bytes):
            // 8 (disc) + 32 (player) + 32 (session) + 44 (deck) + 2 (head/len) + 4 (room min) + 28 (remaining scalar fields) = 150 bytes
            if (data == null || data.Length < 150)
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

            var state = new DungeonRunState
            {
                PlayerPubkey = new PublicKey(playerBytes).Key,
                SessionSignerPubkey = new PublicKey(sessionBytes).Key,
                Deck = new byte[ScoundrelCardDecoder.DeckSize],
                Room = new int[4] { -1, -1, -1, -1 }
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

            // Remaining fixed-width tail is 28 bytes (through bump)
            if (offset + 28 > data.Length)
            {
                Debug.LogWarning("[SolanaScoundrelBridge] DungeonRun buffer truncated before scalar tail.");
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
            state.Status = rawStatus <= (byte)RunStatus.Defeat ? (RunStatus)rawStatus : RunStatus.Active;

            // Consume trailing bump byte
            _ = data[offset++];

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
                if (TryDeriveDungeonRunPda(effectivePlayer.PublicKey, progKey, out PublicKey pda, out _))
                {
                    _dungeonRunPda = pda;
                    DungeonRunPdaAddress = pda.Key;
                }
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

        private static void WriteUInt64LE(byte[] buffer, int offset, ulong value)
        {
            buffer[offset + 0] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 4] = (byte)((value >> 32) & 0xFF);
            buffer[offset + 5] = (byte)((value >> 40) & 0xFF);
            buffer[offset + 6] = (byte)((value >> 48) & 0xFF);
            buffer[offset + 7] = (byte)((value >> 56) & 0xFF);
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

        #endregion
    }
}
