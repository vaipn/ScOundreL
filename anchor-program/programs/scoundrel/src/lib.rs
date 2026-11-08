use anchor_lang::prelude::*;
use anchor_lang::solana_program::instruction::{AccountMeta, Instruction};
use anchor_lang::solana_program::program::{invoke, invoke_signed};
use ephemeral_vrf_sdk::anchor::vrf;
use ephemeral_vrf_sdk::instructions::{create_request_randomness_ix, RequestRandomnessParams};
use ephemeral_vrf_sdk::types::SerializableAccountMeta;

declare_id!("7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx");

pub const SPL_TOKEN_PROGRAM_ID: Pubkey = pubkey!("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA");
pub const ASSOCIATED_TOKEN_PROGRAM_ID: Pubkey =
    pubkey!("ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL");
pub const GOLD_MINT_SEED: &[u8] = b"scoundrel_gold_mint";
pub const PROFILE_SEED: &[u8] = b"profile";
pub const LEADERBOARD_SEED: &[u8] = b"leaderboard";

pub const SPL_MINT_LEN: usize = 82;
pub const SPL_TOKEN_ACCOUNT_MIN_LEN: usize = 165;
pub const SPL_GOLD_DECIMALS: u8 = 0;

pub const DECK_SIZE: usize = 44;
pub const ROOM_SIZE: usize = 4;
pub const MAX_HP_TENTHS: u16 = 200;
pub const LEGENDARY_WEAPONS_ENABLED: bool = false;
pub const LEADERBOARD_SIZE: usize = 10;

pub const RUN_STATUS_ACTIVE: u8 = 0;
pub const RUN_STATUS_VICTORY: u8 = 1;
pub const RUN_STATUS_DEFEAT: u8 = 2;
pub const RUN_STATUS_AWAITING_VRF: u8 = 3;

pub const ITEM_OFE_CHARM: u8 = 0;
pub const ITEM_RUSTY_SHIELD: u8 = 1;
pub const ITEM_EPIC_POTION: u8 = 2;

pub const PRICE_OFE_CHARM: u32 = 60;
pub const PRICE_RUSTY_SHIELD: u32 = 45;
pub const PRICE_EPIC_POTION: u32 = 75;

/// Covers ~100 session-signed turns, first-ever Gold mint / ATA creation if needed, and Leaderboard init.
pub const SESSION_SIGNER_MIN_LAMPORTS: u64 = 10_000_000;
/// Bounded SPL Gold delegate cap per run for the ephemeral session key.
pub const MAX_SESSION_GOLD_ALLOWANCE: u64 = 1_000;

fn item_price(item_id: u8) -> Result<u32> {
    match item_id {
        ITEM_OFE_CHARM => Ok(PRICE_OFE_CHARM),
        ITEM_RUSTY_SHIELD => Ok(PRICE_RUSTY_SHIELD),
        ITEM_EPIC_POTION => Ok(PRICE_EPIC_POTION),
        _ => err!(ScoundrelError::InvalidMarketplaceItem),
    }
}

fn expected_gold_ata(player: &Pubkey, gold_mint: &Pubkey) -> Pubkey {
    Pubkey::find_program_address(
        &[
            player.as_ref(),
            SPL_TOKEN_PROGRAM_ID.as_ref(),
            gold_mint.as_ref(),
        ],
        &ASSOCIATED_TOKEN_PROGRAM_ID,
    )
    .0
}

/// Validates SPL Token account owner, byte length, mint, and token owner, returning `(amount, delegate, delegated_amount)`.
fn read_validated_token_account(
    token_account: &AccountInfo,
    expected_mint: &Pubkey,
    expected_owner: &Pubkey,
) -> Result<(u64, Option<Pubkey>, u64)> {
    if token_account.data_is_empty() {
        return Ok((0, None, 0));
    }
    require_keys_eq!(
        *token_account.owner,
        SPL_TOKEN_PROGRAM_ID,
        ScoundrelError::InvalidTokenProgram
    );
    let data = token_account.try_borrow_data()?;
    require!(
        data.len() >= SPL_TOKEN_ACCOUNT_MIN_LEN,
        ScoundrelError::InvalidPlayerGoldAta
    );

    let mint = Pubkey::new_from_array(data[0..32].try_into().unwrap());
    let owner = Pubkey::new_from_array(data[32..64].try_into().unwrap());
    require_keys_eq!(mint, *expected_mint, ScoundrelError::InvalidPlayerGoldAta);
    require_keys_eq!(owner, *expected_owner, ScoundrelError::InvalidPlayerGoldAta);

    let amount = u64::from_le_bytes(data[64..72].try_into().unwrap());
    let delegate_tag = u32::from_le_bytes(data[72..76].try_into().unwrap());
    let delegate = if delegate_tag == 1 {
        Some(Pubkey::new_from_array(data[76..108].try_into().unwrap()))
    } else {
        None
    };
    let delegated_amount = u64::from_le_bytes(data[121..129].try_into().unwrap());

    Ok((amount, delegate, delegated_amount))
}

#[allow(clippy::too_many_arguments)]
fn ensure_gold_mint_and_ata<'info>(
    payer: &AccountInfo<'info>,
    player: &AccountInfo<'info>,
    gold_mint: &AccountInfo<'info>,
    mint_bump: u8,
    player_gold_ata: &AccountInfo<'info>,
    token_program: &AccountInfo<'info>,
    system_program: &AccountInfo<'info>,
) -> Result<()> {
    let expected_ata = expected_gold_ata(player.key, gold_mint.key);
    require_keys_eq!(
        *player_gold_ata.key,
        expected_ata,
        ScoundrelError::InvalidPlayerGoldAta
    );

    let mint_seeds: &[&[u8]] = &[GOLD_MINT_SEED, &[mint_bump]];

    if gold_mint.data_is_empty() {
        let rent_lamports = Rent::get()?.minimum_balance(SPL_MINT_LEN);
        anchor_lang::system_program::create_account(
            CpiContext::new_with_signer(
                *system_program.key,
                anchor_lang::system_program::CreateAccount {
                    from: payer.clone(),
                    to: gold_mint.clone(),
                },
                &[mint_seeds],
            ),
            rent_lamports,
            SPL_MINT_LEN as u64,
            &SPL_TOKEN_PROGRAM_ID,
        )?;

        // SPL Token InitializeMint2 (tag = 20, decimals = 0, mint_authority = gold_mint, freeze_authority = None)
        let mut init_mint_data = Vec::with_capacity(35);
        init_mint_data.push(20u8);
        init_mint_data.push(SPL_GOLD_DECIMALS);
        init_mint_data.extend_from_slice(gold_mint.key.as_ref());
        init_mint_data.push(0u8);

        let init_mint_ix = Instruction {
            program_id: SPL_TOKEN_PROGRAM_ID,
            accounts: vec![AccountMeta::new(*gold_mint.key, false)],
            data: init_mint_data,
        };
        invoke(&init_mint_ix, &[gold_mint.clone()])?;
    } else {
        require_keys_eq!(
            *gold_mint.owner,
            SPL_TOKEN_PROGRAM_ID,
            ScoundrelError::InvalidTokenProgram
        );
    }

    if player_gold_ata.data_is_empty() {
        // Associated Token Account CreateIdempotent (discriminator = 1u8)
        let create_ata_ix = Instruction {
            program_id: ASSOCIATED_TOKEN_PROGRAM_ID,
            accounts: vec![
                AccountMeta::new(*payer.key, true),
                AccountMeta::new(*player_gold_ata.key, false),
                AccountMeta::new_readonly(*player.key, false),
                AccountMeta::new_readonly(*gold_mint.key, false),
                AccountMeta::new_readonly(*system_program.key, false),
                AccountMeta::new_readonly(SPL_TOKEN_PROGRAM_ID, false),
            ],
            data: vec![1u8],
        };
        invoke(
            &create_ata_ix,
            &[
                payer.clone(),
                player_gold_ata.clone(),
                player.clone(),
                gold_mint.clone(),
                system_program.clone(),
                token_program.clone(),
            ],
        )?;
    } else {
        let _ = read_validated_token_account(player_gold_ata, gold_mint.key, player.key)?;
    }

    Ok(())
}

