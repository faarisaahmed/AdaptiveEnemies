# Adaptive Enemies

A Silksong mod that lets enemies **learn how you fight**.

Every enemy contributes what happens to it into a shared pool — framed as Grandmother
Silk watching Hornet — and adapts the attacks it already has based on what has and
hasn't been working. Not just its own encounters: what one creature learns, others
built like it inherit.

Nothing is scripted per enemy. There is no list of enemy names anywhere in the code.
Each one profiles itself from its own state machine the first time you meet it, so the
whole roster is covered, including ones this mod has never seen.

## What actually changes

An enemy may only ever re-weight, re-time and re-space the behaviour it already shipped
with:

- **Which attack it picks**, when it has a choice between several
- **How long its telegraphs and recoveries run**, within ±35%
- **How far out it commits**, by scaling its own attack-range volume
- **Continuous parameters of its behaviour** — how far above you a flier positions
  before diving, how sharply it decelerates out of a lunge

It never gains a new attack, and **base movement speed is never touched**. An attack
whose authored weight is zero stays zero, and no attack falls below 0.2x or rises above
3x its original weight, so an enemy never abandons part of its moveset or becomes a
one-trick fight.

The interesting consequence is that new behaviour still emerges. A MossBone Fly learns
that clipping you on the approach deals damage, promotes its own approach to an attack,
and then tunes how high it positions — arriving at a charge attack it never had, from a
positioning value nobody designed as a weapon.

## Install

Grab `AdaptiveEnemies.zip` from [Releases](../../releases) and merge its `plugins`
folder into your `BepInEx` folder. Requires BepInEx 5.

Learning is written to `BepInEx/plugins/AdaptiveEnemies/hive.json`. Delete it to wipe
what every enemy knows and start fresh.

## Build from source

```
dotnet build -c Release
```

Builds and copies into place:

```
AdaptiveEnemies -> BepInEx/plugins/AdaptiveEnemies/AdaptiveEnemies.dll
```

The only thing a new machine needs to change is where the game lives. Rather than edit
a tracked file, drop a `LocalPaths.props` next to `Directory.Build.props` — it's
gitignored and overrides the default:

```xml
<Project>
  <PropertyGroup>
    <GamePath>C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong</GamePath>
  </PropertyGroup>
</Project>
```

## How the learning works

### Weighted policies, not branches

Silksong's enemies already pick their attacks with PlayMaker's `SendRandomEvent`
actions, which hold a mutable array of weights. The mod rewrites those weights. That is
what keeps adaptation inside the enemy's authored design rather than bolted onto it.

Weights move *multiplicatively* around vanilla and are renormalised back onto the
original total, so an enemy's authored bias between its attacks survives — a boss that
was meant to slam more than it swoops still does, it just shifts within that.

### Confidence, so early fights look vanilla

Deviation scales as `n / (n + k)`, and below a minimum sample count an enemy is pinned
to exactly vanilla. The opening of an encounter is stock Silksong; it only starts
reading you once it has evidence.

### Decay, so it forgets

Every data point decays exponentially from its own insertion time — a point added at
*t* contributes `v · 2^(−(now−t)/halfLife)` today. Per data point, not a table wipe, at
O(1). Sample counts decay alongside values, so confidence fades with the evidence and an
enemy genuinely forgets a counter you have stopped using.

The clock is **gameplay time**, so nothing evaporates while the game sits paused.

### Two memories, at different speeds

What an enemy knows about **itself** — which of its attacks work — persists across
sessions. What it knows about **you** — which dodge you favour, what punishes what —
fades roughly five times faster.

That split is deliberate. Without it a boss walks into every rematch already knowing
you and the fight has no arc. With it, each encounter starts fresh on the read and has
to earn it again, while the enemy keeps its hard-won sense of its own moveset.

### Exploration, so it doesn't collapse

Attack selection carries a UCB1-style optimism bonus that shrinks as an option
accumulates evidence. Without it the policy is pure exploitation: one lucky landed
attack and the enemy does that forever, because the attacks it never retried have no
evidence to argue with.

It also lets an attack that was punished long ago drift back into rotation, so enemies
periodically re-test whether your counter still works.

### Chain credit

An attack is usually a chain — antic, strike, recovery — and the player punishes it
somewhere in the middle. The decision that started the chain never observes that
directly, so outcomes are credited back to the choice that caused them at a discount.

### Indirect damage

Falling rocks and summoned minions are separate objects that no enemy owns. Damage from
them is attributed by recency and proximity to whichever enemy most recently committed
to something, and credited to *the behaviour that caused it* rather than whatever the
enemy is doing by the time the rock lands.

Nothing here knows which enemy spawns what, so a boss that gains a new summon is covered
automatically.

## The hive

Knowledge is filed in three layers:

| layer | scope |
| --- | --- |
| **Private** | one enemy type, one behaviour. Nothing else reads it |
| **Cohort** | pooled across enemies built the same way, by topic |
| **Universal** | a thin layer of player habits every enemy may draw on |

Cohorts come from a signature each enemy derives for itself — locomotion from its
components, weapons from the archetypes its own states fall into:

```
Flying|Dive,Melee        a diving flier
Ground|Melee,Summon      a melee enemy that calls for help
Ground|Contact           hurts by touch, no distinct attack
```

