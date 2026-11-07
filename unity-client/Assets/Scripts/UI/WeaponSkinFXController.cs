using System.Globalization;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Scoundrel
{
    public class WeaponSkinFXController : MonoBehaviour
    {
        public static WeaponSkinFXController Instance { get; private set; }

        private const float DefaultBgmVolume = 0.35f;

        private static readonly Color GoldObliterateTextColor = new Color(1.00f, 0.86f, 0.28f, 1f);
        private static readonly Color CyanFoilObliterateTextColor = new Color(0.36f, 0.95f, 1.00f, 1f);
        private static readonly Color LegendaryCleaveTextColor = new Color(1.00f, 0.80f, 0.20f, 1f);
        private static readonly Color CrimsonDamageTextColor = new Color(1.00f, 0.23f, 0.29f, 1f);
        private static readonly Color HeavyBarehandedTextColor = new Color(0.98f, 0.16f, 0.22f, 1f);
        private static readonly Color PotionHealTextColor = new Color(0.28f, 0.96f, 0.52f, 1f);
        private static readonly Color WastedPotionTextColor = new Color(0.72f, 0.75f, 0.80f, 0.95f);
        private static readonly Color EchoHealTextColor = new Color(0.42f, 1.00f, 0.80f, 1f);
        private static readonly Color EpicAuraGoldColor = new Color(1.00f, 0.84f, 0.24f, 0.88f);
        private static readonly Color OfeVanishCyanColor = new Color(0.32f, 0.96f, 1.00f, 1f);
        private static readonly Color RustyShieldAmberColor = new Color(1.00f, 0.78f, 0.28f, 1f);

        private static Sprite _proceduralRingSprite;
        private static Texture2D _proceduralRingTexture;

        [Header("Scene & Audio References")]
        [SerializeField] private ScoundrelGameManager _gameManager;
        [SerializeField] private RectTransform _tabletopShakeRoot;
        [SerializeField] private CanvasGroup _screenDimOverlay;
        [SerializeField] private RectTransform _vfxContainer;
        [SerializeField] private AudioSource _bgmSource;
        [SerializeField] private AudioSource _sfxSource;
        [SerializeField] private AudioSource _stingerSource;

        private ScoundrelGameManager _subscribedManager;
        private bool _isConfigured;

        private Vector3 _nextActionWorldPos;
        private bool _hasNextActionWorldPos;

        private float _lastHoverSfxTime = -10f;
        private float _lastFanfareTime = -10f;
        private int _lastFloatingTextFrame = -1;
        private int _sameFrameFloatingTextIndex;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (!_isConfigured)
            {
                ScoundrelGameManager targetManager = _gameManager != null ? _gameManager : ScoundrelGameManager.Instance;
                ConfigureReferences(
                    targetManager,
                    _tabletopShakeRoot,
                    _screenDimOverlay,
                    _vfxContainer,
                    _bgmSource,
                    _sfxSource,
                    _stingerSource);
            }
        }

        public void ConfigureReferences(
            ScoundrelGameManager manager,
            RectTransform tabletopShakeRoot,
            CanvasGroup screenDimOverlay,
            RectTransform vfxContainer,
            AudioSource bgmSource,
            AudioSource sfxSource,
            AudioSource stingerSource)
        {
            UnsubscribeFromManager();

            _gameManager = manager != null ? manager : ScoundrelGameManager.Instance;
            _tabletopShakeRoot = tabletopShakeRoot;
            _screenDimOverlay = screenDimOverlay;
            _vfxContainer = vfxContainer;
            _bgmSource = bgmSource;
            _sfxSource = sfxSource;
            _stingerSource = stingerSource;

            EnsureAudioSources();

            if (_screenDimOverlay != null)
            {
                _screenDimOverlay.DOKill(false);
                _screenDimOverlay.alpha = 0f;
                _screenDimOverlay.interactable = false;
                _screenDimOverlay.blocksRaycasts = false;
            }

            SubscribeToManager(_gameManager);
            StartLoopingDungeonBgm();
            _isConfigured = true;
        }

        public void SetNextActionWorldPosition(Vector3 worldPos)
        {
            _nextActionWorldPos = worldPos;
            _hasNextActionWorldPos = true;
        }

        public void PlayCardDeal()
        {
            PlayOneShotSfx(ScoundrelAssetPaths.SfxCardDeal, 0.72f, RandomPitch(0.96f, 1.05f));
        }

        public void PlayCardHover()
        {
            float now = Time.unscaledTime;
            if (now - _lastHoverSfxTime < 0.035f) return;
            _lastHoverSfxTime = now;

            PlayOneShotSfx(ScoundrelAssetPaths.SfxCardHover, 0.52f, RandomPitch(0.98f, 1.04f));
        }

        public void PlayCardDiscard()
        {
            PlayOneShotSfx(ScoundrelAssetPaths.SfxCardDiscard, 0.76f, RandomPitch(0.95f, 1.03f));
        }

        public void PlaySpoilsFanfare()
        {
            float now = Time.unscaledTime;
            if (now - _lastFanfareTime < 0.08f) return;
            _lastFanfareTime = now;

            PlayOneShotStinger(ScoundrelAssetPaths.SfxSpoilsFanfare, 0.85f);
        }

        public void PlayWeaponEquip(WeaponSkinTier tier)
        {
            Vector3 actionPos = ResolveActionWorldPosition();

            switch (tier)
            {
                case WeaponSkinTier.Legendary:
                    PlayOneShotSfx(ScoundrelAssetPaths.SfxWeaponParryClink, 0.85f, 1.0f);
                    PlayOneShotStinger(ScoundrelAssetPaths.SfxLegendaryImpact, 0.58f);
                    SpawnAuraPulse(actionPos, LegendaryCleaveTextColor, 170f, 0.34f);
                    break;

                case WeaponSkinTier.EpicFoil:
                    PlayOneShotSfx(ScoundrelAssetPaths.SfxWeaponParryClink, 0.82f, 1.05f);
                    SpawnAuraPulse(actionPos, CyanFoilObliterateTextColor, 145f, 0.28f);
                    break;

                default:
                    PlayOneShotSfx(ScoundrelAssetPaths.SfxWeaponParryClink, 0.74f, 1.0f);
                    break;
            }
        }

        public void SpawnFloatingText(string text, Color color, Vector3 worldPos, float fontSize = 36f)
        {
            RectTransform container = ResolveVfxContainer();
            if (container == null || string.IsNullOrEmpty(text)) return;

            if (Time.frameCount == _lastFloatingTextFrame)
            {
                _sameFrameFloatingTextIndex++;
            }
            else
            {
                _lastFloatingTextFrame = Time.frameCount;
                _sameFrameFloatingTextIndex = 0;
            }

            GameObject rootGo = new GameObject("FloatingCombatText", typeof(RectTransform), typeof(CanvasGroup));
            rootGo.transform.SetParent(container, false);
            rootGo.transform.SetAsLastSibling();

            RectTransform rootRect = rootGo.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(720f, 96f);
            PositionRectAtWorldPoint(rootRect, worldPos);
            rootRect.anchoredPosition += new Vector2(0f, _sameFrameFloatingTextIndex * 42f);
            rootRect.localScale = Vector3.one * 0.62f;

            CanvasGroup cg = rootGo.GetComponent<CanvasGroup>();
            cg.alpha = 1f;
            cg.interactable = false;
            cg.blocksRaycasts = false;

            // Crisp vector drop-shadow layer for contrast over both dark felt and bright cards
            GameObject shadowGo = new GameObject("ShadowLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
            shadowGo.transform.SetParent(rootRect, false);
            RectTransform shadowRect = shadowGo.GetComponent<RectTransform>();
            shadowRect.anchorMin = Vector2.zero;
            shadowRect.anchorMax = Vector2.one;
            shadowRect.sizeDelta = Vector2.zero;
            shadowRect.anchoredPosition = new Vector2(2.5f, -3f);

            TextMeshProUGUI shadowTmp = shadowGo.GetComponent<TextMeshProUGUI>();
            ConfigureFloatingTmp(shadowTmp, text, new Color(0.02f, 0.03f, 0.06f, 0.92f), fontSize);

            // Foreground label
            GameObject mainGo = new GameObject("MainLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
            mainGo.transform.SetParent(rootRect, false);
            RectTransform mainRect = mainGo.GetComponent<RectTransform>();
            mainRect.anchorMin = Vector2.zero;
            mainRect.anchorMax = Vector2.one;
            mainRect.sizeDelta = Vector2.zero;
            mainRect.anchoredPosition = Vector2.zero;

            TextMeshProUGUI mainTmp = mainGo.GetComponent<TextMeshProUGUI>();
            ConfigureFloatingTmp(mainTmp, text, color, fontSize);

            Vector2 startAnchored = rootRect.anchoredPosition;
            Sequence seq = DOTween.Sequence().SetTarget(this);
            seq.Append(rootRect.DOScale(1.0f, 0.22f).SetEase(Ease.OutBack, 1.45f));
            seq.Join(rootRect.DOAnchorPosY(startAnchored.y + 74f, 0.88f).SetEase(Ease.OutCubic));
            seq.Insert(0.46f, cg.DOFade(0f, 0.42f).SetEase(Ease.InQuad));
            seq.OnComplete(() =>
            {
                if (rootGo != null)
                {
                    Destroy(rootGo);
                }
            });
        }

        private static void ConfigureFloatingTmp(TextMeshProUGUI tmp, string content, Color color, float fontSize)
        {
            ScoundrelResources.ApplyFont(tmp);
            tmp.text = content;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
        }

        private void HandleMonsterSlain(CardData monsterCard, CombatOutcome outcome, float damageTaken, WeaponSkinTier skinTier)
        {
            Vector3 targetPos = ResolveActionWorldPosition();

            switch (outcome)
            {
                case CombatOutcome.ObliteratedWithWeapon:
                    if (skinTier == WeaponSkinTier.Legendary)
                    {
                        PlayLegendaryObliterateFX(targetPos);
                    }
                    else
                    {
                        PlayStandardOrFoilObliterateFX(targetPos, skinTier == WeaponSkinTier.EpicFoil);
                    }
                    break;

                case CombatOutcome.ChipDamageWithWeapon:
                    PlayChipDamageWithWeaponFX(targetPos, damageTaken);
                    break;

                case CombatOutcome.BarehandedHit:
                    PlayBarehandedHitFX(targetPos, damageTaken);
                    break;
            }
        }

        private void PlayStandardOrFoilObliterateFX(Vector3 targetPos, bool isEpicFoil)
        {
            PlayOneShotSfx(ScoundrelAssetPaths.SfxWeaponObliterateStd, 0.92f, 1.0f);

            Color slashTint = isEpicFoil
                ? new Color(0.42f, 0.96f, 1.00f, 0.98f)
                : new Color(1.00f, 0.94f, 0.72f, 0.98f);

            SpawnSlashSpriteVfx(
                ScoundrelAssetPaths.VfxSlashStandard,
                targetPos,
                new Vector2(230f, 230f),
                startAngleZ: -24f,
                endAngleZ: -12f,
                startScale: 0.42f,
                endScale: 1.25f,
                duration: 0.26f,
                tint: slashTint);

            TriggerTabletopShake(0.14f, new Vector2(7f, 6f), 14);

            Color labelColor = isEpicFoil ? CyanFoilObliterateTextColor : GoldObliterateTextColor;
            SpawnFloatingText("OBLITERATED!", labelColor, targetPos, 38f);
        }

        private void PlayLegendaryObliterateFX(Vector3 targetPos)
        {
            // 1. Brief dramatic screen dim (0.55 in 0.07s -> 0.0 over 0.32s)
            if (_screenDimOverlay != null)
            {
                _screenDimOverlay.gameObject.SetActive(true);
                _screenDimOverlay.blocksRaycasts = false;
                _screenDimOverlay.interactable = false;
                _screenDimOverlay.DOKill(false);

                Sequence dimSeq = DOTween.Sequence().SetTarget(this);
                dimSeq.Append(_screenDimOverlay.DOFade(0.55f, 0.07f).SetEase(Ease.OutQuad));
                dimSeq.Append(_screenDimOverlay.DOFade(0f, 0.32f).SetEase(Ease.InOutQuad));
            }

            // 2. Heavy bass impact + metallic synth stinger
            PlayOneShotSfx(ScoundrelAssetPaths.SfxLegendaryImpact, 1.0f, 1.0f);
            PlayOneShotStinger(ScoundrelAssetPaths.SfxLegendaryStinger, 0.92f);

            // 3. Dramatic diagonal cleave trail + molten shockwave
            SpawnAuraPulse(targetPos, LegendaryCleaveTextColor, 260f, 0.36f);
            SpawnSlashSpriteVfx(
                ScoundrelAssetPaths.VfxSlashLegendary,
                targetPos,
                new Vector2(320f, 320f),
                startAngleZ: -42f,
                endAngleZ: -16f,
                startScale: 0.38f,
                endScale: 1.55f,
                duration: 0.36f,
                tint: new Color(1.00f, 0.88f, 0.38f, 1f));

            // 4. Crisp tabletop punch shake + floating cleave callout
            TriggerTabletopShake(0.28f, new Vector2(18f, 15f), 22);
            SpawnFloatingText("LEGENDARY CLEAVE!", LegendaryCleaveTextColor, targetPos, 42f);
        }

        private void PlayChipDamageWithWeaponFX(Vector3 targetPos, float damageTaken)
        {
            // 0.00s: Metallic parry/deflect clink + compact deflect spark (suppresses triumphant stinger)
            PlayOneShotSfx(ScoundrelAssetPaths.SfxWeaponParryClink, 0.88f, 1.0f);
            SpawnDeflectSparkVfx(targetPos);

            // 0.12s: Player damage impact + tabletop shake + crimson HP loss text
            Vector3 capturedPos = targetPos;
            string dmgStr = FormatAmount(damageTaken);

            DOVirtual.DelayedCall(0.12f, () =>
            {
                if (this == null) return;
                PlayOneShotSfx(ScoundrelAssetPaths.SfxPlayerTakeDamage, 0.92f, 1.0f);
                TriggerTabletopShake(0.24f, new Vector2(14f, 12f), 18);
                SpawnFloatingText($"-{dmgStr} HP", CrimsonDamageTextColor, capturedPos, 38f);
            }).SetTarget(this);
        }

        private void PlayBarehandedHitFX(Vector3 targetPos, float damageTaken)
        {
            // If Ofe Vanishing Charm dodged 100% of barehanded damage, suppress heavy damage SFX & red HP loss text
            if (damageTaken <= 0.001f)
            {
                return;
            }

            // Barehanded hit: ONLY heavy player damage sound + stronger shake + crimson HP text
            PlayOneShotSfx(ScoundrelAssetPaths.SfxPlayerTakeDamage, 1.0f, 0.95f);
            TriggerTabletopShake(0.34f, new Vector2(24f, 20f), 24);

            string dmgStr = FormatAmount(damageTaken);
            SpawnFloatingText($"-{dmgStr} HP", HeavyBarehandedTextColor, targetPos, 42f);
        }

        private void HandlePotionDrank(float healAmount, bool wasted)
        {
            Vector3 targetPos = ResolveActionWorldPosition();

            if (!wasted)
            {
                PlayOneShotSfx(ScoundrelAssetPaths.SfxPotionDrink, 0.88f, 1.0f);
                SpawnAuraPulse(targetPos, PotionHealTextColor, 165f, 0.32f);

                string healStr = FormatAmount(healAmount);
                SpawnFloatingText($"+{healStr} HP", PotionHealTextColor, targetPos, 38f);
            }
            else
            {
                PlayOneShotSfx(ScoundrelAssetPaths.SfxPotionDrink, 0.48f, 0.84f);
                SpawnFloatingText("2ND POTION DISCARDED (0 HP)", WastedPotionTextColor, targetPos, 30f);
            }
        }

        private void HandleEpicPotionActivated(int buffRoomsLeft)
        {
            Vector3 targetPos = ResolveActionWorldPosition();
            PlayOneShotStinger(ScoundrelAssetPaths.SfxEpicPotionUse, 0.92f);

            SpawnAuraPulse(targetPos, EpicAuraGoldColor, 260f, 0.46f);
            SpawnAuraPulse(targetPos, new Color(0.45f, 0.96f, 1.0f, 0.72f), 190f, 0.34f);
            SpawnFloatingText($"EPIC ELIXIR ({buffRoomsLeft} ROOMS)", LegendaryCleaveTextColor, targetPos, 34f);
        }

        private void HandleMarketplaceItemActivated(int itemId, int buffRoomsLeft)
        {
            Vector3 targetPos = ResolveActionWorldPosition();
            PlayOneShotStinger(ScoundrelAssetPaths.SfxEpicPotionUse, 0.90f);

            if (itemId == (int)MarketplaceItemId.OfeCharm)
            {
                SpawnAuraPulse(targetPos, OfeVanishCyanColor, 255f, 0.44f);
                SpawnAuraPulse(targetPos, new Color(0.68f, 0.42f, 1.00f, 0.76f), 185f, 0.32f);
                SpawnFloatingText($"OFE CHARM ({buffRoomsLeft} ROOMS)", OfeVanishCyanColor, targetPos, 34f);
            }
            else if (itemId == (int)MarketplaceItemId.RustyShield)
            {
                SpawnAuraPulse(targetPos, RustyShieldAmberColor, 250f, 0.44f);
                SpawnAuraPulse(targetPos, new Color(0.82f, 0.88f, 0.96f, 0.76f), 185f, 0.32f);
                SpawnFloatingText($"RUSTY SHIELD ({buffRoomsLeft} ROOMS)", RustyShieldAmberColor, targetPos, 34f);
            }
        }

        private void HandleOfeDodgeTriggered(CardData monster)
        {
            Vector3 targetPos = ResolveActionWorldPosition();

            // Phase-shift whoosh + cyan aura + horizontal phase jitter
            PlayOneShotSfx(ScoundrelAssetPaths.SfxCardDiscard, 0.94f, 1.14f);
            SpawnAuraPulse(targetPos, OfeVanishCyanColor, 230f, 0.38f);

            if (_tabletopShakeRoot != null)
            {
                _tabletopShakeRoot.DOComplete(false);
                _tabletopShakeRoot.DOShakeAnchorPos(0.24f, new Vector2(26f, 4f), 28, 0f, false, true);
            }

            SpawnFloatingText("OFE! VANISHED (0 DMG)!", OfeVanishCyanColor, targetPos, 38f);
        }

        private void HandleRustyShieldTriggered(CardData monster, float damageBlocked)
        {
            Vector3 targetPos = ResolveActionWorldPosition();

            PlayOneShotSfx(ScoundrelAssetPaths.SfxWeaponParryClink, 0.92f, 0.94f);
            SpawnDeflectSparkVfx(targetPos);
            SpawnAuraPulse(targetPos, RustyShieldAmberColor, 200f, 0.34f);

            SpawnFloatingText("RUSTY SHIELD (-50% DMG)!", RustyShieldAmberColor, targetPos, 36f);
        }

        private void HandleRoomCleared(int threat, SpoilsTier tier, float echoHealed)
        {
            PlaySpoilsFanfare();

            if (echoHealed > 0.001f)
            {
                Vector3 targetPos = ResolveActionWorldPosition();
                string echoStr = FormatAmount(echoHealed);
                SpawnAuraPulse(targetPos, EchoHealTextColor, 175f, 0.34f);
                SpawnFloatingText($"+{echoStr} ECHO HP", EchoHealTextColor, targetPos, 34f);
            }
        }

        private void SpawnSlashSpriteVfx(
            string spriteResourcePath,
            Vector3 worldPos,
            Vector2 baseSize,
            float startAngleZ,
            float endAngleZ,
            float startScale,
            float endScale,
            float duration,
            Color tint)
        {
            RectTransform container = ResolveVfxContainer();
            if (container == null) return;

            Sprite slashSprite = ScoundrelResources.LoadSprite(spriteResourcePath);
            if (slashSprite == null)
            {
                slashSprite = GetOrCreateProceduralRingSprite();
            }

            GameObject slashGo = new GameObject("SlashVFX", typeof(RectTransform), typeof(Image));
            slashGo.transform.SetParent(container, false);
            slashGo.transform.SetAsLastSibling();

            RectTransform rect = slashGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = baseSize;
            PositionRectAtWorldPoint(rect, worldPos);
            rect.localEulerAngles = new Vector3(0f, 0f, startAngleZ);
            rect.localScale = Vector3.one * startScale;

            Image img = slashGo.GetComponent<Image>();
            img.sprite = slashSprite;
            img.color = tint;
            img.preserveAspect = true;
            img.raycastTarget = false;

            Sequence seq = DOTween.Sequence().SetTarget(this);
            seq.Append(rect.DOScale(endScale, duration).SetEase(Ease.OutExpo));
            seq.Join(rect.DORotate(new Vector3(0f, 0f, endAngleZ), duration).SetEase(Ease.OutCubic));
            seq.Insert(duration * 0.30f, img.DOFade(0f, duration * 0.70f).SetEase(Ease.InQuad));
            seq.OnComplete(() =>
            {
                if (slashGo != null)
                {
                    Destroy(slashGo);
                }
            });
        }

        private void SpawnDeflectSparkVfx(Vector3 worldPos)
        {
            RectTransform container = ResolveVfxContainer();
            if (container == null) return;

            Sprite sparkSprite = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.UiShieldBadge);
            if (sparkSprite == null)
            {
                sparkSprite = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.VfxSlashStandard);
            }
            if (sparkSprite == null)
            {
                sparkSprite = GetOrCreateProceduralRingSprite();
            }

            GameObject sparkGo = new GameObject("DeflectSparkVFX", typeof(RectTransform), typeof(Image));
            sparkGo.transform.SetParent(container, false);
            sparkGo.transform.SetAsLastSibling();

            RectTransform rect = sparkGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(115f, 115f);
            PositionRectAtWorldPoint(rect, worldPos);
            rect.localEulerAngles = new Vector3(0f, 0f, 14f);
            rect.localScale = Vector3.one * 0.45f;

            Image img = sparkGo.GetComponent<Image>();
            img.sprite = sparkSprite;
            img.color = new Color(1.00f, 0.86f, 0.52f, 0.96f);
            img.preserveAspect = true;
            img.raycastTarget = false;

            Sequence seq = DOTween.Sequence().SetTarget(this);
            seq.Append(rect.DOScale(1.05f, 0.14f).SetEase(Ease.OutBack));
            seq.Join(rect.DORotate(new Vector3(0f, 0f, -10f), 0.16f).SetEase(Ease.OutQuad));
            seq.Insert(0.05f, img.DOFade(0f, 0.12f).SetEase(Ease.InQuad));
            seq.OnComplete(() =>
            {
                if (sparkGo != null)
                {
                    Destroy(sparkGo);
                }
            });
        }

        private void SpawnAuraPulse(Vector3 worldPos, Color auraColor, float targetDiameter, float duration)
        {
            RectTransform container = ResolveVfxContainer();
            if (container == null) return;

            GameObject pulseGo = new GameObject("AuraPulseVFX", typeof(RectTransform), typeof(Image));
            pulseGo.transform.SetParent(container, false);
            pulseGo.transform.SetAsLastSibling();

            RectTransform rect = pulseGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(targetDiameter, targetDiameter);
            PositionRectAtWorldPoint(rect, worldPos);
            rect.localScale = Vector3.one * 0.28f;

            Image img = pulseGo.GetComponent<Image>();
            img.sprite = GetOrCreateProceduralRingSprite();
            img.color = auraColor;
            img.preserveAspect = true;
            img.raycastTarget = false;

            Sequence seq = DOTween.Sequence().SetTarget(this);
            seq.Append(rect.DOScale(1.18f, duration).SetEase(Ease.OutCubic));
            seq.Join(img.DOFade(0f, duration).SetEase(Ease.OutQuad));
            seq.OnComplete(() =>
            {
                if (pulseGo != null)
                {
                    Destroy(pulseGo);
                }
            });
        }

        private void TriggerTabletopShake(float duration, Vector2 strength, int vibrato)
        {
            if (_tabletopShakeRoot == null) return;

            _tabletopShakeRoot.DOComplete(false);
            _tabletopShakeRoot.DOShakeAnchorPos(duration, strength, vibrato, 90f, false, true);
        }

        private void StartLoopingDungeonBgm()
        {
            if (_bgmSource == null) return;

            AudioClip bgmClip = ScoundrelResources.LoadAudio(ScoundrelAssetPaths.BgmDungeonLoop);
            if (bgmClip == null) return;

            _bgmSource.loop = true;
            _bgmSource.playOnAwake = false;
            _bgmSource.volume = DefaultBgmVolume;

            if (_bgmSource.clip != bgmClip)
            {
                _bgmSource.clip = bgmClip;
            }
            if (!_bgmSource.isPlaying)
            {
                _bgmSource.Play();
            }
        }

        private void PlayOneShotSfx(string resourcePath, float volumeScale, float pitch)
        {
            if (_sfxSource == null) return;

            AudioClip clip = ScoundrelResources.LoadAudio(resourcePath);
            if (clip == null) return;

            _sfxSource.pitch = pitch;
            _sfxSource.PlayOneShot(clip, volumeScale);
        }

        private void PlayOneShotStinger(string resourcePath, float volumeScale)
        {
            AudioSource source = _stingerSource != null ? _stingerSource : _sfxSource;
            if (source == null) return;

            AudioClip clip = ScoundrelResources.LoadAudio(resourcePath);
            if (clip == null) return;

            source.pitch = 1f;
            source.PlayOneShot(clip, volumeScale);
        }

        private void EnsureAudioSources()
        {
            if (_bgmSource == null)
            {
                _bgmSource = CreateChildAudioSource("BGM_AudioSource", loop: true, volume: DefaultBgmVolume);
            }
            if (_sfxSource == null)
            {
                _sfxSource = CreateChildAudioSource("SFX_AudioSource", loop: false, volume: 1f);
            }
            if (_stingerSource == null)
            {
                _stingerSource = CreateChildAudioSource("Stinger_AudioSource", loop: false, volume: 1f);
            }
        }

        private AudioSource CreateChildAudioSource(string sourceName, bool loop, float volume)
        {
            Transform existing = transform.Find(sourceName);
            if (existing != null)
            {
                AudioSource found = existing.GetComponent<AudioSource>();
                if (found != null) return found;
            }

            GameObject go = new GameObject(sourceName, typeof(AudioSource));
            go.transform.SetParent(transform, false);
            AudioSource src = go.GetComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = loop;
            src.spatialBlend = 0f;
            src.volume = volume;
            return src;
        }

        private void SubscribeToManager(ScoundrelGameManager manager)
        {
            if (manager == null) return;

            _subscribedManager = manager;
            _subscribedManager.OnMonsterSlain += HandleMonsterSlain;
            _subscribedManager.OnPotionDrank += HandlePotionDrank;
            _subscribedManager.OnEpicPotionActivated += HandleEpicPotionActivated;
            _subscribedManager.OnMarketplaceItemActivated += HandleMarketplaceItemActivated;
            _subscribedManager.OnOfeDodgeTriggered += HandleOfeDodgeTriggered;
            _subscribedManager.OnRustyShieldTriggered += HandleRustyShieldTriggered;
            _subscribedManager.OnRoomCleared += HandleRoomCleared;
        }

        private void UnsubscribeFromManager()
        {
            if (_subscribedManager == null) return;

            _subscribedManager.OnMonsterSlain -= HandleMonsterSlain;
            _subscribedManager.OnPotionDrank -= HandlePotionDrank;
            _subscribedManager.OnEpicPotionActivated -= HandleEpicPotionActivated;
            _subscribedManager.OnMarketplaceItemActivated -= HandleMarketplaceItemActivated;
            _subscribedManager.OnOfeDodgeTriggered -= HandleOfeDodgeTriggered;
            _subscribedManager.OnRustyShieldTriggered -= HandleRustyShieldTriggered;
            _subscribedManager.OnRoomCleared -= HandleRoomCleared;
            _subscribedManager = null;
        }

        private RectTransform ResolveVfxContainer()
        {
            if (_vfxContainer != null) return _vfxContainer;
            if (_tabletopShakeRoot != null) return _tabletopShakeRoot;

            Canvas parentCanvas = GetComponentInParent<Canvas>();
            if (parentCanvas != null)
            {
                return parentCanvas.transform as RectTransform;
            }
            return null;
        }

        private Vector3 ResolveActionWorldPosition()
        {
            if (_hasNextActionWorldPos)
            {
                return _nextActionWorldPos;
            }

            RectTransform container = ResolveVfxContainer();
            return container != null ? container.position : transform.position;
        }

        private static void PositionRectAtWorldPoint(RectTransform rect, Vector3 worldPos)
        {
            rect.position = worldPos;
            Vector3 localPos = rect.localPosition;
            localPos.z = 0f;
            rect.localPosition = localPos;
        }

        private static string FormatAmount(float value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static float RandomPitch(float min, float max)
        {
            return UnityEngine.Random.Range(min, max);
        }

        private static Sprite GetOrCreateProceduralRingSprite()
        {
            if (_proceduralRingSprite != null)
            {
                return _proceduralRingSprite;
            }

            const int size = 64;
            _proceduralRingTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ScoundrelProceduralAuraRing",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color[] pixels = new Color[size * size];
            float center = (size - 1) * 0.5f;
            float invRadius = 1f / center;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center) * invRadius;
                    float dy = (y - center) * invRadius;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    // Crisp vector ring + soft inner glow
                    float outerMask = 1f - Mathf.SmoothStep(0.84f, 0.98f, dist);
                    float ringPeak = Mathf.SmoothStep(0.45f, 0.78f, dist) * outerMask;
                    float innerSoft = (1f - Mathf.SmoothStep(0.0f, 0.82f, dist)) * 0.28f;
                    float alpha = Mathf.Clamp01(ringPeak + innerSoft);

                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            _proceduralRingTexture.SetPixels(pixels);
            _proceduralRingTexture.Apply(false, false);

            _proceduralRingSprite = Sprite.Create(
                _proceduralRingTexture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f);

            return _proceduralRingSprite;
        }

        private void OnDestroy()
        {
            UnsubscribeFromManager();
            DOTween.Kill(this);

            if (_tabletopShakeRoot != null)
            {
                _tabletopShakeRoot.DOKill(false);
            }
            if (_screenDimOverlay != null)
            {
                _screenDimOverlay.DOKill(false);
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
