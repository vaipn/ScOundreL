using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Scoundrel
{
    public class ScoundrelTabletopUIController : MonoBehaviour
    {
        [Header("Core References")]
        [SerializeField] private ScoundrelGameManager gameManager;
        [SerializeField] private SpoilsModalUI spoilsModal;
        [SerializeField] private Material sharedFoilMaterial;
        [SerializeField] private RectTransform animationLayer;

        [Header("Top HUD Bar")]
        [SerializeField] private Image hpBarFill;
        [SerializeField] private TextMeshProUGUI hpValueText;
        [SerializeField] private GameObject epicEchoBadgeRoot;
        [SerializeField] private Image epicEchoBadgeBg;
        [SerializeField] private TextMeshProUGUI epicEchoBadgeText;
        [SerializeField] private TextMeshProUGUI goldValueText;
        [SerializeField] private TextMeshProUGUI sessionStatusText;
        [SerializeField] private Button avoidRoomButton;
        [SerializeField] private TextMeshProUGUI avoidRoomButtonText;

        [Header("Center Tabletop — Dungeon Deck, Room Slots & Discard")]
        [SerializeField] private RectTransform dungeonDeckAnchor;
        [SerializeField] private Image dungeonDeckBackImage;
        [SerializeField] private TextMeshProUGUI dungeonCountText;
        [SerializeField] private RectTransform[] roomSlotAnchors = new RectTransform[4];
        [SerializeField] private RectTransform discardPileAnchor;
        [SerializeField] private Image discardPileBackImage;
        [SerializeField] private TextMeshProUGUI discardCountText;

        [Header("Center Right — Balatro Consumable Slot (Epic Health Potion)")]
        [SerializeField] private RectTransform consumableCardHolder;
        [SerializeField] private Image consumableGoldenAura;
        [SerializeField] private TextMeshProUGUI consumableCountBadgeText;
        [SerializeField] private Button useEpicPotionButton;
        [SerializeField] private TextMeshProUGUI useEpicPotionButtonText;

        [Header("Bottom Play Area — Equipped Weapon & Combat Prompt")]
        [SerializeField] private RectTransform equippedWeaponAnchor;
        [SerializeField] private RectTransform weaponMonsterStackContainer;
        [SerializeField] private GameObject weaponEmptyPlaceholder;
        [SerializeField] private Image shieldBadgeImage;
        [SerializeField] private TextMeshProUGUI weaponCeilingBadgeText;
        [SerializeField] private TextMeshProUGUI roomThreatLiveText;

        [Header("Monster Combat Choice Prompt")]
        [SerializeField] private GameObject combatChoicePanel;
        [SerializeField] private TextMeshProUGUI combatPromptTitleText;
        [SerializeField] private Button useWeaponButton;
        [SerializeField] private TextMeshProUGUI useWeaponButtonText;
        [SerializeField] private Button fightBarehandedButton;
        [SerializeField] private TextMeshProUGUI fightBarehandedButtonText;
        [SerializeField] private Button cancelCombatPromptButton;

        [Header("Game Over (Victory / Defeat) Overlay")]
        [SerializeField] private GameObject gameOverModalRoot;
        [SerializeField] private CanvasGroup gameOverCanvasGroup;
        [SerializeField] private TextMeshProUGUI gameOverTitleText;
        [SerializeField] private TextMeshProUGUI gameOverSubtitleText;
        [SerializeField] private Button newRunStandardButton;
        [SerializeField] private Button newRunLegendaryNftButton;

        [Header("Card Layout Settings")]
        [SerializeField] private Vector2 roomCardSize = new Vector2(182f, 256f);
        [SerializeField] private Vector2 weaponCardSize = new Vector2(168f, 236f);
        [SerializeField] private Vector2 consumableCardSize = new Vector2(132f, 186f);
        [SerializeField] private Vector2 monsterStackCascadeOffset = new Vector2(38f, -12f);

        private readonly BalatroCardVisual[] _activeRoomVisuals = new BalatroCardVisual[4];
        private BalatroCardVisual _equippedWeaponVisual;
        private readonly List<BalatroCardVisual> _stackedMonsterVisuals = new List<BalatroCardVisual>();
        private BalatroCardVisual _consumablePotionVisual;
        private int _selectedMonsterSlot = -1;
        private bool _isBound;

        public void ConfigureSceneReferences(
            ScoundrelGameManager manager,
            SpoilsModalUI spoils,
            Material foilMat,
            RectTransform animLayer,
            Image hpFill,
            TextMeshProUGUI hpText,
            GameObject echoRoot,
            Image echoBg,
            TextMeshProUGUI echoText,
            TextMeshProUGUI goldText,
            TextMeshProUGUI sessionText,
            Button avoidBtn,
            TextMeshProUGUI avoidBtnText,
            RectTransform deckAnchor,
            Image deckBackImg,
            TextMeshProUGUI deckCount,
            RectTransform[] slots,
            RectTransform discardAnchor,
            Image discardBackImg,
            TextMeshProUGUI discardCount,
            RectTransform consumableHolder,
            Image goldenAura,
            TextMeshProUGUI consumableCountText,
            Button usePotionBtn,
            TextMeshProUGUI usePotionBtnText,
            RectTransform weaponAnchor,
            RectTransform weaponStackRoot,
            GameObject weaponPlaceholder,
            Image shieldImg,
            TextMeshProUGUI ceilingText,
            TextMeshProUGUI threatLiveText,
            GameObject choicePanel,
            TextMeshProUGUI choiceTitle,
            Button weaponChoiceBtn,
            TextMeshProUGUI weaponChoiceBtnText,
            Button barehandedChoiceBtn,
            TextMeshProUGUI barehandedChoiceBtnText,
            Button cancelChoiceBtn,
            GameObject gameOverRoot,
            CanvasGroup gameOverGroup,
            TextMeshProUGUI gameOverTitle,
            TextMeshProUGUI gameOverSubtitle,
            Button restartStdBtn,
            Button restartNftBtn)
        {
            gameManager = manager;
            spoilsModal = spoils;
            sharedFoilMaterial = foilMat;
            animationLayer = animLayer;
            hpBarFill = hpFill;
            hpValueText = hpText;
            epicEchoBadgeRoot = echoRoot;
            epicEchoBadgeBg = echoBg;
            epicEchoBadgeText = echoText;
            goldValueText = goldText;
            sessionStatusText = sessionText;
            avoidRoomButton = avoidBtn;
            avoidRoomButtonText = avoidBtnText;
            dungeonDeckAnchor = deckAnchor;
            dungeonDeckBackImage = deckBackImg;
            dungeonCountText = deckCount;
            roomSlotAnchors = slots;
            discardPileAnchor = discardAnchor;
            discardPileBackImage = discardBackImg;
            discardCountText = discardCount;
            consumableCardHolder = consumableHolder;
            consumableGoldenAura = goldenAura;
            consumableCountBadgeText = consumableCountText;
            useEpicPotionButton = usePotionBtn;
            useEpicPotionButtonText = usePotionBtnText;
            equippedWeaponAnchor = weaponAnchor;
            weaponMonsterStackContainer = weaponStackRoot;
            weaponEmptyPlaceholder = weaponPlaceholder;
            shieldBadgeImage = shieldImg;
            weaponCeilingBadgeText = ceilingText;
            roomThreatLiveText = threatLiveText;
            combatChoicePanel = choicePanel;
            combatPromptTitleText = choiceTitle;
            useWeaponButton = weaponChoiceBtn;
            useWeaponButtonText = weaponChoiceBtnText;
            fightBarehandedButton = barehandedChoiceBtn;
            fightBarehandedButtonText = barehandedChoiceBtnText;
            cancelCombatPromptButton = cancelChoiceBtn;
            gameOverModalRoot = gameOverRoot;
            gameOverCanvasGroup = gameOverGroup;
            gameOverTitleText = gameOverTitle;
            gameOverSubtitleText = gameOverSubtitle;
            newRunStandardButton = restartStdBtn;
            newRunLegendaryNftButton = restartNftBtn;
        }

        private void Awake()
        {
            if (gameManager == null)
            {
                gameManager = FindFirstObjectByType<ScoundrelGameManager>();
            }
            if (sharedFoilMaterial == null)
            {
                Shader sh = Shader.Find(ScoundrelAssetPaths.HolographicShaderName);
                if (sh != null)
                {
                    sharedFoilMaterial = new Material(sh);
                }
            }

            WireButtons();
            BindEvents();
            EnsureConsumableVisual();
            HideCombatPrompt();
            HideGameOverModal();
        }

        private void Start()
        {
            BindEvents();
            ApplyLegendaryFeatureGate();
            if (gameManager != null && gameManager.State != null)
            {
                RefreshHUD(gameManager.State);
                // If StartNewRun already fired during GameManager.Start before us, sync room cards now
                bool anyMissing = false;
                for (int i = 0; i < 4; i++)
                {
                    if (gameManager.State.Room[i] >= 0 && _activeRoomVisuals[i] == null)
                    {
                        anyMissing = true;
                        break;
                    }
                }
                if (anyMissing)
                {
                    HandleRoomDealt();
                }
            }
        }

        private void OnDestroy()
        {
            UnbindEvents();
        }

        private void WireButtons()
        {
            if (avoidRoomButton != null)
            {
                avoidRoomButton.onClick.RemoveAllListeners();
                avoidRoomButton.onClick.AddListener(OnAvoidRoomClicked);
            }

            if (useEpicPotionButton != null)
            {
                useEpicPotionButton.onClick.RemoveAllListeners();
                useEpicPotionButton.onClick.AddListener(OnUseEpicPotionClicked);
            }

            if (useWeaponButton != null)
            {
                useWeaponButton.onClick.RemoveAllListeners();
                useWeaponButton.onClick.AddListener(() => ExecuteSelectedMonsterCombat(true));
            }

            if (fightBarehandedButton != null)
            {
                fightBarehandedButton.onClick.RemoveAllListeners();
                fightBarehandedButton.onClick.AddListener(() => ExecuteSelectedMonsterCombat(false));
            }

            if (cancelCombatPromptButton != null)
            {
                cancelCombatPromptButton.onClick.RemoveAllListeners();
                cancelCombatPromptButton.onClick.AddListener(HideCombatPrompt);
            }

            if (newRunStandardButton != null)
            {
                newRunStandardButton.onClick.RemoveAllListeners();
                newRunStandardButton.onClick.AddListener(() => RestartRun(false));
            }

            if (newRunLegendaryNftButton != null)
            {
                newRunLegendaryNftButton.onClick.RemoveAllListeners();
                newRunLegendaryNftButton.onClick.AddListener(() => RestartRun(true));
            }
        }

        private void BindEvents()
        {
            if (_isBound || gameManager == null) return;
            _isBound = true;

            gameManager.OnStateChanged += RefreshHUD;
            gameManager.OnRoomDealt += HandleRoomDealt;
            gameManager.OnRoomAvoided += HandleRoomAvoided;
            gameManager.OnEpicPotionActivated += HandleEpicPotionActivated;
            gameManager.OnGameOver += HandleGameOver;

            if (gameManager.Bridge != null)
            {
                gameManager.Bridge.OnBridgeStatusChanged += HandleBridgeStatusChanged;
            }

            if (spoilsModal != null)
            {
                spoilsModal.Bind(gameManager);
            }
        }

        private void UnbindEvents()
        {
            if (!_isBound || gameManager == null) return;
            _isBound = false;

            gameManager.OnStateChanged -= RefreshHUD;
            gameManager.OnRoomDealt -= HandleRoomDealt;
            gameManager.OnRoomAvoided -= HandleRoomAvoided;
            gameManager.OnEpicPotionActivated -= HandleEpicPotionActivated;
            gameManager.OnGameOver -= HandleGameOver;

            if (gameManager.Bridge != null)
            {
                gameManager.Bridge.OnBridgeStatusChanged -= HandleBridgeStatusChanged;
            }
        }

        private void EnsureConsumableVisual()
        {
            if (consumableCardHolder == null || _consumablePotionVisual != null) return;

            Sprite epicSprite = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.CardEpicPotion);
            _consumablePotionVisual = BalatroCardVisual.CreateCardVisual(consumableCardHolder, consumableCardSize, sharedFoilMaterial);
            _consumablePotionVisual.RectTransform.anchoredPosition = Vector2.zero;
            _consumablePotionVisual.SetupCustomSprite(epicSprite, true, false, sharedFoilMaterial);
            _consumablePotionVisual.OnCardClicked += _ => OnUseEpicPotionClicked();
        }

        private void HandleBridgeStatusChanged(string statusMsg)
        {
            if (sessionStatusText != null)
            {
                sessionStatusText.text = statusMsg;
            }
        }

        public void RefreshHUD(DungeonRunState state)
        {
            if (state == null) return;

            // 1. HP Bar & Text ("20.0 / 20 HP" or "18.5 / 20 HP")
            if (hpValueText != null)
            {
                hpValueText.text = state.FormatHpText();
            }
            if (hpBarFill != null)
            {
                hpBarFill.DOKill();
                hpBarFill.DOFillAmount(state.HpNormalized, 0.25f).SetEase(Ease.OutQuad);
                hpBarFill.color = state.HpNormalized > 0.35f
                    ? new Color(0.84f, 0.20f, 0.25f, 1f)
                    : new Color(0.96f, 0.34f, 0.16f, 1f);
            }

            // 2. Active Epic Potion Echo Badge
            if (epicEchoBadgeRoot != null && epicEchoBadgeText != null)
            {
                if (state.EpicPotionBuffRoomsLeft > 0)
                {
                    epicEchoBadgeRoot.SetActive(true);
                    float echoPreview = (state.FirstPotionHealedTenths / 10) / 10f;
                    string previewPart = echoPreview > 0f ? $" (+{echoPreview:0.0} HP)" : "";
                    epicEchoBadgeText.text = $"+10% Potion Echo: {state.EpicPotionBuffRoomsLeft} Room{(state.EpicPotionBuffRoomsLeft == 1 ? "" : "s")} Left{previewPart}";
                    epicEchoBadgeText.color = new Color(1.0f, 0.90f, 0.42f, 1f);
                    if (epicEchoBadgeBg != null)
                    {
                        epicEchoBadgeBg.color = new Color(0.32f, 0.24f, 0.06f, 0.95f);
                    }
                }
                else
                {
                    epicEchoBadgeRoot.SetActive(true);
                    epicEchoBadgeText.text = "+10% Potion Echo: Inactive";
                    epicEchoBadgeText.color = new Color(0.55f, 0.60f, 0.68f, 0.8f);
                    if (epicEchoBadgeBg != null)
                    {
                        epicEchoBadgeBg.color = new Color(0.10f, 0.13f, 0.18f, 0.78f);
                    }
                }
            }

            // 3. Accumulated SPL Gold
            if (goldValueText != null)
            {
                goldValueText.text = $"SPL Gold: {state.UnclaimedGold}";
            }

            // 4. Session Key Status
            if (sessionStatusText != null && gameManager != null && gameManager.Bridge != null)
            {
                sessionStatusText.text = gameManager.Bridge.LastStatusMessage;
            }

            // 5. Avoid Room Button
            if (avoidRoomButton != null)
            {
                bool canAvoid = state.CanAvoidRoom;
                avoidRoomButton.interactable = canAvoid;
                if (avoidRoomButtonText != null)
                {
                    if (canAvoid)
                    {
                        avoidRoomButtonText.text = "AVOID ROOM";
                        avoidRoomButtonText.color = new Color(0.95f, 0.97f, 1f, 1f);
                    }
                    else if (state.FledLastRoom && state.CardsResolvedInRoom == 0)
                    {
                        avoidRoomButtonText.text = "MUST FACE ROOM";
                        avoidRoomButtonText.color = new Color(0.62f, 0.56f, 0.56f, 0.85f);
                    }
                    else
                    {
                        avoidRoomButtonText.text = "ROOM LOCKED";
                        avoidRoomButtonText.color = new Color(0.55f, 0.58f, 0.64f, 0.8f);
                    }
                }
            }

            // 6. Dungeon & Discard Pile Counts
            if (dungeonCountText != null)
            {
                dungeonCountText.text = state.DeckLen.ToString();
            }
            if (dungeonDeckBackImage != null)
            {
                dungeonDeckBackImage.enabled = state.DeckLen > 0;
            }
            if (discardCountText != null)
            {
                discardCountText.text = state.DiscardCount.ToString();
            }
            if (discardPileBackImage != null)
            {
                discardPileBackImage.color = state.DiscardCount > 0
                    ? Color.white
                    : new Color(1f, 1f, 1f, 0.22f);
            }

            // 7. Balatro-Style Epic Health Potion Consumable Slot
            EnsureConsumableVisual();
            if (_consumablePotionVisual != null)
            {
                bool hasPotion = state.EpicPotionsInventory > 0;
                _consumablePotionVisual.gameObject.SetActive(true);
                _consumablePotionVisual.IsInteractable = state.CanUseEpicPotion;
                CanvasGroup cg = _consumablePotionVisual.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    cg.alpha = hasPotion ? 1f : 0.28f;
                }
            }
            if (consumableCountBadgeText != null)
            {
                consumableCountBadgeText.text = $"x{state.EpicPotionsInventory}";
            }
            if (useEpicPotionButton != null)
            {
                useEpicPotionButton.interactable = state.CanUseEpicPotion;
                if (useEpicPotionButtonText != null)
                {
                    if (state.EpicPotionBuffRoomsLeft > 0)
                        useEpicPotionButtonText.text = $"ACTIVE ({state.EpicPotionBuffRoomsLeft}R)";
                    else if (state.EpicPotionsInventory > 0)
                        useEpicPotionButtonText.text = "USE";
                    else
                        useEpicPotionButtonText.text = "EMPTY";
                }
            }
            if (consumableGoldenAura != null && state.EpicPotionBuffRoomsLeft == 0)
            {
                consumableGoldenAura.color = state.CanUseEpicPotion
                    ? new Color(1f, 0.84f, 0.28f, 0.25f)
                    : new Color(1f, 0.84f, 0.28f, 0f);
            }

            // 8. Equipped Weapon Badge ("Can Slay <= X") & Forged Edge Upgrade Sync
            if (weaponEmptyPlaceholder != null)
            {
                weaponEmptyPlaceholder.SetActive(state.EquippedWeaponRank == 0);
            }
            if (_equippedWeaponVisual != null && state.EquippedWeaponRank > 0)
            {
                bool enableFoil = state.ActiveWeaponSkinTier != WeaponSkinTier.Standard;
                bool isLegendary = state.ActiveWeaponSkinTier == WeaponSkinTier.Legendary;
                _equippedWeaponVisual.SetFoilState(enableFoil, isLegendary, sharedFoilMaterial);
            }
            if (weaponCeilingBadgeText != null)
            {
                if (state.EquippedWeaponRank == 0)
                {
                    weaponCeilingBadgeText.text = "Barehanded (No Weapon)";
                    weaponCeilingBadgeText.color = new Color(0.72f, 0.76f, 0.82f, 0.9f);
                }
                else
                {
                    string ceilingLabel = ScoundrelCardDecoder.GetRankLabel(state.WeaponCeiling);
                    string weaponLabel = ScoundrelCardDecoder.GetRankLabel(state.EquippedWeaponRank);
                    string forgedSuffix = state.LegendaryForgedEdgeCharges > 0 ? "  [+1 FORGED EDGE]" : "";
                    weaponCeilingBadgeText.text = $"Weapon {weaponLabel}  |  Can Slay <= {ceilingLabel}{forgedSuffix}";
                    weaponCeilingBadgeText.color = state.HasLegendarySkin
                        ? new Color(1f, 0.82f, 0.34f, 1f)
                        : new Color(0.78f, 0.92f, 1f, 1f);
                }
            }
            if (shieldBadgeImage != null)
            {
                shieldBadgeImage.color = state.EquippedWeaponRank > 0
                    ? (state.HasLegendarySkin ? new Color(1f, 0.80f, 0.28f, 1f) : Color.white)
                    : new Color(0.5f, 0.55f, 0.62f, 0.5f);
            }

            // 9. Live Room Threat Counter
            if (roomThreatLiveText != null)
            {
                SpoilsTier previewTier = ScoundrelCardDecoder.EvaluateSpoilsTier(state.RoomThreatAccumulator);
                roomThreatLiveText.text = $"Room Threat T: {state.RoomThreatAccumulator} ({previewTier})  •  Resolved: {state.CardsResolvedInRoom}/3";
            }
        }

        private void HandleRoomAvoided(List<CardData> avoidedCards)
        {
            HideCombatPrompt();
            Vector3 deckPos = dungeonDeckAnchor != null ? dungeonDeckAnchor.position : Vector3.zero;
            int idx = 0;
            for (int slot = 0; slot < 4; slot++)
            {
                BalatroCardVisual existing = _activeRoomVisuals[slot];
                if (existing != null)
                {
                    _activeRoomVisuals[slot] = null;
                    if (animationLayer != null)
                    {
                        existing.transform.SetParent(animationLayer, true);
                    }
                    BalatroCardVisual captured = existing;
                    captured.AnimateReturnToDeck(deckPos, idx * 0.04f, () =>
                    {
                        if (captured != null) Destroy(captured.gameObject);
                    });
                    idx++;
                }
            }
        }

        private void HandleRoomDealt()
        {
            if (gameManager == null || gameManager.State == null) return;
            HideCombatPrompt();

            DungeonRunState state = gameManager.State;
            Vector3 deckPos = dungeonDeckAnchor != null ? dungeonDeckAnchor.position : transform.position;
            int dealOrder = 0;

            for (int slot = 0; slot < 4; slot++)
            {
                if (slot >= roomSlotAnchors.Length || roomSlotAnchors[slot] == null) continue;

                int rawCardId = state.Room[slot];
                BalatroCardVisual currentVisual = _activeRoomVisuals[slot];

                if (rawCardId < 0)
                {
                    if (currentVisual != null)
                    {
                        Destroy(currentVisual.gameObject);
                        _activeRoomVisuals[slot] = null;
                    }
                    continue;
                }

                CardData targetCard = ScoundrelCardDecoder.Decode((byte)rawCardId);

                // Keep surviving carryover card seamlessly in its slot
                if (currentVisual != null && currentVisual.CardData.CardId == targetCard.CardId)
                {
                    currentVisual.RoomSlotIndex = slot;
                    currentVisual.IsInteractable = true;
                    currentVisual.SetSelectedHighlight(false);
                    continue;
                }

                if (currentVisual != null)
                {
                    Destroy(currentVisual.gameObject);
                    _activeRoomVisuals[slot] = null;
                }

                BalatroCardVisual freshCard = BalatroCardVisual.CreateCardVisual(
                    roomSlotAnchors[slot],
                    roomCardSize,
                    sharedFoilMaterial);

                bool showWeaponSkinPreview = targetCard.IsWeapon && state.HasLegendarySkin;
                freshCard.SetupCard(
                    targetCard,
                    showWeaponSkinPreview,
                    showWeaponSkinPreview,
                    sharedFoilMaterial);

                freshCard.RoomSlotIndex = slot;
                freshCard.OnCardClicked += HandleRoomCardClicked;
                _activeRoomVisuals[slot] = freshCard;

                Vector3 slotWorldPos = roomSlotAnchors[slot].position;
                freshCard.AnimateDealFromDeck(deckPos, slotWorldPos, dealOrder * 0.085f);
                dealOrder++;
            }

            RefreshHUD(state);
        }

        private void HandleRoomCardClicked(BalatroCardVisual clickedCard)
        {
            if (clickedCard == null || gameManager == null || gameManager.State == null) return;
            DungeonRunState state = gameManager.State;
            if (state.Status != RunStatus.Active || state.PendingSpoilsThreat > 0) return;

            int slot = clickedCard.RoomSlotIndex;
            if (slot < 0 || slot >= 4 || state.Room[slot] < 0) return;

            CardData card = clickedCard.CardData;

            if (card.IsMonster)
            {
                ShowMonsterCombatPrompt(slot, card, clickedCard);
                return;
            }

            HideCombatPrompt();

            if (card.IsWeapon)
            {
                ResolveWeaponCard(slot, clickedCard);
            }
            else if (card.IsPotion)
            {
                ResolvePotionCard(slot, clickedCard);
            }
        }

        private void ResolveWeaponCard(int slot, BalatroCardVisual newWeaponVisual)
        {
            _activeRoomVisuals[slot] = null;
            newWeaponVisual.RoomSlotIndex = -1;
            newWeaponVisual.OnCardClicked -= HandleRoomCardClicked;

            // Sweep old weapon & staggered monster stack into DiscardPile
            SweepCurrentWeaponStackToDiscard();

            if (equippedWeaponAnchor != null)
            {
                WeaponSkinFXController.Instance?.SetNextActionWorldPosition(equippedWeaponAnchor.position);
            }

            bool ok = gameManager.TryResolveCard(slot, false);
            if (!ok)
            {
                Destroy(newWeaponVisual.gameObject);
                return;
            }

            _equippedWeaponVisual = newWeaponVisual;
            _equippedWeaponVisual.RectTransform.sizeDelta = weaponCardSize;

            DungeonRunState state = gameManager.State;
            bool enableFoil = state.ActiveWeaponSkinTier != WeaponSkinTier.Standard;
            bool isLegendary = state.ActiveWeaponSkinTier == WeaponSkinTier.Legendary;
            _equippedWeaponVisual.SetFoilState(enableFoil, isLegendary, sharedFoilMaterial);

            if (equippedWeaponAnchor != null)
            {
                _equippedWeaponVisual.AnimateEquipWeapon(equippedWeaponAnchor, Vector2.zero, () =>
                {
                    if (_equippedWeaponVisual != null)
                    {
                        _equippedWeaponVisual.IsInteractable = false;
                    }
                });
            }

            WeaponSkinFXController.Instance?.PlayWeaponEquip(state.ActiveWeaponSkinTier);
        }

        private void SweepCurrentWeaponStackToDiscard()
        {
            Vector3 discardPos = discardPileAnchor != null ? discardPileAnchor.position : transform.position;
            float delay = 0f;

            if (_equippedWeaponVisual != null)
            {
                BalatroCardVisual oldWeapon = _equippedWeaponVisual;
                _equippedWeaponVisual = null;
                if (animationLayer != null) oldWeapon.transform.SetParent(animationLayer, true);
                oldWeapon.AnimateSweepToDiscard(discardPos, delay, true);
                delay += 0.04f;
            }

            for (int i = 0; i < _stackedMonsterVisuals.Count; i++)
            {
                BalatroCardVisual m = _stackedMonsterVisuals[i];
                if (m != null)
                {
                    if (animationLayer != null) m.transform.SetParent(animationLayer, true);
                    m.AnimateSweepToDiscard(discardPos, delay, true);
                    delay += 0.035f;
                }
            }
            _stackedMonsterVisuals.Clear();
        }

        private void ResolvePotionCard(int slot, BalatroCardVisual potionVisual)
        {
            _activeRoomVisuals[slot] = null;
            potionVisual.RoomSlotIndex = -1;
            potionVisual.OnCardClicked -= HandleRoomCardClicked;

            WeaponSkinFXController.Instance?.SetNextActionWorldPosition(potionVisual.transform.position);
            gameManager.TryResolveCard(slot, false);

            Vector3 discardPos = discardPileAnchor != null ? discardPileAnchor.position : transform.position;
            if (animationLayer != null)
            {
                potionVisual.transform.SetParent(animationLayer, true);
            }
            potionVisual.AnimateSweepToDiscard(discardPos, 0.05f, true);
        }

        private void ShowMonsterCombatPrompt(int slot, CardData monster, BalatroCardVisual monsterVisual)
        {
            _selectedMonsterSlot = slot;
            for (int i = 0; i < 4; i++)
            {
                if (_activeRoomVisuals[i] != null)
                {
                    _activeRoomVisuals[i].SetSelectedHighlight(i == slot);
                }
            }

            if (combatChoicePanel == null) return;
            combatChoicePanel.SetActive(true);
            combatChoicePanel.transform.DOKill();
            combatChoicePanel.transform.localScale = new Vector3(0.92f, 0.92f, 1f);
            combatChoicePanel.transform.DOScale(1f, 0.18f).SetEase(Ease.OutBack);

            DungeonRunState state = gameManager.State;
            if (combatPromptTitleText != null)
            {
                combatPromptTitleText.text = $"ENGAGE {monster.DisplayName.ToUpperInvariant()} (RANK {monster.Rank})";
            }

            bool canUseWeapon = state.CanUseWeaponAgainst(monster.Rank);
            if (useWeaponButton != null)
            {
                useWeaponButton.interactable = canUseWeapon;
            }
            if (useWeaponButtonText != null)
            {
                if (canUseWeapon)
                {
                    int chip = Mathf.Max(0, monster.Rank - state.EquippedWeaponRank);
                    bool usesForged = state.WouldConsumeForgedEdge(monster.Rank);
                    if (chip == 0)
                    {
                        useWeaponButtonText.text = usesForged
                            ? "USE WEAPON (0 DMG • FORGED EDGE!)"
                            : "USE WEAPON (0 DMG • OBLITERATE)";
                    }
                    else
                    {
                        useWeaponButtonText.text = usesForged
                            ? $"USE WEAPON (-{chip}.0 HP • FORGED EDGE!)"
                            : $"USE WEAPON (-{chip}.0 HP CHIP)";
                    }
                }
                else if (state.EquippedWeaponRank == 0)
                {
                    useWeaponButtonText.text = "USE WEAPON (NONE EQUIPPED)";
                }
                else
                {
                    string maxLbl = ScoundrelCardDecoder.GetRankLabel(state.WeaponCeiling);
                    useWeaponButtonText.text = $"WEAPON LOCKED (CAN SLAY <= {maxLbl})";
                }
            }

            if (fightBarehandedButtonText != null)
            {
                fightBarehandedButtonText.text = $"FIGHT BAREHANDED (-{monster.Rank}.0 HP)";
            }
        }

        public void HideCombatPrompt()
        {
            _selectedMonsterSlot = -1;
            for (int i = 0; i < 4; i++)
            {
                if (_activeRoomVisuals[i] != null)
                {
                    _activeRoomVisuals[i].SetSelectedHighlight(false);
                }
            }
            if (combatChoicePanel != null)
            {
                combatChoicePanel.SetActive(false);
            }
        }

        private void ExecuteSelectedMonsterCombat(bool useWeapon)
        {
            int slot = _selectedMonsterSlot;
            if (slot < 0 || slot >= 4 || gameManager == null) return;

            BalatroCardVisual monsterVisual = _activeRoomVisuals[slot];
            if (monsterVisual == null)
            {
                HideCombatPrompt();
                return;
            }

            if (useWeapon && !gameManager.State.CanUseWeaponAgainst(monsterVisual.CardData.Rank))
            {
                return;
            }

            HideCombatPrompt();
            _activeRoomVisuals[slot] = null;
            monsterVisual.RoomSlotIndex = -1;
            monsterVisual.OnCardClicked -= HandleRoomCardClicked;

            Vector3 fxWorldPos = useWeapon && equippedWeaponAnchor != null
                ? Vector3.Lerp(monsterVisual.transform.position, equippedWeaponAnchor.position, 0.35f)
                : monsterVisual.transform.position;
            WeaponSkinFXController.Instance?.SetNextActionWorldPosition(fxWorldPos);

            int stackIndex = _stackedMonsterVisuals.Count;
            bool ok = gameManager.TryResolveCard(slot, useWeapon);
            if (!ok)
            {
                Destroy(monsterVisual.gameObject);
                return;
            }

            if (useWeapon)
            {
                _stackedMonsterVisuals.Add(monsterVisual);
                monsterVisual.RectTransform.sizeDelta = weaponCardSize;
                Transform stackParent = weaponMonsterStackContainer != null ? weaponMonsterStackContainer : equippedWeaponAnchor;
                monsterVisual.AnimateStackOntoWeapon(stackParent, stackIndex, monsterStackCascadeOffset, () =>
                {
                    if (monsterVisual != null) monsterVisual.IsInteractable = false;
                });
            }
            else
            {
                Vector3 discardPos = discardPileAnchor != null ? discardPileAnchor.position : transform.position;
                if (animationLayer != null)
                {
                    monsterVisual.transform.SetParent(animationLayer, true);
                }
                monsterVisual.AnimateSweepToDiscard(discardPos, 0.04f, true);
            }
        }

        private void OnAvoidRoomClicked()
        {
            if (gameManager == null) return;
            gameManager.TryAvoidRoom();
        }

        private void OnUseEpicPotionClicked()
        {
            if (gameManager == null) return;
            if (_consumablePotionVisual != null)
            {
                WeaponSkinFXController.Instance?.SetNextActionWorldPosition(_consumablePotionVisual.transform.position);
            }
            gameManager.TryUseEpicPotion();
        }

        private void HandleEpicPotionActivated(int roomsRemaining)
        {
            if (_consumablePotionVisual != null)
            {
                _consumablePotionVisual.RectTransform.DOKill();
                _consumablePotionVisual.RectTransform.DOPunchScale(new Vector3(0.22f, 0.22f, 0f), 0.38f, 8, 0.85f);
            }

            if (consumableGoldenAura != null)
            {
                consumableGoldenAura.DOKill();
                consumableGoldenAura.rectTransform.DOKill();
                consumableGoldenAura.color = new Color(1f, 0.86f, 0.25f, 0.92f);
                consumableGoldenAura.rectTransform.localScale = Vector3.one * 0.85f;
                consumableGoldenAura.rectTransform.DOScale(1.38f, 0.48f).SetEase(Ease.OutCubic);
                consumableGoldenAura.DOFade(0.32f, 0.55f).SetEase(Ease.OutQuad);
            }

            if (epicEchoBadgeRoot != null)
            {
                epicEchoBadgeRoot.transform.DOKill();
                epicEchoBadgeRoot.transform.localScale = Vector3.one;
                epicEchoBadgeRoot.transform.DOPunchScale(new Vector3(0.14f, 0.14f, 0f), 0.35f, 6, 0.8f);
            }
        }

        private void HandleGameOver(bool victory, int finalScore)
        {
            HideCombatPrompt();
            if (spoilsModal != null)
            {
                spoilsModal.HideImmediate();
            }

            DOVirtual.DelayedCall(0.45f, () =>
            {
                if (this == null || gameOverModalRoot == null) return;
                ApplyLegendaryFeatureGate();
                gameOverModalRoot.SetActive(true);
                if (gameOverCanvasGroup != null)
                {
                    gameOverCanvasGroup.DOKill();
                    gameOverCanvasGroup.alpha = 0f;
                    gameOverCanvasGroup.interactable = true;
                    gameOverCanvasGroup.blocksRaycasts = true;
                    gameOverCanvasGroup.DOFade(1f, 0.3f);
                }

                DungeonRunState st = gameManager != null ? gameManager.State : null;
                uint gold = st != null ? st.UnclaimedGold : 0;

                if (gameOverTitleText != null)
                {
                    gameOverTitleText.text = victory ? "DUNGEON CONQUERED!" : "FALLEN IN THE DUNGEON";
                    gameOverTitleText.color = victory
                        ? new Color(0.98f, 0.82f, 0.26f, 1f)
                        : new Color(0.95f, 0.28f, 0.30f, 1f);
                }

                if (gameOverSubtitleText != null)
                {
                    gameOverSubtitleText.text =
                        $"Official Scoundrel Score: {finalScore}\n" +
                        $"SPL Gold Looted: {gold}\n" +
                        $"Remaining HP: {(st != null ? st.FormatHpText() : "0.0 / 20 HP")}";
                }
            });
        }

        private void ApplyLegendaryFeatureGate()
        {
            bool legendaryAllowed = gameManager != null && gameManager.EnableLegendaryWeapons;
            if (newRunLegendaryNftButton != null)
            {
                newRunLegendaryNftButton.gameObject.SetActive(legendaryAllowed);
            }
            if (newRunStandardButton != null && newRunStandardButton.transform is RectTransform stdRect)
            {
                stdRect.anchoredPosition = legendaryAllowed ? new Vector2(-175f, 74f) : new Vector2(0f, 74f);
            }
        }

        private void HideGameOverModal()
        {
            if (gameOverModalRoot != null)
            {
                gameOverModalRoot.SetActive(false);
            }
        }

        private void RestartRun(bool withLegendaryNft)
        {
            HideGameOverModal();
            HideCombatPrompt();
            if (spoilsModal != null)
            {
                spoilsModal.HideImmediate();
            }

            // Clean up all tabletop card visuals before fresh deal
            for (int i = 0; i < 4; i++)
            {
                if (_activeRoomVisuals[i] != null)
                {
                    Destroy(_activeRoomVisuals[i].gameObject);
                    _activeRoomVisuals[i] = null;
                }
            }
            if (_equippedWeaponVisual != null)
            {
                Destroy(_equippedWeaponVisual.gameObject);
                _equippedWeaponVisual = null;
            }
            for (int i = 0; i < _stackedMonsterVisuals.Count; i++)
            {
                if (_stackedMonsterVisuals[i] != null)
                {
                    Destroy(_stackedMonsterVisuals[i].gameObject);
                }
            }
            _stackedMonsterVisuals.Clear();

            if (gameManager != null)
            {
                gameManager.StartNewRun(null, withLegendaryNft);
            }
        }
    }
}
