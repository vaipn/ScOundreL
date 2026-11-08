using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Scoundrel
{
    public enum CardSuit
    {
        Clubs = 0,
        Spades = 1,
        Diamonds = 2,
        Hearts = 3
    }

    public enum CardType
    {
        Monster = 0,
        Weapon = 1,
        Potion = 2
    }

    public enum SpoilsTier
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    public enum WeaponSkinTier
    {
        Standard = 0,
        EpicFoil = 1,
        Legendary = 2
    }

    public enum CombatOutcome
    {
        ObliteratedWithWeapon = 0,
        ChipDamageWithWeapon = 1,
        BarehandedHit = 2
    }

    public enum RunStatus : byte
    {
        Active = 0,
        Victory = 1,
        Defeat = 2,
        AwaitingVrf = 3
    }

    public enum MarketplaceItemId : byte
    {
        OfeCharm = 0,
        RustyShield = 1,
        EpicPotion = 2
    }

    [Serializable]
    public class PlayerProfileData
    {
        public string PlayerPubkey = string.Empty;
        public bool HasCompletedRun;
        public uint RunsStarted;
        public uint Victories;
        public uint Defeats;
        public int BestScore;
        public uint MonstersSlain;
        public ulong TotalGoldEarned;
        public ulong TotalGoldMinted;
        public ulong TotalGoldSpent;
        public uint TotalVrfRequests;
        public ulong TotalVrfCombatRolls;
        public byte[] LastVrfSeed = new byte[32];
    }

    [Serializable]
    public class LeaderboardEntryData
    {
        public string PlayerPubkey = string.Empty;
        public int Score;
        public ushort HpTenths;
        public bool Victory;
        public byte[] VrfSeedPrefix = new byte[8];
        public long Timestamp;

        public string ShortVrfHex()
        {
            if (VrfSeedPrefix == null || VrfSeedPrefix.Length < 4) return "0x0000";
            return $"0x{VrfSeedPrefix[0]:x2}{VrfSeedPrefix[1]:x2}{VrfSeedPrefix[2]:x2}{VrfSeedPrefix[3]:x2}";
        }
    }

    [Serializable]
    public class LeaderboardData
    {
        public byte EntryCount;
        public ulong TotalRunsCompleted;
        public List<LeaderboardEntryData> Entries = new List<LeaderboardEntryData>();
    }

    [Serializable]
    public struct CardData : IEquatable<CardData>
    {
        public byte CardId;
        public CardSuit Suit;
        public CardType Type;
        public int Rank;
        public string RankLabel;
        public string SuitName;
        public string DisplayName;
        public string ResourcePath;

        public bool IsMonster => Type == CardType.Monster;
        public bool IsWeapon => Type == CardType.Weapon;
        public bool IsPotion => Type == CardType.Potion;

        public bool Equals(CardData other) => CardId == other.CardId && Rank == other.Rank && Suit == other.Suit;
        public override bool Equals(object obj) => obj is CardData other && Equals(other);
        public override int GetHashCode() => CardId;
        public override string ToString() => $"{DisplayName} (#{CardId})";
    }

    public static class ScoundrelCardDecoder
    {
        public const int DeckSize = 44;

        public static CardData Decode(byte cardId)
        {
            if (cardId > 43)
            {
                throw new ArgumentOutOfRangeException(nameof(cardId), $"Invalid Scoundrel card ID {cardId}. Expected 0..43.");
            }

            CardSuit suit;
            CardType type;
            int rank;
            string suitName;

            if (cardId <= 12)
            {
                suit = CardSuit.Clubs;
                type = CardType.Monster;
                rank = cardId + 2; // 2..14
                suitName = "Clubs";
            }
            else if (cardId <= 25)
            {
                suit = CardSuit.Spades;
                type = CardType.Monster;
                rank = (cardId - 13) + 2; // 2..14
                suitName = "Spades";
            }
            else if (cardId <= 34)
            {
                suit = CardSuit.Diamonds;
                type = CardType.Weapon;
                rank = (cardId - 26) + 2; // 2..10
                suitName = "Diamonds";
            }
            else
            {
                suit = CardSuit.Hearts;
                type = CardType.Potion;
                rank = (cardId - 35) + 2; // 2..10
                suitName = "Hearts";
            }

            string rankLabel = GetRankLabel(rank);
            string rankFullName = GetRankFullName(rank);
            string resourcePath = $"Cards/card{suitName}{rankLabel}";

            return new CardData
            {
                CardId = cardId,
                Suit = suit,
                Type = type,
                Rank = rank,
                RankLabel = rankLabel,
                SuitName = suitName,
                DisplayName = $"{rankFullName} of {suitName}",
                ResourcePath = resourcePath
            };
        }

        public static string GetRankLabel(int rank)
        {
            switch (rank)
            {
                case 11: return "J";
                case 12: return "Q";
                case 13: return "K";
                case 14: return "A";
                default: return rank.ToString();
            }
        }

        public static string GetRankFullName(int rank)
        {
            switch (rank)
            {
                case 11: return "Jack";
                case 12: return "Queen";
                case 13: return "King";
                case 14: return "Ace";
                default: return rank.ToString();
            }
        }

        public static SpoilsTier EvaluateSpoilsTier(int threatScore)
        {
            if (threatScore >= 25) return SpoilsTier.High;
            if (threatScore >= 12) return SpoilsTier.Medium;
            return SpoilsTier.Low;
        }
    }

    public static class ScoundrelAssetPaths
    {
        public const string FontRobotoSlab = "Fonts/RobotoSlab-Regular SDF";

        public const string CardBackDungeon = "Cards/cardBack_dungeon";
        public const string CardBackDiscard = "Cards/cardBack_discard";

        public const string GoldCoinSingle = "Sprites/LootAndSkins/gold_coin_single";
        public const string GoldCoinStack = "Sprites/LootAndSkins/gold_coin_stack";
        public const string CardEpicPotion = "Sprites/LootAndSkins/card_epic_potion";
        public const string ItemOfeCharm = "Sprites/LootAndSkins/item_ofe_charm";
        public const string ItemRustyShield = "Sprites/LootAndSkins/item_rusty_shield";
        public const string SkinWeaponLegendary = "Sprites/LootAndSkins/skin_weapon_legendary";

        public const string VfxSlashStandard = "Sprites/VFX/vfx_slash_standard";
        public const string VfxSlashLegendary = "Sprites/VFX/vfx_slash_legendary";
        public const string UiShieldBadge = "Sprites/VFX/ui_shield_badge";

        public const string SfxCardDeal = "Audio/SFX/card_deal";
        public const string SfxCardHover = "Audio/SFX/card_hover";
        public const string SfxCardDiscard = "Audio/SFX/card_discard";
        public const string SfxWeaponObliterateStd = "Audio/SFX/weapon_obliterate_std";
        public const string SfxLegendaryImpact = "Audio/SFX/legendary_impact";
        public const string SfxLegendaryStinger = "Audio/SFX/legendary_stinger";
        public const string SfxWeaponParryClink = "Audio/SFX/weapon_parry_clink";
        public const string SfxPlayerTakeDamage = "Audio/SFX/player_take_damage";
        public const string SfxPotionDrink = "Audio/SFX/potion_drink";
        public const string SfxEpicPotionUse = "Audio/SFX/epic_potion_use";
        public const string SfxSpoilsFanfare = "Audio/SFX/spoils_fanfare";

        public const string BgmDungeonLoop = "Audio/Music/bgm_dungeon_loop";
        public const string BgmDungeonLoopFallback = "Audio/SFX/bgm_dungeon_loop";

        public const string HolographicShaderName = "Custom/HolographicFoilCard";
        public const string HolographicMaterialResource = "Materials/HolographicFoilCardMat";
    }

    public static class ScoundrelResources
    {
        private static TMP_FontAsset _cachedFont;
        private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>(64);
        private static readonly Dictionary<string, AudioClip> _audioCache = new Dictionary<string, AudioClip>(24);

        public static TMP_FontAsset LoadFont()
        {
            if (_cachedFont != null) return _cachedFont;
            _cachedFont = Resources.Load<TMP_FontAsset>(ScoundrelAssetPaths.FontRobotoSlab);
            if (_cachedFont == null)
            {
                _cachedFont = TMP_Settings.defaultFontAsset;
            }
            return _cachedFont;
        }

        public static void ApplyFont(TMP_Text textComponent)
        {
            if (textComponent == null) return;
            TMP_FontAsset font = LoadFont();
            if (font != null)
            {
                textComponent.font = font;
            }
        }

        public static Sprite LoadSprite(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath)) return null;
            if (_spriteCache.TryGetValue(resourcePath, out Sprite cached) && cached != null)
            {
                return cached;
            }

            Sprite sprite = Resources.Load<Sprite>(resourcePath);
            if (sprite == null)
            {
                Sprite[] subSprites = Resources.LoadAll<Sprite>(resourcePath);
                if (subSprites != null && subSprites.Length > 0)
                {
                    sprite = subSprites[0];
                }
            }

            if (sprite != null)
            {
                _spriteCache[resourcePath] = sprite;
            }
            return sprite;
        }

        public static Sprite LoadOfeCharmSprite()
        {
            Sprite s = LoadSprite(ScoundrelAssetPaths.ItemOfeCharm);
            return s != null ? s : LoadSprite(ScoundrelAssetPaths.CardEpicPotion);
        }

        public static Sprite LoadRustyShieldSprite()
        {
            Sprite s = LoadSprite(ScoundrelAssetPaths.ItemRustyShield);
            return s != null ? s : LoadSprite(ScoundrelAssetPaths.UiShieldBadge);
        }

        public static AudioClip LoadAudio(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath)) return null;
            if (_audioCache.TryGetValue(resourcePath, out AudioClip cached) && cached != null)
            {
                return cached;
            }

            AudioClip clip = Resources.Load<AudioClip>(resourcePath);
            if (clip == null && resourcePath == ScoundrelAssetPaths.BgmDungeonLoop)
            {
                clip = Resources.Load<AudioClip>(ScoundrelAssetPaths.BgmDungeonLoopFallback);
            }

            if (clip != null)
            {
                _audioCache[resourcePath] = clip;
            }
            return clip;
        }
    }

    [Serializable]
    public class DungeonRunState
    {
        public string PlayerPubkey = string.Empty;
        public string SessionSignerPubkey = string.Empty;

        public byte[] Deck = new byte[ScoundrelCardDecoder.DeckSize];
        public byte DeckHead;
        public byte DeckLen;

        // -1 represents Option<u8>::None; 0..43 represents Some(card_id)
        public int[] Room = new int[4] { -1, -1, -1, -1 };

        public byte CardsResolvedInRoom;
        public ushort HpTenths = 200;
        public ushort MaxHpTenths = 200;

        public byte EquippedWeaponRank;
        public int EquippedWeaponCardId = -1;
        public byte WeaponCeiling;

        public byte LegendaryForgedEdgeCharges;
        public bool HasLegendarySkin;

        public bool PotionUsedThisRoom;
        public ushort FirstPotionHealedTenths;
        public byte EpicPotionsInventory;
        public byte EpicPotionBuffRoomsLeft;

        public bool FledLastRoom;
        public byte RoomThreatAccumulator;
        public byte PendingSpoilsThreat;

        public const uint PriceOfeCharm = 60;
        public const uint PriceRustyShield = 45;
        public const uint PriceEpicPotion = 75;
        public const uint OfeCostGold = PriceOfeCharm;
        public const uint RustyShieldCostGold = PriceRustyShield;
        public const uint EpicPotionCostGold = PriceEpicPotion;

        public uint UnclaimedGold;
        public int Score;
        public RunStatus Status = RunStatus.Active;

        public byte[] VrfSeed = new byte[32];
        public ulong CombatRngNonce;
        public byte OfeInventory;
        public byte OfeBuffRoomsLeft;
        public byte RustyShieldInventory;
        public byte RustyShieldBuffRoomsLeft;

        // Local UI tracking for cascade stacking and discard pile count
        public List<CardData> StackedMonstersOnWeapon = new List<CardData>();
        public int DiscardCount;

        public float HpFloat => HpTenths / 10f;
        public float MaxHpFloat => MaxHpTenths / 10f;
        public float HpNormalized => MaxHpTenths > 0 ? Mathf.Clamp01((float)HpTenths / MaxHpTenths) : 0f;

        public string FormatHpText()
        {
            int whole = HpTenths / 10;
            int tenths = HpTenths % 10;
            int maxWhole = MaxHpTenths / 10;
            return $"{whole}.{tenths} / {maxWhole} HP";
        }

        public bool HasNonZeroVrfSeed()
        {
            if (VrfSeed == null || VrfSeed.Length < 32) return false;
            for (int i = 0; i < 32; i++)
            {
                if (VrfSeed[i] != 0) return true;
            }
            return false;
        }

        public string FormatVrfSeedShort()
        {
            if (!HasNonZeroVrfSeed()) return "Pending VRF";
            return $"0x{VrfSeed[0]:x2}{VrfSeed[1]:x2}{VrfSeed[2]:x2}{VrfSeed[3]:x2}..{VrfSeed[28]:x2}{VrfSeed[29]:x2}{VrfSeed[30]:x2}{VrfSeed[31]:x2}";
        }

        public static uint GetItemPrice(MarketplaceItemId item)
        {
            switch (item)
            {
                case MarketplaceItemId.OfeCharm: return PriceOfeCharm;
                case MarketplaceItemId.RustyShield: return PriceRustyShield;
                case MarketplaceItemId.EpicPotion: return PriceEpicPotion;
                default: return 999999;
            }
        }

        public bool CanAffordItem(MarketplaceItemId item, ulong mintedAtaGold = 0UL)
        {
            if (Status != RunStatus.Active) return false;
            ulong totalAvailable = (ulong)UnclaimedGold + mintedAtaGold;
            return totalAvailable >= GetItemPrice(item);
        }

        public WeaponSkinTier ActiveWeaponSkinTier
        {
            get
            {
                if (HasLegendarySkin) return WeaponSkinTier.Legendary;
                if (LegendaryForgedEdgeCharges > 0 || EpicPotionBuffRoomsLeft > 0) return WeaponSkinTier.EpicFoil;
                return WeaponSkinTier.Standard;
            }
        }

        public bool IsRoomEmpty()
        {
            for (int i = 0; i < 4; i++)
            {
                if (Room[i] >= 0) return false;
            }
            return true;
        }

        public int ActiveCardsInRoomCount()
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
            {
                if (Room[i] >= 0) count++;
            }
            return count;
        }

        public bool CanAvoidRoom =>
            Status == RunStatus.Active &&
            CardsResolvedInRoom == 0 &&
            !FledLastRoom &&
            PendingSpoilsThreat == 0 &&
            ActiveCardsInRoomCount() == 4;

        public bool CanUseEpicPotion =>
            Status == RunStatus.Active &&
            EpicPotionsInventory > 0 &&
            EpicPotionBuffRoomsLeft == 0;

        public bool CanBuyOfe =>
            Status == RunStatus.Active &&
            UnclaimedGold >= PriceOfeCharm;

        public bool CanBuyRustyShield =>
            Status == RunStatus.Active &&
            UnclaimedGold >= PriceRustyShield;

        public bool CanUseOfe =>
            Status == RunStatus.Active &&
            OfeInventory > 0 &&
            OfeBuffRoomsLeft == 0;

        public bool CanUseRustyShield =>
            Status == RunStatus.Active &&
            RustyShieldInventory > 0 &&
            RustyShieldBuffRoomsLeft == 0;

        public bool CanUseWeaponAgainst(int monsterRank)
        {
            if (EquippedWeaponRank == 0) return false;
            return monsterRank <= WeaponCeiling || LegendaryForgedEdgeCharges > 0;
        }

        public bool WouldConsumeForgedEdge(int monsterRank)
        {
            return EquippedWeaponRank > 0 && monsterRank > WeaponCeiling && LegendaryForgedEdgeCharges > 0;
        }
    }
}
