using System;
using UnityEngine;

namespace Focus.Howl.Opsive
{
    /// <summary>
    /// Auto-wire rules for UCC scenes. Loaded from Resources/HowlOpsiveSettings; defaults apply when the asset is missing.
    /// </summary>
    [CreateAssetMenu(menuName = "Howl/Opsive Settings", fileName = "HowlOpsiveSettings")]
    public sealed class HowlOpsiveSettings : ScriptableObject
    {
        public const string ResourcePath = "HowlOpsiveSettings";

        [Serializable]
        public struct CategoryRule
        {
            [Tooltip("Case-insensitive match against the GameObject name. First match wins.")]
            public string NameContains;
            public string Category;
        }

        [Tooltip("Scan every loaded scene at play start and attach Howl sources. No scene edits.")]
        public bool AutoWire = true;
        [Tooltip("Log a summary of what was wired.")]
        public bool LogWiring = true;

        [Header("Player")]
        public bool PlayerEvents = true;
        [Tooltip("item_use fires per shot or swing.")]
        public bool ItemUseEvents = true;
        [Tooltip("ability_start fires for every ability, including movement ones.")]
        public bool AbilityEvents = false;

        [Header("Agents")]
        public bool AgentEvents = true;
        public bool AgentDamageEvents = false;
        [Tooltip("roster.<agent>.health / alive info keys.")]
        public bool AgentRosterInfo = true;

        [Header("Objects (Health without a character)")]
        public bool ObjectEvents = true;
        public bool ObjectDamageEvents = false;
        public string DefaultCategory = "object";
        public CategoryRule[] CategoryRules = DefaultRules();

        public static HowlOpsiveSettings Load()
        {
            var settings = Resources.Load<HowlOpsiveSettings>(ResourcePath);
            return settings != null ? settings : CreateInstance<HowlOpsiveSettings>();
        }

        public string Categorize(string objectName)
        {
            if (CategoryRules != null)
            {
                foreach (var rule in CategoryRules)
                {
                    if (!string.IsNullOrEmpty(rule.NameContains) &&
                        objectName.IndexOf(rule.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        return rule.Category;
                }
            }
            return DefaultCategory;
        }

        static CategoryRule[] DefaultRules() => new[]
        {
            new CategoryRule { NameContains = "ExplodingCrate", Category = "exploding_crate" },
            new CategoryRule { NameContains = "FlammableCrate", Category = "flammable_crate" },
            new CategoryRule { NameContains = "Crate", Category = "crate" },
            new CategoryRule { NameContains = "Turret", Category = "turret" },
            new CategoryRule { NameContains = "Target", Category = "target" },
        };
    }
}
