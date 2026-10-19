using Opsive.Shared.Inventory;
using Opsive.UltimateCharacterController.Camera;
using Opsive.UltimateCharacterController.Character.Abilities;
using Opsive.UltimateCharacterController.Items;
using Opsive.UltimateCharacterController.Items.Actions;
using Opsive.UltimateCharacterController.Traits;
using UnityEngine;
using EventHandler = Opsive.Shared.Events.EventHandler;

namespace Focus.Howl.Opsive
{
    /// <summary>
    /// Attached at runtime to every UCC character. The character the camera follows is the player (me.*);
    /// every other character is an agent (roster.*). Role follows the camera, so character swaps just work.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class HowlCharacterSource : MonoBehaviour
    {
        HowlEntity m_Entity;
        HowlOpsiveSettings m_Settings;
        Health m_Health;
        Transform m_Transform;
        KeyId m_RosterHealth, m_RosterAlive;
        bool m_Initialized;
        bool m_PublishPending;
        // UCC fires the pickup event once per slot the item fits (dual-wield fills several), so repeats are dropped.
        int m_LastPickupFrame = -1, m_LastPickupItem, m_LastPickupAmount;

        public HowlEntity Entity => m_Entity;
        bool IsPlayer => m_Entity.Role == HowlEntityRole.Player;

        internal void Init(HowlEntity entity, HowlOpsiveSettings settings, bool isPlayer)
        {
            m_Entity = entity;
            m_Settings = settings;
            m_Health = GetComponent<Health>();
            m_Transform = transform;

            var go = gameObject;
            EventHandler.RegisterEvent<CameraController>(go, "OnCharacterAttachCamera", OnAttachCamera);
            EventHandler.RegisterEvent<float, Vector3, Vector3, GameObject, Collider>(go, "OnHealthDamage", OnHealthDamage);
            EventHandler.RegisterEvent<float>(go, "OnHealthHeal", OnHealthHeal);
            EventHandler.RegisterEvent<Vector3, Vector3, GameObject>(go, "OnDeath", OnDeath);
            EventHandler.RegisterEvent(go, "OnRespawn", OnRespawn);
            EventHandler.RegisterEvent<IItemIdentifier, int, bool, bool>(go, "OnInventoryPickupItemIdentifier", OnPickupItem);
            EventHandler.RegisterEvent<IUsableItem>(go, "OnItemUse", OnItemUse);
            EventHandler.RegisterEvent<IReloadableItem>(go, "OnItemReload", OnItemReload);
            EventHandler.RegisterEvent<CharacterItem, int>(go, "OnInventoryEquipItem", OnEquipItem);
            EventHandler.RegisterEvent<Ability, bool>(go, "OnCharacterAbilityActive", OnAbilityActive);
            m_Initialized = true;

            SetRole(isPlayer);
            // Inactive characters publish once they activate; until then they have no roster entry.
            m_PublishPending = go.activeInHierarchy;
        }

        void OnEnable()
        {
            // UCC initializes health after OnEnable on activation (demo zones), so publish on the next LateUpdate.
            if (m_Initialized) m_PublishPending = true;
        }

        void OnDestroy()
        {
            if (!m_Initialized) return;
            var go = gameObject;
            EventHandler.UnregisterEvent<CameraController>(go, "OnCharacterAttachCamera", OnAttachCamera);
            EventHandler.UnregisterEvent<float, Vector3, Vector3, GameObject, Collider>(go, "OnHealthDamage", OnHealthDamage);
            EventHandler.UnregisterEvent<float>(go, "OnHealthHeal", OnHealthHeal);
            EventHandler.UnregisterEvent<Vector3, Vector3, GameObject>(go, "OnDeath", OnDeath);
            EventHandler.UnregisterEvent(go, "OnRespawn", OnRespawn);
            EventHandler.UnregisterEvent<IItemIdentifier, int, bool, bool>(go, "OnInventoryPickupItemIdentifier", OnPickupItem);
            EventHandler.UnregisterEvent<IUsableItem>(go, "OnItemUse", OnItemUse);
            EventHandler.UnregisterEvent<IReloadableItem>(go, "OnItemReload", OnItemReload);
            EventHandler.UnregisterEvent<CharacterItem, int>(go, "OnInventoryEquipItem", OnEquipItem);
            EventHandler.UnregisterEvent<Ability, bool>(go, "OnCharacterAbilityActive", OnAbilityActive);
            if (HowlOpsiveRegistry.Player == m_Entity) HowlOpsiveRegistry.Player = null;
        }

        void SetRole(bool isPlayer)
        {
            if (isPlayer)
            {
                if (HowlOpsiveRegistry.Player != null && HowlOpsiveRegistry.Player != m_Entity)
                    HowlOpsiveRegistry.Player.Role = HowlEntityRole.Agent;
                m_Entity.Role = HowlEntityRole.Player;
                HowlOpsiveRegistry.Player = m_Entity;
                // A character wired before the camera attached was published as an agent first.
                Howl.Clear(m_RosterHealth);
                Howl.Clear(m_RosterAlive);
                Howl.SetString(HowlOpsiveEvents.PlayerName, m_Entity.Name);
            }
            else if (IsPlayer)
            {
                m_Entity.Role = HowlEntityRole.Agent;
                if (HowlOpsiveRegistry.Player == m_Entity) HowlOpsiveRegistry.Player = null;
            }
        }

        void LateUpdate()
        {
            if (m_PublishPending)
            {
                m_PublishPending = false;
                PublishHealth();
            }
            // Continuous state is written into its slot every frame; the worker samples it at its own rate.
            if (!IsPlayer) return;
            Howl.Set(HowlOpsiveEvents.MePos, m_Transform.position);
            Howl.Set(HowlOpsiveEvents.MeYaw, m_Transform.eulerAngles.y);
        }

        void PublishHealth()
        {
            if (m_Health == null) return;
            bool alive = m_Health.IsAlive();
            if (IsPlayer)
            {
                Howl.Set(HowlOpsiveEvents.MeHealth, m_Health.HealthValue);
                Howl.Set(HowlOpsiveEvents.MeShield, m_Health.ShieldValue);
                Howl.Set(HowlOpsiveEvents.MeAlive, alive);
            }
            else if (m_Settings.AgentRosterInfo)
            {
                if (!m_RosterHealth.IsValid)
                {
                    // Registered on first use, so the player never gets roster keys unless it becomes an agent.
                    string name = Howl.GetString(m_Entity.Name);
                    m_RosterHealth = Howl.RegisterKey("roster", name + ".health", HowlValueKind.Float);
                    m_RosterAlive = Howl.RegisterKey("roster", name + ".alive", HowlValueKind.Bool);
                }
                Howl.Set(m_RosterHealth, m_Health.HealthValue);
                Howl.Set(m_RosterAlive, alive);
            }
        }

        void OnAttachCamera(CameraController cameraController)
        {
            SetRole(cameraController != null);
            PublishHealth();
        }

        void OnHealthDamage(float amount, Vector3 position, Vector3 force, GameObject attacker, Collider hitCollider)
        {
            var source = HowlOpsiveRegistry.Resolve(attacker);
            if (IsPlayer)
            {
                if (m_Settings.PlayerEvents) Howl.Emit(HowlOpsiveEvents.DamageTaken, HowlOpsiveRegistry.NameOf(source), 0, amount);
            }
            else
            {
                if (m_Settings.AgentDamageEvents) Howl.Emit(HowlOpsiveEvents.AgentDamage, m_Entity.Name, HowlOpsiveRegistry.NameOf(source), amount);
                if (m_Settings.PlayerEvents && source != null && source == HowlOpsiveRegistry.Player)
                    Howl.Emit(HowlOpsiveEvents.Hit, m_Entity.Name, m_Entity.Category, amount);
            }
            PublishHealth();
        }

        void OnHealthHeal(float amount)
        {
            if (IsPlayer && m_Settings.PlayerEvents) Howl.Emit(HowlOpsiveEvents.Heal, 0, 0, amount);
            PublishHealth();
        }

        void OnDeath(Vector3 position, Vector3 force, GameObject attacker)
        {
            var source = HowlOpsiveRegistry.Resolve(attacker);
            if (IsPlayer)
            {
                if (m_Settings.PlayerEvents) Howl.Emit(HowlOpsiveEvents.Death, HowlOpsiveRegistry.NameOf(source), 0, position);
            }
            else
            {
                if (m_Settings.AgentEvents) Howl.Emit(HowlOpsiveEvents.AgentDeath, m_Entity.Name, HowlOpsiveRegistry.NameOf(source), position);
                if (m_Settings.PlayerEvents && source != null && source == HowlOpsiveRegistry.Player)
                    Howl.Emit(HowlOpsiveEvents.Kill, m_Entity.Name, m_Entity.Category, position);
            }
            PublishHealth();
        }

        void OnRespawn()
        {
            if (IsPlayer)
            {
                if (m_Settings.PlayerEvents) Howl.Emit(HowlOpsiveEvents.Respawn, 0, 0, m_Transform.position);
            }
            else if (m_Settings.AgentEvents)
            {
                Howl.Emit(HowlOpsiveEvents.AgentRespawn, m_Entity.Name, 0, m_Transform.position);
            }
            PublishHealth();
        }

        void OnPickupItem(IItemIdentifier itemIdentifier, int amount, bool immediatePickup, bool forceEquip)
        {
            if (!IsPlayer || !m_Settings.PlayerEvents || itemIdentifier == null) return;
            int item = HowlOpsiveRegistry.ItemName(itemIdentifier.GetItemDefinition());
            int frame = Time.frameCount;
            if (frame == m_LastPickupFrame && item == m_LastPickupItem && amount == m_LastPickupAmount) return;
            m_LastPickupFrame = frame;
            m_LastPickupItem = item;
            m_LastPickupAmount = amount;
            Howl.Emit(HowlOpsiveEvents.Pickup, item, amount);
        }

        void OnItemUse(IUsableItem usableItem)
        {
            if (!IsPlayer || !m_Settings.PlayerEvents || !m_Settings.ItemUseEvents) return;
            Howl.Emit(HowlOpsiveEvents.ItemUse, ItemName(usableItem?.CharacterItem));
        }

        void OnItemReload(IReloadableItem reloadableItem)
        {
            if (!IsPlayer || !m_Settings.PlayerEvents) return;
            Howl.Emit(HowlOpsiveEvents.Reload, ItemName(reloadableItem?.CharacterItem));
        }

        void OnEquipItem(CharacterItem characterItem, int slotID)
        {
            if (!IsPlayer) return;
            int item = ItemName(characterItem);
            // me.item is the main hand (slot 0); off-hand equips are still reported as events.
            if (slotID == 0) Howl.SetString(HowlOpsiveEvents.MeItem, item);
            if (m_Settings.PlayerEvents) Howl.Emit(HowlOpsiveEvents.Equip, item, slotID);
        }

        void OnAbilityActive(Ability ability, bool active)
        {
            if (!active || ability == null || !IsPlayer || !m_Settings.PlayerEvents || !m_Settings.AbilityEvents) return;
            Howl.Emit(HowlOpsiveEvents.AbilityStart, HowlOpsiveRegistry.TypeName(ability.GetType()));
        }

        static int ItemName(CharacterItem characterItem) =>
            characterItem != null && characterItem.ItemIdentifier != null
                ? HowlOpsiveRegistry.ItemName(characterItem.ItemIdentifier.GetItemDefinition())
                : HowlOpsiveEvents.None;
    }
}
