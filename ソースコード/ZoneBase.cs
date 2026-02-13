using System;
using System.Collections.Generic;
using UnityEngine;
using CardGame.Core.Cards;
using CardGame.Core.Zones;
using CardGame.Data.Defs;

namespace CardGame.Gameplay.Zones
{
    /// <summary>
    /// 機能区（探索区/採集/戦闘）の共通基底
    /// </summary>
    public abstract class ZoneBase : MonoBehaviour, IZone
    {
        [SerializeField] private ZoneDefinition definition;

        protected readonly List<ICardEntity> Inside = new();

        public string ZoneId => definition != null ? definition.zoneId : name;
        public string DebugName => definition != null ? definition.displayName : name;

        public event Action<ICardEntity> OnEntered;
        public event Action<ICardEntity> OnExited;

        protected virtual void OnEnable()
        {
            ZoneRegistry.Register(this);
        }

        protected virtual void OnDisable()
        {
            ZoneRegistry.Unregister(this);
        }

        public virtual bool CanEnter(ICardEntity card) => card != null;

        public virtual void Enter(ICardEntity card)
        {
            if (card == null) return;
            if (!CanEnter(card)) return;
            if (Inside.Contains(card)) return;

            Inside.Add(card);
            OnEntered?.Invoke(card);
            OnEnterInternal(card);
        }

        public virtual void Exit(ICardEntity card)
        {
            if (card == null) return;
            if (!Inside.Remove(card)) return;

            OnExited?.Invoke(card);
            OnExitInternal(card);
        }

        protected abstract void OnEnterInternal(ICardEntity card);
        protected abstract void OnExitInternal(ICardEntity card);
    }
}
