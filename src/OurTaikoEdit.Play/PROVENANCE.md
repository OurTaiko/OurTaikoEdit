# The game's judge

The files in `Game/` are copied unchanged from OurTaikoPlay (`Assets/OurTaiko/Runtime/Core`, main
`0efa6911`, 2026-11-15), GPL-3.0 like OurTaikoEdit; see that repository's `NOTICE` for its
authors. They are the game's pure C# play logic: the judge (`PlaySession`), the Shinuchi score,
the soul gauge and its rules, branch conditions, the kusudama, the notes' faces and the timing
statistics. To take a newer version, copy the same files again; `PlaySettings.cs` here stands in
for the one thing they read of the game's settings.
