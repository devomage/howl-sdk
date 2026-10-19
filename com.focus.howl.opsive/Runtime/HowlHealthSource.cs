using Opsive.UltimateCharacterController.Traits;
using UnityEngine;
using EventHandler = Opsive.Shared.Events.EventHandler;

namespace Focus.Howl.Opsive
{
    /// <summary>Attached at runtime to anything with a UCC Health that isn't a character: crates, turrets, targets.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class HowlHealthSource : MonoBehaviour
    {
        HowlEntity m_Entity;
        HowlOpsiveSettings m_Settings;
        Transform m_Transform;
        bool m_Initialized;

        public HowlEntity Entity => m_Entity;

        internal void Init(HowlEntity entity, HowlOpsiveSettings settings)
        {
            m_Entity = entity;
            m_Settings = settings;
            m_Transform = transform;

            // Registered in Init/OnDestroy rather than OnEnable/OnDisable: destroyed objects are deactivated and the
            // Respawner re-enables them, and the respawn event must still arrive.
            var go = gameObject;
            EventHandler.RegisterEvent<float, Vector3, Vector3, GameObject, Collider>(go, "OnHealthDamage", OnHealthDamage);
            EventHandler.RegisterEvent<Vector3, Vector3, GameObject>(go, "OnDeath", OnDeath);
            EventHandler.RegisterEvent(go, "OnRespawn", OnRespawn);
            m_Initialized = true;
        }

        void OnDestroy()
        {
            if (!m_Initialized) return;
            var go = gameObject;
            EventHandler.UnregisterEvent<float, Vector3, Vector3, GameObject, Collider>(go, "OnHealthDamage", OnHealthDamage);
            EventHandler.UnregisterEvent<Vector3, Vector3, GameObject>(go, "OnDeath", OnDeath);
            EventHandler.UnregisterEvent(go, "OnRespawn", OnRespawn);
        }

        void OnHealthDamage(float amount, Vector3 position, Vector3 force, GameObject attacker, Collider hitCollider)
        {
            if (m_Settings.ObjectDamageEvents) Howl.Emit(HowlOpsiveEvents.ObjectDamaged, m_Entity.Name, m_Entity.Category, amount);
            var source = HowlOpsiveRegistry.Resolve(attacker);
            if (m_Settings.PlayerEvents && source != null && source == HowlOpsiveRegistry.Player)
                Howl.Emit(HowlOpsiveEvents.Hit, m_Entity.Name, m_Entity.Category, amount);
        }

        void OnDeath(Vector3 position, Vector3 force, GameObject attacker)
        {
            if (m_Settings.ObjectEvents) Howl.Emit(HowlOpsiveEvents.ObjectDestroyed, m_Entity.Name, m_Entity.Category, m_Transform.position);
        }

        void OnRespawn()
        {
            if (m_Settings.ObjectEvents) Howl.Emit(HowlOpsiveEvents.ObjectRespawn, m_Entity.Name, m_Entity.Category);
        }
    }
}
