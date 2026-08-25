using System;
using System.Collections.Generic;

namespace AdaptiveEnemies
{
    /// <summary>
    /// Maps Silksong tool items onto functional buckets.
    /// <para>
    /// The game's own <c>ToolItemType</c> enum is only Red/Blue/Yellow/Skill — an
    /// inventory colour, not a function — so the functional grouping has to be an
    /// explicit table. Names are the real asset names from <c>dataassets/tools</c>.
    /// </para>
    /// Unlisted tools fall through to <see cref="PlayerAction.ToolUnclassified"/> and
    /// are logged once, so the table can be tightened without guesswork.
    /// </summary>
    public static class ToolBuckets
    {
        private static readonly Dictionary<string, PlayerAction> Map =
            new Dictionary<string, PlayerAction>(StringComparer.OrdinalIgnoreCase)
        {
            // Standout tool: its own bucket, per the design.
            { "Cogwork Flier",        PlayerAction.ToolCogflies },

            // Ranged projectiles
            { "Straight Pin",         PlayerAction.ToolRangedProjectile },
            { "Ladybug Straight Pin", PlayerAction.ToolRangedProjectile },
            { "Tri Pin",              PlayerAction.ToolRangedProjectile },
            { "Sting Shard",          PlayerAction.ToolRangedProjectile },
            { "Silk Spear",           PlayerAction.ToolRangedProjectile },
            { "Silk Boss Needle",     PlayerAction.ToolRangedProjectile },
            { "Harpoon",              PlayerAction.ToolRangedProjectile },
            { "Rosary Cannon",        PlayerAction.ToolRangedProjectile },
            { "Conch Drill",          PlayerAction.ToolRangedProjectile },
            { "Brolly Spike",         PlayerAction.ToolRangedProjectile },
            { "Pimpilo",              PlayerAction.ToolRangedProjectile },
            { "Dustpilo",             PlayerAction.ToolRangedProjectile },
            { "Tack",                 PlayerAction.ToolRangedProjectile },
            { "Thread Sphere",        PlayerAction.ToolRangedProjectile },
            { "WebShot Architect",    PlayerAction.ToolRangedProjectile },
            { "WebShot Forge",        PlayerAction.ToolRangedProjectile },
            { "WebShot Weaver",       PlayerAction.ToolRangedProjectile },
            { "Pinstress Tool",       PlayerAction.ToolRangedProjectile },

            // Close burst
            { "Silk Bomb",            PlayerAction.ToolCloseBurst },
            { "Silk Charge",          PlayerAction.ToolCloseBurst },
            { "Flintstone",           PlayerAction.ToolCloseBurst },
            { "Cogwork Saw",          PlayerAction.ToolCloseBurst },
            { "Thief Claw",           PlayerAction.ToolCloseBurst },
            { "Curve Claws",          PlayerAction.ToolCloseBurst },
            { "Curve Claws Upgraded", PlayerAction.ToolCloseBurst },
            { "Longneedle",           PlayerAction.ToolCloseBurst },
            { "Screw Attack",         PlayerAction.ToolCloseBurst },

            // Trap / zone denial
            { "Silk Snare",           PlayerAction.ToolTrapZone },
            { "Barbed Wire",          PlayerAction.ToolTrapZone },
            { "Poison Pouch",         PlayerAction.ToolTrapZone },
            { "Lightning Rod",        PlayerAction.ToolTrapZone },
            { "Lava Charm",           PlayerAction.ToolTrapZone },
            { "Zap Imbuement",        PlayerAction.ToolTrapZone },
            { "Dazzle Bind",          PlayerAction.ToolTrapZone },
            { "Dazzle Bind Upgraded", PlayerAction.ToolTrapZone },
            { "Bell Bind",            PlayerAction.ToolTrapZone },
            { "Multibind",            PlayerAction.ToolTrapZone },
            { "Reserve Bind",         PlayerAction.ToolTrapZone },

            // Mobility
            { "Sprintmaster",         PlayerAction.ToolMobility },
            { "Wallcling",            PlayerAction.ToolMobility },
            { "Quick Sling",          PlayerAction.ToolMobility },
            { "Quickbind",            PlayerAction.ToolMobility },
            { "Scuttlebrace",         PlayerAction.ToolMobility },
            { "Weighted Anklet",      PlayerAction.ToolMobility },
            { "Spool Extender",       PlayerAction.ToolMobility },

            // Automated / summon
            { "Flea Brew",            PlayerAction.ToolSummon },
            { "Flea Charm",           PlayerAction.ToolSummon },
            { "Magnetite Dice",       PlayerAction.ToolSummon },
            { "Revenge Crystal",      PlayerAction.ToolSummon },
            { "Shakra Ring",          PlayerAction.ToolSummon },
            { "White Ring",           PlayerAction.ToolSummon },
            { "Wisp Lantern",         PlayerAction.ToolSummon },
            { "Bone Necklace",        PlayerAction.ToolSummon },
            { "Mosscreep Tool 1",     PlayerAction.ToolSummon },
            { "Mosscreep Tool 2",     PlayerAction.ToolSummon },
        };

        private static readonly HashSet<string> Unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static PlayerAction Classify(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return PlayerAction.ToolUnclassified;

            if (Map.TryGetValue(toolName, out var bucket)) return bucket;

            var cleaned = Naming.StripInstanceSuffix(toolName);
            if (!ReferenceEquals(cleaned, toolName) && Map.TryGetValue(cleaned, out bucket)) return bucket;

            if (Unknown.Add(cleaned))
                Log.Info($"Unclassified tool '{cleaned}' — bucketed as ToolUnclassified. Add it to ToolBuckets to refine.");

            return PlayerAction.ToolUnclassified;
        }
    }
}
