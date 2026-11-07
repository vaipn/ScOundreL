using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Scoundrel
{
    public class MarketplaceModalUI : MonoBehaviour
    {
        [Header("Modal Containers")]
        [SerializeField] private CanvasGroup backdropGroup;
        [SerializeField] private RectTransform modalPanel;

        [Header("Header & Treasury")]
        [SerializeField] private TextMeshProUGUI headerTitleText;
        [SerializeField] private TextMeshProUGUI subtitleText;
        [SerializeField] private TextMeshProUGUI goldBalanceText;
        [SerializeField] private Button closeButton;

        [Header("Ofe (Yoruba Vanishing Charm) — 60 Gold")]
        [SerializeField] private RectTransform ofeCardContainer;
        [SerializeField] private RectTransform ofeVisualHolder;
        [SerializeField] private TextMeshProUGUI ofeTitleText;
        [SerializeField] private TextMeshProUGUI ofeDescText;
        [SerializeField] private TextMeshProUGUI ofeOwnedText;
        [SerializeField] private Button buyOfeButton;
        [SerializeField] private TextMeshProUGUI buyOfeButtonText;

        [Header("Rusty Shield — 45 Gold")]
        [SerializeField] private RectTransform shieldCardContainer;
        [SerializeField] private RectTransform shieldVisualHolder;
        [SerializeField] private TextMeshProUGUI shieldTitleText;
        [SerializeField] private TextMeshProUGUI shieldDescText;
        [SerializeField] private TextMeshProUGUI shieldOwnedText;
        [SerializeField] private Button buyShieldButton;
        [SerializeField] private TextMeshProUGUI buyShieldButtonText;

        [Header("Shader Material")]
        [SerializeField] private Material sharedFoilMaterial;

        private ScoundrelGameManager _gameManager;
        private BalatroCardVisual _ofeCardVisual;
        private BalatroCardVisual _shieldCardVisual;
        private bool _isOpen;
        private readonly float _restingY = 0f;
        private readonly float _hiddenY = -980f;

        public void ConfigureReferences(
            ScoundrelGameManager manager,
            CanvasGroup backdrop,
            RectTransform panel,
            TextMeshProUGUI headerTitle,
            TextMeshProUGUI subtitle,
            TextMeshProUGUI goldBalance,
            Button closeBtn,
            RectTransform ofeContainer,
            RectTransform ofeHolder,
            TextMeshProUGUI ofeTitle,
            TextMeshProUGUI ofeDesc,
            TextMeshProUGUI ofeOwned,
            Button buyOfeBtn,
            TextMeshProUGUI buyOfeBtnText,
            RectTransform shieldContainer,
            RectTransform shieldHolder,
            TextMeshProUGUI shieldTitle,
            TextMeshProUGUI shieldDesc,
            TextMeshProUGUI shieldOwned,
            Button buyShieldBtn,
            TextMeshProUGUI buyShieldBtnText,
            Material foilMat)
        {
            _gameManager = manager;
            backdropGroup = backdrop;
            modalPanel = panel;
            headerTitleText = headerTitle;
            subtitleText = subtitle;
            goldBalanceText = goldBalance;
            closeButton = closeBtn;
            ofeCardContainer = ofeContainer;
            ofeVisualHolder = ofeHolder;
            ofeTitleText = ofeTitle;
            ofeDescText = ofeDesc;
            ofeOwnedText = ofeOwned;
            buyOfeButton = buyOfeBtn;
            buyOfeButtonText = buyOfeBtnText;
            shieldCardContainer = shieldContainer;
            shieldVisualHolder = shieldHolder;
            shieldTitleText = shieldTitle;
            shieldDescText = shieldDesc;
            shieldOwnedText = shieldOwned;
            buyShieldButton = buyShieldBtn;
            buyShieldButtonText = buyShieldBtnText;
            sharedFoilMaterial = foilMat;
        }

        private void Awake()
        {
            WireButtons();
            EnsureCardVisuals();
            if (!_isOpen)
            {
                HideImmediate();
            }
        }

        public void Bind(ScoundrelGameManager manager)
        {
            if (_gameManager != null)
            {
                _gameManager.OnStateChanged -= RefreshState;
                _gameManager.OnMarketplaceItemBought -= HandleItemBought;
            }

            _gameManager = manager;
            if (_gameManager != null)
            {
                _gameManager.OnStateChanged += RefreshState;
                _gameManager.OnMarketplaceItemBought += HandleItemBought;
            }
        }

        private void OnDestroy()
        {
            if (_gameManager != null)
            {
                _gameManager.OnStateChanged -= RefreshState;
                _gameManager.OnMarketplaceItemBought -= HandleItemBought;
            }
            if (backdropGroup != null) backdropGroup.DOKill();
            if (modalPanel != null) modalPanel.DOKill();
        }

        private void WireButtons()
        {
            if (closeButton != null)
            {
                closeButton.onClick.RemoveAllListeners();
                closeButton.onClick.AddListener(CloseModal);
            }

            if (buyOfeButton != null)
            {
                buyOfeButton.onClick.RemoveAllListeners();
                buyOfeButton.onClick.AddListener(() => OnBuyClicked((int)MarketplaceItemId.OfeCharm));
            }

            if (buyShieldButton != null)
            {
                buyShieldButton.onClick.RemoveAllListeners();
                buyShieldButton.onClick.AddListener(() => OnBuyClicked((int)MarketplaceItemId.RustyShield));
            }
        }

        private void EnsureCardVisuals()
        {
            Vector2 previewSize = new Vector2(148f, 206f);

            if (ofeVisualHolder != null && _ofeCardVisual == null)
            {
                Sprite ofeSprite = ScoundrelResources.LoadOfeCharmSprite();
                _ofeCardVisual = BalatroCardVisual.CreateCardVisual(ofeVisualHolder, previewSize, sharedFoilMaterial);
                _ofeCardVisual.RectTransform.anchoredPosition = Vector2.zero;
                _ofeCardVisual.SetupCustomSprite(ofeSprite, true, false, sharedFoilMaterial);
                _ofeCardVisual.OnCardClicked += _ => OnBuyClicked((int)MarketplaceItemId.OfeCharm);
            }

            if (shieldVisualHolder != null && _shieldCardVisual == null)
            {
                Sprite shieldSprite = ScoundrelResources.LoadRustyShieldSprite();
                _shieldCardVisual = BalatroCardVisual.CreateCardVisual(shieldVisualHolder, previewSize, sharedFoilMaterial);
                _shieldCardVisual.RectTransform.anchoredPosition = Vector2.zero;
                _shieldCardVisual.SetupCustomSprite(shieldSprite, false, false, sharedFoilMaterial);
                _shieldCardVisual.OnCardClicked += _ => OnBuyClicked((int)MarketplaceItemId.RustyShield);
            }
        }

        public void ShowModal()
        {
            if (_gameManager == null)
            {
                _gameManager = ScoundrelGameManager.Instance;
                Bind(_gameManager);
            }

            WireButtons();
            EnsureCardVisuals();

            _isOpen = true;
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            if (_gameManager != null && _gameManager.State != null)
            {
                RefreshState(_gameManager.State);
            }

            WeaponSkinFXController.Instance?.PlayCardHover();

            if (backdropGroup != null)
            {
                backdropGroup.DOKill();
                backdropGroup.alpha = 0f;
                backdropGroup.interactable = true;
                backdropGroup.blocksRaycasts = true;
                backdropGroup.DOFade(1f, 0.22f).SetEase(Ease.OutQuad);
            }

            if (modalPanel != null)
            {
                modalPanel.DOKill();
                modalPanel.anchoredPosition = new Vector2(0f, _hiddenY);
                modalPanel.localScale = new Vector3(0.95f, 0.95f, 1f);
                modalPanel.DOAnchorPosY(_restingY, 0.36f).SetEase(Ease.OutBack);
                modalPanel.DOScale(1f, 0.32f).SetEase(Ease.OutBack);
            }
        }

        public void CloseModal()
        {
            if (!_isOpen) return;
            _isOpen = false;

            WeaponSkinFXController.Instance?.PlayCardHover();

            if (backdropGroup != null)
            {
                backdropGroup.interactable = false;
                backdropGroup.blocksRaycasts = false;
                backdropGroup.DOFade(0f, 0.20f).SetEase(Ease.InQuad);
            }

            if (modalPanel != null)
            {
                modalPanel.DOAnchorPosY(_hiddenY, 0.24f).SetEase(Ease.InBack).OnComplete(() =>
                {
                    if (this != null)
                    {
                        gameObject.SetActive(false);
                    }
                });
            }
            else
            {
                gameObject.SetActive(false);
            }
        }

        public void HideImmediate()
        {
            _isOpen = false;
            if (backdropGroup != null)
            {
                backdropGroup.DOKill();
                backdropGroup.alpha = 0f;
                backdropGroup.interactable = false;
                backdropGroup.blocksRaycasts = false;
            }
            if (modalPanel != null)
            {
                modalPanel.DOKill();
                modalPanel.anchoredPosition = new Vector2(0f, _hiddenY);
            }
            gameObject.SetActive(false);
        }

        public void RefreshState(DungeonRunState state)
        {
            if (state == null) return;

            if (goldBalanceText != null)
            {
                goldBalanceText.text = $"AVAILABLE TREASURY: {state.UnclaimedGold} SPL GOLD";
            }

            if (ofeOwnedText != null)
            {
                string activePart = state.OfeBuffRoomsLeft > 0
                    ? $"  •  ACTIVE ({state.OfeBuffRoomsLeft}R LEFT)"
                    : "";
                ofeOwnedText.text = $"Owned in Rack: x{state.OfeInventory}{activePart}";
            }

            if (buyOfeButton != null)
            {
                bool canBuyOfe = state.CanBuyOfe;
                buyOfeButton.interactable = canBuyOfe;
                if (buyOfeButtonText != null)
                {
                    buyOfeButtonText.text = canBuyOfe
                        ? "BUY (60 GOLD)"
                        : "NEED 60 GOLD";
                }
            }

            if (shieldOwnedText != null)
            {
                string activePart = state.RustyShieldBuffRoomsLeft > 0
                    ? $"  •  ACTIVE ({state.RustyShieldBuffRoomsLeft}R LEFT)"
                    : "";
                shieldOwnedText.text = $"Owned in Rack: x{state.RustyShieldInventory}{activePart}";
            }

            if (buyShieldButton != null)
            {
                bool canBuyShield = state.CanBuyRustyShield;
                buyShieldButton.interactable = canBuyShield;
                if (buyShieldButtonText != null)
                {
                    buyShieldButtonText.text = canBuyShield
                        ? "BUY (45 GOLD)"
                        : "NEED 45 GOLD";
                }
            }
        }

        private void OnBuyClicked(int itemId)
        {
            if (_gameManager == null) return;
            bool bought = _gameManager.BuyItem(itemId);
            if (bought)
            {
                WeaponSkinFXController.Instance?.PlayCardDeal();
            }
        }

        private void HandleItemBought(int itemId, int newCount)
        {
            RectTransform targetCard = itemId == (int)MarketplaceItemId.OfeCharm
                ? ofeCardContainer
                : shieldCardContainer;

            if (targetCard != null)
            {
                targetCard.DOKill();
                targetCard.localScale = Vector3.one;
                targetCard.DOPunchScale(new Vector3(0.08f, 0.08f, 0f), 0.24f, 8, 0.85f);
            }

            if (_gameManager != null && _gameManager.State != null)
            {
                RefreshState(_gameManager.State);
            }
        }
    }
}