fn approve_session_delegate<'info>(
    player_signer: &AccountInfo<'info>,
    player_gold_ata: &AccountInfo<'info>,
    session_signer: &AccountInfo<'info>,
    allowance: u64,
) -> Result<()> {
    let bounded = allowance.min(MAX_SESSION_GOLD_ALLOWANCE);
    // SPL Token Approve (tag = 4, amount: u64 LE)
    let mut approve_data = Vec::with_capacity(9);
    approve_data.push(4u8);
    approve_data.extend_from_slice(&bounded.to_le_bytes());

    let approve_ix = Instruction {
        program_id: SPL_TOKEN_PROGRAM_ID,
        accounts: vec![
            AccountMeta::new(*player_gold_ata.key, false),
            AccountMeta::new_readonly(*session_signer.key, false),
            AccountMeta::new_readonly(*player_signer.key, true),
        ],
        data: approve_data,
    };
    invoke(
        &approve_ix,
        &[
            player_gold_ata.clone(),
            session_signer.clone(),
            player_signer.clone(),
        ],
    )?;
    Ok(())
}

fn sync_profile_identity(profile: &mut PlayerProfile, player: Pubkey, bump: u8, vrf_seed: &[u8; 32]) {
    if profile.player == Pubkey::default() {
        profile.player = player;
        profile.bump = bump;
    }
    if *vrf_seed != [0u8; 32] {
        profile.last_vrf_seed = *vrf_seed;
    }
}

