using UnityEngine;

namespace Focus.Howl.Opsive
{
    /// <summary>The Opsive event vocabulary, registered once per play session.</summary>
    public static class HowlOpsiveEvents
    {
        // Player
        public static EventId DamageTaken, Hit, Kill, Death, Respawn, Heal, Pickup, ItemUse, Reload, Equip, AbilityStart;
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

            None = Howl.RegisterString("none");

            DamageTaken = Howl.RegisterEvent("damage_taken", "attacker", S, null, HowlField.None, "amount", Sc);
            Hit = Howl.RegisterEvent("hit", "target", S, "target_category", S, "amount", Sc);
            Kill = Howl.RegisterEvent("kill", "victim", S, "victim_category", S, "pos", V);
            Death = Howl.RegisterEvent("death", "attacker", S, null, HowlField.None, "pos", V);
            Respawn = Howl.RegisterEvent("respawn", xName: "pos", xKind: V);
            Heal = Howl.RegisterEvent("heal", xName: "amount", xKind: Sc);
            Pickup = Howl.RegisterEvent("pickup", "item", S, "amount", I);
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

            MeHealth = Howl.RegisterKey("me", "health", HowlValueKind.Float);
            MeShield = Howl.RegisterKey("me", "shield", HowlValueKind.Float);
            MeAlive = Howl.RegisterKey("me", "alive", HowlValueKind.Bool);
            MePos = Howl.RegisterKey("me", "pos", HowlValueKind.Vector3);
            MeYaw = Howl.RegisterKey("me", "yaw", HowlValueKind.Float);
            MeItem = Howl.RegisterKey("me", "item", HowlValueKind.String);
            PlayerName = Howl.RegisterKey("game_info", "player_name", HowlValueKind.String);
        }
    }
}
