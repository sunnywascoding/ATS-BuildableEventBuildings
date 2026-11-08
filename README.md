# Against The Storm Mod Buildable Event Building Mod

This mod makes buildings that normally only come from events (such as the Finesmith, Homestead and the Flawless, Hallowed and Holy whatever fuildings) available to build. Each building can be set in the config file to be unlocked at the start of every game, added to the pool of buildings you can pick, or left alone. Because these buildings are powerful, their construction cost is also multiplied (2x by default), and the multiplier can be changed per building.

The compiled mod is meant to be loaded into the game with [BepInEx](https://github.com/BepInEx/BepInEx). I tested with version 5.4.23.

## Settings

The game creates a settings file the first time you run it with the mod: `BepInEx/config/BuildableEventBuildings.cfg`.

- **`[Buildings]`**: set each building to one of:
  - `Pickable` (default): can show up in your building choices.
  - `Unlocked`: unlocked at the start of every new game.
  - `Disabled`: left as in the normal game.
- **`[CostMultipliers]`**: multiplies a building's cost. `1` is normal, `2` is double (default).
- **`[RemoveRegularVersion]`**: set a building to `true` to hide its regular version (e.g. Cellar for Flawless Cellar) from your building choices from the start. Default is `false`.
- **`[RemoveRegularVersionOnPick]`**: if `true` (default), picking an upgraded building hides its regular version afterwards.


## Background
This started because I got the Feast Hall and some of the event other buildings a few times and thought wow wouldn't it be nice if I can use them more consistently? It would definitely be crazy game breaking lol because they are super powerful and efficient, so I also upped the cost.


- Thanks to https://github.com/ats-mods/ModTemplate for the mod template
- Thanks to https://github.com/JoyfulShush/AtS-my-first-mod/ with the really helpful guide
- Thanks to https://github.com/JamesVeug/Josiwe.ATS.Cheats/ for all the really good examples for all the difference things you can do.
- Thanks to Eremite Games for making Against the Storm.
