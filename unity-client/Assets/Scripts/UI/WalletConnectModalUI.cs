using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Scoundrel
{
    public class WalletConnectModalUI : MonoBehaviour
    {
        [Header("Modal Containers")]
        [SerializeField] private CanvasGroup backdropGroup;
        [SerializeField] private RectTransform modalPanel;

        [Header("Header & Status")]
        [SerializeField] private TextMeshProUGUI headerTitleText;
        [SerializeField] private TextMeshProUGUI subtitleText;
        [SerializeField] private TextMeshProUGUI modeBadgeText;
        [SerializeField] private Image modeBadgeBg;
        [SerializeField] private Button closeButton;

        [Header("Account & Balance Readouts")]
        [SerializeField] private TextMeshProUGUI walletAddressesText;
        [SerializeField] private TextMeshProUGUI balancesSummaryText;
        [SerializeField] private TextMeshProUGUI bridgeStatusText;

        [Header("Action Buttons")]
        [SerializeField] private Button connectWalletAdapterButton;
        [SerializeField] private TextMeshProUGUI connectWalletAdapterButtonText;
        [SerializeField] private Button devnetBurnerButton;
        [SerializeField] private TextMeshProUGUI devnetBurnerButtonText;
        [SerializeField] private Button toggleModeButton;
        [SerializeField] private TextMeshProUGUI toggleModeButtonText;
        [SerializeField] private Button mintGoldButton;
        [SerializeField] private TextMeshProUGUI mintGoldButtonText;
        [SerializeField] private Button copyAddressButton;
        [SerializeField] private TextMeshProUGUI copyAddressButtonText;

        private ScoundrelGameManager _gameManager;
        private bool _isOpen;
        private readonly float _restingY = 0f;
        private readonly float _hiddenY = -980f;

        public void ConfigureReferences(
            ScoundrelGameManager manager,
            CanvasGroup backdrop,
            RectTransform panel,
            TextMeshProUGUI headerTitle,
            TextMeshProUGUI subtitle,
            TextMeshProUGUI modeText,
            Image modeBg,
            Button closeBtn,
            TextMeshProUGUI addressesText,
            TextMeshProUGUI balancesText,
            TextMeshProUGUI statusText,
            Button connectAdapterBtn,
            TextMeshProUGUI connectAdapterBtnText,
            Button burnerBtn,
            TextMeshProUGUI burnerBtnText,
            Button toggleBtn,
            TextMeshProUGUI toggleBtnText,
            Button mintBtn,
            TextMeshProUGUI mintBtnText,
            Button copyBtn,
            TextMeshProUGUI copyBtnText)
        {
            _gameManager = manager;
            backdropGroup = backdrop;
            modalPanel = panel;
            headerTitleText = headerTitle;
            subtitleText = subtitle;
            modeBadgeText = modeText;
            modeBadgeBg = modeBg;
            closeButton = closeBtn;
            walletAddressesText = addressesText;
            balancesSummaryText = balancesText;
            bridgeStatusText = statusText;
            connectWalletAdapterButton = connectAdapterBtn;
            connectWalletAdapterButtonText = connectAdapterBtnText;
            devnetBurnerButton = burnerBtn;
            devnetBurnerButtonText = burnerBtnText;
            toggleModeButton = toggleBtn;
            toggleModeButtonText = toggleBtnText;
            mintGoldButton = mintBtn;
            mintGoldButtonText = mintBtnText;
            copyAddressButton = copyBtn;
            copyAddressButtonText = copyBtnText;
        }

        private void Awake()
        {
            WireButtons();
            if (!_isOpen)
            {
                HideImmediate();
            }
        }

        public void Bind(ScoundrelGameManager manager)
        {
            Unbind();
            _gameManager = manager;
            if (_gameManager != null)
            {
                _gameManager.OnStateChanged += HandleGameStateChanged;
                _gameManager.OnGoldTokensMinted += HandleGoldTokensMinted;
                if (_gameManager.Bridge != null)
                {
                    _gameManager.Bridge.OnBridgeStatusChanged += HandleBridgeStatusChanged;
                    _gameManager.Bridge.OnWalletStateChanged += RefreshView;
                }
            }
        }

        private void Unbind()
        {
            if (_gameManager != null)
            {
                _gameManager.OnStateChanged -= HandleGameStateChanged;
                _gameManager.OnGoldTokensMinted -= HandleGoldTokensMinted;
                if (_gameManager.Bridge != null)
                {
                    _gameManager.Bridge.OnBridgeStatusChanged -= HandleBridgeStatusChanged;
                    _gameManager.Bridge.OnWalletStateChanged -= RefreshView;
                }
            }
        }

        private void OnDestroy()
        {
            Unbind();
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

            if (connectWalletAdapterButton != null)
            {
                connectWalletAdapterButton.onClick.RemoveAllListeners();
                connectWalletAdapterButton.onClick.AddListener(OnConnectWalletAdapterClicked);
            }

            if (devnetBurnerButton != null)
            {
                devnetBurnerButton.onClick.RemoveAllListeners();
                devnetBurnerButton.onClick.AddListener(OnDevnetBurnerClicked);
            }

            if (toggleModeButton != null)
            {
                toggleModeButton.onClick.RemoveAllListeners();
                toggleModeButton.onClick.AddListener(OnToggleModeClicked);
            }

            if (mintGoldButton != null)
            {
                mintGoldButton.onClick.RemoveAllListeners();
                mintGoldButton.onClick.AddListener(OnMintGoldClicked);
            }

            if (copyAddressButton != null)
            {
                copyAddressButton.onClick.RemoveAllListeners();
                copyAddressButton.onClick.AddListener(OnCopyAddressClicked);
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

            _isOpen = true;
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            if (_gameManager?.Bridge != null)
            {
                _ = _gameManager.Bridge.RefreshBalancesAsync();
            }

            RefreshView();
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
                modalPanel.DOAnchorPosY(_restingY, 0.34f).SetEase(Ease.OutBack);
                modalPanel.DOScale(1f, 0.30f).SetEase(Ease.OutBack);
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

        private void HandleGameStateChanged(DungeonRunState _)
        {
            if (_isOpen) RefreshView();
        }

        private void HandleBridgeStatusChanged(string _)
        {
            if (_isOpen) RefreshView();
        }

        private void HandleGoldTokensMinted(uint amount)
        {
            WeaponSkinFXController.Instance?.PlayCardDeal();
            if (_isOpen && modalPanel != null)
            {
                modalPanel.DOKill();
                modalPanel.localScale = Vector3.one;
                modalPanel.DOPunchScale(new Vector3(0.03f, 0.03f, 0f), 0.28f, 6, 0.8f);
            }
            RefreshView();
        }

        public void RefreshView()
        {
            SolanaScoundrelBridge bridge = _gameManager != null ? _gameManager.Bridge : null;
            DungeonRunState state = _gameManager != null ? _gameManager.State : null;

            bool isOffline = bridge == null || bridge.OfflineSimulationMode;
            uint unclaimedGold = state != null ? state.UnclaimedGold : 0;
            ulong mintedGold = bridge != null ? bridge.LastKnownMintedGoldBalance : 0UL;
            double solBal = bridge != null ? bridge.LastKnownSolBalance : 0.0;

            if (modeBadgeText != null)
            {
                modeBadgeText.text = isOffline
                    ? "MODE: LOCAL VRF SIMULATION (ZERO RPC DELAY)"
                    : "MODE: SOLANA DEVNET LIVE (MAGICBLOCK VRF + SESSION KEYS)";
                modeBadgeText.color = isOffline
                    ? new Color(1.0f, 0.88f, 0.36f, 1f)
                    : new Color(0.42f, 0.98f, 0.72f, 1f);
            }

            if (modeBadgeBg != null)
            {
                modeBadgeBg.color = isOffline
                    ? new Color(0.22f, 0.16f, 0.05f, 0.96f)
                    : new Color(0.06f, 0.24f, 0.18f, 0.96f);
            }

            if (walletAddressesText != null && bridge != null)
            {
                string playerKey = string.IsNullOrEmpty(bridge.ActivePlayerPubkey) ? "Not Initialized" : bridge.ActivePlayerPubkey;
                string sessionKey = string.IsNullOrEmpty(bridge.ActiveSessionPubkey) ? "Not Initialized" : bridge.ActiveSessionPubkey;
                string pdaKey = string.IsNullOrEmpty(bridge.DungeonRunPdaAddress) ? "Not Derived" : bridge.DungeonRunPdaAddress;
                string goldMint = string.IsNullOrEmpty(bridge.GoldMintPdaAddress) ? "Not Derived" : bridge.GoldMintPdaAddress;
                string goldAta = string.IsNullOrEmpty(bridge.PlayerGoldAtaAddress) ? "Not Derived" : bridge.PlayerGoldAtaAddress;

                walletAddressesText.text =
                    $"<b>Program ID:</b>  {bridge.ProgramId}   •   <b>RPC:</b>  {bridge.RpcUrl}\n" +
                    $"<b>Player Wallet:</b>  {playerKey}\n" +
                    $"<b>Ephemeral Session Key:</b>  {sessionKey}\n" +
                    $"<b>DungeonRun PDA:</b>  {pdaKey}\n" +
                    $"<b>SPL Gold Mint PDA:</b>  {goldMint}   •   <b>Player Gold ATA:</b>  {goldAta}";
            }

            if (balancesSummaryText != null)
            {
                balancesSummaryText.text =
                    $"Devnet Balance:  <b>{solBal:0.000} SOL</b>     •     " +
                    $"Unclaimed Run Gold:  <color=#FFD954><b>{unclaimedGold} GOLD</b></color>     •     " +
                    $"Minted SPL Gold ATA:  <color=#5EFCA8><b>{mintedGold} GOLD</b></color>";
            }

            if (bridgeStatusText != null && bridge != null)
            {
                string sigSuffix = string.IsNullOrEmpty(bridge.LastTransactionSignature)
                    ? ""
                    : $"   •   Last Tx: {bridge.LastTransactionSignature.Substring(0, Mathf.Min(12, bridge.LastTransactionSignature.Length))}..";
                bridgeStatusText.text = $"Status: {bridge.LastStatusMessage}   •   Settled Txs: {bridge.SettledTransactionCount}{sigSuffix}";
            }

            if (toggleModeButtonText != null)
            {
                toggleModeButtonText.text = isOffline
                    ? "SWITCH TO DEVNET LIVE"
                    : "SWITCH TO LOCAL VRF SIM";
            }

            if (mintGoldButton != null)
            {
                bool canMint = unclaimedGold > 0;
                mintGoldButton.interactable = canMint;
                if (mintGoldButtonText != null)
                {
                    mintGoldButtonText.text = canMint
                        ? $"MINT {unclaimedGold} SPL GOLD TO ATA"
                        : "NO UNCLAIMED GOLD TO MINT";
                }
            }
        }

        private async void OnConnectWalletAdapterClicked()
        {
            if (_gameManager?.Bridge == null) return;
            WeaponSkinFXController.Instance?.PlayCardHover();
            await _gameManager.Bridge.ConnectWalletAdapterAsync();
            RefreshView();
        }

        private async void OnDevnetBurnerClicked()
        {
            if (_gameManager?.Bridge == null) return;
            WeaponSkinFXController.Instance?.PlayCardHover();
            await _gameManager.Bridge.ConnectDevnetBurnerWalletAsync(true);
            RefreshView();
        }

        private void OnToggleModeClicked()
        {
            if (_gameManager?.Bridge == null) return;
            WeaponSkinFXController.Instance?.PlayCardHover();
            bool nextOffline = !_gameManager.Bridge.OfflineSimulationMode;
            _gameManager.Bridge.SetNetworkMode(nextOffline);
            RefreshView();
        }

        private void OnMintGoldClicked()
        {
            if (_gameManager == null) return;
            WeaponSkinFXController.Instance?.PlayCardDeal();
            _gameManager.TryMintGoldTokens();
            RefreshView();
        }

        private void OnCopyAddressClicked()
        {
            if (_gameManager?.Bridge == null) return;
            string addr = _gameManager.Bridge.ActivePlayerPubkey;
            if (!string.IsNullOrEmpty(addr))
            {
                GUIUtility.systemCopyBuffer = addr;
                if (copyAddressButtonText != null)
                {
                    copyAddressButtonText.text = "COPIED PUBKEY!";
                    DOVirtual.DelayedCall(1.5f, () =>
                    {
                        if (this != null && copyAddressButtonText != null)
                        {
                            copyAddressButtonText.text = "COPY WALLET ADDRESS";
                        }
                    });
                }
            }
        }
    }
}