fn record_run_completion(
    profile: &mut PlayerProfile,
    leaderboard: &mut Leaderboard,
    leaderboard_bump: u8,
    run: &DungeonRun,
    victory: bool,
    timestamp: i64,
) {
    if victory {
        profile.victories = profile.victories.saturating_add(1);
    } else {
        profile.defeats = profile.defeats.saturating_add(1);
    }

    if !profile.has_completed_run || run.score > profile.best_score {
        profile.best_score = run.score;
        profile.has_completed_run = true;
    }

    leaderboard.bump = leaderboard_bump;
    leaderboard.total_runs_completed = leaderboard.total_runs_completed.saturating_add(1);

    let mut vrf_prefix = [0u8; 8];
    vrf_prefix.copy_from_slice(&run.vrf_seed[0..8]);

    let candidate = LeaderboardEntry {
        player: run.player,
        score: run.score,
        hp_tenths: run.hp_tenths,
        victory,
        vrf_seed_prefix: vrf_prefix,
        timestamp,
    };

    let current_len = (leaderboard.entry_count as usize).min(LEADERBOARD_SIZE);
    let mut insert_idx: Option<usize> = None;

    for idx in 0..current_len {
        let existing = &leaderboard.entries[idx];
        let candidate_key = (candidate.victory as u8, candidate.score, candidate.hp_tenths);
        let existing_key = (existing.victory as u8, existing.score, existing.hp_tenths);
        if candidate_key > existing_key {
            insert_idx = Some(idx);
            break;
        }
    }

    if insert_idx.is_none() && current_len < LEADERBOARD_SIZE {
        insert_idx = Some(current_len);
    }

    if let Some(pos) = insert_idx {
        let new_len = (current_len + 1).min(LEADERBOARD_SIZE);
        for shift in (pos + 1..new_len).rev() {
            leaderboard.entries[shift] = leaderboard.entries[shift - 1];
        }
        leaderboard.entries[pos] = candidate;
        leaderboard.entry_count = new_len as u8;
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum CardCategory {
    Monster,
    Weapon,
    Potion,
}

pub fn decode_card(card_id: u8) -> Result<(CardCategory, u8)> {
    match card_id {
        0..=12 => Ok((CardCategory::Monster, card_id + 2)),
        13..=25 => Ok((CardCategory::Monster, (card_id - 13) + 2)),
        26..=34 => Ok((CardCategory::Weapon, (card_id - 26) + 2)),
        35..=43 => Ok((CardCategory::Potion, (card_id - 35) + 2)),
        _ => err!(ScoundrelError::InvalidCardId),
    }
}

fn splitmix64(state: &mut u64) -> u64 {
    *state = state.wrapping_add(0x9E3779B97F4A7C15);
    let mut z = *state;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

pub fn shuffle_deck_from_vrf(randomness: &[u8; 32]) -> [u8; DECK_SIZE] {
    let mut deck = [0u8; DECK_SIZE];
    for (i, card) in deck.iter_mut().enumerate() {
        *card = i as u8;
    }

    let s0 = u64::from_le_bytes(randomness[0..8].try_into().unwrap());
    let s1 = u64::from_le_bytes(randomness[8..16].try_into().unwrap());
    let s2 = u64::from_le_bytes(randomness[16..24].try_into().unwrap());
    let s3 = u64::from_le_bytes(randomness[24..32].try_into().unwrap());
    let mut rng_state = s0
        ^ s1.wrapping_mul(0x9E3779B97F4A7C15)
        ^ s2.wrapping_mul(0xBF58476D1CE4E5B9)
        ^ s3.wrapping_mul(0x94D049BB133111EB);

    for i in (1..DECK_SIZE).rev() {
        let rand_val = splitmix64(&mut rng_state);
        let j = (rand_val as usize) % (i + 1);
        deck.swap(i, j);
    }
    deck
}

fn is_authorized_vrf_callback_signer(signer: &Pubkey, _run: &DungeonRun) -> bool {
    let is_vrf_oracle = *signer == ephemeral_vrf_sdk::consts::VRF_PROGRAM_IDENTITY
        || *signer == ephemeral_vrf_sdk::consts::scoped_vrf_identity(&crate::ID);
    #[cfg(feature = "localnet-vrf-fallback")]
    {
        if is_vrf_oracle || *signer == _run.player || *signer == _run.session_signer {
            return true;
        }
    }
    is_vrf_oracle
}

fn next_vrf_combat_roll(run: &mut DungeonRun, slot: u64) -> u64 {
    run.combat_rng_nonce = run.combat_rng_nonce.wrapping_add(1);
    let byte_offset = ((run.combat_rng_nonce as usize).wrapping_mul(8)) % 32;
    let seed_chunk = u64::from_le_bytes(
        run.vrf_seed[byte_offset..byte_offset + 8]
            .try_into()
            .unwrap(),
    );
    let mut state = seed_chunk
        ^ run.combat_rng_nonce.wrapping_mul(0x9E3779B97F4A7C15)
        ^ slot.wrapping_mul(0xBF58476D1CE4E5B9)
        ^ (run.hp_tenths as u64).wrapping_mul(0x94D049BB133111EB);
    splitmix64(&mut state)
}

fn draw_card(run: &mut DungeonRun) -> Option<u8> {
    if run.deck_len == 0 {
        return None;
    }
    let idx = (run.deck_head as usize) % DECK_SIZE;
    let card = run.deck[idx];
    run.deck_head = ((idx + 1) % DECK_SIZE) as u8;
    run.deck_len -= 1;
    Some(card)
}

fn push_card_bottom(run: &mut DungeonRun, card: u8) -> Result<()> {
    require!((run.deck_len as usize) < DECK_SIZE, ScoundrelError::DeckOverflow);
    let tail = ((run.deck_head as usize) + (run.deck_len as usize)) % DECK_SIZE;
    run.deck[tail] = card;
    run.deck_len += 1;
    Ok(())
}

fn compute_defeat_score(run: &DungeonRun) -> Result<i32> {
    let mut remaining_monster_sum: i32 = 0;

    for slot in run.room.iter() {
        if let Some(card_id) = slot {
            let (cat, rank) = decode_card(*card_id)?;
            if cat == CardCategory::Monster {
                remaining_monster_sum = remaining_monster_sum.saturating_add(rank as i32);
            }
        }
    }

    let head = run.deck_head as usize;
    let len = run.deck_len as usize;
    for offset in 0..len {
        let idx = (head + offset) % DECK_SIZE;
        let card_id = run.deck[idx];
        let (cat, rank) = decode_card(card_id)?;
        if cat == CardCategory::Monster {
            remaining_monster_sum = remaining_monster_sum.saturating_add(rank as i32);
        }
    }

    Ok(-remaining_monster_sum)
}

#[program]
pub mod scoundrel {
    use super::*;

    pub fn start_run(ctx: Context<StartRun>, client_seed: u8) -> Result<()> {
        let current_session_lamports = ctx.accounts.session_signer.lamports();
        if ctx.accounts.session_signer.key() != ctx.accounts.player.key()
            && current_session_lamports < SESSION_SIGNER_MIN_LAMPORTS
        {
            let top_up = SESSION_SIGNER_MIN_LAMPORTS.saturating_sub(current_session_lamports);
            anchor_lang::system_program::transfer(
                CpiContext::new(
                    ctx.accounts.system_program.key(),
                    anchor_lang::system_program::Transfer {
                        from: ctx.accounts.player.to_account_info(),
                        to: ctx.accounts.session_signer.to_account_info(),
                    },
                ),
                top_up,
            )?;
        }

        let player_key = ctx.accounts.player.key();
        let dungeon_run_key = ctx.accounts.dungeon_run.key();

        let profile = &mut ctx.accounts.player_profile;
        sync_profile_identity(
            profile,
            player_key,
            ctx.bumps.player_profile,
            &[0u8; 32],
        );
        profile.runs_started = profile.runs_started.saturating_add(1);
        profile.total_vrf_requests = profile.total_vrf_requests.saturating_add(1);

        let run = &mut ctx.accounts.dungeon_run;
        run.player = player_key;
        run.session_signer = ctx.accounts.session_signer.key();
        run.bump = ctx.bumps.dungeon_run;

        let mut deck = [0u8; DECK_SIZE];
        for (i, card) in deck.iter_mut().enumerate() {
            *card = i as u8;
        }

        run.deck = deck;
        run.deck_head = 0;
        run.deck_len = DECK_SIZE as u8;
        run.room = [None; ROOM_SIZE];
        run.cards_resolved_in_room = 0;
        run.hp_tenths = MAX_HP_TENTHS;
        run.max_hp_tenths = MAX_HP_TENTHS;
        run.equipped_weapon_rank = 0;
        run.weapon_ceiling = 0;
        run.has_legendary_skin = false;
        run.legendary_forged_edge_charges = 0;
        run.potion_used_this_room = false;
        run.first_potion_healed_tenths = 0;
        run.epic_potions_inventory = 0;
        run.epic_potion_buff_rooms_left = 0;
        run.fled_last_room = false;
        run.room_threat_accumulator = 0;
        run.pending_spoils_threat = 0;
        run.unclaimed_gold = 0;
        run.score = 0;
        run.run_status = RUN_STATUS_AWAITING_VRF;
        run.vrf_seed = [0u8; 32];
        run.combat_rng_nonce = 0;
        run.ofe_inventory = 0;
        run.ofe_buff_rooms_left = 0;
        run.rusty_shield_inventory = 0;
        run.rusty_shield_buff_rooms_left = 0;

        let ix = create_request_randomness_ix(RequestRandomnessParams {
            payer: player_key,
            oracle_queue: ctx.accounts.oracle_queue.key(),
            callback_program_id: ID,
            callback_discriminator: instruction::CallbackStartRun::DISCRIMINATOR.to_vec(),
            caller_seed: [client_seed; 32],
            accounts_metas: Some(vec![SerializableAccountMeta {
                pubkey: dungeon_run_key,
                is_signer: false,
                is_writable: true,
            }]),
            ..Default::default()
        });

        #[cfg(feature = "localnet-vrf-fallback")]
        {
            if ctx.accounts.vrf_program.executable && !ctx.accounts.oracle_queue.data_is_empty() {
                ctx.accounts
                    .invoke_signed_vrf(&ctx.accounts.player.to_account_info(), &ix)?;
            } else {
                msg!("VRF oracle queue uninitialized on local validator; awaiting callback_start_run");
            }
        }
        #[cfg(not(feature = "localnet-vrf-fallback"))]
        {
            ctx.accounts
                .invoke_signed_vrf(&ctx.accounts.player.to_account_info(), &ix)?;
        }

        Ok(())
    }

    /// Initializes the canonical SPL Gold Mint + Player ATA (if needed) and approves a bounded
    /// SPL Token delegate allowance for `dungeon_run.session_signer` so Merchant burns work popup-free.
    pub fn prepare_session_vault(
        ctx: Context<PrepareSessionVault>,
        allowance: u64,
    ) -> Result<()> {
        ensure_gold_mint_and_ata(
            &ctx.accounts.player.to_account_info(),
            &ctx.accounts.player.to_account_info(),
            &ctx.accounts.gold_mint.to_account_info(),
            ctx.bumps.gold_mint,
            &ctx.accounts.player_gold_ata.to_account_info(),
            &ctx.accounts.token_program.to_account_info(),
            &ctx.accounts.system_program.to_account_info(),
        )?;

        if ctx.accounts.session_signer.key() != ctx.accounts.player.key() && allowance > 0 {
            approve_session_delegate(
                &ctx.accounts.player.to_account_info(),
                &ctx.accounts.player_gold_ata.to_account_info(),
                &ctx.accounts.session_signer.to_account_info(),
                allowance,
            )?;
        }

        Ok(())
    }

    pub fn callback_start_run(
        ctx: Context<CallbackStartRun>,
        randomness: [u8; 32],
    ) -> Result<()> {
        let dungeon_run_key = ctx.accounts.dungeon_run.key();
        let run = &mut ctx.accounts.dungeon_run;
        require!(
            run.run_status == RUN_STATUS_AWAITING_VRF,
            ScoundrelError::NotAwaitingVrf
        );

        run.vrf_seed = randomness;
        run.combat_rng_nonce = 0;
        run.deck = shuffle_deck_from_vrf(&randomness);
        run.deck_head = 0;
        run.deck_len = DECK_SIZE as u8;
        run.room = [None; ROOM_SIZE];

        let mut initial_room = [255u8; ROOM_SIZE];
        for (slot, card_out) in initial_room.iter_mut().enumerate() {
            let drawn = draw_card(run);
            run.room[slot] = drawn;
            if let Some(c) = drawn {
                *card_out = c;
            }
        }

        run.run_status = RUN_STATUS_ACTIVE;

        emit!(VrfRunStarted {
            player: run.player,
            dungeon_run: dungeon_run_key,
            vrf_seed: randomness,
            initial_room,
        });

        Ok(())
    }

    pub fn buy_item(ctx: Context<BuyItem>, item_id: u8) -> Result<()> {
        let run = &mut ctx.accounts.dungeon_run;
        require!(
            run.run_status == RUN_STATUS_ACTIVE,
            ScoundrelError::RunNotActive
        );

        let price = item_price(item_id)?;
        let expected_ata = expected_gold_ata(&ctx.accounts.player.key(), &ctx.accounts.gold_mint.key());
        require_keys_eq!(
            ctx.accounts.player_gold_ata.key(),
            expected_ata,
            ScoundrelError::InvalidPlayerGoldAta
        );

        let paid_unclaimed = run.unclaimed_gold.min(price);
        let deficit_spl = (price - paid_unclaimed) as u64;

        if deficit_spl > 0 {
            require!(
                !ctx.accounts.gold_mint.data_is_empty()
                    && *ctx.accounts.gold_mint.owner == SPL_TOKEN_PROGRAM_ID,
                ScoundrelError::InsufficientGold
            );

            let (ata_balance, delegate_opt, delegated_amount) = read_validated_token_account(
                &ctx.accounts.player_gold_ata.to_account_info(),
                &ctx.accounts.gold_mint.key(),
                &ctx.accounts.player.key(),
            )?;
            require!(ata_balance >= deficit_spl, ScoundrelError::InsufficientGold);

            // Enforce dual-layer authority: session key also requires bounded SPL Token delegate approval.
            if ctx.accounts.signer.key() != ctx.accounts.player.key() {
                let approved = delegate_opt == Some(ctx.accounts.signer.key())
                    && delegated_amount >= deficit_spl;
                require!(approved, ScoundrelError::MissingTokenDelegateApproval);
            }
        }

        // Checks-Effects-Interactions: mutate run + profile state prior to SPL Token Burn CPI
        run.unclaimed_gold = run.unclaimed_gold.saturating_sub(paid_unclaimed);
        match item_id {
            ITEM_OFE_CHARM => {
                run.ofe_inventory = run.ofe_inventory.saturating_add(1);
            }
            ITEM_RUSTY_SHIELD => {
                run.rusty_shield_inventory = run.rusty_shield_inventory.saturating_add(1);
            }
            ITEM_EPIC_POTION => {
                run.epic_potions_inventory = run.epic_potions_inventory.saturating_add(1);
            }
            _ => return err!(ScoundrelError::InvalidMarketplaceItem),
        }

        let profile = &mut ctx.accounts.player_profile;
        sync_profile_identity(
            profile,
            ctx.accounts.player.key(),
            ctx.bumps.player_profile,
            &run.vrf_seed,
        );
        profile.total_gold_spent = profile.total_gold_spent.saturating_add(price as u64);

        if deficit_spl > 0 {
            // SPL Token Burn (tag = 8, amount: u64 LE)
            let mut burn_data = Vec::with_capacity(9);
            burn_data.push(8u8);
            burn_data.extend_from_slice(&deficit_spl.to_le_bytes());

            let burn_ix = Instruction {
                program_id: SPL_TOKEN_PROGRAM_ID,
                accounts: vec![
                    AccountMeta::new(ctx.accounts.player_gold_ata.key(), false),
                    AccountMeta::new(ctx.accounts.gold_mint.key(), false),
                    AccountMeta::new_readonly(ctx.accounts.signer.key(), true),
                ],
                data: burn_data,
            };
            invoke(
                &burn_ix,
                &[
                    ctx.accounts.player_gold_ata.to_account_info(),
                    ctx.accounts.gold_mint.to_account_info(),
                    ctx.accounts.signer.to_account_info(),
                ],
            )?;
        }

        emit!(MarketplaceItemPurchased {
            player: ctx.accounts.player.key(),
            item_id,
            paid_unclaimed_gold: paid_unclaimed,
            burned_spl_gold: deficit_spl,
        });

        Ok(())
    }

    pub fn use_marketplace_item(ctx: Context<PlayerTurn>, item_id: u8) -> Result<()> {
        let run = &mut ctx.accounts.dungeon_run;
        require!(
            run.run_status == RUN_STATUS_ACTIVE,
            ScoundrelError::RunNotActive
        );

        match item_id {
            ITEM_OFE_CHARM => {
                require!(
                    run.ofe_inventory > 0,
                    ScoundrelError::NoMarketplaceItemAvailable
                );
                require!(
                    run.ofe_buff_rooms_left == 0,
                    ScoundrelError::MarketplaceBuffAlreadyActive
                );
                run.ofe_inventory -= 1;
                run.ofe_buff_rooms_left = 3;
            }
            ITEM_RUSTY_SHIELD => {
                require!(
                    run.rusty_shield_inventory > 0,
                    ScoundrelError::NoMarketplaceItemAvailable
                );
                require!(
                    run.rusty_shield_buff_rooms_left == 0,
                    ScoundrelError::MarketplaceBuffAlreadyActive
                );
                run.rusty_shield_inventory -= 1;
                run.rusty_shield_buff_rooms_left = 3;
            }
            ITEM_EPIC_POTION => {
                require!(
                    run.epic_potions_inventory > 0,
                    ScoundrelError::NoEpicPotionsAvailable
                );
                require!(
                    run.epic_potion_buff_rooms_left == 0,
                    ScoundrelError::EpicPotionAlreadyActive
                );
                run.epic_potions_inventory -= 1;
                run.epic_potion_buff_rooms_left = 3;
            }
            _ => return err!(ScoundrelError::InvalidMarketplaceItem),
        }

        Ok(())
    }

    pub fn flee_room(ctx: Context<PlayerTurn>) -> Result<()> {
        let run = &mut ctx.accounts.dungeon_run;
        require!(
            run.run_status == RUN_STATUS_ACTIVE,
            ScoundrelError::RunNotActive
        );
        require!(
            run.cards_resolved_in_room == 0,
            ScoundrelError::CannotFleeMidRoom
        );
        require!(
            !run.fled_last_room,
            ScoundrelError::CannotFleeConsecutiveRooms
        );
        require!(
            run.pending_spoils_threat == 0,
            ScoundrelError::PendingSpoilsMustBeClaimed
        );

        for slot in 0..ROOM_SIZE {
            if let Some(card_id) = run.room[slot].take() {
                push_card_bottom(run, card_id)?;
            }
        }

        for slot in 0..ROOM_SIZE {
            run.room[slot] = draw_card(run);
        }

        run.fled_last_room = true;
        Ok(())
    }

    pub fn use_epic_potion(ctx: Context<PlayerTurn>) -> Result<()> {
        let run = &mut ctx.accounts.dungeon_run;
        require!(
            run.run_status == RUN_STATUS_ACTIVE,
            ScoundrelError::RunNotActive
        );
        require!(
            run.epic_potions_inventory > 0,
            ScoundrelError::NoEpicPotionsAvailable
        );
        require!(
            run.epic_potion_buff_rooms_left == 0,
            ScoundrelError::EpicPotionAlreadyActive
        );

        run.epic_potions_inventory -= 1;
        run.epic_potion_buff_rooms_left = 3;
        Ok(())
    }

    pub fn resolve_card(
        ctx: Context<ResolveCard>,
        slot_index: u8,
        use_weapon: bool,
    ) -> Result<()> {
        let clock = Clock::get()?;
        let run = &mut ctx.accounts.dungeon_run;
        require!(
            run.run_status == RUN_STATUS_ACTIVE,
            ScoundrelError::RunNotActive
        );
        require!(
            run.pending_spoils_threat == 0,
            ScoundrelError::PendingSpoilsMustBeClaimed
        );
        require!(
            (slot_index as usize) < ROOM_SIZE,
            ScoundrelError::InvalidSlotIndex
        );

        let profile = &mut ctx.accounts.player_profile;
        sync_profile_identity(
            profile,
            ctx.accounts.player.key(),
            ctx.bumps.player_profile,
            &run.vrf_seed,
        );
        let prev_nonce = run.combat_rng_nonce;

        let slot = slot_index as usize;
        let card_id = run.room[slot].take().ok_or(ScoundrelError::EmptyRoomSlot)?;
        let (category, card_rank) = decode_card(card_id)?;

        match category {
            CardCategory::Monster => {
                let monster_rank = card_rank;
                let mut damage_tenths: u16;

                if use_weapon {
                    let prev_ceiling = run.weapon_ceiling;
                    require!(
                        run.equipped_weapon_rank > 0,
                        ScoundrelError::NoWeaponEquipped
                    );
                    require!(
                        monster_rank <= prev_ceiling || run.legendary_forged_edge_charges > 0,
                        ScoundrelError::WeaponCeilingExceeded
                    );

                    damage_tenths =
                        (monster_rank.saturating_sub(run.equipped_weapon_rank) as u16) * 10;

                    if monster_rank > prev_ceiling {
                        run.legendary_forged_edge_charges =
                            run.legendary_forged_edge_charges.saturating_sub(1);
                    }

                    let close_to_ceiling_high_rank =
                        prev_ceiling.saturating_sub(monster_rank) <= 2 && monster_rank >= 8;
                    let took_chip_damage = monster_rank > run.equipped_weapon_rank;
                    let bonus_threat: u8 =
                        if close_to_ceiling_high_rank || took_chip_damage {
                            3
                        } else {
                            0
                        };

                    run.room_threat_accumulator = run
                        .room_threat_accumulator
                        .saturating_add(monster_rank)
                        .saturating_add(bonus_threat);

                    run.weapon_ceiling = monster_rank;
                } else {
                    damage_tenths = (monster_rank as u16) * 10;
                    run.room_threat_accumulator = run
                        .room_threat_accumulator
                        .saturating_add(monster_rank.saturating_add(5));
                }

                // Ofe Dodge Check (50% VRF roll when fighting barehanded)
                if !use_weapon && run.ofe_buff_rooms_left > 0 && damage_tenths > 0 {
                    let roll = next_vrf_combat_roll(run, clock.slot);
                    if roll % 2 == 0 {
                        damage_tenths = 0;
                        msg!("OfeDodged");
                        emit!(OfeDodged {
                            player: run.player,
                            monster_rank,
                            nonce: run.combat_rng_nonce,
                        });
                    }
                }

                // Rusty Shield Block Check (50% VRF roll to block 50% of incoming damage)
                if damage_tenths > 0 && run.rusty_shield_buff_rooms_left > 0 {
                    let roll = next_vrf_combat_roll(run, clock.slot);
                    if roll % 2 == 0 {
                        damage_tenths /= 2;
                        msg!("RustyShieldBlocked");
                        emit!(RustyShieldBlocked {
                            player: run.player,
                            monster_rank,
                            damage_tenths,
                            nonce: run.combat_rng_nonce,
                        });
                    }
                }

                let rolls_used = run.combat_rng_nonce.saturating_sub(prev_nonce);
                profile.total_vrf_combat_rolls =
                    profile.total_vrf_combat_rolls.saturating_add(rolls_used);

                run.hp_tenths = run.hp_tenths.saturating_sub(damage_tenths);
                if run.hp_tenths == 0 {
                    run.run_status = RUN_STATUS_DEFEAT;
                    run.score = compute_defeat_score(run)?;
                    record_run_completion(
                        profile,
                        &mut ctx.accounts.leaderboard,
                        ctx.bumps.leaderboard,
                        run,
                        false,
                        clock.unix_timestamp,
                    );
                    emit!(RunCompleted {
                        player: run.player,
                        victory: false,
                        score: run.score,
                        hp_tenths: 0,
                        vrf_seed: run.vrf_seed,
                    });
                    return Ok(());
                }

                profile.monsters_slain = profile.monsters_slain.saturating_add(1);
            }
            CardCategory::Weapon => {
                run.equipped_weapon_rank = card_rank;
                run.weapon_ceiling = 14;
                if LEGENDARY_WEAPONS_ENABLED && run.has_legendary_skin {
                    run.legendary_forged_edge_charges = 1;
                }
            }
            CardCategory::Potion => {
                if !run.potion_used_this_room {
                    let heal_tenths = (card_rank as u16) * 10;
                    run.hp_tenths = run
                        .hp_tenths
                        .saturating_add(heal_tenths)
                        .min(run.max_hp_tenths);
                    run.potion_used_this_room = true;
                    run.first_potion_healed_tenths = heal_tenths;
                }
            }
        }

        run.cards_resolved_in_room += 1;
        let all_slots_empty = run.room.iter().all(|s| s.is_none());
        let room_complete =
            run.cards_resolved_in_room == 3 || (run.deck_len == 0 && all_slots_empty);

        if room_complete {
            if run.epic_potion_buff_rooms_left > 0 && run.first_potion_healed_tenths > 0 {
                let echo_heal = run.first_potion_healed_tenths / 10;
                run.hp_tenths = run
                    .hp_tenths
                    .saturating_add(echo_heal)
                    .min(run.max_hp_tenths);
                run.epic_potion_buff_rooms_left -= 1;
            }
            if run.ofe_buff_rooms_left > 0 {
                run.ofe_buff_rooms_left -= 1;
            }
            if run.rusty_shield_buff_rooms_left > 0 {
                run.rusty_shield_buff_rooms_left -= 1;
            }

            if run.deck_len == 0 && all_slots_empty {
                run.run_status = RUN_STATUS_VICTORY;
                let base_score = (run.hp_tenths / 10) as i32;
                let potion_bonus =
                    if run.hp_tenths == run.max_hp_tenths && category == CardCategory::Potion {
                        card_rank as i32
                    } else {
                        0
                    };
                run.score = base_score + potion_bonus;
                record_run_completion(
                    profile,
                    &mut ctx.accounts.leaderboard,
                    ctx.bumps.leaderboard,
                    run,
                    true,
                    clock.unix_timestamp,
                );
                emit!(RunCompleted {
                    player: run.player,
                    victory: true,
                    score: run.score,
                    hp_tenths: run.hp_tenths,
                    vrf_seed: run.vrf_seed,
                });
            } else {
                run.pending_spoils_threat = run.room_threat_accumulator.max(1);
                run.room_threat_accumulator = 0;
                run.cards_resolved_in_room = 0;
                run.potion_used_this_room = false;
                run.fled_last_room = false;
            }
        }

        Ok(())
    }

    pub fn claim_spoils(ctx: Context<ClaimSpoils>, choice: u8) -> Result<()> {
        let run = &mut ctx.accounts.dungeon_run;
        require!(
            run.run_status == RUN_STATUS_ACTIVE,
            ScoundrelError::RunNotActive
        );
        let threat = run.pending_spoils_threat;
        require!(threat > 0, ScoundrelError::NoSpoilsToClaim);

        let mut gold_earned: u32 = 0;

        if threat < 12 {
            require!(choice == 0, ScoundrelError::InvalidSpoilsChoice);
            gold_earned = 15;
            run.unclaimed_gold = run.unclaimed_gold.saturating_add(15);
        } else if threat <= 24 {
            match choice {
                0 => {
                    gold_earned = 40;
                    run.unclaimed_gold = run.unclaimed_gold.saturating_add(40);
                }
                1 => {
                    run.epic_potions_inventory = run.epic_potions_inventory.saturating_add(1);
                }
                _ => return err!(ScoundrelError::InvalidSpoilsChoice),
            }
        } else {
            match choice {
                0 => {
                    gold_earned = 85;
                    run.unclaimed_gold = run.unclaimed_gold.saturating_add(85);
                }
                1 => {
                    gold_earned = 25;
                    run.epic_potions_inventory = run.epic_potions_inventory.saturating_add(1);
                    run.unclaimed_gold = run.unclaimed_gold.saturating_add(25);
                }
                2 => {
                    require!(
                        LEGENDARY_WEAPONS_ENABLED,
                        ScoundrelError::InvalidSpoilsChoice
                    );
                    run.has_legendary_skin = true;
                    run.legendary_forged_edge_charges = 1;
                    emit!(LegendarySkinUnlocked {
                        player: run.player,
                        threat_score: threat,
                    });
                }
                _ => return err!(ScoundrelError::InvalidSpoilsChoice),
            }
        }

        run.pending_spoils_threat = 0;

        for slot in 0..ROOM_SIZE {
            if run.room[slot].is_none() {
                if let Some(card) = draw_card(run) {
                    run.room[slot] = Some(card);
                }
            }
        }

        let profile = &mut ctx.accounts.player_profile;
        sync_profile_identity(
            profile,
            ctx.accounts.player.key(),
            ctx.bumps.player_profile,
            &run.vrf_seed,
        );
        profile.total_gold_earned = profile.total_gold_earned.saturating_add(gold_earned as u64);

        Ok(())
    }

    pub fn claim_gold_tokens(ctx: Context<ClaimGoldTokens>) -> Result<()> {
        let amount = ctx.accounts.dungeon_run.unclaimed_gold as u64;
        require!(amount > 0, ScoundrelError::NoGoldToClaim);

        ensure_gold_mint_and_ata(
            &ctx.accounts.payer.to_account_info(),
            &ctx.accounts.player.to_account_info(),
            &ctx.accounts.gold_mint.to_account_info(),
            ctx.bumps.gold_mint,
            &ctx.accounts.player_gold_ata.to_account_info(),
            &ctx.accounts.token_program.to_account_info(),
            &ctx.accounts.system_program.to_account_info(),
        )?;

        let mint_bump = ctx.bumps.gold_mint;
        let mint_seeds: &[&[u8]] = &[GOLD_MINT_SEED, &[mint_bump]];

        // Checks-Effects-Interactions: clear unclaimed_gold & update profile before MintTo CPI
        let vrf_seed = ctx.accounts.dungeon_run.vrf_seed;
        ctx.accounts.dungeon_run.unclaimed_gold = 0;

        let profile = &mut ctx.accounts.player_profile;
        sync_profile_identity(
            profile,
            ctx.accounts.player.key(),
            ctx.bumps.player_profile,
            &vrf_seed,
        );
        profile.total_gold_minted = profile.total_gold_minted.saturating_add(amount);

        let mut mint_to_data = Vec::with_capacity(9);
        mint_to_data.push(7u8);
        mint_to_data.extend_from_slice(&amount.to_le_bytes());

        let mint_to_ix = Instruction {
            program_id: SPL_TOKEN_PROGRAM_ID,
            accounts: vec![
                AccountMeta::new(ctx.accounts.gold_mint.key(), false),
                AccountMeta::new(ctx.accounts.player_gold_ata.key(), false),
                AccountMeta::new_readonly(ctx.accounts.gold_mint.key(), true),
            ],
            data: mint_to_data,
        };
        invoke_signed(
            &mint_to_ix,
            &[
                ctx.accounts.gold_mint.to_account_info(),
                ctx.accounts.player_gold_ata.to_account_info(),
            ],
            &[mint_seeds],
        )?;

        // When the wallet owner signs directly, also top up the session delegate's bounded allowance.
        if ctx.accounts.payer.key() == ctx.accounts.player.key()
            && ctx.accounts.session_signer.key() != ctx.accounts.player.key()
        {
            let (_, existing_delegate, existing_allowance) = read_validated_token_account(
                &ctx.accounts.player_gold_ata.to_account_info(),
                &ctx.accounts.gold_mint.key(),
                &ctx.accounts.player.key(),
            )?;
            let base = if existing_delegate == Some(ctx.accounts.session_signer.key()) {
                existing_allowance
            } else {
                0
            };
            approve_session_delegate(
                &ctx.accounts.payer.to_account_info(),
                &ctx.accounts.player_gold_ata.to_account_info(),
                &ctx.accounts.session_signer.to_account_info(),
                base.saturating_add(amount),
            )?;
        }

        emit!(GoldTokensMinted {
            player: ctx.accounts.player.key(),
            mint: ctx.accounts.gold_mint.key(),
            token_account: ctx.accounts.player_gold_ata.key(),
            amount,
        });

        Ok(())
    }

    pub fn close_run(_ctx: Context<CloseRun>) -> Result<()> {
        Ok(())
    }
}

#[vrf]
#[derive(Accounts)]
pub struct StartRun<'info> {
    #[account(mut)]
    pub player: Signer<'info>,

    /// CHECK: Ephemeral session keypair authorized to sign turn actions on behalf of `player`.
    #[account(mut)]
    pub session_signer: UncheckedAccount<'info>,

    #[account(
        init_if_needed,
        payer = player,
        space = 8 + DungeonRun::INIT_SPACE,
        seeds = [b"dungeon_run", player.key().as_ref()],
        bump
    )]
    pub dungeon_run: Box<Account<'info, DungeonRun>>,

    #[account(
        init_if_needed,
        payer = player,
        space = 8 + PlayerProfile::INIT_SPACE,
        seeds = [PROFILE_SEED, player.key().as_ref()],
        bump
    )]
    pub player_profile: Box<Account<'info, PlayerProfile>>,

    /// CHECK: MagicBlock VRF oracle queue
    #[account(
        mut,
        constraint = oracle_queue.key() == ephemeral_vrf_sdk::consts::DEFAULT_QUEUE
            || oracle_queue.key() == ephemeral_vrf_sdk::consts::DEFAULT_EPHEMERAL_QUEUE
            || oracle_queue.key() == ephemeral_vrf_sdk::consts::DEFAULT_TEST_QUEUE
            || oracle_queue.key() == ephemeral_vrf_sdk::consts::DEFAULT_EPHEMERAL_TEST_QUEUE
            @ ScoundrelError::InvalidOracleQueue
    )]
    pub oracle_queue: UncheckedAccount<'info>,

    /// CHECK: MagicBlock VRF program (`Vrf1RNUjXmQGjmQrQLvJHs9SNkvDJEsRVFPkfSQUwGz`)
    #[account(address = ephemeral_vrf_sdk::consts::VRF_PROGRAM_ID)]
    pub vrf_program: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct PrepareSessionVault<'info> {
    #[account(mut)]
    pub player: Signer<'info>,

    /// CHECK: Verified against `dungeon_run.session_signer`.
    #[account(
        constraint = session_signer.key() == dungeon_run.session_signer @ ScoundrelError::UnauthorizedSigner
    )]
    pub session_signer: UncheckedAccount<'info>,

    #[account(
        seeds = [b"dungeon_run", player.key().as_ref()],
        bump = dungeon_run.bump,
        constraint = dungeon_run.player == player.key() @ ScoundrelError::UnauthorizedSigner
    )]
    pub dungeon_run: Box<Account<'info, DungeonRun>>,

    /// CHECK: Canonical SPL Gold Mint PDA (`seeds = [b"scoundrel_gold_mint"]`).
    #[account(
        mut,
        seeds = [GOLD_MINT_SEED],
        bump
    )]
    pub gold_mint: UncheckedAccount<'info>,

    /// CHECK: Canonical Associated Token Account for `player` and `gold_mint`.
    #[account(mut)]
    pub player_gold_ata: UncheckedAccount<'info>,

    /// CHECK: SPL Token program (`TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA`)
    #[account(address = SPL_TOKEN_PROGRAM_ID @ ScoundrelError::InvalidTokenProgram)]
    pub token_program: UncheckedAccount<'info>,

    /// CHECK: SPL Associated Token Account program (`ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL`)
    #[account(address = ASSOCIATED_TOKEN_PROGRAM_ID @ ScoundrelError::InvalidAssociatedTokenProgram)]
    pub associated_token_program: UncheckedAccount<'info>,

    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct CallbackStartRun<'info> {
    #[account(
        constraint = is_authorized_vrf_callback_signer(&vrf_program_identity.key(), &dungeon_run)
            @ ScoundrelError::UnauthorizedVrfCallback
    )]
    pub vrf_program_identity: Signer<'info>,

    #[account(mut)]
    pub dungeon_run: Box<Account<'info, DungeonRun>>,
}

