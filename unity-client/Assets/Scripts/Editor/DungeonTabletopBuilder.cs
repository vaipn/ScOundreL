#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Scoundrel.EditorTools
{
    public static class DungeonTabletopBuilder
    {
        private const string TargetScenePath = "Assets/Scenes/DungeonTabletop.unity";
        private const string SourceFontAssetPath = "Assets/Fonts/RobotoSlab-Regular SDF.asset";
        private const string TargetFontResourcePath = "Assets/Resources/Fonts/RobotoSlab-Regular SDF.asset";
        private const string SourceBgmPath = "Assets/Resources/Audio/SFX/bgm_dungeon_loop.mp3";
        private const string TargetBgmResourcePath = "Assets/Resources/Audio/Music/bgm_dungeon_loop.mp3";
        private const string FoilMaterialAssetPath = "Assets/Resources/Materials/HolographicFoilCardMat.mat";

        [MenuItem("Scoundrel/Build DungeonTabletop Scene & Resources")]
        public static void BuildFromMenu()
        {
            string summary = BuildDungeonTabletopCommand();
            Debug.Log($"[DungeonTabletopBuilder] {summary}");
        }

        public static void BuildAndVerifyAll()
        {
            string buildSummary = BuildDungeonTabletopCommand();
            Debug.Log($"[DungeonTabletopBuilder] {buildSummary}");
            string verifySummary = VerifyVrfMarketplaceCommand();
            Debug.Log($"[DungeonTabletopBuilder] {verifySummary}");
        }

        [CliCommand("scoundrel", "verify_vrf_marketplace", "Verify MagicBlock VRF deck shuffle/combat rolls, Borsh DungeonRun deserialization, and Mini Marketplace UI")]
        public static string VerifyVrfMarketplaceCommand()
        {
            ScoundrelGameManager gm = UnityEngine.Object.FindFirstObjectByType<ScoundrelGameManager>();
            ScoundrelTabletopUIController ui = UnityEngine.Object.FindFirstObjectByType<ScoundrelTabletopUIController>();
            MarketplaceModalUI market = UnityEngine.Object.FindFirstObjectByType<MarketplaceModalUI>(FindObjectsInactive.Include);
            SpoilsModalUI spoils = UnityEngine.Object.FindFirstObjectByType<SpoilsModalUI>(FindObjectsInactive.Include);
            WalletConnectModalUI walletModal = UnityEngine.Object.FindFirstObjectByType<WalletConnectModalUI>(FindObjectsInactive.Include);

            if (gm == null || ui == null || market == null || spoils == null || walletModal == null)
            {
                throw new Exception("Missing ScoundrelGameManager, ScoundrelTabletopUIController, MarketplaceModalUI, SpoilsModalUI, or WalletConnectModalUI in scene.");
            }

            // 1. Start deterministic VRF run and verify 44 unique cards
            gm.StartNewRun(123456789UL, false);
            HashSet<byte> unique = new HashSet<byte>(gm.State.Deck);
            if (unique.Count != 44 || gm.State.VrfSeed == null || gm.State.VrfSeed.Length != 32)
            {
                throw new Exception($"Invalid VRF deck or seed length ({unique.Count}, {gm.State.VrfSeed?.Length ?? 0}).");
            }

            // 2. Grant 150 Gold and test buying Ofe Charm (60G) and Rusty Shield (45G)
            gm.State.UnclaimedGold = 150;
            bool boughtOfe = gm.BuyItem((int)MarketplaceItemId.OfeCharm);
            bool boughtShield = gm.BuyItem((int)MarketplaceItemId.RustyShield);
            if (!boughtOfe || !boughtShield || gm.State.UnclaimedGold != 45 || gm.State.OfeInventory != 1 || gm.State.RustyShieldInventory != 1)
            {
                throw new Exception($"Marketplace purchase mismatch: Gold={gm.State.UnclaimedGold}, Ofe={gm.State.OfeInventory}, Shield={gm.State.RustyShieldInventory}");
            }

            // 3. Activate both Ofe and Rusty Shield (3 rooms each)
            bool usedOfe = gm.UseMarketplaceItem((int)MarketplaceItemId.OfeCharm);
            bool usedShield = gm.UseMarketplaceItem((int)MarketplaceItemId.RustyShield);
            if (!usedOfe || !usedShield || gm.State.OfeBuffRoomsLeft != 3 || gm.State.RustyShieldBuffRoomsLeft != 3)
            {
                throw new Exception($"Marketplace activation mismatch: OfeBuff={gm.State.OfeBuffRoomsLeft}, ShieldBuff={gm.State.RustyShieldBuffRoomsLeft}");
            }

            // 4. Test SPL Gold Minting (claim_gold_tokens) for the remaining 45 UnclaimedGold
            bool minted = gm.TryMintGoldTokens();
            if (!minted || gm.State.UnclaimedGold != 0 || gm.Bridge.LastKnownMintedGoldBalance < 45)
            {
                throw new Exception($"SPL Gold minting mismatch: minted={minted}, remaining={gm.State.UnclaimedGold}, ataBalance={gm.Bridge.LastKnownMintedGoldBalance}");
            }

            // 5. Open & close MarketplaceModalUI and WalletConnectModalUI cleanly
            market.ShowModal();
            market.HideImmediate();
            walletModal.ShowModal();
            walletModal.HideImmediate();

            // 6. Reset to clean fresh run for Play Mode
            gm.StartNewRun(null, false);
            ui.RefreshHUD(gm.State);

            return "Verified VRF 32-byte deck shuffle, Ofe (60G) & Rusty Shield (45G) purchases/activations, SPL Gold Minting (claim_gold_tokens), MarketplaceModalUI, and WalletConnectModalUI.";
        }

        [CliCommand("scoundrel", "build_scene", "Configure Resources manifest, Bilinear FullRect sprite pipeline, and build Assets/Scenes/DungeonTabletop.unity")]
        public static string BuildDungeonTabletopCommand()
        {
            int reimportedSprites = PrepareResourcesAndImportSettings();
            Material foilMat = CreateOrUpdateHolographicMaterial();
            TMP_FontAsset fontAsset = LoadRobotoSlabFont();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "DungeonTabletop";

            // 1. Main Camera (Orthographic URP 2D + AudioListener)
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            Camera mainCam = camGo.AddComponent<Camera>();
            mainCam.clearFlags = CameraClearFlags.SolidColor;
            mainCam.backgroundColor = new Color(0.045f, 0.062f, 0.095f, 1f);
            mainCam.orthographic = true;
            mainCam.orthographicSize = 5.4f;
            mainCam.nearClipPlane = 0.1f;
            mainCam.farClipPlane = 100f;
            camGo.transform.position = new Vector3(0f, 0f, -10f);
            camGo.AddComponent<AudioListener>();
            UniversalAdditionalCameraData urpCamData = camGo.AddComponent<UniversalAdditionalCameraData>();
            urpCamData.renderPostProcessing = false;

            // 2. EventSystem (InputSystem compatible)
            GameObject eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
            Type inputSysModuleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSysModuleType != null)
            {
                eventSystemGo.AddComponent(inputSysModuleType);
            }
            else
            {
                eventSystemGo.AddComponent<StandaloneInputModule>();
            }

            // 3. Core Scoundrel Systems + Audio Sources
            GameObject systemsGo = new GameObject("ScoundrelSystems");
            SolanaScoundrelBridge bridge = systemsGo.AddComponent<SolanaScoundrelBridge>();
            ScoundrelGameManager gameManager = systemsGo.AddComponent<ScoundrelGameManager>();
            gameManager.AssignBridge(bridge);
            WeaponSkinFXController fxController = systemsGo.AddComponent<WeaponSkinFXController>();

            AudioSource bgmSource = CreateAudioSourceChild(systemsGo.transform, "BGM_AudioSource", true, 0.35f);
            AudioSource sfxSource = CreateAudioSourceChild(systemsGo.transform, "SFX_AudioSource", false, 0.90f);
            AudioSource stingerSource = CreateAudioSourceChild(systemsGo.transform, "Stinger_AudioSource", false, 0.95f);

            // 4. 1920x1080 Screen-Space Camera Canvas
            GameObject canvasGo = new GameObject("DungeonTabletopCanvas", typeof(RectTransform));
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = mainCam;
            canvas.planeDistance = 10f;
            canvas.pixelPerfect = false;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();
            Sprite uiWhiteSprite = GetSmoothWhiteSprite();

            // Root Shake Container
            RectTransform shakeRoot = CreateFullStretchRect("TabletopShakeRoot", canvasGo.transform);

            // Tabletop Felt & Inner Frame
            Image outerBg = CreatePanel("OuterDungeonBackdrop", shakeRoot, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                new Color(0.055f, 0.078f, 0.118f, 1f), uiWhiteSprite);
            outerBg.raycastTarget = false;

            RectTransform feltBoard = CreateAnchoredRect("DungeonFeltBoard", shakeRoot,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -38f), new Vector2(1856f, 948f));
            Image feltImg = feltBoard.gameObject.AddComponent<Image>();
            feltImg.sprite = uiWhiteSprite;
            feltImg.color = new Color(0.082f, 0.125f, 0.176f, 0.96f);
            feltImg.raycastTarget = false;
            AddOutline(feltBoard.gameObject, new Color(0.24f, 0.34f, 0.46f, 0.55f), 2f);

            // =====================================================================
            // A. TOP HUD BAR (1920 x 92)
            // =====================================================================
            RectTransform topHud = CreateAnchoredRect("TopHUDBar", shakeRoot,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -46f), new Vector2(0f, 92f));
            Image topHudBg = topHud.gameObject.AddComponent<Image>();
            topHudBg.sprite = uiWhiteSprite;
            topHudBg.color = new Color(0.042f, 0.060f, 0.092f, 0.98f);
            AddOutline(topHud.gameObject, new Color(0.32f, 0.42f, 0.56f, 0.45f), 2f);

            // 1. Player HP Bar ("20.0 / 20 HP")
            RectTransform hpContainer = CreateAnchoredRect("PlayerHPContainer", topHud,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(215f, 0f), new Vector2(390f, 56f));
            Image hpFrame = hpContainer.gameObject.AddComponent<Image>();
            hpFrame.sprite = uiWhiteSprite;
            hpFrame.color = new Color(0.11f, 0.14f, 0.20f, 1f);
            AddOutline(hpContainer.gameObject, new Color(0.48f, 0.20f, 0.24f, 0.85f), 2f);

            RectTransform hpFillBg = CreateAnchoredRect("HPBarTrack", hpContainer,
                new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, new Vector2(-12f, -12f));
            Image hpTrackImg = hpFillBg.gameObject.AddComponent<Image>();
            hpTrackImg.sprite = uiWhiteSprite;
            hpTrackImg.color = new Color(0.06f, 0.07f, 0.10f, 1f);

            RectTransform hpFillRect = CreateAnchoredRect("HPBarFill", hpFillBg,
                new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            Image hpBarFill = hpFillRect.gameObject.AddComponent<Image>();
            hpBarFill.sprite = uiWhiteSprite;
            hpBarFill.type = Image.Type.Filled;
            hpBarFill.fillMethod = Image.FillMethod.Horizontal;
            hpBarFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            hpBarFill.fillAmount = 1f;
            hpBarFill.color = new Color(0.84f, 0.20f, 0.25f, 1f);

            TextMeshProUGUI hpValueText = CreateTMP("HPValueText", hpContainer, fontAsset,
                "20.0 / 20 HP", 24f, FontStyles.Bold, TextAlignmentOptions.Center, Color.white);
            StretchFull(hpValueText.rectTransform);

            // 2. Active Consumable Buffs Badge (Epic Potion Echo / Ofe Dodge / Rusty Shield)
            RectTransform echoBadgeRect = CreateAnchoredRect("EpicPotionEchoBadge", topHud,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(605f, 0f), new Vector2(360f, 54f));
            Image echoBadgeBg = echoBadgeRect.gameObject.AddComponent<Image>();
            echoBadgeBg.sprite = uiWhiteSprite;
            echoBadgeBg.color = new Color(0.10f, 0.13f, 0.18f, 0.85f);
            AddOutline(echoBadgeRect.gameObject, new Color(0.85f, 0.68f, 0.24f, 0.55f), 1.5f);

            TextMeshProUGUI echoBadgeText = CreateTMP("EpicEchoBadgeText", echoBadgeRect, fontAsset,
                "Active Buffs: None", 16.5f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(0.62f, 0.68f, 0.76f, 0.9f));
            StretchFull(echoBadgeText.rectTransform, 10f, 4f);

            // 3. Accumulated SPL Gold + Session Signer Status
            RectTransform goldBadgeRect = CreateAnchoredRect("GoldCounterPanel", topHud,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(985f, 0f), new Vector2(370f, 56f));
            Image goldBg = goldBadgeRect.gameObject.AddComponent<Image>();
            goldBg.sprite = uiWhiteSprite;
            goldBg.color = new Color(0.11f, 0.14f, 0.20f, 0.95f);
            AddOutline(goldBadgeRect.gameObject, new Color(0.92f, 0.72f, 0.22f, 0.65f), 1.5f);

            RectTransform goldIconRect = CreateAnchoredRect("GoldCoinIcon", goldBadgeRect,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(34f, 0f), new Vector2(40f, 40f));
            Image goldIconImg = goldIconRect.gameObject.AddComponent<Image>();
            goldIconImg.sprite = LoadSpriteAtAssetPath("Assets/Resources/Sprites/LootAndSkins/gold_coin_stack.png") ?? uiWhiteSprite;
            goldIconImg.preserveAspect = true;

            TextMeshProUGUI goldValueText = CreateTMP("GoldValueText", goldBadgeRect, fontAsset,
                "SPL Gold: 0", 21f, FontStyles.Bold, TextAlignmentOptions.Left,
                new Color(0.98f, 0.82f, 0.28f, 1f));
            goldValueText.rectTransform.anchorMin = new Vector2(0f, 0.36f);
            goldValueText.rectTransform.anchorMax = new Vector2(1f, 1f);
            goldValueText.rectTransform.offsetMin = new Vector2(64f, 0f);
            goldValueText.rectTransform.offsetMax = new Vector2(-12f, -4f);

            TextMeshProUGUI sessionStatusText = CreateTMP("SessionKeyStatusText", goldBadgeRect, fontAsset,
                "MagicBlock VRF Seeded • Local Sim", 12f, FontStyles.Normal, TextAlignmentOptions.Left,
                new Color(0.52f, 0.86f, 0.76f, 0.92f));
            sessionStatusText.rectTransform.anchorMin = new Vector2(0f, 0f);
            sessionStatusText.rectTransform.anchorMax = new Vector2(1f, 0.42f);
            sessionStatusText.rectTransform.offsetMin = new Vector2(64f, 4f);
            sessionStatusText.rectTransform.offsetMax = new Vector2(-12f, 0f);

            // 4. Wallet & Devnet Modal Button (Opens Live Wallet Adapter & SPL Gold Mint Modal)
            Button openWalletBtn = CreateStyledButton("WalletConnectButton", topHud, fontAsset, uiWhiteSprite,
                new Vector2(0f, 0.5f), new Vector2(1280f, 0f), new Vector2(192f, 56f),
                "WALLET / DEVNET", 16.5f,
                new Color(0.14f, 0.52f, 0.38f, 1f),
                new Color(0.20f, 0.68f, 0.50f, 1f),
                new Color(0.16f, 0.20f, 0.18f, 0.8f),
                out _);

            // 5. Merchant Button (Opens Mini Marketplace Modal)
            Button openMarketBtn = CreateStyledButton("MarketButton", topHud, fontAsset, uiWhiteSprite,
                new Vector2(0f, 0.5f), new Vector2(1490f, 0f), new Vector2(196f, 56f),
                "MERCHANT", 18.5f,
                new Color(0.76f, 0.52f, 0.14f, 1f),
                new Color(0.92f, 0.66f, 0.20f, 1f),
                new Color(0.18f, 0.18f, 0.22f, 0.8f),
                out _);

            // 6. Avoid Room Button (Right side of Top HUD)
            Button avoidRoomBtn = CreateStyledButton("AvoidRoomButton", topHud, fontAsset, uiWhiteSprite,
                new Vector2(1f, 0.5f), new Vector2(-150f, 0f), new Vector2(248f, 56f),
                "AVOID ROOM", 19f,
                new Color(0.18f, 0.42f, 0.56f, 1f),
                new Color(0.24f, 0.54f, 0.70f, 1f),
                new Color(0.14f, 0.17f, 0.22f, 0.8f),
                out TextMeshProUGUI avoidRoomBtnText);

            // =====================================================================
            // B. CENTER TABLETOP (DungeonDeck, 4 RoomSlots, DiscardPile, 3-Slot Consumable Rack)
            // =====================================================================
            RectTransform centerTabletop = CreateAnchoredRect("CenterTabletop", shakeRoot,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 92f), new Vector2(1820f, 390f));

            // 1. Left: DungeonDeck Pile + Remaining Count Badge
            RectTransform dungeonZone = CreateAnchoredRect("DungeonDeckZone", centerTabletop,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-786f, 0f), new Vector2(202f, 336f));
            CreatePileFrame(dungeonZone, fontAsset, uiWhiteSprite, "DUNGEON DECK", new Color(0.75f, 0.84f, 0.96f, 0.9f));

            RectTransform dungeonDeckAnchor = CreateAnchoredRect("DungeonDeck", dungeonZone,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -12f), new Vector2(182f, 256f));
            Image dungeonBackImg = dungeonDeckAnchor.gameObject.AddComponent<Image>();
            dungeonBackImg.sprite = LoadSpriteAtAssetPath("Assets/Resources/Cards/cardBack_dungeon.png") ?? uiWhiteSprite;
            dungeonBackImg.preserveAspect = true;

            TextMeshProUGUI dungeonCountText = CreateCountBadge("DungeonCountBadge", dungeonDeckAnchor, fontAsset, uiWhiteSprite,
                "40", new Color(0.14f, 0.30f, 0.54f, 0.96f));

            // 2. Center: 4 Horizontal RoomSlots
            RectTransform roomRow = CreateAnchoredRect("RoomSlotsContainer", centerTabletop,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-196f, 0f), new Vector2(900f, 336f));

            TextMeshProUGUI roomHeader = CreateTMP("RoomHeaderLabel", roomRow, fontAsset,
                "ACTIVE DUNGEON ROOM  (RESOLVE 3 OF 4 CARDS)", 17f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(0.78f, 0.84f, 0.92f, 0.85f));
            roomHeader.rectTransform.anchorMin = new Vector2(0f, 1f);
            roomHeader.rectTransform.anchorMax = new Vector2(1f, 1f);
            roomHeader.rectTransform.anchoredPosition = new Vector2(0f, -18f);
            roomHeader.rectTransform.sizeDelta = new Vector2(0f, 28f);

            RectTransform[] roomSlotAnchors = new RectTransform[4];
            float[] slotX = { -324f, -108f, 108f, 324f };
            string[] numerals = { "I", "II", "III", "IV" };
            for (int i = 0; i < 4; i++)
            {
                RectTransform slotWell = CreateAnchoredRect($"RoomSlotWell_{i}", roomRow,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(slotX[i], -12f), new Vector2(194f, 272f));
                Image wellImg = slotWell.gameObject.AddComponent<Image>();
                wellImg.sprite = uiWhiteSprite;
                wellImg.color = new Color(0.055f, 0.085f, 0.125f, 0.78f);
                AddOutline(slotWell.gameObject, new Color(0.28f, 0.40f, 0.54f, 0.42f), 1.5f);

                TextMeshProUGUI slotNum = CreateTMP($"SlotNumeral_{i}", slotWell, fontAsset,
                    numerals[i], 28f, FontStyles.Bold, TextAlignmentOptions.Center,
                    new Color(0.24f, 0.34f, 0.46f, 0.35f));
                StretchFull(slotNum.rectTransform);
                slotNum.raycastTarget = false;

                RectTransform anchor = CreateAnchoredRect($"RoomSlot_{i}", slotWell,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(182f, 256f));
                roomSlotAnchors[i] = anchor;
            }

            // 3. Right-Inner: DiscardPile
            RectTransform discardZone = CreateAnchoredRect("DiscardPileZone", centerTabletop,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(394f, 0f), new Vector2(198f, 336f));
            CreatePileFrame(discardZone, fontAsset, uiWhiteSprite, "DISCARD PILE", new Color(0.68f, 0.72f, 0.80f, 0.85f));

            RectTransform discardPileAnchor = CreateAnchoredRect("DiscardPile", discardZone,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -12f), new Vector2(182f, 256f));
            Image discardBackImg = discardPileAnchor.gameObject.AddComponent<Image>();
            discardBackImg.sprite = LoadSpriteAtAssetPath("Assets/Resources/Cards/cardBack_discard.png") ?? uiWhiteSprite;
            discardBackImg.preserveAspect = true;
            discardBackImg.color = new Color(1f, 1f, 1f, 0.25f);

            TextMeshProUGUI discardCountText = CreateCountBadge("DiscardCountBadge", discardPileAnchor, fontAsset, uiWhiteSprite,
                "0", new Color(0.26f, 0.28f, 0.36f, 0.95f));

            // 4. Right-Outer: 3-Slot Balatro-Style Consumable Rack (Epic Potion, Ofe Charm, Rusty Shield)
            RectTransform consumableZone = CreateAnchoredRect("BalatroConsumableSlot", centerTabletop,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(706f, 0f), new Vector2(362f, 348f));
            Image consBg = consumableZone.gameObject.AddComponent<Image>();
            consBg.sprite = uiWhiteSprite;
            consBg.color = new Color(0.09f, 0.12f, 0.19f, 0.92f);
            AddOutline(consumableZone.gameObject, new Color(0.92f, 0.74f, 0.28f, 0.60f), 2f);

            TextMeshProUGUI consHeader = CreateTMP("ConsumableHeader", consumableZone, fontAsset,
                "CONSUMABLE RACK (3-ROOM BUFFS)", 14.5f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(0.98f, 0.84f, 0.38f, 0.95f));
            consHeader.rectTransform.anchorMin = new Vector2(0f, 1f);
            consHeader.rectTransform.anchorMax = new Vector2(1f, 1f);
            consHeader.rectTransform.anchoredPosition = new Vector2(0f, -17f);
            consHeader.rectTransform.sizeDelta = new Vector2(0f, 24f);

            BuildConsumableSubSlot(
                "EpicPotionSubSlot",
                consumableZone,
                fontAsset,
                uiWhiteSprite,
                -114f,
                "ELIXIR",
                new Color(1f, 0.84f, 0.28f, 0.24f),
                new Color(0.55f, 0.38f, 0.08f, 0.96f),
                new Color(0.82f, 0.58f, 0.12f, 1f),
                new Color(0.96f, 0.72f, 0.18f, 1f),
                "x1",
                "USE",
                out RectTransform consumableCardHolder,
                out Image consumableGoldenAura,
                out TextMeshProUGUI consumableCountBadgeText,
                out Button useEpicPotionBtn,
                out TextMeshProUGUI useEpicPotionBtnText);

            BuildConsumableSubSlot(
                "OfeCharmSubSlot",
                consumableZone,
                fontAsset,
                uiWhiteSprite,
                0f,
                "OFE (50%)",
                new Color(0.28f, 0.94f, 1f, 0.24f),
                new Color(0.10f, 0.44f, 0.56f, 0.96f),
                new Color(0.14f, 0.56f, 0.72f, 1f),
                new Color(0.20f, 0.70f, 0.88f, 1f),
                "x0",
                "EMPTY",
                out RectTransform ofeCardHolder,
                out Image ofeCyanAura,
                out TextMeshProUGUI ofeCountBadgeText,
                out Button useOfeBtn,
                out TextMeshProUGUI useOfeBtnText);

            BuildConsumableSubSlot(
                "RustyShieldSubSlot",
                consumableZone,
                fontAsset,
                uiWhiteSprite,
                114f,
                "SHIELD",
                new Color(1f, 0.78f, 0.28f, 0.24f),
                new Color(0.46f, 0.34f, 0.16f, 0.96f),
                new Color(0.68f, 0.48f, 0.18f, 1f),
                new Color(0.84f, 0.60f, 0.24f, 1f),
                "x0",
                "EMPTY",
                out RectTransform rustyShieldCardHolder,
                out Image rustyShieldAmberAura,
                out TextMeshProUGUI rustyShieldCountBadgeText,
                out Button useRustyShieldBtn,
                out TextMeshProUGUI useRustyShieldBtnText);

            // =====================================================================
            // C. BOTTOM PLAY AREA (EquippedWeaponSlot + Staggered Stack + Shield Badge + Combat Prompt)
            // =====================================================================
            RectTransform bottomArea = CreateAnchoredRect("BottomPlayArea", shakeRoot,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 174f), new Vector2(1820f, 316f));
            Image bottomPanelImg = bottomArea.gameObject.AddComponent<Image>();
            bottomPanelImg.sprite = uiWhiteSprite;
            bottomPanelImg.color = new Color(0.058f, 0.086f, 0.130f, 0.92f);
            AddOutline(bottomArea.gameObject, new Color(0.24f, 0.35f, 0.48f, 0.45f), 1.5f);

            // Left/Center-Left: EquippedWeaponSlot + Slain Monster Cascade Stack
            TextMeshProUGUI weaponZoneTitle = CreateTMP("EquippedWeaponHeader", bottomArea, fontAsset,
                "EQUIPPED BINDING WEAPON  &  SLAIN MONSTER STACK", 16f, FontStyles.Bold, TextAlignmentOptions.Left,
                new Color(0.75f, 0.84f, 0.95f, 0.88f));
            weaponZoneTitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            weaponZoneTitle.rectTransform.anchorMax = new Vector2(0f, 1f);
            weaponZoneTitle.rectTransform.pivot = new Vector2(0f, 1f);
            weaponZoneTitle.rectTransform.anchoredPosition = new Vector2(36f, -12f);
            weaponZoneTitle.rectTransform.sizeDelta = new Vector2(620f, 26f);

            RectTransform weaponWell = CreateAnchoredRect("EquippedWeaponSlot", bottomArea,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(132f, -12f), new Vector2(176f, 244f));
            Image weaponWellImg = weaponWell.gameObject.AddComponent<Image>();
            weaponWellImg.sprite = uiWhiteSprite;
            weaponWellImg.color = new Color(0.042f, 0.062f, 0.095f, 0.85f);
            AddOutline(weaponWell.gameObject, new Color(0.35f, 0.52f, 0.72f, 0.50f), 1.5f);

            GameObject weaponPlaceholderGo = new GameObject("WeaponEmptyPlaceholder", typeof(RectTransform));
            weaponPlaceholderGo.transform.SetParent(weaponWell, false);
            StretchFull(weaponPlaceholderGo.GetComponent<RectTransform>(), 12f, 12f);
            TextMeshProUGUI placeholderTxt = CreateTMP("PlaceholderLabel", weaponPlaceholderGo.transform, fontAsset,
                "BAREHANDED\n\nPick Diamond\nto Equip", 17f, FontStyles.Normal, TextAlignmentOptions.Center,
                new Color(0.45f, 0.55f, 0.68f, 0.65f));
            StretchFull(placeholderTxt.rectTransform);

            RectTransform equippedWeaponAnchor = CreateAnchoredRect("EquippedWeaponCardAnchor", weaponWell,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(168f, 236f));

            RectTransform weaponMonsterStackContainer = CreateAnchoredRect("WeaponMonsterStackContainer", weaponWell,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(168f, 236f));

            // Shield Ceiling Badge ("Can Slay <= X") + Live Threat Readout
            RectTransform ceilingBadgePanel = CreateAnchoredRect("WeaponCeilingBadgePanel", bottomArea,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(760f, 38f), new Vector2(450f, 68f));
            Image ceilingBg = ceilingBadgePanel.gameObject.AddComponent<Image>();
            ceilingBg.sprite = uiWhiteSprite;
            ceilingBg.color = new Color(0.09f, 0.13f, 0.20f, 0.96f);
            AddOutline(ceilingBadgePanel.gameObject, new Color(0.40f, 0.62f, 0.86f, 0.60f), 1.5f);

            RectTransform shieldRect = CreateAnchoredRect("ShieldBadgeIcon", ceilingBadgePanel,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(38f, 0f), new Vector2(48f, 48f));
            Image shieldBadgeImage = shieldRect.gameObject.AddComponent<Image>();
            shieldBadgeImage.sprite = LoadSpriteAtAssetPath("Assets/Resources/Sprites/VFX/ui_shield_badge.png") ?? uiWhiteSprite;
            shieldBadgeImage.preserveAspect = true;

            TextMeshProUGUI weaponCeilingBadgeText = CreateTMP("WeaponCeilingBadgeText", ceilingBadgePanel, fontAsset,
                "Barehanded (No Weapon)", 20f, FontStyles.Bold, TextAlignmentOptions.Left,
                new Color(0.82f, 0.90f, 1.0f, 1f));
            weaponCeilingBadgeText.rectTransform.anchorMin = new Vector2(0f, 0f);
            weaponCeilingBadgeText.rectTransform.anchorMax = new Vector2(1f, 1f);
            weaponCeilingBadgeText.rectTransform.offsetMin = new Vector2(74f, 6f);
            weaponCeilingBadgeText.rectTransform.offsetMax = new Vector2(-14f, -6f);

            RectTransform threatInfoPanel = CreateAnchoredRect("LiveRoomThreatPanel", bottomArea,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(760f, -44f), new Vector2(450f, 52f));
            Image threatInfoBg = threatInfoPanel.gameObject.AddComponent<Image>();
            threatInfoBg.sprite = uiWhiteSprite;
            threatInfoBg.color = new Color(0.075f, 0.105f, 0.160f, 0.90f);

            TextMeshProUGUI roomThreatLiveText = CreateTMP("RoomThreatLiveText", threatInfoPanel, fontAsset,
                "Room Threat T: 0 (Low)  •  Resolved: 0/3", 18f, FontStyles.Normal, TextAlignmentOptions.Center,
                new Color(0.76f, 0.84f, 0.94f, 0.92f));
            StretchFull(roomThreatLiveText.rectTransform, 12f, 4f);

            // Right of Bottom Area: Monster Combat Choice Prompt ("Use Weapon" vs "Fight Barehanded")
            RectTransform combatChoiceRect = CreateAnchoredRect("MonsterCombatChoicePrompt", bottomArea,
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-396f, 0f), new Vector2(736f, 196f));
            Image choiceBg = combatChoiceRect.gameObject.AddComponent<Image>();
            choiceBg.sprite = uiWhiteSprite;
            choiceBg.color = new Color(0.11f, 0.15f, 0.23f, 0.98f);
            AddOutline(combatChoiceRect.gameObject, new Color(0.95f, 0.72f, 0.26f, 0.85f), 2.5f);

            TextMeshProUGUI combatPromptTitleText = CreateTMP("CombatPromptTitle", combatChoiceRect, fontAsset,
                "ENGAGE MONSTER", 22f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(0.98f, 0.88f, 0.56f, 1f));
            combatPromptTitleText.rectTransform.anchorMin = new Vector2(0f, 1f);
            combatPromptTitleText.rectTransform.anchorMax = new Vector2(1f, 1f);
            combatPromptTitleText.rectTransform.anchoredPosition = new Vector2(-24f, -34f);
            combatPromptTitleText.rectTransform.sizeDelta = new Vector2(-80f, 42f);

            Button cancelCombatBtn = CreateStyledButton("CancelCombatButton", combatChoiceRect, fontAsset, uiWhiteSprite,
                new Vector2(1f, 1f), new Vector2(-34f, -34f), new Vector2(44f, 44f),
                "X", 20f,
                new Color(0.24f, 0.28f, 0.36f, 1f),
                new Color(0.36f, 0.40f, 0.50f, 1f),
                new Color(0.16f, 0.18f, 0.22f, 0.6f),
                out _);

            Button useWeaponBtn = CreateStyledButton("UseWeaponButton", combatChoiceRect, fontAsset, uiWhiteSprite,
                new Vector2(0.5f, 0f), new Vector2(-172f, 58f), new Vector2(326f, 68f),
                "USE WEAPON", 18f,
                new Color(0.14f, 0.42f, 0.74f, 1f),
                new Color(0.20f, 0.54f, 0.90f, 1f),
                new Color(0.16f, 0.19f, 0.25f, 0.85f),
                out TextMeshProUGUI useWeaponBtnText);

            Button fightBarehandedBtn = CreateStyledButton("FightBarehandedButton", combatChoiceRect, fontAsset, uiWhiteSprite,
                new Vector2(0.5f, 0f), new Vector2(178f, 58f), new Vector2(320f, 68f),
                "FIGHT BAREHANDED", 18f,
                new Color(0.68f, 0.18f, 0.24f, 1f),
                new Color(0.84f, 0.24f, 0.30f, 1f),
                new Color(0.25f, 0.15f, 0.18f, 0.8f),
                out TextMeshProUGUI fightBarehandedBtnText);

            // =====================================================================
            // D. ANIMATION, SCREEN-DIM & VFX LAYERS
            // =====================================================================
            RectTransform animationLayer = CreateFullStretchRect("CardAnimationLayer", canvasGo.transform);

            RectTransform dimRect = CreateFullStretchRect("LegendaryScreenDimOverlay", canvasGo.transform);
            Image dimImg = dimRect.gameObject.AddComponent<Image>();
            dimImg.sprite = uiWhiteSprite;
            dimImg.color = new Color(0.02f, 0.03f, 0.06f, 1f);
            dimImg.raycastTarget = false;
            CanvasGroup screenDimGroup = dimRect.gameObject.AddComponent<CanvasGroup>();
            screenDimGroup.alpha = 0f;
            screenDimGroup.interactable = false;
            screenDimGroup.blocksRaycasts = false;

            RectTransform vfxContainer = CreateFullStretchRect("CombatVFXContainer", canvasGo.transform);

            // =====================================================================
            // E. SLAY THE SPIRE SPOILS MODAL OVERLAY
            // =====================================================================
            RectTransform spoilsRoot = CreateFullStretchRect("SpoilsModal", canvasGo.transform);
            Image spoilsBackdropImg = spoilsRoot.gameObject.AddComponent<Image>();
            spoilsBackdropImg.sprite = uiWhiteSprite;
            spoilsBackdropImg.color = new Color(0.02f, 0.04f, 0.08f, 0.86f);
            CanvasGroup spoilsBackdropGroup = spoilsRoot.gameObject.AddComponent<CanvasGroup>();

            RectTransform spoilsModalPanel = CreateAnchoredRect("SpoilsModalPanel", spoilsRoot,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1320f, 700f));
            Image spoilsPanelImg = spoilsModalPanel.gameObject.AddComponent<Image>();
            spoilsPanelImg.sprite = uiWhiteSprite;
            spoilsPanelImg.color = new Color(0.075f, 0.105f, 0.162f, 0.99f);
            AddOutline(spoilsModalPanel.gameObject, new Color(0.95f, 0.76f, 0.28f, 0.85f), 3f);

            RectTransform threatPill = CreateAnchoredRect("SpoilsThreatBadge", spoilsModalPanel,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -44f), new Vector2(420f, 44f));
            Image threatBadgeBg = threatPill.gameObject.AddComponent<Image>();
            threatBadgeBg.sprite = uiWhiteSprite;
            threatBadgeBg.color = new Color(0.20f, 0.16f, 0.06f, 0.96f);
            AddOutline(threatPill.gameObject, new Color(0.95f, 0.76f, 0.28f, 0.7f), 1.5f);

            TextMeshProUGUI threatBadgeText = CreateTMP("SpoilsThreatBadgeText", threatPill, fontAsset,
                "ROOM THREAT SCORE (T): 15", 20f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(1f, 0.92f, 0.68f, 1f));
            StretchFull(threatBadgeText.rectTransform);

            TextMeshProUGUI spoilsHeaderTitle = CreateTMP("SpoilsHeaderTitle", spoilsModalPanel, fontAsset,
                "MEDIUM THREAT SPOILS", 42f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(0.98f, 0.80f, 0.26f, 1f));
            spoilsHeaderTitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            spoilsHeaderTitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            spoilsHeaderTitle.rectTransform.anchoredPosition = new Vector2(0f, -102f);
            spoilsHeaderTitle.rectTransform.sizeDelta = new Vector2(-80f, 54f);

            TextMeshProUGUI spoilsEchoSubtitle = CreateTMP("SpoilsEchoSubtitle", spoilsModalPanel, fontAsset,
                "1 Carryover Card Survives to Next Room • Select Your Loot:", 21f, FontStyles.Normal, TextAlignmentOptions.Center,
                new Color(0.82f, 0.86f, 0.92f, 1f));
            spoilsEchoSubtitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            spoilsEchoSubtitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            spoilsEchoSubtitle.rectTransform.anchoredPosition = new Vector2(0f, -150f);
            spoilsEchoSubtitle.rectTransform.sizeDelta = new Vector2(-80f, 36f);

            RectTransform lootCardsRow = CreateAnchoredRect("LootCardsRow", spoilsModalPanel,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -68f), new Vector2(1200f, 440f));
            HorizontalLayoutGroup hlg = lootCardsRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.spacing = 42f;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            SpoilsLootCardButton[] lootCardButtons = new SpoilsLootCardButton[3];
            for (int i = 0; i < 3; i++)
            {
                lootCardButtons[i] = CreateSpoilsLootCard($"SpoilsLootCard_{i}", lootCardsRow, fontAsset, uiWhiteSprite);
            }

            Button visitMerchantBtn = CreateStyledButton("VisitMerchantButton", spoilsModalPanel, fontAsset, uiWhiteSprite,
                new Vector2(1f, 1f), new Vector2(-156f, -44f), new Vector2(248f, 44f),
                "VISIT MERCHANT", 17f,
                new Color(0.76f, 0.52f, 0.14f, 1f),
                new Color(0.92f, 0.66f, 0.20f, 1f),
                new Color(0.20f, 0.20f, 0.24f, 0.8f),
                out _);

            // =====================================================================
            // E2. MINI MARKETPLACE MODAL OVERLAY (OFE CHARM & RUSTY SHIELD)
            // =====================================================================
            RectTransform marketRoot = CreateFullStretchRect("MarketplaceModal", canvasGo.transform);
            Image marketBackdropImg = marketRoot.gameObject.AddComponent<Image>();
            marketBackdropImg.sprite = uiWhiteSprite;
            marketBackdropImg.color = new Color(0.02f, 0.04f, 0.08f, 0.88f);
            CanvasGroup marketBackdropGroup = marketRoot.gameObject.AddComponent<CanvasGroup>();

            RectTransform marketModalPanel = CreateAnchoredRect("MarketplaceModalPanel", marketRoot,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1180f, 680f));
            Image marketPanelImg = marketModalPanel.gameObject.AddComponent<Image>();
            marketPanelImg.sprite = uiWhiteSprite;
            marketPanelImg.color = new Color(0.072f, 0.102f, 0.158f, 0.99f);
            AddOutline(marketModalPanel.gameObject, new Color(0.95f, 0.76f, 0.28f, 0.88f), 3f);

            RectTransform marketGoldPill = CreateAnchoredRect("MarketplaceGoldPill", marketModalPanel,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -42f), new Vector2(460f, 44f));
            Image marketGoldPillBg = marketGoldPill.gameObject.AddComponent<Image>();
            marketGoldPillBg.sprite = uiWhiteSprite;
            marketGoldPillBg.color = new Color(0.22f, 0.16f, 0.05f, 0.96f);
            AddOutline(marketGoldPill.gameObject, new Color(0.96f, 0.78f, 0.26f, 0.75f), 1.5f);

            TextMeshProUGUI marketGoldBalanceText = CreateTMP("MarketplaceGoldBalanceText", marketGoldPill, fontAsset,
                "AVAILABLE TREASURY: 0 SPL GOLD", 19f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(1f, 0.88f, 0.36f, 1f));
            StretchFull(marketGoldBalanceText.rectTransform);

            TextMeshProUGUI marketHeaderTitle = CreateTMP("MarketplaceHeaderTitle", marketModalPanel, fontAsset,
                "DUNGEON MERCHANT — MINI MARKETPLACE", 36f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(0.98f, 0.82f, 0.28f, 1f));
            marketHeaderTitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            marketHeaderTitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            marketHeaderTitle.rectTransform.anchoredPosition = new Vector2(0f, -96f);
            marketHeaderTitle.rectTransform.sizeDelta = new Vector2(-80f, 48f);

            TextMeshProUGUI marketSubtitle = CreateTMP("MarketplaceSubtitle", marketModalPanel, fontAsset,
                "Spend Unclaimed SPL Gold on VRF-Seeded 3-Room Consumables • Activate Anytime from Your Rack", 18.5f,
                FontStyles.Normal, TextAlignmentOptions.Center, new Color(0.80f, 0.86f, 0.94f, 0.95f));
            marketSubtitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            marketSubtitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            marketSubtitle.rectTransform.anchoredPosition = new Vector2(0f, -138f);
            marketSubtitle.rectTransform.sizeDelta = new Vector2(-80f, 32f);

            Button closeMarketBtn = CreateStyledButton("CloseMarketplaceButton", marketModalPanel, fontAsset, uiWhiteSprite,
                new Vector2(1f, 1f), new Vector2(-88f, -42f), new Vector2(124f, 44f),
                "CLOSE", 18f,
                new Color(0.24f, 0.28f, 0.38f, 1f),
                new Color(0.34f, 0.40f, 0.52f, 1f),
                new Color(0.16f, 0.18f, 0.22f, 0.8f),
                out _);

            CreateMerchantItemCard(
                "OfeMerchantCard",
                marketModalPanel,
                fontAsset,
                uiWhiteSprite,
                new Vector2(-270f, -58f),
                "YORUBA VANISHING CHARM",
                "OFE CHARM",
                "50% MagicBlock VRF chance to vanish and take 0 damage when fighting barehanded.\nLasts for 3 completed rooms once activated.",
                "Owned in Rack: x0",
                "BUY (60 GOLD)",
                new Color(0.32f, 0.94f, 1.00f, 1f),
                new Color(0.14f, 0.52f, 0.68f, 1f),
                new Color(0.20f, 0.68f, 0.86f, 1f),
                out RectTransform ofeMerchantCardRect,
                out RectTransform ofeMerchantVisualHolder,
                out TextMeshProUGUI ofeMerchantTitle,
                out TextMeshProUGUI ofeMerchantDesc,
                out TextMeshProUGUI ofeMerchantOwned,
                out Button buyOfeBtn,
                out TextMeshProUGUI buyOfeBtnText);

            CreateMerchantItemCard(
                "RustyShieldMerchantCard",
                marketModalPanel,
                fontAsset,
                uiWhiteSprite,
                new Vector2(270f, -58f),
                "IRONBOUND BUCKLER",
                "RUSTY SHIELD",
                "50% MagicBlock VRF chance to block half (-50%) of incoming monster damage (weapon chip or barehanded).\nLasts for 3 completed rooms once activated.",
                "Owned in Rack: x0",
                "BUY (45 GOLD)",
                new Color(1.00f, 0.78f, 0.28f, 1f),
                new Color(0.72f, 0.48f, 0.14f, 1f),
                new Color(0.88f, 0.62f, 0.20f, 1f),
                out RectTransform shieldMerchantCardRect,
                out RectTransform shieldMerchantVisualHolder,
                out TextMeshProUGUI shieldMerchantTitle,
                out TextMeshProUGUI shieldMerchantDesc,
                out TextMeshProUGUI shieldMerchantOwned,
                out Button buyShieldBtn,
                out TextMeshProUGUI buyShieldBtnText);

            MarketplaceModalUI marketplaceModal = marketRoot.gameObject.AddComponent<MarketplaceModalUI>();
            marketplaceModal.ConfigureReferences(
                gameManager,
                marketBackdropGroup,
                marketModalPanel,
                marketHeaderTitle,
                marketSubtitle,
                marketGoldBalanceText,
                closeMarketBtn,
                ofeMerchantCardRect,
                ofeMerchantVisualHolder,
                ofeMerchantTitle,
                ofeMerchantDesc,
                ofeMerchantOwned,
                buyOfeBtn,
                buyOfeBtnText,
                shieldMerchantCardRect,
                shieldMerchantVisualHolder,
                shieldMerchantTitle,
                shieldMerchantDesc,
                shieldMerchantOwned,
                buyShieldBtn,
                buyShieldBtnText,
                foilMat);

            SpoilsModalUI spoilsModal = spoilsRoot.gameObject.AddComponent<SpoilsModalUI>();
            spoilsModal.ConfigureReferences(
                gameManager,
                spoilsBackdropGroup,
                spoilsModalPanel,
                spoilsHeaderTitle,
                threatBadgeText,
                threatBadgeBg,
                spoilsEchoSubtitle,
                lootCardButtons,
                foilMat,
                visitMerchantBtn,
                marketplaceModal);

            // =====================================================================
            // E3. SOLANA DEVNET WALLET ADAPTER & SPL GOLD MINT MODAL OVERLAY
            // =====================================================================
            RectTransform walletRoot = CreateFullStretchRect("WalletConnectModal", canvasGo.transform);
            Image walletBackdropImg = walletRoot.gameObject.AddComponent<Image>();
            walletBackdropImg.sprite = uiWhiteSprite;
            walletBackdropImg.color = new Color(0.02f, 0.04f, 0.08f, 0.88f);
            CanvasGroup walletBackdropGroup = walletRoot.gameObject.AddComponent<CanvasGroup>();

            RectTransform walletModalPanel = CreateAnchoredRect("WalletConnectModalPanel", walletRoot,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1140f, 650f));
            Image walletPanelImg = walletModalPanel.gameObject.AddComponent<Image>();
            walletPanelImg.sprite = uiWhiteSprite;
            walletPanelImg.color = new Color(0.068f, 0.098f, 0.152f, 0.99f);
            AddOutline(walletModalPanel.gameObject, new Color(0.32f, 0.88f, 0.64f, 0.88f), 3f);

            RectTransform walletModePill = CreateAnchoredRect("WalletModeBadge", walletModalPanel,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -42f), new Vector2(580f, 44f));
            Image walletModePillBg = walletModePill.gameObject.AddComponent<Image>();
            walletModePillBg.sprite = uiWhiteSprite;
            walletModePillBg.color = new Color(0.06f, 0.24f, 0.18f, 0.96f);
            AddOutline(walletModePill.gameObject, new Color(0.38f, 0.92f, 0.68f, 0.75f), 1.5f);

            TextMeshProUGUI walletModeBadgeText = CreateTMP("WalletModeBadgeText", walletModePill, fontAsset,
                "MODE: LOCAL VRF SIMULATION (ZERO RPC DELAY)", 18f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(1.0f, 0.88f, 0.36f, 1f));
            StretchFull(walletModeBadgeText.rectTransform);

            TextMeshProUGUI walletHeaderTitle = CreateTMP("WalletHeaderTitle", walletModalPanel, fontAsset,
                "SOLANA DEVNET WALLET & SPL GOLD MINT", 34f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(0.46f, 0.98f, 0.74f, 1f));
            walletHeaderTitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            walletHeaderTitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            walletHeaderTitle.rectTransform.anchoredPosition = new Vector2(0f, -94f);
            walletHeaderTitle.rectTransform.sizeDelta = new Vector2(-80f, 44f);

            TextMeshProUGUI walletSubtitle = CreateTMP("WalletSubtitle", walletModalPanel, fontAsset,
                "Connect Wallet Adapter or Devnet Burner • Ephemeral Session Keys Sign Gameplay Turns • Mint Unclaimed Gold to ATA",
                17.5f, FontStyles.Normal, TextAlignmentOptions.Center, new Color(0.80f, 0.86f, 0.94f, 0.95f));
            walletSubtitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            walletSubtitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            walletSubtitle.rectTransform.anchoredPosition = new Vector2(0f, -134f);
            walletSubtitle.rectTransform.sizeDelta = new Vector2(-80f, 30f);

            Button closeWalletBtn = CreateStyledButton("CloseWalletModalButton", walletModalPanel, fontAsset, uiWhiteSprite,
                new Vector2(1f, 1f), new Vector2(-88f, -42f), new Vector2(124f, 44f),
                "CLOSE", 18f,
                new Color(0.24f, 0.28f, 0.38f, 1f),
                new Color(0.34f, 0.40f, 0.52f, 1f),
                new Color(0.16f, 0.18f, 0.22f, 0.8f),
                out _);

            RectTransform addrBox = CreateAnchoredRect("WalletAddressesBox", walletModalPanel,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 46f), new Vector2(1040f, 194f));
            Image addrBoxBg = addrBox.gameObject.AddComponent<Image>();
            addrBoxBg.sprite = uiWhiteSprite;
            addrBoxBg.color = new Color(0.045f, 0.068f, 0.108f, 0.96f);
            AddOutline(addrBox.gameObject, new Color(0.26f, 0.42f, 0.58f, 0.6f), 1.5f);

            TextMeshProUGUI walletAddressesText = CreateTMP("WalletAddressesText", addrBox, fontAsset,
                "Program ID: 7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx", 16f, FontStyles.Normal,
                TextAlignmentOptions.Left, new Color(0.86f, 0.92f, 0.98f, 0.96f));
            StretchFull(walletAddressesText.rectTransform, 24f, 14f);

            RectTransform balBox = CreateAnchoredRect("WalletBalancesBox", walletModalPanel,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -86f), new Vector2(1040f, 52f));
            Image balBoxBg = balBox.gameObject.AddComponent<Image>();
            balBoxBg.sprite = uiWhiteSprite;
            balBoxBg.color = new Color(0.09f, 0.13f, 0.20f, 0.96f);
            AddOutline(balBox.gameObject, new Color(0.92f, 0.76f, 0.28f, 0.6f), 1.5f);

            TextMeshProUGUI balancesSummaryText = CreateTMP("WalletBalancesSummaryText", balBox, fontAsset,
                "Devnet Balance: 0.000 SOL   •   Unclaimed Run Gold: 0 GOLD   •   Minted SPL Gold ATA: 0 GOLD",
                18.5f, FontStyles.Normal, TextAlignmentOptions.Center, Color.white);
            StretchFull(balancesSummaryText.rectTransform, 18f, 6f);

            TextMeshProUGUI bridgeStatusReadout = CreateTMP("WalletBridgeStatusText", walletModalPanel, fontAsset,
                "Status: Session Key Ready", 15.5f, FontStyles.Normal, TextAlignmentOptions.Center,
                new Color(0.62f, 0.88f, 0.78f, 0.92f));
            bridgeStatusReadout.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            bridgeStatusReadout.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            bridgeStatusReadout.rectTransform.anchoredPosition = new Vector2(0f, -134f);
            bridgeStatusReadout.rectTransform.sizeDelta = new Vector2(-80f, 28f);

            // Row 1 Action Buttons: Connect Wallet Adapter, Devnet Burner + Airdrop, Switch Network Mode
            Button connectAdapterBtn = CreateStyledButton("ConnectWalletAdapterButton", walletModalPanel, fontAsset, uiWhiteSprite,
                new Vector2(0.5f, 0f), new Vector2(-346f, 118f), new Vector2(326f, 56f),
                "CONNECT WALLET ADAPTER", 17f,
                new Color(0.28f, 0.22f, 0.68f, 1f),
                new Color(0.38f, 0.30f, 0.86f, 1f),
                new Color(0.18f, 0.16f, 0.24f, 0.8f),
                out TextMeshProUGUI connectAdapterBtnText);

            Button devnetBurnerBtn = CreateStyledButton("DevnetBurnerWalletButton", walletModalPanel, fontAsset, uiWhiteSprite,
                new Vector2(0.5f, 0f), new Vector2(0f, 118f), new Vector2(326f, 56f),
                "DEVNET BURNER + AIRDROP", 17f,
                new Color(0.14f, 0.52f, 0.38f, 1f),
                new Color(0.20f, 0.68f, 0.50f, 1f),
                new Color(0.16f, 0.20f, 0.18f, 0.8f),
                out TextMeshProUGUI devnetBurnerBtnText);

            Button toggleModeBtn = CreateStyledButton("ToggleNetworkModeButton", walletModalPanel, fontAsset, uiWhiteSprite,
                new Vector2(0.5f, 0f), new Vector2(346f, 118f), new Vector2(326f, 56f),
                "SWITCH TO DEVNET LIVE", 17f,
                new Color(0.16f, 0.42f, 0.64f, 1f),
                new Color(0.22f, 0.54f, 0.80f, 1f),
                new Color(0.16f, 0.18f, 0.22f, 0.8f),
                out TextMeshProUGUI toggleModeBtnText);

            // Row 2 Action Buttons: Mint SPL Gold to ATA & Copy Wallet Address
            Button mintGoldBtn = CreateStyledButton("MintSplGoldButton", walletModalPanel, fontAsset, uiWhiteSprite,
                new Vector2(0.5f, 0f), new Vector2(-180f, 48f), new Vector2(460f, 54f),
                "MINT SPL GOLD TO ATA", 18f,
                new Color(0.76f, 0.52f, 0.12f, 1f),
                new Color(0.92f, 0.66f, 0.18f, 1f),
                new Color(0.20f, 0.18f, 0.14f, 0.8f),
                out TextMeshProUGUI mintGoldBtnText);

            Button copyAddressBtn = CreateStyledButton("CopyWalletAddressButton", walletModalPanel, fontAsset, uiWhiteSprite,
                new Vector2(0.5f, 0f), new Vector2(260f, 48f), new Vector2(360f, 54f),
                "COPY WALLET ADDRESS", 17.5f,
                new Color(0.24f, 0.30f, 0.40f, 1f),
                new Color(0.34f, 0.42f, 0.54f, 1f),
                new Color(0.16f, 0.18f, 0.22f, 0.8f),
                out TextMeshProUGUI copyAddressBtnText);

            WalletConnectModalUI walletModal = walletRoot.gameObject.AddComponent<WalletConnectModalUI>();
            walletModal.ConfigureReferences(
                gameManager,
                walletBackdropGroup,
                walletModalPanel,
                walletHeaderTitle,
                walletSubtitle,
                walletModeBadgeText,
                walletModePillBg,
                closeWalletBtn,
                walletAddressesText,
                balancesSummaryText,
                bridgeStatusReadout,
                connectAdapterBtn,
                connectAdapterBtnText,
                devnetBurnerBtn,
                devnetBurnerBtnText,
                toggleModeBtn,
                toggleModeBtnText,
                mintGoldBtn,
                mintGoldBtnText,
                copyAddressBtn,
                copyAddressBtnText);

            // =====================================================================
            // F. GAME OVER (VICTORY / DEFEAT) MODAL
            // =====================================================================
            RectTransform gameOverRoot = CreateFullStretchRect("GameOverModal", canvasGo.transform);
            Image goBackdrop = gameOverRoot.gameObject.AddComponent<Image>();
            goBackdrop.sprite = uiWhiteSprite;
            goBackdrop.color = new Color(0.02f, 0.03f, 0.06f, 0.88f);
            CanvasGroup gameOverGroup = gameOverRoot.gameObject.AddComponent<CanvasGroup>();

            RectTransform goPanel = CreateAnchoredRect("GameOverPanel", gameOverRoot,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(780f, 470f));
            Image goPanelImg = goPanel.gameObject.AddComponent<Image>();
            goPanelImg.sprite = uiWhiteSprite;
            goPanelImg.color = new Color(0.08f, 0.11f, 0.17f, 0.99f);
            AddOutline(goPanel.gameObject, new Color(0.88f, 0.68f, 0.24f, 0.8f), 2.5f);

            TextMeshProUGUI gameOverTitleText = CreateTMP("GameOverTitleText", goPanel, fontAsset,
                "DUNGEON CONQUERED!", 42f, FontStyles.Bold, TextAlignmentOptions.Center,
                new Color(0.98f, 0.82f, 0.26f, 1f));
            gameOverTitleText.rectTransform.anchorMin = new Vector2(0f, 1f);
            gameOverTitleText.rectTransform.anchorMax = new Vector2(1f, 1f);
            gameOverTitleText.rectTransform.anchoredPosition = new Vector2(0f, -68f);
            gameOverTitleText.rectTransform.sizeDelta = new Vector2(-60f, 58f);

            TextMeshProUGUI gameOverSubtitleText = CreateTMP("GameOverSubtitleText", goPanel, fontAsset,
                "Official Scoundrel Score: 20\nSPL Gold Looted: 0", 24f, FontStyles.Normal, TextAlignmentOptions.Center,
                new Color(0.88f, 0.91f, 0.96f, 1f));
            gameOverSubtitleText.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            gameOverSubtitleText.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            gameOverSubtitleText.rectTransform.anchoredPosition = new Vector2(0f, 22f);
            gameOverSubtitleText.rectTransform.sizeDelta = new Vector2(-80f, 140f);

            Button newRunStdBtn = CreateStyledButton("NewRunStandardButton", goPanel, fontAsset, uiWhiteSprite,
                new Vector2(0.5f, 0f), new Vector2(0f, 74f), new Vector2(310f, 64f),
                "NEW DUNGEON RUN", 20f,
                new Color(0.16f, 0.48f, 0.34f, 1f),
                new Color(0.22f, 0.62f, 0.44f, 1f),
                new Color(0.15f, 0.20f, 0.18f, 0.8f),
                out _);

            Button newRunNftBtn = CreateStyledButton("NewRunLegendaryNftButton", goPanel, fontAsset, uiWhiteSprite,
                new Vector2(0.5f, 0f), new Vector2(175f, 74f), new Vector2(310f, 64f),
                "NEW RUN (LEGENDARY NFT)", 18f,
                new Color(0.76f, 0.48f, 0.12f, 1f),
                new Color(0.92f, 0.60f, 0.16f, 1f),
                new Color(0.22f, 0.18f, 0.12f, 0.8f),
                out _);
            newRunNftBtn.gameObject.SetActive(false);

            gameOverRoot.gameObject.SetActive(false);
            combatChoiceRect.gameObject.SetActive(false);
            spoilsRoot.gameObject.SetActive(false);
            marketRoot.gameObject.SetActive(false);
            walletRoot.gameObject.SetActive(false);

            // =====================================================================
            // G. WIRE CONTROLLERS & SAVE SCENE
            // =====================================================================
            fxController.ConfigureReferences(
                gameManager,
                shakeRoot,
                screenDimGroup,
                vfxContainer,
                bgmSource,
                sfxSource,
                stingerSource);

            ScoundrelTabletopUIController uiController = canvasGo.AddComponent<ScoundrelTabletopUIController>();
            uiController.ConfigureSceneReferences(
                gameManager,
                spoilsModal,
                foilMat,
                animationLayer,
                hpBarFill,
                hpValueText,
                echoBadgeRect.gameObject,
                echoBadgeBg,
                echoBadgeText,
                goldValueText,
                sessionStatusText,
                avoidRoomBtn,
                avoidRoomBtnText,
                dungeonDeckAnchor,
                dungeonBackImg,
                dungeonCountText,
                roomSlotAnchors,
                discardPileAnchor,
                discardBackImg,
                discardCountText,
                consumableCardHolder,
                consumableGoldenAura,
                consumableCountBadgeText,
                useEpicPotionBtn,
                useEpicPotionBtnText,
                equippedWeaponAnchor,
                weaponMonsterStackContainer,
                weaponPlaceholderGo,
                shieldBadgeImage,
                weaponCeilingBadgeText,
                roomThreatLiveText,
                combatChoiceRect.gameObject,
                combatPromptTitleText,
                useWeaponBtn,
                useWeaponBtnText,
                fightBarehandedBtn,
                fightBarehandedBtnText,
                cancelCombatBtn,
                gameOverRoot.gameObject,
                gameOverGroup,
                gameOverTitleText,
                gameOverSubtitleText,
                newRunStdBtn,
                newRunNftBtn);

            uiController.ConfigureMarketplaceAndConsumables(
                marketplaceModal,
                openMarketBtn,
                ofeCardHolder,
                ofeCyanAura,
                ofeCountBadgeText,
                useOfeBtn,
                useOfeBtnText,
                rustyShieldCardHolder,
                rustyShieldAmberAura,
                rustyShieldCountBadgeText,
                useRustyShieldBtn,
                useRustyShieldBtnText);

            uiController.ConfigureWalletModal(walletModal, openWalletBtn);

            // Enforce RobotoSlab-Regular SDF across EVERY TextMeshProUGUI in the scene
            int tmpCount = 0;
            foreach (TextMeshProUGUI tmp in canvasGo.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (fontAsset != null)
                {
                    tmp.font = fontAsset;
                }
                EditorUtility.SetDirty(tmp);
                tmpCount++;
            }

            EnsureDirectory("Assets/Scenes");
            EditorUtility.SetDirty(bridge);
            EditorUtility.SetDirty(gameManager);
            EditorUtility.SetDirty(fxController);
            EditorUtility.SetDirty(spoilsModal);
            EditorUtility.SetDirty(marketplaceModal);
            EditorUtility.SetDirty(walletModal);
            EditorUtility.SetDirty(uiController);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, TargetScenePath);

            RegisterSceneInBuildSettings(TargetScenePath);
            AssetDatabase.SaveAssets();

            return $"Saved {TargetScenePath} cleanly (TMP elements styled with RobotoSlab-Regular SDF: {tmpCount}, sprites updated: {reimportedSprites}).";
        }

        private static int PrepareResourcesAndImportSettings()
        {
            EnsureDirectory("Assets/Resources");
            EnsureDirectory("Assets/Resources/Fonts");
            EnsureDirectory("Assets/Resources/Audio");
            EnsureDirectory("Assets/Resources/Audio/Music");
            EnsureDirectory("Assets/Resources/Materials");
            EnsureDirectory("Assets/Scenes");

            if (!File.Exists(TargetFontResourcePath) && File.Exists(SourceFontAssetPath))
            {
                AssetDatabase.CopyAsset(SourceFontAssetPath, TargetFontResourcePath);
            }

            if (!File.Exists(TargetBgmResourcePath) && File.Exists(SourceBgmPath))
            {
                AssetDatabase.CopyAsset(SourceBgmPath, TargetBgmResourcePath);
            }

            string[] searchFolders = { "Assets/Resources/Cards", "Assets/Resources/Sprites" };
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", searchFolders);
            int updatedCount = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                TextureImporterSettings settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);

                bool needsChange =
                    importer.textureType != TextureImporterType.Sprite ||
                    importer.spriteImportMode != SpriteImportMode.Single ||
                    importer.filterMode != FilterMode.Bilinear ||
                    importer.mipmapEnabled ||
                    settings.spriteMeshType != SpriteMeshType.FullRect ||
                    Mathf.Abs(settings.spritePivot.x - 0.5f) > 0.001f ||
                    Mathf.Abs(settings.spritePivot.y - 0.5f) > 0.001f;

                if (needsChange)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.filterMode = FilterMode.Bilinear;
                    importer.mipmapEnabled = false;
                    importer.alphaIsTransparency = true;

                    settings.spriteMode = (int)SpriteImportMode.Single;
                    settings.spriteMeshType = SpriteMeshType.FullRect;
                    settings.spriteAlignment = (int)SpriteAlignment.Center;
                    settings.spritePivot = new Vector2(0.5f, 0.5f);
                    importer.SetTextureSettings(settings);

                    importer.SaveAndReimport();
                    updatedCount++;
                }
            }

            AssetDatabase.Refresh();
            return updatedCount;
        }

        private static Material CreateOrUpdateHolographicMaterial()
        {
            Shader foilShader = Shader.Find(ScoundrelAssetPaths.HolographicShaderName);
            if (foilShader == null)
            {
                foilShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/HolographicFoilCard.shader");
            }
            if (foilShader == null)
            {
                foilShader = Shader.Find("Sprites/Default");
            }

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(FoilMaterialAssetPath);
            if (mat == null)
            {
                mat = new Material(foilShader) { name = "HolographicFoilCardMat" };
                AssetDatabase.CreateAsset(mat, FoilMaterialAssetPath);
            }
            else
            {
                mat.shader = foilShader;
                EditorUtility.SetDirty(mat);
            }

            return mat;
        }

        private static TMP_FontAsset LoadRobotoSlabFont()
        {
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TargetFontResourcePath);
            if (font == null)
            {
                font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(SourceFontAssetPath);
            }
            return font;
        }

        private static Sprite LoadSpriteAtAssetPath(string assetPath)
        {
            Sprite s = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (s != null) return s;
            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            foreach (UnityEngine.Object obj in all)
            {
                if (obj is Sprite sp) return sp;
            }
            return null;
        }

        private static Sprite GetSmoothWhiteSprite()
        {
            EnsureDirectory("Assets/Resources/Sprites/VFX");
            const string whiteSpritePath = "Assets/Resources/Sprites/VFX/ui_solid_quad.png";
            if (!File.Exists(whiteSpritePath))
            {
                Texture2D tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                Color32[] cols = new Color32[64];
                for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(255, 255, 255, 255);
                tex.SetPixels32(cols);
                tex.Apply();
                File.WriteAllBytes(whiteSpritePath, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(whiteSpritePath, ImportAssetOptions.ForceSynchronousImport);

                TextureImporter imp = AssetImporter.GetAtPath(whiteSpritePath) as TextureImporter;
                if (imp != null)
                {
                    imp.textureType = TextureImporterType.Sprite;
                    imp.spriteImportMode = SpriteImportMode.Single;
                    imp.filterMode = FilterMode.Bilinear;
                    imp.mipmapEnabled = false;
                    TextureImporterSettings s = new TextureImporterSettings();
                    imp.ReadTextureSettings(s);
                    s.spriteMeshType = SpriteMeshType.FullRect;
                    imp.SetTextureSettings(s);
                    imp.SaveAndReimport();
                }
            }
            return LoadSpriteAtAssetPath(whiteSpritePath);
        }

        private static void EnsureDirectory(string assetFolderPath)
        {
            if (AssetDatabase.IsValidFolder(assetFolderPath)) return;
            string[] parts = assetFolderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static void RegisterSceneInBuildSettings(string scenePath)
        {
            List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == scenePath);
            scenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static AudioSource CreateAudioSourceChild(Transform parent, string name, bool loop, float volume)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            AudioSource src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = loop;
            src.volume = volume;
            src.spatialBlend = 0f;
            return src;
        }

        private static RectTransform CreateFullStretchRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            StretchFull(rt);
            return rt;
        }

        private static void StretchFull(RectTransform rt, float padX = 0f, float padY = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padX, padY);
            rt.offsetMax = new Vector2(-padX, -padY);
            rt.localScale = Vector3.one;
        }

        private static RectTransform CreateAnchoredRect(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 anchoredPos,
            Vector2 sizeDelta)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;
            rt.localScale = Vector3.one;
            return rt;
        }

        private static Image CreatePanel(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 anchoredPos,
            Vector2 sizeDelta,
            Color color,
            Sprite sprite)
        {
            RectTransform rt = CreateAnchoredRect(name, parent, anchorMin, anchorMax, anchoredPos, sizeDelta);
            Image img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            return img;
        }

        private static void AddOutline(GameObject target, Color color, float thickness)
        {
            Outline outline = target.AddComponent<Outline>();
            outline.effectColor = color;
            outline.effectDistance = new Vector2(thickness, -thickness);
        }

        private static TextMeshProUGUI CreateTMP(
            string name,
            Transform parent,
            TMP_FontAsset font,
            string text,
            float fontSize,
            FontStyles style,
            TextAlignmentOptions alignment,
            Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static void CreatePileFrame(RectTransform zone, TMP_FontAsset font, Sprite sprite, string label, Color labelColor)
        {
            Image bg = zone.gameObject.AddComponent<Image>();
            bg.sprite = sprite;
            bg.color = new Color(0.06f, 0.09f, 0.14f, 0.80f);
            AddOutline(zone.gameObject, new Color(0.24f, 0.34f, 0.48f, 0.45f), 1.5f);

            TextMeshProUGUI header = CreateTMP("PileHeader", zone, font, label, 15f, FontStyles.Bold, TextAlignmentOptions.Center, labelColor);
            header.rectTransform.anchorMin = new Vector2(0f, 1f);
            header.rectTransform.anchorMax = new Vector2(1f, 1f);
            header.rectTransform.anchoredPosition = new Vector2(0f, -18f);
            header.rectTransform.sizeDelta = new Vector2(0f, 24f);
        }

        private static TextMeshProUGUI CreateCountBadge(
            string name,
            Transform parent,
            TMP_FontAsset font,
            Sprite sprite,
            string initialCount,
            Color pillColor)
        {
            RectTransform pill = CreateAnchoredRect(name, parent,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-18f, 20f), new Vector2(58f, 36f));
            Image bg = pill.gameObject.AddComponent<Image>();
            bg.sprite = sprite;
            bg.color = pillColor;
            AddOutline(pill.gameObject, new Color(1f, 1f, 1f, 0.35f), 1.5f);

            TextMeshProUGUI txt = CreateTMP("CountText", pill, font, initialCount, 20f, FontStyles.Bold, TextAlignmentOptions.Center, Color.white);
            StretchFull(txt.rectTransform);
            return txt;
        }

        private static Button CreateStyledButton(
            string name,
            Transform parent,
            TMP_FontAsset font,
            Sprite sprite,
            Vector2 anchor,
            Vector2 anchoredPos,
            Vector2 size,
            string label,
            float fontSize,
            Color normalColor,
            Color highlightedColor,
            Color disabledColor,
            out TextMeshProUGUI labelOut)
        {
            RectTransform rt = CreateAnchoredRect(name, parent, anchor, anchor, anchoredPos, size);
            Image bg = rt.gameObject.AddComponent<Image>();
            bg.sprite = sprite;
            bg.color = Color.white;
            AddOutline(rt.gameObject, new Color(1f, 1f, 1f, 0.25f), 1.5f);

            Button btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            ColorBlock cb = btn.colors;
            cb.normalColor = normalColor;
            cb.highlightedColor = highlightedColor;
            cb.pressedColor = new Color(normalColor.r * 0.78f, normalColor.g * 0.78f, normalColor.b * 0.78f, 1f);
            cb.selectedColor = highlightedColor;
            cb.disabledColor = disabledColor;
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            btn.colors = cb;

            labelOut = CreateTMP("Label", rt, font, label, fontSize, FontStyles.Bold, TextAlignmentOptions.Center, Color.white);
            StretchFull(labelOut.rectTransform, 8f, 4f);
            return btn;
        }

        private static SpoilsLootCardButton CreateSpoilsLootCard(string name, Transform parent, TMP_FontAsset font, Sprite whiteSprite)
        {
            GameObject rootGo = new GameObject(name, typeof(RectTransform));
            rootGo.transform.SetParent(parent, false);
            RectTransform rootRect = rootGo.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(350f, 420f);

            RectTransform shadowRect = CreateAnchoredRect("Shadow", rootRect,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(6f, -10f), new Vector2(350f, 420f));
            Image shadowImg = shadowRect.gameObject.AddComponent<Image>();
            shadowImg.sprite = whiteSprite;
            shadowImg.color = new Color(0.01f, 0.02f, 0.04f, 0.65f);
            shadowImg.raycastTarget = false;

            RectTransform cardRect = CreateAnchoredRect("CardBody", rootRect,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(350f, 420f));
            Image cardBg = cardRect.gameObject.AddComponent<Image>();
            cardBg.sprite = whiteSprite;
            cardBg.color = new Color(0.11f, 0.15f, 0.23f, 1f);

            RectTransform borderRect = CreateFullStretchRect("CardBorder", cardRect);
            Image borderImg = borderRect.gameObject.AddComponent<Image>();
            borderImg.sprite = whiteSprite;
            borderImg.color = new Color(0.95f, 0.78f, 0.28f, 0.22f);
            borderImg.raycastTarget = false;
            AddOutline(cardRect.gameObject, new Color(0.95f, 0.78f, 0.28f, 0.85f), 2.5f);

            TextMeshProUGUI tagText = CreateTMP("RarityTag", cardRect, font, "TREASURY", 14f, FontStyles.Bold,
                TextAlignmentOptions.Center, new Color(0.95f, 0.80f, 0.30f, 1f));
            tagText.rectTransform.anchorMin = new Vector2(0f, 1f);
            tagText.rectTransform.anchorMax = new Vector2(1f, 1f);
            tagText.rectTransform.anchoredPosition = new Vector2(0f, -26f);
            tagText.rectTransform.sizeDelta = new Vector2(-24f, 24f);

            RectTransform iconRect = CreateAnchoredRect("LootIcon", cardRect,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -142f), new Vector2(164f, 174f));
            Image iconImg = iconRect.gameObject.AddComponent<Image>();
            iconImg.sprite = whiteSprite;
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;

            TextMeshProUGUI titleText = CreateTMP("LootTitle", cardRect, font, "+40 SPL GOLD", 24f, FontStyles.Bold,
                TextAlignmentOptions.Center, Color.white);
            titleText.rectTransform.anchorMin = new Vector2(0f, 0f);
            titleText.rectTransform.anchorMax = new Vector2(1f, 0f);
            titleText.rectTransform.anchoredPosition = new Vector2(0f, 134f);
            titleText.rectTransform.sizeDelta = new Vector2(-28f, 44f);

            TextMeshProUGUI descText = CreateTMP("LootDescription", cardRect, font,
                "Claim Dungeon Spoils", 17f, FontStyles.Normal,
                TextAlignmentOptions.Center, new Color(0.80f, 0.85f, 0.92f, 0.95f));
            descText.rectTransform.anchorMin = new Vector2(0f, 0f);
            descText.rectTransform.anchorMax = new Vector2(1f, 0f);
            descText.rectTransform.anchoredPosition = new Vector2(0f, 64f);
            descText.rectTransform.sizeDelta = new Vector2(-34f, 86f);

            Button btn = rootGo.AddComponent<Button>();
            btn.targetGraphic = cardBg;

            SpoilsLootCardButton lootComp = rootGo.AddComponent<SpoilsLootCardButton>();
            lootComp.Button = btn;
            lootComp.CardRect = cardRect;
            lootComp.ShadowRect = shadowRect;
            lootComp.BackgroundImage = cardBg;
            lootComp.BorderImage = borderImg;
            lootComp.IconImage = iconImg;
            lootComp.TitleText = titleText;
            lootComp.DescriptionText = descText;
            lootComp.TagText = tagText;

            return lootComp;
        }

        private static void BuildConsumableSubSlot(
            string name,
            Transform parent,
            TMP_FontAsset font,
            Sprite whiteSprite,
            float offsetX,
            string title,
            Color auraColor,
            Color badgeColor,
            Color btnNormal,
            Color btnHighlight,
            string initialCount,
            string initialBtnLabel,
            out RectTransform cardHolder,
            out Image auraImage,
            out TextMeshProUGUI countBadgeText,
            out Button useButton,
            out TextMeshProUGUI useButtonText)
        {
            RectTransform slotRoot = CreateAnchoredRect(name, parent,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(offsetX, -14f), new Vector2(108f, 304f));
            Image slotBg = slotRoot.gameObject.AddComponent<Image>();
            slotBg.sprite = whiteSprite;
            slotBg.color = new Color(0.055f, 0.080f, 0.125f, 0.85f);
            AddOutline(slotRoot.gameObject, new Color(auraColor.r, auraColor.g, auraColor.b, 0.45f), 1.5f);

            TextMeshProUGUI subHeader = CreateTMP("SlotTitle", slotRoot, font, title, 12.5f, FontStyles.Bold,
                TextAlignmentOptions.Center, new Color(0.92f, 0.94f, 0.98f, 0.92f));
            subHeader.rectTransform.anchorMin = new Vector2(0f, 1f);
            subHeader.rectTransform.anchorMax = new Vector2(1f, 1f);
            subHeader.rectTransform.anchoredPosition = new Vector2(0f, -15f);
            subHeader.rectTransform.sizeDelta = new Vector2(-6f, 20f);

            RectTransform auraRect = CreateAnchoredRect("ConsumableAura", slotRoot,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 16f), new Vector2(96f, 134f));
            auraImage = auraRect.gameObject.AddComponent<Image>();
            auraImage.sprite = whiteSprite;
            auraImage.color = auraColor;
            auraImage.raycastTarget = false;

            cardHolder = CreateAnchoredRect("CardHolder", slotRoot,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 16f), new Vector2(86f, 120f));

            RectTransform pill = CreateAnchoredRect("CountBadge", slotRoot,
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-24f, -38f), new Vector2(42f, 26f));
            Image pillBg = pill.gameObject.AddComponent<Image>();
            pillBg.sprite = whiteSprite;
            pillBg.color = badgeColor;
            AddOutline(pill.gameObject, new Color(1f, 1f, 1f, 0.35f), 1f);

            countBadgeText = CreateTMP("CountText", pill, font, initialCount, 14f, FontStyles.Bold, TextAlignmentOptions.Center, Color.white);
            StretchFull(countBadgeText.rectTransform);

            useButton = CreateStyledButton("UseButton", slotRoot, font, whiteSprite,
                new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(96f, 38f),
                initialBtnLabel, 13.5f,
                btnNormal,
                btnHighlight,
                new Color(0.16f, 0.18f, 0.24f, 0.82f),
                out useButtonText);
        }

        private static void CreateMerchantItemCard(
            string name,
            Transform parent,
            TMP_FontAsset font,
            Sprite whiteSprite,
            Vector2 anchoredPos,
            string tagLabel,
            string itemTitle,
            string itemDescription,
            string ownedInitial,
            string buyBtnLabel,
            Color accentColor,
            Color btnNormal,
            Color btnHighlight,
            out RectTransform cardRectOut,
            out RectTransform visualHolderOut,
            out TextMeshProUGUI titleTextOut,
            out TextMeshProUGUI descTextOut,
            out TextMeshProUGUI ownedTextOut,
            out Button buyButtonOut,
            out TextMeshProUGUI buyButtonTextOut)
        {
            cardRectOut = CreateAnchoredRect(name, parent,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), anchoredPos, new Vector2(490f, 468f));
            Image cardBg = cardRectOut.gameObject.AddComponent<Image>();
            cardBg.sprite = whiteSprite;
            cardBg.color = new Color(0.105f, 0.145f, 0.220f, 0.99f);
            AddOutline(cardRectOut.gameObject, new Color(accentColor.r, accentColor.g, accentColor.b, 0.82f), 2.5f);

            TextMeshProUGUI tagTmp = CreateTMP("ItemTag", cardRectOut, font, tagLabel, 14f, FontStyles.Bold,
                TextAlignmentOptions.Center, accentColor);
            tagTmp.rectTransform.anchorMin = new Vector2(0f, 1f);
            tagTmp.rectTransform.anchorMax = new Vector2(1f, 1f);
            tagTmp.rectTransform.anchoredPosition = new Vector2(0f, -24f);
            tagTmp.rectTransform.sizeDelta = new Vector2(-24f, 24f);

            visualHolderOut = CreateAnchoredRect("CardVisualHolder", cardRectOut,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(148f, 206f));

            titleTextOut = CreateTMP("ItemTitle", cardRectOut, font, itemTitle, 25f, FontStyles.Bold,
                TextAlignmentOptions.Center, accentColor);
            titleTextOut.rectTransform.anchorMin = new Vector2(0f, 0f);
            titleTextOut.rectTransform.anchorMax = new Vector2(1f, 0f);
            titleTextOut.rectTransform.anchoredPosition = new Vector2(0f, 184f);
            titleTextOut.rectTransform.sizeDelta = new Vector2(-32f, 36f);

            descTextOut = CreateTMP("ItemDescription", cardRectOut, font, itemDescription, 15.5f, FontStyles.Normal,
                TextAlignmentOptions.Center, new Color(0.84f, 0.89f, 0.96f, 0.96f));
            descTextOut.rectTransform.anchorMin = new Vector2(0f, 0f);
            descTextOut.rectTransform.anchorMax = new Vector2(1f, 0f);
            descTextOut.rectTransform.anchoredPosition = new Vector2(0f, 126f);
            descTextOut.rectTransform.sizeDelta = new Vector2(-42f, 74f);

            ownedTextOut = CreateTMP("ItemOwnedText", cardRectOut, font, ownedInitial, 16f, FontStyles.Bold,
                TextAlignmentOptions.Center, new Color(0.98f, 0.88f, 0.52f, 0.95f));
            ownedTextOut.rectTransform.anchorMin = new Vector2(0f, 0f);
            ownedTextOut.rectTransform.anchorMax = new Vector2(1f, 0f);
            ownedTextOut.rectTransform.anchoredPosition = new Vector2(0f, 78f);
            ownedTextOut.rectTransform.sizeDelta = new Vector2(-32f, 28f);

            buyButtonOut = CreateStyledButton("BuyItemButton", cardRectOut, font, whiteSprite,
                new Vector2(0.5f, 0f), new Vector2(0f, 34f), new Vector2(260f, 48f),
                buyBtnLabel, 18f,
                btnNormal,
                btnHighlight,
                new Color(0.18f, 0.20f, 0.26f, 0.8f),
                out buyButtonTextOut);
        }
    }
}
#endif
