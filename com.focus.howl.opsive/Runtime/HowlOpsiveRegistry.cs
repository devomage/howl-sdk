using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Focus.Howl.Opsive
{
    public enum HowlEntityRole : byte { Player, Agent, Object }

    public sealed class HowlEntity
    {
        public GameObject GameObject;
        public HowlEntityRole Role;
        /// <summary>String table index of the unique entity name.</summary>
        public int Name;
        /// <summary>String table index of the category.</summary>
        public int Category;
    }

    /// <summary>Wired entities plus cached string-table indices, so event handlers never build strings.</summary>
    public static class HowlOpsiveRegistry
    {
        static readonly Dictionary<GameObject, HowlEntity> s_Entities = new Dictionary<GameObject, HowlEntity>();
        static readonly Dictionary<string, int> s_NameCounts = new Dictionary<string, int>();
        static readonly Dictionary<object, int> s_ItemNames = new Dictionary<object, int>();
        static readonly Dictionary<Type, int> s_TypeNames = new Dictionary<Type, int>();

        public static HowlEntity Player { get; internal set; }
        public static IReadOnlyCollection<HowlEntity> Entities => s_Entities.Values;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset()
        {
            s_Entities.Clear();
            s_NameCounts.Clear();
            s_ItemNames.Clear();
            s_TypeNames.Clear();
            Player = null;
        }

        public static bool Contains(GameObject go) => s_Entities.ContainsKey(go);

        public static HowlEntity Add(GameObject go, HowlEntityRole role, string category)
        {
            string baseName = go.name.Replace("(Clone)", "").Trim();
            s_NameCounts.TryGetValue(baseName, out int n);
            s_NameCounts[baseName] = n + 1;
            var entity = new HowlEntity
            {
                GameObject = go,
                Role = role,
                Name = Howl.RegisterString(n == 0 ? baseName : baseName + "#" + n),
                Category = Howl.RegisterString(category)
            };
            s_Entities.Add(go, entity);
            return entity;
        }

        /// <summary>Finds the wired entity for an attacker or hit object, walking up the hierarchy. Null if unknown.</summary>
        public static HowlEntity Resolve(GameObject go)
        {
            if (go == null) return null;
            var t = go.transform;
            while (t != null)
            {
                if (s_Entities.TryGetValue(t.gameObject, out var entity)) return entity;
                t = t.parent;
            }
            return null;
        }

        /// <summary>String index for an entity, or the "none" string for unknown sources (environment, fall damage).</summary>
        public static int NameOf(HowlEntity entity) => entity != null ? entity.Name : HowlOpsiveEvents.None;

        public static int ItemName(object itemDefinition)
        {
            if (itemDefinition == null) return HowlOpsiveEvents.None;
            if (s_ItemNames.TryGetValue(itemDefinition, out int index)) return index;
            string name = itemDefinition is Object unityObject ? unityObject.name : itemDefinition.ToString();
            index = Howl.RegisterString(name);
            s_ItemNames.Add(itemDefinition, index);
            return index;
        }

        public static int TypeName(Type type)
        {
            if (s_TypeNames.TryGetValue(type, out int index)) return index;
            index = Howl.RegisterString(type.Name);
            s_TypeNames.Add(type, index);
            return index;
        }
    }
}