#[derive(Accounts)]
pub struct PlayerTurn<'info> {
    pub signer: Signer<'info>,

    #[account(
        mut,
        seeds = [b"dungeon_run", dungeon_run.player.as_ref()],
        bump = dungeon_run.bump,
        constraint = signer.key() == dungeon_run.player || signer.key() == dungeon_run.session_signer @ ScoundrelError::UnauthorizedSigner
    )]
    pub dungeon_run: Box<Account<'info, DungeonRun>>,
}

#[derive(Accounts)]
pub struct ResolveCard<'info> {
    #[account(
        mut,
        constraint = signer.key() == dungeon_run.player || signer.key() == dungeon_run.session_signer @ ScoundrelError::UnauthorizedSigner
    )]
    pub signer: Signer<'info>,

    /// CHECK: Verified against `dungeon_run.player`.
    #[account(
        constraint = player.key() == dungeon_run.player @ ScoundrelError::UnauthorizedSigner
    )]
    pub player: UncheckedAccount<'info>,

    #[account(
        mut,
        seeds = [b"dungeon_run", dungeon_run.player.as_ref()],
        bump = dungeon_run.bump
    )]
    pub dungeon_run: Box<Account<'info, DungeonRun>>,

    #[account(
        init_if_needed,
        payer = signer,
        space = 8 + PlayerProfile::INIT_SPACE,
        seeds = [PROFILE_SEED, player.key().as_ref()],
        bump
    )]
    pub player_profile: Box<Account<'info, PlayerProfile>>,

    #[account(
        init_if_needed,
        payer = signer,
        space = 8 + Leaderboard::INIT_SPACE,
        seeds = [LEADERBOARD_SEED],
        bump
    )]
    pub leaderboard: Box<Account<'info, Leaderboard>>,

    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct ClaimSpoils<'info> {
    #[account(
        mut,
        constraint = signer.key() == dungeon_run.player || signer.key() == dungeon_run.session_signer @ ScoundrelError::UnauthorizedSigner
    )]
    pub signer: Signer<'info>,

    /// CHECK: Verified against `dungeon_run.player`.
    #[account(
        constraint = player.key() == dungeon_run.player @ ScoundrelError::UnauthorizedSigner
    )]
    pub player: UncheckedAccount<'info>,

    #[account(
        mut,
        seeds = [b"dungeon_run", dungeon_run.player.as_ref()],
        bump = dungeon_run.bump
    )]
    pub dungeon_run: Box<Account<'info, DungeonRun>>,

    #[account(
        init_if_needed,
        payer = signer,
        space = 8 + PlayerProfile::INIT_SPACE,
        seeds = [PROFILE_SEED, player.key().as_ref()],
        bump
    )]
    pub player_profile: Box<Account<'info, PlayerProfile>>,

    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct BuyItem<'info> {
    #[account(
        mut,
        constraint = signer.key() == dungeon_run.player || signer.key() == dungeon_run.session_signer @ ScoundrelError::UnauthorizedSigner
    )]
    pub signer: Signer<'info>,

    /// CHECK: Verified against `dungeon_run.player`.
    #[account(
        constraint = player.key() == dungeon_run.player @ ScoundrelError::UnauthorizedSigner
    )]
    pub player: UncheckedAccount<'info>,

    #[account(
        mut,
        seeds = [b"dungeon_run", dungeon_run.player.as_ref()],
        bump = dungeon_run.bump
    )]
    pub dungeon_run: Box<Account<'info, DungeonRun>>,

    #[account(
        init_if_needed,
        payer = signer,
        space = 8 + PlayerProfile::INIT_SPACE,
        seeds = [PROFILE_SEED, player.key().as_ref()],
        bump
    )]
    pub player_profile: Box<Account<'info, PlayerProfile>>,

    /// CHECK: Canonical SPL Gold Mint PDA (`seeds = [b"scoundrel_gold_mint"]`).
    #[account(
        mut,
        seeds = [GOLD_MINT_SEED],
        bump
    )]
    pub gold_mint: UncheckedAccount<'info>,

    /// CHECK: Canonical Player Gold ATA verified by PDA derivation.
    #[account(mut)]
    pub player_gold_ata: UncheckedAccount<'info>,

    /// CHECK: SPL Token program (`TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA`)
    #[account(address = SPL_TOKEN_PROGRAM_ID @ ScoundrelError::InvalidTokenProgram)]
    pub token_program: UncheckedAccount<'info>,

    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct ClaimGoldTokens<'info> {
    #[account(
        mut,
        constraint = payer.key() == dungeon_run.player || payer.key() == dungeon_run.session_signer @ ScoundrelError::UnauthorizedSigner
    )]
    pub payer: Signer<'info>,

    /// CHECK: Verified against `dungeon_run.player`.
    #[account(
        constraint = player.key() == dungeon_run.player @ ScoundrelError::UnauthorizedSigner
    )]
    pub player: UncheckedAccount<'info>,

    /// CHECK: Verified against `dungeon_run.session_signer`.
    #[account(
        constraint = session_signer.key() == dungeon_run.session_signer @ ScoundrelError::UnauthorizedSigner
    )]
    pub session_signer: UncheckedAccount<'info>,

    #[account(
        mut,
        seeds = [b"dungeon_run", dungeon_run.player.as_ref()],
        bump = dungeon_run.bump
    )]
    pub dungeon_run: Box<Account<'info, DungeonRun>>,

    #[account(
        init_if_needed,
        payer = payer,
        space = 8 + PlayerProfile::INIT_SPACE,
        seeds = [PROFILE_SEED, player.key().as_ref()],
        bump
    )]
    pub player_profile: Box<Account<'info, PlayerProfile>>,

    /// CHECK: Canonical SPL Gold Mint PDA (`seeds = [b"scoundrel_gold_mint"]`), initialized on first claim and used as its own mint authority.
    #[account(
        mut,
        seeds = [GOLD_MINT_SEED],
        bump
    )]
    pub gold_mint: UncheckedAccount<'info>,

    /// CHECK: Player's Associated Token Account for `gold_mint`, verified by PDA derivation and created idempotently if empty.
    #[account(mut)]
    pub player_gold_ata: UncheckedAccount<'info>,

    /// CHECK: SPL Token program (`TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA`)
    #[account(address = SPL_TOKEN_PROGRAM_ID @ ScoundrelError::InvalidTokenProgram)]
    pub token_program: UncheckedAccount<'info>,

    /// CHECK: SPL Associated Token Account program (`ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL`)
    #[account(address = ASSOCIATED_TOKEN_PROGRAM_ID @ ScoundrelError::InvalidAssociatedTokenProgram)]
    pub associated_token_program: UncheckedAccount<'info>,

    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct CloseRun<'info> {
    #[account(mut)]
    pub player: Signer<'info>,

    #[account(
        mut,
        close = player,
        seeds = [b"dungeon_run", player.key().as_ref()],
        bump = dungeon_run.bump,
        constraint = dungeon_run.player == player.key() @ ScoundrelError::UnauthorizedSigner
    )]
    pub dungeon_run: Box<Account<'info, DungeonRun>>,
}

