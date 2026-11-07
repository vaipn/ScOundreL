using System;
using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Scoundrel
{
    public class BalatroCardVisual : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerMoveHandler,
        IPointerClickHandler
    {
        private static readonly int TiltPropId = Shader.PropertyToID("_Tilt");
        private static readonly int FoilIntensityPropId = Shader.PropertyToID("_FoilIntensity");
        private static readonly int SheenWidthPropId = Shader.PropertyToID("_SheenWidth");
        private static readonly int FoilModePropId = Shader.PropertyToID("_FoilMode");

        private static readonly Color DefaultShadowTint = new Color(0.03f, 0.04f, 0.07f, 0.58f);
        private static readonly Color SelectionGlowColor = new Color(1.00f, 0.84f, 0.28f, 0.92f);
        private static readonly Vector2 BaseShadowOffset = new Vector2(6f, -8f);

        private const float MaxTiltDegrees = 14f;
        private const float MaxSubtleRollDegrees = 2.6f;
        private const float TiltSmoothingSpeed = 20f;

        [Header("Hierarchy References (Auto-Created if Null)")]
        [SerializeField] private CanvasGroup _canvasGroup;
        [SerializeField] private RectTransform _shadowRect;
        [SerializeField] private Image _shadowImage;
        [SerializeField] private RectTransform _tiltPivot;
        [SerializeField] private Image _selectionBorderImage;
        [SerializeField] private Image _cardFaceImage;
        [SerializeField] private Image _legendaryEmblemImage;
        [SerializeField] private Image _raycastHitboxImage;

        private Canvas _cachedCanvas;
        private Material _defaultBaseFoilMaterial;
        private Material _foilMaterialInstance;
        private Sprite _frontSprite;

        private bool _hierarchyInitialized;
        private bool _foilEnabled;
        private bool _legendaryMode;
        private bool _isShowingCardBack;
        private bool _isFlipping;
        private bool _isSelected;

        private Vector2 _targetNormTilt;
        private Vector2 _currentNormTilt;
        private float _currentHoverLift;

        private Sequence _motionSequence;
        private Tween _hoverPunchTween;
        private Tween _selectionTween;

        public CardData CardData { get; private set; }
        public int RoomSlotIndex { get; set; } = -1;
        public bool IsInteractable { get; set; } = true;
        public bool IsHovered { get; private set; }
        public RectTransform RectTransform { get; private set; }

        public event Action<BalatroCardVisual> OnCardClicked;

        public static BalatroCardVisual CreateCardVisual(Transform parent, Vector2 cardSize, Material baseFoilMaterial = null)
        {
            GameObject rootGo = new GameObject("BalatroCard", typeof(RectTransform), typeof(CanvasGroup));
            if (parent != null)
            {
                rootGo.transform.SetParent(parent, false);
            }

            RectTransform rootRect = rootGo.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = cardSize;
            rootRect.anchoredPosition = Vector2.zero;
            rootRect.localScale = Vector3.one;
            rootRect.localRotation = Quaternion.identity;

            BalatroCardVisual visual = rootGo.AddComponent<BalatroCardVisual>();
            visual._defaultBaseFoilMaterial = baseFoilMaterial;
            visual.EnsureHierarchy();
            return visual;
        }

        private void Awake()
        {
            EnsureHierarchy();
        }

        private void EnsureHierarchy()
        {
            if (_hierarchyInitialized && RectTransform != null && _cardFaceImage != null && _tiltPivot != null && _shadowRect != null)
            {
                return;
            }

            RectTransform = GetComponent<RectTransform>();
            if (RectTransform == null)
            {
                RectTransform = gameObject.AddComponent<RectTransform>();
            }

            _canvasGroup = GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
            {
                _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }

            // Invisible static hitbox on root prevents 3D hover tilt from causing edge-raycast oscillation
            if (_raycastHitboxImage == null)
            {
                Image existingRootImg = GetComponent<Image>();
                if (existingRootImg != null)
                {
                    _raycastHitboxImage = existingRootImg;
                }
                else
                {
                    _raycastHitboxImage = gameObject.AddComponent<Image>();
                    _raycastHitboxImage.color = new Color(0f, 0f, 0f, 0f);
                    _raycastHitboxImage.raycastTarget = true;
                }
            }

            Sprite defaultBackSprite = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.CardBackDungeon);

            // 1. Dynamic Drop Shadow
            if (_shadowRect == null)
            {
                Transform existingShadow = transform.Find("Shadow");
                if (existingShadow != null)
                {
                    _shadowRect = existingShadow as RectTransform;
                    _shadowImage = existingShadow.GetComponent<Image>();
                }
            }

            if (_shadowRect == null)
            {
                GameObject shadowGo = new GameObject("Shadow", typeof(RectTransform), typeof(Image));
                shadowGo.transform.SetParent(transform, false);
                _shadowRect = shadowGo.GetComponent<RectTransform>();
                _shadowRect.anchorMin = Vector2.zero;
                _shadowRect.anchorMax = Vector2.one;
                _shadowRect.pivot = new Vector2(0.5f, 0.5f);
                _shadowRect.sizeDelta = Vector2.zero;
                _shadowRect.anchoredPosition = BaseShadowOffset;

                _shadowImage = shadowGo.GetComponent<Image>();
                _shadowImage.sprite = defaultBackSprite;
                _shadowImage.color = DefaultShadowTint;
                _shadowImage.preserveAspect = true;
                _shadowImage.raycastTarget = false;
            }

            // 2. 3D Tilt Pivot Container
            if (_tiltPivot == null)
            {
                Transform existingPivot = transform.Find("TiltPivot");
                if (existingPivot != null)
                {
                    _tiltPivot = existingPivot as RectTransform;
                }
            }

            if (_tiltPivot == null)
            {
                GameObject pivotGo = new GameObject("TiltPivot", typeof(RectTransform));
                pivotGo.transform.SetParent(transform, false);
                _tiltPivot = pivotGo.GetComponent<RectTransform>();
                _tiltPivot.anchorMin = Vector2.zero;
                _tiltPivot.anchorMax = Vector2.one;
                _tiltPivot.pivot = new Vector2(0.5f, 0.5f);
                _tiltPivot.sizeDelta = Vector2.zero;
                _tiltPivot.anchoredPosition = Vector2.zero;
                _tiltPivot.localScale = Vector3.one;
                _tiltPivot.localRotation = Quaternion.identity;
            }

            // 3. Selection Highlight Border
            if (_selectionBorderImage == null)
            {
                Transform existingBorder = _tiltPivot.Find("SelectionHighlight");
                if (existingBorder != null)
                {
                    _selectionBorderImage = existingBorder.GetComponent<Image>();
                }
            }

            if (_selectionBorderImage == null)
            {
                GameObject borderGo = new GameObject("SelectionHighlight", typeof(RectTransform), typeof(Image));
                borderGo.transform.SetParent(_tiltPivot, false);
                RectTransform borderRect = borderGo.GetComponent<RectTransform>();
                borderRect.anchorMin = Vector2.zero;
                borderRect.anchorMax = Vector2.one;
                borderRect.pivot = new Vector2(0.5f, 0.5f);
                borderRect.sizeDelta = new Vector2(14f, 14f);
                borderRect.anchoredPosition = Vector2.zero;

                _selectionBorderImage = borderGo.GetComponent<Image>();
                _selectionBorderImage.sprite = defaultBackSprite;
                _selectionBorderImage.color = SelectionGlowColor;
                _selectionBorderImage.preserveAspect = true;
                _selectionBorderImage.raycastTarget = false;
                borderGo.SetActive(false);
            }

            // 4. Primary Card Face
            if (_cardFaceImage == null)
            {
                Transform existingFace = _tiltPivot.Find("CardFace");
                if (existingFace != null)
                {
                    _cardFaceImage = existingFace.GetComponent<Image>();
                }
            }

            if (_cardFaceImage == null)
            {
                GameObject faceGo = new GameObject("CardFace", typeof(RectTransform), typeof(Image));
                faceGo.transform.SetParent(_tiltPivot, false);
                RectTransform faceRect = faceGo.GetComponent<RectTransform>();
                faceRect.anchorMin = Vector2.zero;
                faceRect.anchorMax = Vector2.one;
                faceRect.pivot = new Vector2(0.5f, 0.5f);
                faceRect.sizeDelta = Vector2.zero;
                faceRect.anchoredPosition = Vector2.zero;

                _cardFaceImage = faceGo.GetComponent<Image>();
                _cardFaceImage.sprite = defaultBackSprite;
                _cardFaceImage.color = Color.white;
                _cardFaceImage.preserveAspect = true;
                _cardFaceImage.raycastTarget = (_raycastHitboxImage == null);
            }

            // 5. Optional Legendary Weapon Emblem Badge
            if (_legendaryEmblemImage == null)
            {
                Transform existingEmblem = _tiltPivot.Find("LegendaryEmblem");
                if (existingEmblem != null)
                {
                    _legendaryEmblemImage = existingEmblem.GetComponent<Image>();
                }
            }

            if (_legendaryEmblemImage == null)
            {
                GameObject emblemGo = new GameObject("LegendaryEmblem", typeof(RectTransform), typeof(Image));
                emblemGo.transform.SetParent(_tiltPivot, false);
                RectTransform emblemRect = emblemGo.GetComponent<RectTransform>();
                emblemRect.anchorMin = new Vector2(1f, 1f);
                emblemRect.anchorMax = new Vector2(1f, 1f);
                emblemRect.pivot = new Vector2(0.5f, 0.5f);
                emblemRect.sizeDelta = new Vector2(36f, 36f);
                emblemRect.anchoredPosition = new Vector2(-20f, -20f);

                _legendaryEmblemImage = emblemGo.GetComponent<Image>();
                _legendaryEmblemImage.sprite = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.SkinWeaponLegendary);
                _legendaryEmblemImage.color = Color.white;
                _legendaryEmblemImage.preserveAspect = true;
                _legendaryEmblemImage.raycastTarget = false;
                emblemGo.SetActive(false);
            }

            _cachedCanvas = GetComponentInParent<Canvas>();
            _hierarchyInitialized = true;
        }

        public void SetupCard(CardData card, bool enableFoil = false, bool legendaryMode = false, Material sharedFoilMaterial = null)
        {
            EnsureHierarchy();
            CardData = card;

            Sprite loaded = ScoundrelResources.LoadSprite(card.ResourcePath);
            if (loaded == null)
            {
                loaded = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.CardBackDungeon);
            }

            _frontSprite = loaded;
            _isShowingCardBack = false;
            ApplyDisplayedSprite(_frontSprite);
            SetFoilState(enableFoil, legendaryMode, sharedFoilMaterial);
        }

        public void SetupCustomSprite(Sprite faceSprite, bool enableFoil = false, bool legendaryMode = false, Material sharedFoilMaterial = null)
        {
            EnsureHierarchy();
            _frontSprite = faceSprite != null
                ? faceSprite
                : ScoundrelResources.LoadSprite(ScoundrelAssetPaths.CardBackDungeon);

            _isShowingCardBack = false;
            ApplyDisplayedSprite(_frontSprite);
            SetFoilState(enableFoil, legendaryMode, sharedFoilMaterial);
        }

        public void SetFoilState(bool enableFoil, bool legendaryMode = false, Material sharedFoilMaterial = null)
        {
            EnsureHierarchy();
            _foilEnabled = enableFoil || legendaryMode;
            _legendaryMode = legendaryMode;

            if (sharedFoilMaterial != null)
            {
                _defaultBaseFoilMaterial = sharedFoilMaterial;
            }

            RefreshFoilAndEmblemVisuals();
        }

        public void SetSelectedHighlight(bool selected)
        {
            EnsureHierarchy();
            _isSelected = selected;

            if (_selectionBorderImage != null)
            {
                _selectionTween?.Kill();
                _selectionBorderImage.gameObject.SetActive(selected);

                if (selected)
                {
                    _selectionBorderImage.color = SelectionGlowColor;
                    _selectionTween = _selectionBorderImage
                        .DOFade(0.50f, 0.52f)
                        .SetLoops(-1, LoopType.Yoyo)
                        .SetEase(Ease.InOutSine);
                }
            }

            if (_tiltPivot != null && !_isFlipping)
            {
                _tiltPivot.DOKill(false);
                float targetLiftY = selected ? 10f : 0f;
                _tiltPivot.DOAnchorPosY(targetLiftY, 0.16f).SetEase(Ease.OutQuad);
                if (!IsHovered)
                {
                    _tiltPivot.localScale = Vector3.one;
                }
            }
        }

        public void AnimateDealFromDeck(Vector3 deckWorldPos, Vector3 targetWorldPos, float delay, Action onComplete = null)
        {
            EnsureHierarchy();
            KillActiveMotionTweens();
            ResetHoverImmediate();

            Sprite cardBack = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.CardBackDungeon);
            _isShowingCardBack = cardBack != null;
            _isFlipping = true;

            if (_isShowingCardBack)
            {
                ApplyDisplayedSprite(cardBack);
            }
            RefreshFoilAndEmblemVisuals();

            transform.position = deckWorldPos;
            transform.localScale = Vector3.one * 0.80f;
            transform.localRotation = Quaternion.Euler(0f, 0f, -10f);
            _tiltPivot.localRotation = Quaternion.identity;
            _tiltPivot.anchoredPosition = Vector2.zero;
            if (_canvasGroup != null) _canvasGroup.alpha = 1f;

            const float moveDuration = 0.34f;
            const float halfFlipDuration = 0.13f;

            _motionSequence = DOTween.Sequence();
            if (delay > 0f)
            {
                _motionSequence.AppendInterval(delay);
            }

            _motionSequence.AppendCallback(() =>
            {
                WeaponSkinFXController.Instance?.PlayCardDeal();
            });

            _motionSequence.Append(transform.DOMove(targetWorldPos, moveDuration).SetEase(Ease.OutBack, 1.12f));
            _motionSequence.Join(transform.DOScale(Vector3.one, moveDuration * 0.9f).SetEase(Ease.OutBack));
            _motionSequence.Join(transform.DORotate(Vector3.zero, moveDuration).SetEase(Ease.OutCubic));

            // Mid-flight 3D flip from dungeon card back to face sprite
            Sequence flipSubSeq = DOTween.Sequence();
            flipSubSeq.AppendInterval(0.04f);
            flipSubSeq.Append(_tiltPivot.DOLocalRotate(new Vector3(0f, 90f, 0f), halfFlipDuration).SetEase(Ease.InSine));
            flipSubSeq.AppendCallback(() =>
            {
                _isShowingCardBack = false;
                if (_frontSprite != null)
                {
                    ApplyDisplayedSprite(_frontSprite);
                }
                RefreshFoilAndEmblemVisuals();
                _tiltPivot.localEulerAngles = new Vector3(0f, -90f, 0f);
            });
            flipSubSeq.Append(_tiltPivot.DOLocalRotate(Vector3.zero, halfFlipDuration + 0.03f).SetEase(Ease.OutBack));

            _motionSequence.Join(flipSubSeq);
            _motionSequence.OnComplete(() =>
            {
                _isFlipping = false;
                _isShowingCardBack = false;
                if (_frontSprite != null)
                {
                    ApplyDisplayedSprite(_frontSprite);
                }
                RefreshFoilAndEmblemVisuals();
                _tiltPivot.localRotation = Quaternion.identity;
                onComplete?.Invoke();
            });
        }

        public void AnimateReturnToDeck(Vector3 deckWorldPos, float delay, Action onComplete = null)
        {
            EnsureHierarchy();
            KillActiveMotionTweens();
            SetSelectedHighlight(false);
            ResetHoverImmediate();

            IsInteractable = false;
            _isFlipping = true;

            const float moveDuration = 0.28f;
            Sprite cardBack = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.CardBackDungeon);

            _motionSequence = DOTween.Sequence();
            if (delay > 0f)
            {
                _motionSequence.AppendInterval(delay);
            }

            _motionSequence.AppendCallback(() =>
            {
                WeaponSkinFXController.Instance?.PlayCardDeal();
            });

            _motionSequence.Append(transform.DOMove(deckWorldPos, moveDuration).SetEase(Ease.InBack, 1.08f));
            _motionSequence.Join(transform.DOScale(Vector3.one * 0.82f, moveDuration).SetEase(Ease.InQuad));
            _motionSequence.Join(transform.DORotate(new Vector3(0f, 0f, 8f), moveDuration).SetEase(Ease.InCubic));

            Sequence flipBackSeq = DOTween.Sequence();
            flipBackSeq.Append(_tiltPivot.DOLocalRotate(new Vector3(0f, 90f, 0f), moveDuration * 0.45f).SetEase(Ease.InSine));
            flipBackSeq.AppendCallback(() =>
            {
                if (cardBack != null)
                {
                    _isShowingCardBack = true;
                    ApplyDisplayedSprite(cardBack);
                    RefreshFoilAndEmblemVisuals();
                }
                _tiltPivot.localEulerAngles = new Vector3(0f, -90f, 0f);
            });
            flipBackSeq.Append(_tiltPivot.DOLocalRotate(Vector3.zero, moveDuration * 0.45f).SetEase(Ease.OutSine));

            _motionSequence.Join(flipBackSeq);
            _motionSequence.OnComplete(() =>
            {
                _isFlipping = false;
                onComplete?.Invoke();
            });
        }

        public void AnimateSweepToDiscard(Vector3 discardWorldPos, float delay, bool destroyOnComplete = true, Action onComplete = null)
        {
            EnsureHierarchy();
            KillActiveMotionTweens();
            SetSelectedHighlight(false);
            ResetHoverImmediate();

            IsInteractable = false;
            _isFlipping = true;

            const float sweepDuration = 0.34f;
            float spinAngle = UnityEngine.Random.Range(-26f, 26f) - 18f;

            _motionSequence = DOTween.Sequence();
            if (delay > 0f)
            {
                _motionSequence.AppendInterval(delay);
            }

            _motionSequence.AppendCallback(() =>
            {
                WeaponSkinFXController.Instance?.PlayCardDiscard();
            });

            _motionSequence.Append(transform.DOMove(discardWorldPos, sweepDuration).SetEase(Ease.InCubic));
            _motionSequence.Join(_tiltPivot.DOPunchAnchorPos(new Vector2(0f, 34f), sweepDuration * 0.88f, 1, 0f));
            _motionSequence.Join(transform.DORotate(new Vector3(0f, 0f, spinAngle), sweepDuration).SetEase(Ease.OutQuad));
            _motionSequence.Join(transform.DOScale(Vector3.one * 0.74f, sweepDuration).SetEase(Ease.InBack));

            if (_canvasGroup != null)
            {
                _motionSequence.Join(_canvasGroup.DOFade(0.86f, sweepDuration).SetEase(Ease.InQuad));
            }

            _motionSequence.OnComplete(() =>
            {
                _isFlipping = false;
                onComplete?.Invoke();
                if (destroyOnComplete && this != null && gameObject != null)
                {
                    Destroy(gameObject);
                }
            });
        }

        public void AnimateEquipWeapon(Transform weaponSlotParent, Vector2 targetAnchoredPos, Action onComplete = null)
        {
            EnsureHierarchy();
            KillActiveMotionTweens();
            SetSelectedHighlight(false);
            ResetHoverImmediate();

            Vector3 preservedWorldPos = transform.position;
            if (weaponSlotParent != null)
            {
                transform.SetParent(weaponSlotParent, true);
                transform.SetAsLastSibling();
                _cachedCanvas = GetComponentInParent<Canvas>();
            }

            RectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            RectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            RectTransform.pivot = new Vector2(0.5f, 0.5f);
            transform.position = preservedWorldPos;
            FixLocalZ();

            _isShowingCardBack = false;
            if (_frontSprite != null)
            {
                ApplyDisplayedSprite(_frontSprite);
            }
            RefreshFoilAndEmblemVisuals();

            const float slideDuration = 0.28f;
            _motionSequence = DOTween.Sequence();
            _motionSequence.Append(RectTransform.DOAnchorPos(targetAnchoredPos, slideDuration).SetEase(Ease.OutBack, 1.2f));
            _motionSequence.Join(transform.DORotate(Vector3.zero, slideDuration * 0.85f).SetEase(Ease.OutCubic));
            _motionSequence.Join(transform.DOScale(Vector3.one, slideDuration * 0.85f).SetEase(Ease.OutQuad));
            _motionSequence.Append(transform.DOPunchScale(new Vector3(0.15f, 0.15f, 0f), 0.20f, 8, 0.65f));
            _motionSequence.OnComplete(() =>
            {
                onComplete?.Invoke();
            });
        }

        public void AnimateStackOntoWeapon(Transform weaponStackParent, int stackIndex, Vector2 cascadeOffsetPerCard, Action onComplete = null)
        {
            EnsureHierarchy();
            KillActiveMotionTweens();
            SetSelectedHighlight(false);
            ResetHoverImmediate();

            IsInteractable = false;

            Vector3 preservedWorldPos = transform.position;
            if (weaponStackParent != null)
            {
                transform.SetParent(weaponStackParent, true);
                transform.SetAsLastSibling();
                _cachedCanvas = GetComponentInParent<Canvas>();
            }

            RectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            RectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            RectTransform.pivot = new Vector2(0.5f, 0.5f);
            transform.position = preservedWorldPos;
            FixLocalZ();

            _isShowingCardBack = false;
            if (_frontSprite != null)
            {
                ApplyDisplayedSprite(_frontSprite);
            }
            RefreshFoilAndEmblemVisuals();

            // Staggered face-up cascade so equipped weapon rank AND all slain monsters stay visible
            Vector2 targetAnchoredPos = cascadeOffsetPerCard * (stackIndex + 1);
            float subtleStackAngle = (stackIndex % 2 == 0) ? -1.6f : 1.6f;

            const float stackDuration = 0.28f;
            _motionSequence = DOTween.Sequence();
            _motionSequence.Append(RectTransform.DOAnchorPos(targetAnchoredPos, stackDuration).SetEase(Ease.OutBack, 1.15f));
            _motionSequence.Join(transform.DOLocalRotate(new Vector3(0f, 0f, subtleStackAngle), stackDuration).SetEase(Ease.OutCubic));
            _motionSequence.Join(transform.DOScale(Vector3.one, stackDuration * 0.85f).SetEase(Ease.OutQuad));
            _motionSequence.Append(_tiltPivot.DOPunchScale(new Vector3(0.08f, 0.08f, 0f), 0.16f, 6, 0.6f));
            _motionSequence.OnComplete(() =>
            {
                onComplete?.Invoke();
            });
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!IsInteractable || _isFlipping) return;

            IsHovered = true;
            UpdateTargetTiltFromPointer(eventData);

            if (_tiltPivot != null)
            {
                _hoverPunchTween?.Kill();
                _tiltPivot.localScale = Vector3.one * 1.05f;
                _hoverPunchTween = _tiltPivot.DOPunchScale(new Vector3(0.075f, 0.075f, 0f), 0.18f, 8, 0.65f);
            }

            WeaponSkinFXController.Instance?.PlayCardHover();
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            if (!IsInteractable || _isFlipping) return;

            if (!IsHovered)
            {
                IsHovered = true;
            }
            UpdateTargetTiltFromPointer(eventData);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            IsHovered = false;
            _targetNormTilt = Vector2.zero;

            if (_tiltPivot != null && !_isFlipping)
            {
                _hoverPunchTween?.Kill();
                _hoverPunchTween = _tiltPivot.DOScale(Vector3.one, 0.15f).SetEase(Ease.OutQuad);
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!IsInteractable || _isFlipping) return;
            if (eventData != null && eventData.button != PointerEventData.InputButton.Left) return;

            if (_tiltPivot != null)
            {
                _hoverPunchTween?.Kill();
                _tiltPivot.localScale = IsHovered ? Vector3.one * 1.05f : Vector3.one;
                _hoverPunchTween = _tiltPivot.DOPunchScale(new Vector3(-0.06f, -0.06f, 0f), 0.12f, 6, 0.5f);
            }

            OnCardClicked?.Invoke(this);
        }

        private void Update()
        {
            if (!_hierarchyInitialized || _isFlipping) return;

            if (!IsInteractable && IsHovered)
            {
                IsHovered = false;
                _targetNormTilt = Vector2.zero;
                if (_tiltPivot != null)
                {
                    _tiltPivot.localScale = Vector3.one;
                }
            }

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            float blend = 1f - Mathf.Exp(-TiltSmoothingSpeed * dt);

            _currentNormTilt = Vector2.Lerp(_currentNormTilt, _targetNormTilt, blend);
            float targetLift = IsHovered ? 1f : 0f;
            _currentHoverLift = Mathf.Lerp(_currentHoverLift, targetLift, blend);

            if (_tiltPivot != null)
            {
                // Tilt card surface toward cursor in 3D perspective
                float tiltX = _currentNormTilt.y * MaxTiltDegrees;
                float tiltY = -_currentNormTilt.x * MaxTiltDegrees;
                float subtleZ = -_currentNormTilt.x * MaxSubtleRollDegrees;
                _tiltPivot.localRotation = Quaternion.Euler(tiltX, tiltY, subtleZ);
            }

            if (_shadowRect != null)
            {
                Vector2 dynamicOffset = new Vector2(
                    -_currentNormTilt.x * 14f,
                    -_currentNormTilt.y * 14f - 6f * _currentHoverLift);
                _shadowRect.anchoredPosition = BaseShadowOffset + dynamicOffset;
            }

            if (_foilEnabled && !_isShowingCardBack && _foilMaterialInstance != null)
            {
                Vector4 tiltVec = new Vector4(_currentNormTilt.x, _currentNormTilt.y, 0f, 0f);
                _foilMaterialInstance.SetVector(TiltPropId, tiltVec);

                if (_cardFaceImage != null)
                {
                    Material renderMat = _cardFaceImage.materialForRendering;
                    if (renderMat != null && renderMat != _foilMaterialInstance && _cardFaceImage.material == _foilMaterialInstance)
                    {
                        renderMat.SetVector(TiltPropId, tiltVec);
                    }
                }
            }
        }

        private void UpdateTargetTiltFromPointer(PointerEventData eventData)
        {
            if (eventData == null || RectTransform == null) return;

            Camera eventCam = ResolveCanvasCamera(eventData);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    RectTransform,
                    eventData.position,
                    eventCam,
                    out Vector2 localPoint))
            {
                Rect rect = RectTransform.rect;
                float halfW = Mathf.Max(1f, rect.width * 0.5f);
                float halfH = Mathf.Max(1f, rect.height * 0.5f);

                float normX = Mathf.Clamp((localPoint.x - rect.center.x) / halfW, -1f, 1f);
                float normY = Mathf.Clamp((localPoint.y - rect.center.y) / halfH, -1f, 1f);
                _targetNormTilt = new Vector2(normX, normY);
            }
        }

        private Camera ResolveCanvasCamera(PointerEventData eventData)
        {
            if (_cachedCanvas == null)
            {
                _cachedCanvas = GetComponentInParent<Canvas>();
            }

            if (_cachedCanvas != null)
            {
                Canvas root = _cachedCanvas.rootCanvas != null ? _cachedCanvas.rootCanvas : _cachedCanvas;
                if (root.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    return null;
                }
                if (root.worldCamera != null)
                {
                    return root.worldCamera;
                }
            }

            if (eventData != null && eventData.enterEventCamera != null)
            {
                return eventData.enterEventCamera;
            }

            return Camera.main;
        }

        private void ApplyDisplayedSprite(Sprite sprite)
        {
            if (_cardFaceImage != null)
            {
                _cardFaceImage.sprite = sprite;
            }
            if (_shadowImage != null && sprite != null)
            {
                _shadowImage.sprite = sprite;
            }
            if (_selectionBorderImage != null && sprite != null)
            {
                _selectionBorderImage.sprite = sprite;
            }
        }

        private void RefreshFoilAndEmblemVisuals()
        {
            bool showActiveFoil = _foilEnabled && !_isShowingCardBack;

            if (_cardFaceImage != null)
            {
                if (showActiveFoil)
                {
                    Material mat = GetOrCreateFoilMaterialInstance();
                    if (mat != null)
                    {
                        mat.SetFloat(FoilModePropId, _legendaryMode ? 1f : 0f);
                        mat.SetFloat(FoilIntensityPropId, _legendaryMode ? 1.05f : 0.85f);
                        mat.SetFloat(SheenWidthPropId, _legendaryMode ? 0.19f : 0.16f);
                        mat.SetVector(TiltPropId, new Vector4(_currentNormTilt.x, _currentNormTilt.y, 0f, 0f));
                        _cardFaceImage.material = mat;
                    }
                }
                else
                {
                    _cardFaceImage.material = null;
                }
            }

            if (_legendaryEmblemImage != null)
            {
                if (_legendaryEmblemImage.sprite == null && _legendaryMode)
                {
                    _legendaryEmblemImage.sprite = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.SkinWeaponLegendary);
                }
                bool showEmblem = _legendaryMode && !_isShowingCardBack && _legendaryEmblemImage.sprite != null;
                _legendaryEmblemImage.gameObject.SetActive(showEmblem);
            }
        }

        private Material GetOrCreateFoilMaterialInstance()
        {
            if (_foilMaterialInstance != null)
            {
                return _foilMaterialInstance;
            }

            if (_defaultBaseFoilMaterial != null)
            {
                _foilMaterialInstance = new Material(_defaultBaseFoilMaterial);
                _foilMaterialInstance.name = "HolographicFoilCard_Instance";
                return _foilMaterialInstance;
            }

            Material resMat = Resources.Load<Material>(ScoundrelAssetPaths.HolographicMaterialResource);
            if (resMat != null)
            {
                _foilMaterialInstance = new Material(resMat);
                _foilMaterialInstance.name = "HolographicFoilCard_Instance";
                return _foilMaterialInstance;
            }

            Shader foilShader = Shader.Find(ScoundrelAssetPaths.HolographicShaderName);
            if (foilShader != null)
            {
                _foilMaterialInstance = new Material(foilShader);
                _foilMaterialInstance.name = "HolographicFoilCard_Instance";
                return _foilMaterialInstance;
            }

            return null;
        }

        private void ResetHoverImmediate()
        {
            IsHovered = false;
            _targetNormTilt = Vector2.zero;
            _currentNormTilt = Vector2.zero;
            _currentHoverLift = 0f;

            _hoverPunchTween?.Kill();
            if (_tiltPivot != null)
            {
                _tiltPivot.localScale = Vector3.one;
                _tiltPivot.localRotation = Quaternion.identity;
                if (!_isSelected)
                {
                    _tiltPivot.anchoredPosition = Vector2.zero;
                }
            }
            if (_shadowRect != null)
            {
                _shadowRect.anchoredPosition = BaseShadowOffset;
            }
        }

        private void FixLocalZ()
        {
            Vector3 localPos = transform.localPosition;
            if (Mathf.Abs(localPos.z) > 0.001f)
            {
                localPos.z = 0f;
                transform.localPosition = localPos;
            }
        }

        private void KillActiveMotionTweens()
        {
            _isFlipping = false;
            _motionSequence?.Kill();
            _motionSequence = null;

            transform.DOKill(false);
            if (RectTransform != null) RectTransform.DOKill(false);
            if (_tiltPivot != null) _tiltPivot.DOKill(false);
            if (_canvasGroup != null) _canvasGroup.DOKill(false);
        }

        private void OnDisable()
        {
            IsHovered = false;
            _targetNormTilt = Vector2.zero;
            _currentNormTilt = Vector2.zero;
            _currentHoverLift = 0f;
        }

        private void OnDestroy()
        {
            _motionSequence?.Kill();
            _hoverPunchTween?.Kill();
            _selectionTween?.Kill();

            DOTween.Kill(this);
            if (transform != null) transform.DOKill(false);
            if (RectTransform != null) RectTransform.DOKill(false);
            if (_tiltPivot != null) _tiltPivot.DOKill(false);
            if (_shadowRect != null) _shadowRect.DOKill(false);
            if (_canvasGroup != null) _canvasGroup.DOKill(false);
            if (_selectionBorderImage != null) _selectionBorderImage.DOKill(false);

            if (_foilMaterialInstance != null)
            {
                Destroy(_foilMaterialInstance);
                _foilMaterialInstance = null;
            }
        }
    }
}