Sharing runs at full trust inside a signature, half across shared locomotion, and low
across different locomotion. Because knowledge is filed per archetype, an enemy with no
diving state can never receive diving knowledge — there is nowhere for it to land.

Shared knowledge is applied **only at encounter start**, as a prior. From there the
enemy refines on its own experience.

### Rare enemies learn faster

A boss is fought once and must adapt within that fight or the feature is invisible on
exactly the enemies where it matters most. A common enemy is met constantly and should
creep. Learning rate scales with durability and rarity together:

| | hp | met | rate |
| --- | --- | --- | --- |
| common enemy, hundredth time | 12 | 80 | x1.2 |
| the same enemy, first time | 12 | 0 | x2.5 |
| rare heavy | 60 | 3 | x5.0 |

## Reading the player

Enemies track, per attack of theirs:

- **Pogo, left / right / up slash** — landed or missed
- **Tools**, bucketed by function (ranged, close burst, trap, mobility, summon), with
  Cogflies given its own bucket
- **How you avoided it** — jump, evade, needle-parry, or cross-stitch counter
- **Whether it landed, what it cost them, and whether you were healing**

Healing is treated as a distinguishable *situation* rather than an instruction. Enemies
keep a separate policy for fighting a healing player, interrupting a heal is worth more
than an ordinary hit, and letting one complete within reach counts against whatever they
were doing. Which of their attacks can actually reach in time is theirs to discover.

## Configuration

Everything is exposed through BepInEx config and can be edited live with
[ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

| key | |
| --- | --- |
| `Enabled` | master switch — off is exactly vanilla, nothing recorded |
| `TestingMode` | rate x6, threshold 1.5 samples, full authority. Adaptation inside a few exchanges |
| `Authority` | ceiling on how far learning may override vanilla |
| `AdaptAllEnemies` | cover the whole roster rather than a named list |
| `AdaptBosses` | whether high-HP enemies adapt |
| `PersistToDisk` | carry learning across sessions |
| `DumpEveryNewEnemy` | write an FSM dump and roster line for each new enemy type met |
| `VerboseLogging` | log every observation and weight change |

`TestingMode` is the one to reach for first — it compresses hours of play into a handful
of fights so you can actually watch an enemy adapt.

## Debug

| key | |
| --- | --- |
| `1` | Free move — fly with arrows or WASD, shift for speed |

Turn on `LogAdaptationSummary` and every enemy type reports what it has learned every 15
seconds:

```
Mossbone Mother | Swoop Antic: score -0.31 n=8.2 conf=84% weight x0.71 (explore +0.22);
Rock: score +0.44 n=6.1 conf=80% weight x1.63; timing x0.82, spacing x1.11
```

## Layout

```
AdaptiveEnemies.sln
Directory.Build.props          game path
AdaptiveEnemies/               the plugin
```

All under `AdaptiveEnemies/src/`.

| File | Job |
| --- | --- |
| `Plugin.cs` | BepInEx entry point, hive clock, scene sweeps, saving |
| `ModConfig.cs` | every tunable |
| `Taxonomy.cs` | action/avoid/archetype enums, and FSM state classification |
| `ToolBuckets.cs` | tool name to functional bucket |
| `Support.cs` | logging, Unity name normalisation |
| `Learning/StatCell.cs` | one decaying evidence accumulator |
| `Learning/HiveMind.cs` | the three-layer shared pool |
| `Learning/Policy.cs` | priors, confidence, exploration, scoring |
| `Learning/Store.cs` | atomic JSON persistence |
| `Runtime/FsmAdapter.cs` | reflection discovery of weights, timings, parameters |
| `Runtime/AdaptiveBrain.cs` | one enemy: episodes, policy, expression |
| `Runtime/EnemyProfile.cs` | self-classification and cohort affinity |
| `Runtime/EnemyIdentity.cs` | what counts as an adaptable enemy |
| `Runtime/CombatTracker.cs` | player action classification, damage attribution |
| `Runtime/CrawlerAdapter.cs` | contact-damage enemies with no attack graph |
| `Diagnostics/FsmDumper.cs` | FSM dumps and the enemy roster |
| `Patches/CombatPatches.cs` | the Harmony hooks |

## Notes and known limits

- **Mono, not IL2CPP.** Verified against the shipped assemblies; BepInEx 5.4.23.2,
  Unity 6000.0.50.
- **Enemies with one attack can't adapt selection.** No amount of learning creates a
  choice that isn't there — those adapt timing, spacing and parameters instead.
- **Variable-driven parameters can't be learned.** Many PlayMaker floats are bound to
  FSM variables rather than literals. Writing through them would corrupt the enemy's own
  logic, so they're skipped and reported in the log.
- **Contact mode only recognises the `Crawler` component.** Contact-damage enemies
  driven by something else currently have no adaptable structure.
- **`hive.json` is global**, not per save file.
- **State classification is keyword-based**, validated against the vocabulary of all 590
  scene bundles. An enemy using unusual names still learns privately, just with weaker
  cohort transfer.

All four Harmony hooks are postfixes that only observe, so a failure inside the mod
cannot change a combat result.

## License

MIT