#[account]
#[derive(InitSpace)]
pub struct DungeonRun {
    pub player: Pubkey,
    pub session_signer: Pubkey,
    pub bump: u8,
    pub deck: [u8; 44],
    pub deck_head: u8,
    pub deck_len: u8,
    pub room: [Option<u8>; 4],
    pub cards_resolved_in_room: u8,
    pub hp_tenths: u16,
    pub max_hp_tenths: u16,
    pub equipped_weapon_rank: u8,
    pub weapon_ceiling: u8,
    pub legendary_forged_edge_charges: u8,
    pub has_legendary_skin: bool,
    pub potion_used_this_room: bool,
    pub first_potion_healed_tenths: u16,
    pub epic_potions_inventory: u8,
    pub epic_potion_buff_rooms_left: u8,
    pub fled_last_room: bool,
    pub room_threat_accumulator: u8,
    pub pending_spoils_threat: u8,
    pub unclaimed_gold: u32,
    pub score: i32,
    pub run_status: u8,
    pub vrf_seed: [u8; 32],
    pub combat_rng_nonce: u64,
    pub ofe_inventory: u8,
    pub ofe_buff_rooms_left: u8,
    pub rusty_shield_inventory: u8,
    pub rusty_shield_buff_rooms_left: u8,
}

#[account]
#[derive(InitSpace)]
pub struct PlayerProfile {
    pub player: Pubkey,
    pub bump: u8,
    pub has_completed_run: bool,
    pub runs_started: u32,
    pub victories: u32,
    pub defeats: u32,
    pub best_score: i32,
    pub monsters_slain: u32,
    pub total_gold_earned: u64,
    pub total_gold_minted: u64,
    pub total_gold_spent: u64,
    pub total_vrf_requests: u32,
    pub total_vrf_combat_rolls: u64,
    pub last_vrf_seed: [u8; 32],
}

