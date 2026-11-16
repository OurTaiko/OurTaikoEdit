# Note sprites

`notes_atlas.png`, `moji.png` and `moji_drumroll_mid.png` are copied unchanged from OurTaikoPlay
(`Assets/OurTaiko/Art/game/notes`, main `a998c33`, 2026-11-04), GPL-3.0 like OurTaikoEdit; see that
repository's `NOTICE` for the authors of its assets. Used here with the OurTaiko maintainer's
approval (2026-11-05). The frames are cut as the game's sprite metadata cuts them
(`Controls/GameSprites.cs`).

# The play scene's art

Everything under `play/` is copied unchanged from OurTaikoPlay (`Assets/OurTaiko/Art`, main
`0efa6911`, 2026-11-15), for the play scene (`Controls/PlayStage.cs`), which draws the game's
single-player scene. The folders are the game's `game/<folder>` ones, except:
`background/header.png` (`background/donbg/0_1/background/0.png`), `background/festival.png` and
`festival_lights.png` (`background/bg_normal/bg_0/background.png`, `overlay.png`),
`background/footer.png` (`background/footer/0.png`) and `judge_counter/labels.png`
(`result/score/max_combo_ja.png`). Copied on the user's request of 2026-11-15 to bring the game's
play into the editor looking as the game does.
