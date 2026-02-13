using System;
using UnityEngine;
using CardGame.Core.Cards;
using CardGame.Gameplay.Units;

namespace CardGame.Gameplay.Zones
{
    /// <summary>
    /// 採集区：人物が入ると一定間隔で素材生成
    /// </summary>
    public sealed class GatheringZone : ZoneBase
    {
        public enum GatherType { Wood, Stone }

        [SerializeField] private GatherType gatherType = GatherType.Wood;
        [SerializeField] private float intervalFallback = 5f;

        public event Action<CharacterCard, GatherType> OnGatherTick;

        private float _timer;

        private void Update()
        {
            _timer += UnityEngine.Time.deltaTime;
            if (_timer < intervalFallback) return;
            _timer = 0f;

            for (int i = 0; i < Inside.Count; i++)
            {
                var entity = Inside[i];
                if (entity == null) continue;

                var view = entity as CardWorldView;
                if (view == null) continue;

                var character = view.GetComponent<CharacterCard>();
                if (character == null) continue;
                if (character.IsDead) continue;

                OnGatherTick?.Invoke(character, gatherType);
                // TODO: 素材カード生成
            }
        }

        protected override void OnEnterInternal(ICardEntity card) { }
        protected override void OnExitInternal(ICardEntity card) { }
    }
}