#[derive(AnchorSerialize, AnchorDeserialize, Clone, Copy, Default, InitSpace)]
pub struct LeaderboardEntry {
    pub player: Pubkey,
    pub score: i32,
    pub hp_tenths: u16,
    pub victory: bool,
    pub vrf_seed_prefix: [u8; 8],
    pub timestamp: i64,
}

#[account]
#[derive(InitSpace)]
pub struct Leaderboard {
    pub bump: u8,
    pub entry_count: u8,
    pub total_runs_completed: u64,
    pub entries: [LeaderboardEntry; 10],
}

#[event]
pub struct VrfRunStarted {
    pub player: Pubkey,
    pub dungeon_run: Pubkey,
    pub vrf_seed: [u8; 32],
    pub initial_room: [u8; 4],
}

#[event]
pub struct MarketplaceItemPurchased {
    pub player: Pubkey,
    pub item_id: u8,
    pub paid_unclaimed_gold: u32,
    pub burned_spl_gold: u64,
}

#[event]
pub struct RunCompleted {
    pub player: Pubkey,
    pub victory: bool,
    pub score: i32,
    pub hp_tenths: u16,
    pub vrf_seed: [u8; 32],
}

#[event]
pub struct LegendarySkinUnlocked {
    pub player: Pubkey,
    pub threat_score: u8,
}

