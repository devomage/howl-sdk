using UnityEngine;

namespace Focus.Howl.Opsive
{
    /// <summary>
    /// The Opsive event vocabulary: shared names come from <see cref="HowlStandard"/>, Opsive-only ones are registered here.
    /// Registered on the first wired entity, so scenes without UCC content don't carry the Opsive vocabulary.
    /// </summary>
    public static class HowlOpsiveEvents
    {
        // Player (shared)
        public static EventId DamageTaken, Hit, Kill, Death, Respawn, Heal, Pickup;
        // Player (Opsive)
        public static EventId ItemUse, Reload, Equip, AbilityStart;
        // Agents
        public static EventId AgentDeath, AgentRespawn, AgentDamage;
        // Objects
        public static EventId ObjectDestroyed, ObjectDamaged, ObjectRespawn;

        public static KeyId MeHealth, MeShield, MeAlive, MePos, MeYaw, MeItem, PlayerName;

        /// <summary>String index used for unknown attackers and empty item slots.</summary>
        public static int None;

        static bool s_Registered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset() => s_Registered = false;

        public static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;

            const HowlField S = HowlField.String;
            const HowlField I = HowlField.Int;
            const HowlVectorField Sc = HowlVectorField.Scalar;
            const HowlVectorField V = HowlVectorField.Vector;

            HowlStandard.Register();
            None = HowlStandard.None;

            DamageTaken = HowlStandard.DamageTaken;
            Hit = HowlStandard.Hit;
            Kill = HowlStandard.Kill;
            Death = HowlStandard.Death;
            Respawn = HowlStandard.Respawn;
            Heal = HowlStandard.Heal;
            Pickup = HowlStandard.Pickup;

            ItemUse = Howl.RegisterEvent("item_use", "item", S);
            Reload = Howl.RegisterEvent("reload", "item", S);
            Equip = Howl.RegisterEvent("equip", "item", S, "slot", I);
            AbilityStart = Howl.RegisterEvent("ability_start", "ability", S);

            AgentDeath = Howl.RegisterEvent("agent_death", "agent", S, "attacker", S, "pos", V);
            AgentRespawn = Howl.RegisterEvent("agent_respawn", "agent", S, null, HowlField.None, "pos", V);
            AgentDamage = Howl.RegisterEvent("agent_damage", "agent", S, "attacker", S, "amount", Sc);

            ObjectDestroyed = Howl.RegisterEvent("object_destroyed", "object", S, "category", S, "pos", V);
            ObjectDamaged = Howl.RegisterEvent("object_damaged", "object", S, "category", S, "amount", Sc);
            ObjectRespawn = Howl.RegisterEvent("object_respawn", "object", S, "category", S);

            MeHealth = HowlStandard.MeHealth;
            MeShield = HowlStandard.MeShield;
            MeAlive = HowlStandard.MeAlive;
            MePos = HowlStandard.MePos;
            MeYaw = HowlStandard.MeYaw;
            MeItem = HowlStandard.MeItem;
            PlayerName = HowlStandard.PlayerName;
        }
    }
}
