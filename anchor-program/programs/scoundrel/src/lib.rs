use anchor_lang::prelude::*;

declare_id!("7r9dGYN2WiNKgr7wD89Heh4rrWX1zkGhxs8ckjKfbNQx");

pub const DECK_SIZE: usize = 44;
pub const ROOM_SIZE: usize = 4;
pub const MAX_HP_TENTHS: u16 = 200;
pub const LEGENDARY_WEAPONS_ENABLED: bool = false;

pub const RUN_STATUS_ACTIVE: u8 = 0;
pub const RUN_STATUS_VICTORY: u8 = 1;
pub const RUN_STATUS_DEFEAT: u8 = 2;

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

    pub fn start_run(ctx: Context<StartRun>, seed: u64, has_legendary_nft: bool) -> Result<()> {
        let clock = Clock::get()?;
        let run = &mut ctx.accounts.dungeon_run;

        run.player = ctx.accounts.player.key();
        run.session_signer = ctx.accounts.session_signer.key();
        run.bump = ctx.bumps.dungeon_run;

        let mut deck = [0u8; DECK_SIZE];
        for (i, card) in deck.iter_mut().enumerate() {
            *card = i as u8;
        }

        let mut rng_state = seed.wrapping_add(clock.slot);
        for i in (1..DECK_SIZE).rev() {
            let rand_val = splitmix64(&mut rng_state);
            let j = (rand_val as usize) % (i + 1);
            deck.swap(i, j);
        }

        run.deck = deck;
        run.deck_head = 0;
        run.deck_len = DECK_SIZE as u8;
        run.room = [None; ROOM_SIZE];

        for slot in 0..ROOM_SIZE {
            run.room[slot] = draw_card(run);
        }

        run.cards_resolved_in_room = 0;
        run.hp_tenths = MAX_HP_TENTHS;
        run.max_hp_tenths = MAX_HP_TENTHS;
        let allow_legendary = LEGENDARY_WEAPONS_ENABLED && has_legendary_nft;
        run.equipped_weapon_rank = 0;
        run.weapon_ceiling = 0;
        run.has_legendary_skin = allow_legendary;
        run.legendary_forged_edge_charges = if allow_legendary { 1 } else { 0 };
        run.potion_used_this_room = false;
        run.first_potion_healed_tenths = 0;
        run.epic_potions_inventory = 0;
        run.epic_potion_buff_rooms_left = 0;
        run.fled_last_room = false;
        run.room_threat_accumulator = 0;
        run.pending_spoils_threat = 0;
        run.unclaimed_gold = 0;
        run.score = 0;
        run.run_status = RUN_STATUS_ACTIVE;

        Ok(())
    }

    pub fn flee_room(ctx: Context<GameplayAction>) -> Result<()> {
        let run = &mut ctx.accounts.dungeon_run;

        require!(run.run_status == RUN_STATUS_ACTIVE, ScoundrelError::RunNotActive);
        require!(run.cards_resolved_in_room == 0, ScoundrelError::CannotFleeMidRoom);
        require!(!run.fled_last_room, ScoundrelError::CannotFleeConsecutiveRooms);
        require!(run.pending_spoils_threat == 0, ScoundrelError::PendingSpoilsMustBeClaimed);

        for slot in 0..ROOM_SIZE {
            if let Some(card) = run.room[slot].take() {
                push_card_bottom(run, card)?;
            }
        }

        for slot in 0..ROOM_SIZE {
            run.room[slot] = draw_card(run);
        }

        run.fled_last_room = true;
        run.potion_used_this_room = false;
        run.room_threat_accumulator = 0;
        run.cards_resolved_in_room = 0;

        Ok(())
    }

    pub fn use_epic_potion(ctx: Context<GameplayAction>) -> Result<()> {
        let run = &mut ctx.accounts.dungeon_run;

        require!(run.run_status == RUN_STATUS_ACTIVE, ScoundrelError::RunNotActive);
        require!(run.epic_potions_inventory > 0, ScoundrelError::NoEpicPotionsAvailable);
        require!(run.epic_potion_buff_rooms_left == 0, ScoundrelError::EpicPotionAlreadyActive);

        run.epic_potions_inventory -= 1;
        run.epic_potion_buff_rooms_left = 3;

        Ok(())
    }

    pub fn resolve_card(
        ctx: Context<GameplayAction>,
        slot_index: u8,
        use_weapon: bool,
    ) -> Result<()> {
        let run = &mut ctx.accounts.dungeon_run;

        require!(run.run_status == RUN_STATUS_ACTIVE, ScoundrelError::RunNotActive);
        require!(run.pending_spoils_threat == 0, ScoundrelError::PendingSpoilsMustBeClaimed);
        require!((slot_index as usize) < ROOM_SIZE, ScoundrelError::InvalidSlotIndex);

        let idx = slot_index as usize;
        require!(run.room[idx].is_some(), ScoundrelError::EmptyRoomSlot);

        let card_id = run.room[idx].take().unwrap();
        let (category, card_rank) = decode_card(card_id)?;

        match category {
            CardCategory::Monster => {
                let monster_rank = card_rank;
                let damage_tenths: u16;

                if use_weapon {
                    let prev_ceiling = run.weapon_ceiling;
                    require!(run.equipped_weapon_rank > 0, ScoundrelError::NoWeaponEquipped);
                    require!(
                        monster_rank <= prev_ceiling || run.legendary_forged_edge_charges > 0,
                        ScoundrelError::WeaponCeilingExceeded
                    );

                    damage_tenths =
                        (monster_rank.saturating_sub(run.equipped_weapon_rank) as u16) * 10;

                    if monster_rank > prev_ceiling {
                        run.legendary_forged_edge_charges -= 1;
                    }

                    let bonus_threat: u8 = if (prev_ceiling.saturating_sub(monster_rank) <= 2
                        && monster_rank >= 8)
                        || monster_rank > run.equipped_weapon_rank
                    {
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
                        .saturating_add(monster_rank)
                        .saturating_add(5);
                }

                run.hp_tenths = run.hp_tenths.saturating_sub(damage_tenths);
                if run.hp_tenths == 0 {
                    run.run_status = RUN_STATUS_DEFEAT;
                    run.score = compute_defeat_score(run)?;
                    return Ok(());
                }
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

            if run.deck_len == 0 && all_slots_empty {
                run.run_status = RUN_STATUS_VICTORY;
                let base_score = (run.hp_tenths / 10) as i32;
                let potion_bonus =
                    if run.hp_tenths == MAX_HP_TENTHS && category == CardCategory::Potion {
                        card_rank as i32
                    } else {
                        0
                    };
                run.score = base_score + potion_bonus;
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

    pub fn claim_spoils(ctx: Context<GameplayAction>, choice: u8) -> Result<()> {
        let run = &mut ctx.accounts.dungeon_run;

        require!(run.run_status == RUN_STATUS_ACTIVE, ScoundrelError::RunNotActive);
        let threat = run.pending_spoils_threat;
        require!(threat > 0, ScoundrelError::NoSpoilsToClaim);

        if threat < 12 {
            require!(choice == 0, ScoundrelError::InvalidSpoilsChoice);
            run.unclaimed_gold = run.unclaimed_gold.saturating_add(15);
        } else if threat <= 24 {
            match choice {
                0 => {
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
                    run.unclaimed_gold = run.unclaimed_gold.saturating_add(85);
                }
                1 => {
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

        Ok(())
    }

    pub fn close_run(_ctx: Context<CloseRun>) -> Result<()> {
        Ok(())
    }
}

#[derive(Accounts)]
pub struct StartRun<'info> {
    #[account(mut)]
    pub player: Signer<'info>,

    /// CHECK: Ephemeral session keypair authorized to sign turn actions on behalf of `player`.
    pub session_signer: UncheckedAccount<'info>,

    #[account(
        init,
        payer = player,
        space = 8 + DungeonRun::INIT_SPACE,
        seeds = [b"dungeon_run", player.key().as_ref()],
        bump,
    )]
    pub dungeon_run: Account<'info, DungeonRun>,

    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct GameplayAction<'info> {
    pub signer: Signer<'info>,

    #[account(
        mut,
        seeds = [b"dungeon_run", dungeon_run.player.as_ref()],
        bump = dungeon_run.bump,
        constraint = (signer.key() == dungeon_run.session_signer || signer.key() == dungeon_run.player) @ ScoundrelError::UnauthorizedSigner,
    )]
    pub dungeon_run: Account<'info, DungeonRun>,
}

#[derive(Accounts)]
pub struct CloseRun<'info> {
    #[account(mut)]
    pub player: Signer<'info>,

    #[account(
        mut,
        close = player,
        has_one = player @ ScoundrelError::UnauthorizedSigner,
        seeds = [b"dungeon_run", player.key().as_ref()],
        bump = dungeon_run.bump,
    )]
    pub dungeon_run: Account<'info, DungeonRun>,
}

#[account]
#[derive(InitSpace)]
pub struct DungeonRun {
    pub player: Pubkey,
    pub session_signer: Pubkey,
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
    pub bump: u8,
}

#[event]
pub struct LegendarySkinUnlocked {
    pub player: Pubkey,
    pub threat_score: u8,
}

#[error_code]
pub enum ScoundrelError {
    #[msg("Invalid card ID outside 0..=43 range.")]
    InvalidCardId,
    #[msg("Deck capacity exceeded.")]
    DeckOverflow,
    #[msg("The dungeon run is no longer active.")]
    RunNotActive,
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
}