#[event]
pub struct OfeDodged {
    pub player: Pubkey,
    pub monster_rank: u8,
    pub nonce: u64,
}

#[event]
pub struct RustyShieldBlocked {
    pub player: Pubkey,
    pub monster_rank: u8,
    pub damage_tenths: u16,
    pub nonce: u64,
}

#[event]
pub struct GoldTokensMinted {
    pub player: Pubkey,
    pub mint: Pubkey,
    pub token_account: Pubkey,
    pub amount: u64,
}

#[error_code]
pub enum ScoundrelError {
    #[msg("Invalid card ID outside 0..=43 range.")]
    InvalidCardId,
    #[msg("Deck capacity exceeded.")]
    DeckOverflow,
    #[msg("The dungeon run is no longer active.")]
    RunNotActive,
    #[msg("Dungeon run is not awaiting VRF callback.")]
    NotAwaitingVrf,
    #[msg("Unauthorized VRF callback signer.")]
    UnauthorizedVrfCallback,
    #[msg("Invalid MagicBlock VRF oracle queue account.")]
    InvalidOracleQueue,
    #[msg("Signer is neither the player nor the authorized session key.")]
    UnauthorizedSigner,
    #[msg("Cannot flee a room after resolving cards in it.")]
    CannotFleeMidRoom,
    #[msg("Cannot flee two rooms in a row.")]
    CannotFleeConsecutiveRooms,
    #[msg("Must claim pending room spoils before continuing.")]
    PendingSpoilsMustBeClaimed,
    #[msg("No Epic Health Potions available in inventory.")]
    NoEpicPotionsAvailable,
    #[msg("Epic Health Potion buff is already active.")]
    EpicPotionAlreadyActive,
    #[msg("Invalid marketplace item ID.")]
    InvalidMarketplaceItem,
    #[msg("Insufficient unclaimed + SPL Gold balance for marketplace purchase.")]
    InsufficientGold,
    #[msg("No marketplace item of this type available in inventory.")]
    NoMarketplaceItemAvailable,
    #[msg("This marketplace consumable buff is already active.")]
    MarketplaceBuffAlreadyActive,
    #[msg("Room slot index must be 0..=3.")]
    InvalidSlotIndex,
    #[msg("Selected room slot is already empty.")]
    EmptyRoomSlot,
    #[msg("Cannot attack with weapon when barehanded.")]
    NoWeaponEquipped,
    #[msg("Monster rank exceeds current weapon ceiling.")]
    WeaponCeilingExceeded,
    #[msg("No pending spoils to claim.")]
    NoSpoilsToClaim,
    #[msg("Invalid spoils choice for the current threat tier.")]
    InvalidSpoilsChoice,
    #[msg("No unclaimed gold available to mint.")]
    NoGoldToClaim,
    #[msg("Provided player token account does not match canonical Associated Token Address.")]
    InvalidPlayerGoldAta,
    #[msg("Invalid SPL Token program address.")]
    InvalidTokenProgram,
    #[msg("Invalid SPL Associated Token Account program address.")]
    InvalidAssociatedTokenProgram,
    #[msg("Session key lacks bounded SPL Token delegate approval for burning minted gold.")]
    MissingTokenDelegateApproval,
}
