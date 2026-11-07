using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Scoundrel
{
    [Serializable] public class MonsterSlainUnityEvent : UnityEvent<CardData, CombatOutcome, float, WeaponSkinTier> { }
    [Serializable] public class WeaponEquippedUnityEvent : UnityEvent<CardData, WeaponSkinTier> { }
    [Serializable] public class CardsDiscardedUnityEvent : UnityEvent<List<CardData>> { }
    [Serializable] public class PotionDrankUnityEvent : UnityEvent<float, bool> { }
    [Serializable] public class EpicPotionActivatedUnityEvent : UnityEvent<int> { }
    [Serializable] public class RoomClearedUnityEvent : UnityEvent<int, SpoilsTier, float> { }
    [Serializable] public class GameOverUnityEvent : UnityEvent<bool, int> { }

    public class ScoundrelGameManager : MonoBehaviour
    {
        public static ScoundrelGameManager Instance { get; private set; }

        [Header("Dependencies")]
        [SerializeField] private SolanaScoundrelBridge solanaBridge;

        [Header("Run Configuration")]
        [SerializeField] private bool autoStartOnAwake = true;
        [Tooltip("Temporarily disabled for gameplay balance; set true to re-enable Legendary Weapon unlocks & Forged Edge charges.")]
        [SerializeField] private bool enableLegendaryWeapons = false;
        [SerializeField] private bool startWithLegendaryNft = false;
        [Tooltip("Optional starter Epic Potion in Offline Demo so judges can immediately test Balatro consumable slot too; 0 matches pure fresh chain state.")]
        [SerializeField] private byte demoStartingEpicPotions = 1;

        public bool EnableLegendaryWeapons => enableLegendaryWeapons;

        [Header("UnityEvent Hooks (Inspector)")]
        [SerializeField] private UnityEvent onRoomDealt = new UnityEvent();
        [SerializeField] private MonsterSlainUnityEvent onMonsterSlain = new MonsterSlainUnityEvent();
        [SerializeField] private WeaponEquippedUnityEvent onWeaponEquipped = new WeaponEquippedUnityEvent();
        [SerializeField] private CardsDiscardedUnityEvent onCardsDiscarded = new CardsDiscardedUnityEvent();
        [SerializeField] private PotionDrankUnityEvent onPotionDrank = new PotionDrankUnityEvent();
        [SerializeField] private EpicPotionActivatedUnityEvent onEpicPotionActivated = new EpicPotionActivatedUnityEvent();
        [SerializeField] private RoomClearedUnityEvent onRoomCleared = new RoomClearedUnityEvent();
        [SerializeField] private GameOverUnityEvent onGameOver = new GameOverUnityEvent();

        // C# Action Hooks (60fps code-first subscribers)
        public event Action OnRoomDealt;
        public event Action<CardData, CombatOutcome, float, WeaponSkinTier> OnMonsterSlain;
        public event Action<CardData, WeaponSkinTier> OnWeaponEquipped;
        public event Action<List<CardData>> OnCardsDiscarded;
        public event Action<float, bool> OnPotionDrank;
        public event Action<int> OnEpicPotionActivated;
        public event Action<int, SpoilsTier, float> OnRoomCleared;
        public event Action<bool, int> OnGameOver;

        // Additional tabletop UI synchronization hooks
        public event Action<DungeonRunState> OnStateChanged;
        public event Action<List<CardData>> OnRoomAvoided;
        public event Action<int, CardData> OnRoomSlotResolved;

        public DungeonRunState State { get; private set; } = new DungeonRunState();
        public SolanaScoundrelBridge Bridge => solanaBridge;
        public int LastResolvedSlotIndex { get; private set; } = -1;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            if (solanaBridge == null)
            {
                solanaBridge = GetComponent<SolanaScoundrelBridge>();
            }
        }

        private void Start()
        {
            if (autoStartOnAwake)
            {
                StartNewRun(null, startWithLegendaryNft);
            }
        }

        public void AssignBridge(SolanaScoundrelBridge bridge)
        {
            solanaBridge = bridge;
        }

        public void StartNewRun(ulong? explicitSeed = null, bool? overrideLegendaryNft = null)
        {
            ulong seed = explicitSeed ?? (ulong)DateTime.UtcNow.Ticks ^ (ulong)UnityEngine.Random.Range(1, int.MaxValue);
            bool hasLegendary = enableLegendaryWeapons && (overrideLegendaryNft ?? startWithLegendaryNft);

            State = new DungeonRunState
            {
                HpTenths = 200,
                MaxHpTenths = 200,
                EquippedWeaponRank = 0,
                EquippedWeaponCardId = -1,
                WeaponCeiling = 0,
                HasLegendarySkin = hasLegendary,
                LegendaryForgedEdgeCharges = hasLegendary ? (byte)1 : (byte)0,
                PotionUsedThisRoom = false,
                FirstPotionHealedTenths = 0,
                EpicPotionsInventory = (solanaBridge == null || solanaBridge.OfflineSimulationMode) ? demoStartingEpicPotions : (byte)0,
                EpicPotionBuffRoomsLeft = 0,
                FledLastRoom = false,
                RoomThreatAccumulator = 0,
                PendingSpoilsThreat = 0,
                UnclaimedGold = 0,
                Score = 0,
                Status = RunStatus.Active,
                CardsResolvedInRoom = 0,
                DiscardCount = 0
            };

            for (byte i = 0; i < ScoundrelCardDecoder.DeckSize; i++)
            {
                State.Deck[i] = i;
            }

            ulong rngState = seed;
            for (int i = ScoundrelCardDecoder.DeckSize - 1; i >= 1; i--)
            {
                ulong randVal = SplitMix64(ref rngState);
                int j = (int)(randVal % (ulong)(i + 1));
                byte tmp = State.Deck[i];
                State.Deck[i] = State.Deck[j];
                State.Deck[j] = tmp;
            }

            State.DeckHead = 0;
            State.DeckLen = ScoundrelCardDecoder.DeckSize;
            for (int slot = 0; slot < 4; slot++)
            {
                State.Room[slot] = DrawCardFromTop(State);
            }

            if (solanaBridge != null)
            {
                solanaBridge.DispatchStartRun(seed, hasLegendary, ReconcileFromOnChainState);
                State.SessionSignerPubkey = solanaBridge.ActiveSessionPubkey;
            }

            NotifyStateChanged();
            InvokeRoomDealt();
        }

        public void ReconcileFromOnChainState(DungeonRunState onChainState)
        {
            if (onChainState == null) return;

            // Preserve visual weapon stack if weapon rank matches
            List<CardData> existingStack = State.StackedMonstersOnWeapon;
            int existingWeaponCardId = State.EquippedWeaponCardId;
            int existingDiscardCount = State.DiscardCount;

            State = onChainState;
            State.StackedMonstersOnWeapon = existingStack ?? new List<CardData>();
            if (State.EquippedWeaponRank > 0 && existingWeaponCardId >= 0)
            {
                State.EquippedWeaponCardId = existingWeaponCardId;
            }
            State.DiscardCount = existingDiscardCount;

            NotifyStateChanged();
            InvokeRoomDealt();
        }

        public bool TryAvoidRoom()
        {
            if (!State.CanAvoidRoom) return false;

            List<CardData> returnedCards = new List<CardData>(4);
            for (int slot = 0; slot < 4; slot++)
            {
                if (State.Room[slot] >= 0)
                {
                    byte cardId = (byte)State.Room[slot];
                    State.Room[slot] = -1;
                    returnedCards.Add(ScoundrelCardDecoder.Decode(cardId));
                    PushCardToBottom(State, cardId);
                }
            }

            for (int slot = 0; slot < 4; slot++)
            {
                State.Room[slot] = DrawCardFromTop(State);
            }

            State.FledLastRoom = true;
            State.PotionUsedThisRoom = false;
            State.RoomThreatAccumulator = 0;
            State.CardsResolvedInRoom = 0;

            OnRoomAvoided?.Invoke(returnedCards);

            if (solanaBridge != null)
            {
                solanaBridge.DispatchFleeRoom();
            }

            NotifyStateChanged();
            InvokeRoomDealt();
            return true;
        }

        public bool TryUseEpicPotion()
        {
            if (!State.CanUseEpicPotion) return false;

            State.EpicPotionsInventory -= 1;
            State.EpicPotionBuffRoomsLeft = 3;

            if (solanaBridge != null)
            {
                solanaBridge.DispatchUseEpicPotion();
            }

            NotifyStateChanged();
            OnEpicPotionActivated?.Invoke(State.EpicPotionBuffRoomsLeft);
            onEpicPotionActivated?.Invoke(State.EpicPotionBuffRoomsLeft);
            return true;
        }

        public bool TryResolveCard(int slotIndex, bool useWeapon)
        {
            if (State.Status != RunStatus.Active) return false;
            if (State.PendingSpoilsThreat > 0) return false;
            if (slotIndex < 0 || slotIndex >= 4) return false;
            if (State.Room[slotIndex] < 0) return false;

            byte cardId = (byte)State.Room[slotIndex];
            CardData card = ScoundrelCardDecoder.Decode(cardId);

            // Pre-validate weapon legality before mutating room slot
            if (card.IsMonster && useWeapon && !State.CanUseWeaponAgainst(card.Rank))
            {
                return false;
            }

            State.Room[slotIndex] = -1;
            LastResolvedSlotIndex = slotIndex;
            OnRoomSlotResolved?.Invoke(slotIndex, card);

            switch (card.Type)
            {
                case CardType.Monster:
                {
                    int monsterRank = card.Rank;
                    ushort damageTenths;
                    WeaponSkinTier skinTier = State.ActiveWeaponSkinTier;

                    if (useWeapon)
                    {
                        byte prevCeiling = State.WeaponCeiling;
                        int rawDamageRanks = Mathf.Max(0, monsterRank - State.EquippedWeaponRank);
                        damageTenths = (ushort)(rawDamageRanks * 10);

                        if (monsterRank > prevCeiling && State.LegendaryForgedEdgeCharges > 0)
                        {
                            State.LegendaryForgedEdgeCharges -= 1;
                        }

                        bool closeToCeilingHighRank = (Mathf.Max(0, prevCeiling - monsterRank) <= 2) && (monsterRank >= 8);
                        bool tookChipDamage = monsterRank > State.EquippedWeaponRank;
                        int bonusThreat = (closeToCeilingHighRank || tookChipDamage) ? 3 : 0;

                        State.RoomThreatAccumulator = (byte)Mathf.Min(255, State.RoomThreatAccumulator + monsterRank + bonusThreat);
                        State.WeaponCeiling = (byte)monsterRank;
                        State.HpTenths = (ushort)Mathf.Max(0, State.HpTenths - damageTenths);
                        State.StackedMonstersOnWeapon.Add(card);

                        CombatOutcome outcome = (damageTenths == 0)
                            ? CombatOutcome.ObliteratedWithWeapon
                            : CombatOutcome.ChipDamageWithWeapon;

                        float dmgFloat = damageTenths / 10f;
                        OnMonsterSlain?.Invoke(card, outcome, dmgFloat, skinTier);
                        onMonsterSlain?.Invoke(card, outcome, dmgFloat, skinTier);
                    }
                    else
                    {
                        damageTenths = (ushort)(monsterRank * 10);
                        State.RoomThreatAccumulator = (byte)Mathf.Min(255, State.RoomThreatAccumulator + monsterRank + 5);
                        State.HpTenths = (ushort)Mathf.Max(0, State.HpTenths - damageTenths);
                        State.DiscardCount += 1;

                        float dmgFloat = damageTenths / 10f;
                        OnMonsterSlain?.Invoke(card, CombatOutcome.BarehandedHit, dmgFloat, skinTier);
                        onMonsterSlain?.Invoke(card, CombatOutcome.BarehandedHit, dmgFloat, skinTier);

                        List<CardData> singleDiscard = new List<CardData> { card };
                        InvokeCardsDiscarded(singleDiscard);
                    }

                    if (State.HpTenths == 0)
                    {
                        State.Status = RunStatus.Defeat;
                        State.Score = ComputeDefeatScore(State);

                        if (solanaBridge != null)
                        {
                            solanaBridge.DispatchResolveCard((byte)slotIndex, useWeapon);
                        }

                        NotifyStateChanged();
                        InvokeGameOver(false, State.Score);
                        return true;
                    }
                    break;
                }

                case CardType.Weapon:
                {
                    List<CardData> discardedStack = new List<CardData>();
                    if (State.EquippedWeaponCardId >= 0)
                    {
                        discardedStack.Add(ScoundrelCardDecoder.Decode((byte)State.EquippedWeaponCardId));
                    }
                    if (State.StackedMonstersOnWeapon.Count > 0)
                    {
                        discardedStack.AddRange(State.StackedMonstersOnWeapon);
                        State.StackedMonstersOnWeapon.Clear();
                    }

                    if (discardedStack.Count > 0)
                    {
                        State.DiscardCount += discardedStack.Count;
                        InvokeCardsDiscarded(discardedStack);
                    }

                    State.EquippedWeaponRank = (byte)card.Rank;
                    State.EquippedWeaponCardId = card.CardId;
                    State.WeaponCeiling = 14;
                    if (enableLegendaryWeapons && State.HasLegendarySkin)
                    {
                        State.LegendaryForgedEdgeCharges = 1;
                    }

                    WeaponSkinTier skinTier = State.ActiveWeaponSkinTier;
                    OnWeaponEquipped?.Invoke(card, skinTier);
                    onWeaponEquipped?.Invoke(card, skinTier);
                    break;
                }

                case CardType.Potion:
                {
                    if (!State.PotionUsedThisRoom)
                    {
                        ushort rawHealTenths = (ushort)(card.Rank * 10);
                        ushort prevHp = State.HpTenths;
                        State.HpTenths = (ushort)Mathf.Min(State.MaxHpTenths, State.HpTenths + rawHealTenths);
                        State.PotionUsedThisRoom = true;
                        State.FirstPotionHealedTenths = rawHealTenths;

                        float actualHealed = (State.HpTenths - prevHp) / 10f;
                        OnPotionDrank?.Invoke(actualHealed, false);
                        onPotionDrank?.Invoke(actualHealed, false);
                    }
                    else
                    {
                        OnPotionDrank?.Invoke(0f, true);
                        onPotionDrank?.Invoke(0f, true);
                    }

                    State.DiscardCount += 1;
                    InvokeCardsDiscarded(new List<CardData> { card });
                    break;
                }
            }

            State.CardsResolvedInRoom += 1;
            bool allSlotsEmpty = State.IsRoomEmpty();
            bool roomComplete = (State.CardsResolvedInRoom == 3) || (State.DeckLen == 0 && allSlotsEmpty);

            if (roomComplete)
            {
                float echoHealedFloat = 0f;
                if (State.EpicPotionBuffRoomsLeft > 0 && State.FirstPotionHealedTenths > 0)
                {
                    ushort echoTenths = (ushort)(State.FirstPotionHealedTenths / 10);
                    ushort prevHp = State.HpTenths;
                    State.HpTenths = (ushort)Mathf.Min(State.MaxHpTenths, State.HpTenths + echoTenths);
                    State.EpicPotionBuffRoomsLeft -= 1;
                    echoHealedFloat = (State.HpTenths - prevHp) / 10f;
                }

                if (State.DeckLen == 0 && allSlotsEmpty)
                {
                    State.Status = RunStatus.Victory;
                    int baseScore = State.HpTenths / 10;
                    int potionBonus = (State.HpTenths == State.MaxHpTenths && card.IsPotion) ? card.Rank : 0;
                    State.Score = baseScore + potionBonus;

                    if (solanaBridge != null)
                    {
                        solanaBridge.DispatchResolveCard((byte)slotIndex, useWeapon);
                    }

                    NotifyStateChanged();
                    InvokeGameOver(true, State.Score);
                    return true;
                }
                else
                {
                    byte threat = (byte)Mathf.Max(1, (int)State.RoomThreatAccumulator);
                    State.PendingSpoilsThreat = threat;
                    State.RoomThreatAccumulator = 0;
                    State.CardsResolvedInRoom = 0;
                    State.PotionUsedThisRoom = false;
                    State.FledLastRoom = false;

                    SpoilsTier tier = ScoundrelCardDecoder.EvaluateSpoilsTier(threat);

                    if (solanaBridge != null)
                    {
                        solanaBridge.DispatchResolveCard((byte)slotIndex, useWeapon);
                    }

                    NotifyStateChanged();
                    OnRoomCleared?.Invoke(threat, tier, echoHealedFloat);
                    onRoomCleared?.Invoke(threat, tier, echoHealedFloat);
                    return true;
                }
            }

            if (solanaBridge != null)
            {
                solanaBridge.DispatchResolveCard((byte)slotIndex, useWeapon);
            }

            NotifyStateChanged();
            return true;
        }

        public bool TryClaimSpoils(int choiceIndex)
        {
            if (State.Status != RunStatus.Active) return false;
            int threat = State.PendingSpoilsThreat;
            if (threat <= 0) return false;

            SpoilsTier tier = ScoundrelCardDecoder.EvaluateSpoilsTier(threat);
            switch (tier)
            {
                case SpoilsTier.Low:
                    if (choiceIndex != 0) return false;
                    State.UnclaimedGold += 15;
                    break;

                case SpoilsTier.Medium:
                    if (choiceIndex == 0)
                    {
                        State.UnclaimedGold += 40;
                    }
                    else if (choiceIndex == 1)
                    {
                        State.EpicPotionsInventory = (byte)Mathf.Min(255, State.EpicPotionsInventory + 1);
                    }
                    else
                    {
                        return false;
                    }
                    break;

                case SpoilsTier.High:
                    if (choiceIndex == 0)
                    {
                        State.UnclaimedGold += 85;
                    }
                    else if (choiceIndex == 1)
                    {
                        State.EpicPotionsInventory = (byte)Mathf.Min(255, State.EpicPotionsInventory + 1);
                        State.UnclaimedGold += 25;
                    }
                    else if (choiceIndex == 2)
                    {
                        if (!enableLegendaryWeapons) return false;
                        State.HasLegendarySkin = true;
                        State.LegendaryForgedEdgeCharges = 1;
                    }
                    else
                    {
                        return false;
                    }
                    break;
            }

            State.PendingSpoilsThreat = 0;

            for (int slot = 0; slot < 4; slot++)
            {
                if (State.Room[slot] < 0)
                {
                    int drawn = DrawCardFromTop(State);
                    if (drawn >= 0)
                    {
                        State.Room[slot] = drawn;
                    }
                }
            }

            if (solanaBridge != null)
            {
                solanaBridge.DispatchClaimSpoils((byte)choiceIndex);
            }

            NotifyStateChanged();
            InvokeRoomDealt();
            return true;
        }

        private void NotifyStateChanged()
        {
            OnStateChanged?.Invoke(State);
        }

        private void InvokeRoomDealt()
        {
            OnRoomDealt?.Invoke();
            onRoomDealt?.Invoke();
        }

        private void InvokeCardsDiscarded(List<CardData> cards)
        {
            OnCardsDiscarded?.Invoke(cards);
            onCardsDiscarded?.Invoke(cards);
        }

        private void InvokeGameOver(bool victory, int finalScore)
        {
            OnGameOver?.Invoke(victory, finalScore);
            onGameOver?.Invoke(victory, finalScore);
        }

        private static int DrawCardFromTop(DungeonRunState run)
        {
            if (run.DeckLen == 0) return -1;
            int idx = run.DeckHead % ScoundrelCardDecoder.DeckSize;
            byte card = run.Deck[idx];
            run.DeckHead = (byte)((run.DeckHead + 1) % ScoundrelCardDecoder.DeckSize);
            run.DeckLen -= 1;
            return card;
        }

        private static void PushCardToBottom(DungeonRunState run, byte card)
        {
            if (run.DeckLen >= ScoundrelCardDecoder.DeckSize) return;
            int tail = (run.DeckHead + run.DeckLen) % ScoundrelCardDecoder.DeckSize;
            run.Deck[tail] = card;
            run.DeckLen += 1;
        }

        private static int ComputeDefeatScore(DungeonRunState run)
        {
            int remainingMonsterSum = 0;
            for (int i = 0; i < run.DeckLen; i++)
            {
                int idx = (run.DeckHead + i) % ScoundrelCardDecoder.DeckSize;
                CardData c = ScoundrelCardDecoder.Decode(run.Deck[idx]);
                if (c.IsMonster) remainingMonsterSum += c.Rank;
            }
            for (int slot = 0; slot < 4; slot++)
            {
                if (run.Room[slot] >= 0)
                {
                    CardData c = ScoundrelCardDecoder.Decode((byte)run.Room[slot]);
                    if (c.IsMonster) remainingMonsterSum += c.Rank;
                }
            }
            return -remainingMonsterSum;
        }

        public static ulong SplitMix64(ref ulong state)
        {
            state = unchecked(state + 0x9E3779B97F4A7C15UL);
            ulong z = state;
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            return z ^ (z >> 31);
        }
    }
}
