using UnityEngine;

namespace Focus.Howl
{
    /// <summary>
    /// The common GEP-style vocabulary. Every integration uses these, so an event name always carries the same fields
    /// (registration is first-come by name). Integration-specific events are registered next to the integration.
    /// </summary>
    public static class HowlStandard
    {
        public static EventId MatchStart, MatchEnd, Kill, Death, Respawn, Hit, DamageTaken, Heal, Pickup, SpellCast, LevelUp;

        public static KeyId PlayerName;
        public static KeyId Map, Mode, MatchId, MatchState, TimeLeft;
        public static KeyId MeHealth, MeMana, MeShield, MeAlive, MePos, MeYaw, MeLevel, MeKills, MeDeaths, MeItem;

        /// <summary>String index used for unknown sources and empty values.</summary>
        public static int None;

        static bool s_Registered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Registered = false;

        public static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;

            const HowlField S = HowlField.String;
            const HowlField I = HowlField.Int;
            const HowlField No = HowlField.None;
            const HowlVectorField Sc = HowlVectorField.Scalar;
            const HowlVectorField V = HowlVectorField.Vector;

            None = Howl.RegisterString("none");

            MatchStart = Howl.RegisterEvent("match_start", "match_id", S, "map", S);
            MatchEnd = Howl.RegisterEvent("match_end", "match_id", S, "winner", S);
            Kill = Howl.RegisterEvent("kill", "victim", S, "victim_category", S, "pos", V);
            Death = Howl.RegisterEvent("death", "attacker", S, null, No, "pos", V);
            Respawn = Howl.RegisterEvent("respawn", xName: "pos", xKind: V);
            Hit = Howl.RegisterEvent("hit", "target", S, "target_category", S, "amount", Sc);
            DamageTaken = Howl.RegisterEvent("damage_taken", "attacker", S, null, No, "amount", Sc);
            Heal = Howl.RegisterEvent("heal", xName: "amount", xKind: Sc);
            Pickup = Howl.RegisterEvent("pickup", "item", S, "amount", I);
            SpellCast = Howl.RegisterEvent("spell_cast", "spell", S, null, No, "pos", V);
            LevelUp = Howl.RegisterEvent("level_up", "level", I);

            PlayerName = Howl.RegisterKey("game_info", "player_name", HowlValueKind.String);
            Map = Howl.RegisterKey("match_info", "map", HowlValueKind.String);
            Mode = Howl.RegisterKey("match_info", "mode", HowlValueKind.String);
            MatchId = Howl.RegisterKey("match_info", "match_id", HowlValueKind.String);
            MatchState = Howl.RegisterKey("match_info", "state", HowlValueKind.String);
            TimeLeft = Howl.RegisterKey("match_info", "time_left", HowlValueKind.Float);

            MeHealth = Howl.RegisterKey("me", "health", HowlValueKind.Float);
            MeMana = Howl.RegisterKey("me", "mana", HowlValueKind.Float);
            MeShield = Howl.RegisterKey("me", "shield", HowlValueKind.Float);
            MeAlive = Howl.RegisterKey("me", "alive", HowlValueKind.Bool);
            MePos = Howl.RegisterKey("me", "pos", HowlValueKind.Vector3);
            MeYaw = Howl.RegisterKey("me", "yaw", HowlValueKind.Float);
            MeLevel = Howl.RegisterKey("me", "level", HowlValueKind.Int);
            MeKills = Howl.RegisterKey("me", "kills", HowlValueKind.Int);
            MeDeaths = Howl.RegisterKey("me", "deaths", HowlValueKind.Int);
            MeItem = Howl.RegisterKey("me", "item", HowlValueKind.String);
        }
    }
}
