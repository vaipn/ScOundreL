using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Scoundrel
{
    public class SpoilsModalUI : MonoBehaviour
    {
        [Header("Modal Containers")]
        [SerializeField] private CanvasGroup backdropGroup;
        [SerializeField] private RectTransform modalPanel;

        [Header("Header Elements")]
        [SerializeField] private TextMeshProUGUI headerTitleText;
        [SerializeField] private TextMeshProUGUI threatBadgeText;
        [SerializeField] private Image threatBadgeBg;
        [SerializeField] private TextMeshProUGUI echoSubtitleText;

        [Header("Loot Cards (3 Slots)")]
        [SerializeField] private SpoilsLootCardButton[] lootCards = new SpoilsLootCardButton[3];

        [Header("Merchant Hook")]
        [SerializeField] private Button visitMerchantButton;
        [SerializeField] private MarketplaceModalUI marketplaceModal;

        [Header("Shader Material")]
        [SerializeField] private Material sharedFoilMaterial;

        private ScoundrelGameManager _gameManager;
        private bool _isOpen;
        private float _restingY = 0f;
        private float _hiddenY = -980f;

        public void ConfigureReferences(
            ScoundrelGameManager manager,
            CanvasGroup backdrop,
            RectTransform panel,
            TextMeshProUGUI headerTitle,
            TextMeshProUGUI threatBadge,
            Image threatBg,
            TextMeshProUGUI echoSubtitle,
            SpoilsLootCardButton[] cards,
            Material foilMat,
            Button merchantBtn = null,
            MarketplaceModalUI marketModal = null)
        {
            _gameManager = manager;
            backdropGroup = backdrop;
            modalPanel = panel;
            headerTitleText = headerTitle;
            threatBadgeText = threatBadge;
            threatBadgeBg = threatBg;
            echoSubtitleText = echoSubtitle;
            lootCards = cards;
            sharedFoilMaterial = foilMat;
            visitMerchantButton = merchantBtn;
            marketplaceModal = marketModal;
        }

        private void Awake()
        {
            if (visitMerchantButton != null)
            {
                visitMerchantButton.onClick.RemoveAllListeners();
                visitMerchantButton.onClick.AddListener(OnVisitMerchantClicked);
            }

            if (!_isOpen)
            {
                HideImmediate();
            }
        }

        private void OnVisitMerchantClicked()
        {
            if (marketplaceModal != null)
            {
                marketplaceModal.ShowModal();
            }
        }

        public void Bind(ScoundrelGameManager manager)
        {
            if (_gameManager != null)
            {
                _gameManager.OnRoomCleared -= HandleRoomCleared;
            }

            _gameManager = manager;
            if (_gameManager != null)
            {
                _gameManager.OnRoomCleared += HandleRoomCleared;
            }
        }

        private void OnDestroy()
        {
            if (_gameManager != null)
            {
                _gameManager.OnRoomCleared -= HandleRoomCleared;
            }
            if (backdropGroup != null) backdropGroup.DOKill();
            if (modalPanel != null) modalPanel.DOKill();
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

        private void HandleRoomCleared(int threatScore, SpoilsTier tier, float echoHealed)
        {
            // Slight beat so final combat/card discard juice lands cleanly before Spoils slides up
            DOVirtual.DelayedCall(0.32f, () =>
            {
                if (this == null) return;
                ShowSpoils(threatScore, tier, echoHealed);
            });
        }

        public void ShowSpoils(int threatScore, SpoilsTier tier, float echoHealed)
        {
            if (_isOpen) return;
            _isOpen = true;
            gameObject.SetActive(true);

            if (visitMerchantButton != null)
            {
                visitMerchantButton.onClick.RemoveAllListeners();
                visitMerchantButton.onClick.AddListener(OnVisitMerchantClicked);
            }

            ScoundrelResources.ApplyFont(headerTitleText);
            ScoundrelResources.ApplyFont(threatBadgeText);
            ScoundrelResources.ApplyFont(echoSubtitleText);

            Color tierAccent;
            string tierName;
            switch (tier)
            {
                case SpoilsTier.High:
                    tierName = "HIGH THREAT SPOILS";
                    tierAccent = new Color(0.98f, 0.76f, 0.22f, 1f); // Radiant Gold
                    break;
                case SpoilsTier.Medium:
                    tierName = "MEDIUM THREAT SPOILS";
                    tierAccent = new Color(0.38f, 0.78f, 0.98f, 1f); // Arcane Cyan
                    break;
                default:
                    tierName = "ROOM CLEARED — SPOILS";
                    tierAccent = new Color(0.62f, 0.86f, 0.56f, 1f); // Emerald
                    break;
            }

            if (headerTitleText != null)
            {
                headerTitleText.text = tierName;
                headerTitleText.color = tierAccent;
            }

            if (threatBadgeText != null)
            {
                threatBadgeText.text = $"ROOM THREAT SCORE (T): {threatScore}";
            }

            if (threatBadgeBg != null)
            {
                threatBadgeBg.color = new Color(tierAccent.r * 0.25f, tierAccent.g * 0.25f, tierAccent.b * 0.25f, 0.95f);
            }

            if (echoSubtitleText != null)
            {
                if (echoHealed > 0.001f)
                {
                    echoSubtitleText.text = $"+{echoHealed:0.0} HP Restored by Epic Potion Echo! Choose 1 Loot Reward:";
                    echoSubtitleText.color = new Color(0.55f, 0.98f, 0.65f, 1f);
                }
                else
                {
                    echoSubtitleText.text = "1 Carryover Card Survives to Next Room • Select Your Loot:";
                    echoSubtitleText.color = new Color(0.82f, 0.85f, 0.90f, 1f);
                }
            }

            ConfigureLootCardsForTier(tier);

            if (backdropGroup != null)
            {
                backdropGroup.DOKill();
                backdropGroup.alpha = 0f;
                backdropGroup.interactable = true;
                backdropGroup.blocksRaycasts = true;
                backdropGroup.DOFade(1f, 0.26f).SetEase(Ease.OutQuad);
            }

            if (modalPanel != null)
            {
                modalPanel.DOKill();
                modalPanel.anchoredPosition = new Vector2(0f, _hiddenY);
                modalPanel.localScale = new Vector3(0.95f, 0.95f, 1f);
                modalPanel.DOAnchorPosY(_restingY, 0.42f).SetEase(Ease.OutBack);
                modalPanel.DOScale(1f, 0.36f).SetEase(Ease.OutBack);
            }
        }

        private void ConfigureLootCardsForTier(SpoilsTier tier)
        {
            Sprite coinSingle = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.GoldCoinSingle);
            Sprite coinStack = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.GoldCoinStack);
            Sprite epicPotion = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.CardEpicPotion);
            Sprite legendarySkin = ScoundrelResources.LoadSprite(ScoundrelAssetPaths.SkinWeaponLegendary);

            if (tier == SpoilsTier.Low)
            {
                SetupLootSlot(0, true, coinSingle, "+15 SPL GOLD", "Scavenged Dungeon Coin Pouch\nAdded to Run Treasury", "COMMON LOOT",
                    new Color(0.95f, 0.78f, 0.28f, 1f), false, false);
                SetupLootSlot(1, false, null, "", "", "", Color.white, false, false);
                SetupLootSlot(2, false, null, "", "", "", Color.white, false, false);
            }
            else if (tier == SpoilsTier.Medium)
            {
                SetupLootSlot(0, true, coinStack, "+40 SPL GOLD", "Heavy Mercenary Coin Stack\nAdded to Run Treasury", "TREASURY",
                    new Color(0.95f, 0.78f, 0.28f, 1f), false, false);
                SetupLootSlot(1, true, epicPotion, "EPIC HEALTH POTION", "Consumable Card: Heals 10% Echo\nof 1st Heart Potion for 3 Rooms", "FOIL CONSUMABLE",
                    new Color(0.45f, 0.88f, 1.0f, 1f), true, false);
                SetupLootSlot(2, false, null, "", "", "", Color.white, false, false);
            }
            else
            {
                bool legendaryAllowed = _gameManager != null && _gameManager.EnableLegendaryWeapons;
                SetupLootSlot(0, true, coinStack, "+85 SPL GOLD", "Vault Sovereign SPL Hoard\nMassive Gold Injection", "HIGH BOUNTY",
                    new Color(0.98f, 0.80f, 0.25f, 1f), false, false);
                SetupLootSlot(1, true, epicPotion, "EPIC POTION + 25 GOLD", "+1 Epic Health Potion Consumable\nPlus +25 Bonus SPL Gold", "ALCHEMIST CACHE",
                    new Color(0.52f, 0.90f, 1.0f, 1f), true, false);
                if (legendaryAllowed)
                {
                    SetupLootSlot(2, true, legendarySkin, "LEGENDARY WEAPON SKIN", "Unlocks Forged Edge Charge (1x Ignore\nWeapon Ceiling) & Cleave Finisher VFX!", "MYTHIC ARTIFACT",
                        new Color(1.0f, 0.56f, 0.22f, 1f), true, true);
                }
                else
                {
                    SetupLootSlot(2, false, null, "", "", "", Color.white, false, false);
                }
            }
        }

        private void SetupLootSlot(
            int index,
            bool visible,
            Sprite icon,
            string title,
            string description,
            string tagLabel,
            Color accentColor,
            bool enableFoil,
            bool legendaryGold)
        {
            if (lootCards == null || index < 0 || index >= lootCards.Length || lootCards[index] == null)
                return;

            SpoilsLootCardButton slot = lootCards[index];
            slot.gameObject.SetActive(visible);
            if (!visible) return;

            slot.CardRect.localScale = Vector3.one;
            slot.CardRect.localRotation = Quaternion.identity;

            ScoundrelResources.ApplyFont(slot.TitleText);
            ScoundrelResources.ApplyFont(slot.DescriptionText);
            ScoundrelResources.ApplyFont(slot.TagText);

            if (slot.IconImage != null)
            {
                slot.IconImage.sprite = icon;
                slot.IconImage.preserveAspect = true;
            }
            if (slot.TitleText != null)
            {
                slot.TitleText.text = title;
                slot.TitleText.color = accentColor;
            }
            if (slot.DescriptionText != null)
            {
                slot.DescriptionText.text = description;
            }
            if (slot.TagText != null)
            {
                slot.TagText.text = tagLabel;
                slot.TagText.color = accentColor;
            }
            if (slot.BorderImage != null)
            {
                slot.BorderImage.color = new Color(accentColor.r, accentColor.g, accentColor.b, 0.08f);
            }
            if (slot.CardRect != null)
            {
                Outline cardOutline = slot.CardRect.GetComponent<Outline>();
                if (cardOutline != null)
                {
                    cardOutline.effectColor = new Color(accentColor.r, accentColor.g, accentColor.b, 0.88f);
                }
            }

            slot.SetFoil(sharedFoilMaterial, enableFoil, legendaryGold);

            if (slot.Button != null)
            {
                slot.Button.interactable = true;
                slot.Button.onClick.RemoveAllListeners();
                int capturedChoice = index;
                slot.Button.onClick.AddListener(() => OnLootCardChosen(capturedChoice, slot));
            }
        }

        private void OnLootCardChosen(int choiceIndex, SpoilsLootCardButton clickedSlot)
        {
            if (!_isOpen || _gameManager == null) return;
            _isOpen = false;

            if (lootCards != null)
            {
                foreach (var c in lootCards)
                {
                    if (c != null && c.Button != null) c.Button.interactable = false;
                }
            }

            if (clickedSlot != null && clickedSlot.CardRect != null)
            {
                clickedSlot.CardRect.DOPunchScale(new Vector3(0.12f, 0.12f, 0f), 0.22f, 8, 0.8f);
            }

            DOVirtual.DelayedCall(0.18f, () =>
            {
                if (this == null) return;
                CloseAndClaim(choiceIndex);
            });
        }

        private void CloseAndClaim(int choiceIndex)
        {
            if (backdropGroup != null)
            {
                backdropGroup.interactable = false;
                backdropGroup.blocksRaycasts = false;
                backdropGroup.DOFade(0f, 0.22f).SetEase(Ease.InQuad);
            }

            if (modalPanel != null)
            {
                modalPanel.DOAnchorPosY(_hiddenY, 0.26f).SetEase(Ease.InBack).OnComplete(() =>
                {
                    if (this != null)
                    {
                        gameObject.SetActive(false);
                        _gameManager?.TryClaimSpoils(choiceIndex);
                    }
                });
            }
            else
            {
                gameObject.SetActive(false);
                _gameManager?.TryClaimSpoils(choiceIndex);
            }
        }
    }
}
