using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommandSystem;
using Exiled.API.Features;
using Exiled.API.Interfaces;
using Exiled.Events.EventArgs.Player;
using MEC;
using PlayerRoles;
using PlayerRoles.Ragdolls;
using UnityEngine;
using RemoteAdmin;

namespace DisguisePlugin
{
    public class Disguise : Plugin<Config>
    {
        public override string Author => "vityanvsk";
        public override string Name => "Disguise";
        public override string Prefix => "disguise";
        public override Version Version => new Version(1, 5, 4);
        public override Version RequiredExiledVersion => new Version(9, 7, 0);

        internal static Dictionary<Player, CoroutineHandle> ActiveCoroutines = new Dictionary<Player, CoroutineHandle>();
        internal static Dictionary<ReferenceHub, RoleTypeId> DeathRoles = new Dictionary<ReferenceHub, RoleTypeId>();
        public static Disguise Instance { get; private set; }

        public override void OnEnabled()
        {
            Instance = this;
            Exiled.Events.Handlers.Player.Died += OnPlayerDied;
            Exiled.Events.Handlers.Player.Hurting += OnHurting;
            Exiled.Events.Handlers.Player.Destroying += OnPlayerDestroying;
            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            foreach (var coroutine in ActiveCoroutines.Values)
                Timing.KillCoroutines(coroutine);

            ActiveCoroutines.Clear();
            DeathRoles.Clear();
            Exiled.Events.Handlers.Player.Died -= OnPlayerDied;
            Exiled.Events.Handlers.Player.Hurting -= OnHurting;
            Exiled.Events.Handlers.Player.Destroying -= OnPlayerDestroying;
            Instance = null;
            base.OnDisabled();
        }

        private void OnPlayerDied(DiedEventArgs ev)
        {
            DeathRoles[ev.Player.ReferenceHub] = ev.TargetOldRole;

            if (Config.Debug)
                Log.Debug($"Игрок {ev.Player.Nickname} умер как {ev.TargetOldRole}. Роль сохранена.");
        }

        private void OnPlayerDestroying(DestroyingEventArgs ev)
        {
            if (DeathRoles.ContainsKey(ev.Player.ReferenceHub))
                DeathRoles.Remove(ev.Player.ReferenceHub);
        }

        private void OnHurting(HurtingEventArgs ev)
        {
            if (ActiveCoroutines.TryGetValue(ev.Player, out var handle))
            {
                Timing.KillCoroutines(handle);
                ActiveCoroutines.Remove(ev.Player);
                ev.Player.ShowHint("<color=red>Переодевание прервано из-за получения урона!</color>", 5);
            }
        }
    }

    public class Config : IConfig
    {
        [Description("Включен ли плагин")]
        public bool IsEnabled { get; set; } = true;

        [Description("Режим отладки")]
        public bool Debug { get; set; } = false;

        [Description("Время переодевания (секунды)")]
        public float DisguiseTime { get; set; } = 30f;

        [Description("Интервал уведомлений (секунды)")]
        public float NotifyInterval { get; set; } = 10f;

        [Description("Максимальная дистанция для переодевания")]
        public float MaxDisguiseDistance { get; set; } = 3f;

        [Description("Максимальное смещение при переодевании")]
        public float MaxMoveDistance { get; set; } = 0.2f;

        [Description("Запрещенные для переодевания роли")]
        public List<string> BlockedRoles { get; set; } = new List<string>
        {
            "Scp049",
            "Scp939",
            "Scp3114",
            "Scp096",
            "Scp106",
            "Scp173",
            "Scp0492",
            "Scp079",
        };
    }

    [CommandHandler(typeof(ClientCommandHandler))]
    public class ChangeClothesCommand : ICommand
    {
        public string Command => "changeclothes";
        public string[] Aliases => new string[] { "disguise" };
        public string Description => "Переодевает вас в труп, на который вы смотрите.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            response = string.Empty;

            if (!Player.TryGet(sender, out var player))
            {
                response = "Команда доступна только игрокам.";
                return false;
            }

            if (Disguise.ActiveCoroutines.ContainsKey(player))
            {
                response = "Вы уже переодеваетесь.";
                return false;
            }

            if (Disguise.Instance.Config.BlockedRoles.Contains(player.Role.ToString()))
            {
                response = "Ваша роль не может переодеваться.";
                return false;
            }

            if (!TryGetLookedRagdoll(player, out var target, out var deathRole))
            {
                response = $"Вы должны смотреть на труп (макс. {Disguise.Instance.Config.MaxDisguiseDistance}м)";
                return false;
            }

            Disguise.ActiveCoroutines[player] = Timing.RunCoroutine(DisguiseCoroutine(player, target, deathRole));
            response = "Начато переодевание... Не двигайтесь!";
            return true;
        }

        private bool TryGetLookedRagdoll(Player player, out Player target, out RoleTypeId deathRole)
        {
            target = null;
            deathRole = RoleTypeId.None;

            int layerMask = (1 << 0) | (1 << 10) | (1 << 17);

            for (int i = 0; i < 5; i++)
            {
                Vector3 direction = player.CameraTransform.forward;

                if (i > 0)
                {
                    float angle = (i * 5f) - 10f;
                    direction = Quaternion.Euler(0, angle, 0) * direction;
                }

                if (Physics.Raycast(
                    player.CameraTransform.position,
                    direction,
                    out var hit,
                    Disguise.Instance.Config.MaxDisguiseDistance,
                    layerMask))
                {
                    BasicRagdoll ragdoll = hit.collider.GetComponentInParent<BasicRagdoll>();
                    if (ragdoll == null)
                        ragdoll = hit.collider.GetComponent<BasicRagdoll>();

                    if (ragdoll != null)
                    {
                        var targetHub = ragdoll.Info.OwnerHub;
                        if (targetHub != null && Disguise.DeathRoles.TryGetValue(targetHub, out deathRole))
                        {
                            target = Player.Get(targetHub);
                            if (target != null && !target.IsAlive)
                            {
                                if (Disguise.Instance.Config.Debug)
                                    Log.Debug($"Найден труп: {target.Nickname} (Роль при смерти: {deathRole})");
                                return true;
                            }
                        }
                    }
                }
            }

            if (Disguise.Instance.Config.Debug)
                Log.Debug("Труп не найден в поле зрения или данные о роли отсутствуют");
            return false;
        }

        private IEnumerator<float> DisguiseCoroutine(Player player, Player target, RoleTypeId deathRole)
        {
            Vector3 startPos = player.Position;
            float timer = 0;
            float nextNotify = Disguise.Instance.Config.NotifyInterval;

            while (timer < Disguise.Instance.Config.DisguiseTime)
            {
                yield return Timing.WaitForSeconds(1f);
                timer++;

                if (!TryGetLookedRagdoll(player, out var newTarget, out _) || newTarget != target)
                {
                    player.ShowHint("<color=red>Переодевание прервано: вы отвернулись от трупа!</color>", 5);
                    break;
                }

                if (Vector3.Distance(player.Position, startPos) > Disguise.Instance.Config.MaxMoveDistance)
                {
                    player.ShowHint("<color=red>Переодевание прервано: вы сдвинулись с места!</color>", 5);
                    break;
                }

                if (timer >= nextNotify)
                {
                    float remain = Disguise.Instance.Config.DisguiseTime - timer;
                    player.ShowHint($"<color=yellow>Переодевание... {remain} сек. осталось</color>", 3);
                    nextNotify += Disguise.Instance.Config.NotifyInterval;
                }
            }

            if (timer >= Disguise.Instance.Config.DisguiseTime)
            {
                if (deathRole == RoleTypeId.None)
                {
                    player.ShowHint("<color=red>Ошибка: не удалось определить роль трупа!</color>", 5);
                    yield break;
                }

                player.Role.Set(deathRole, RoleSpawnFlags.AssignInventory);

                Timing.CallDelayed(0.5f, () =>
                {
                    player.CustomInfo = target.CustomInfo;
                    player.ShowHint($"<color=green>Вы переоделись в: {target.Nickname} ({deathRole})</color>", 10);

                    if (Disguise.Instance.Config.Debug)
                        Log.Debug($"{player.Nickname} переоделся в {target.Nickname} (Роль: {deathRole})");
                });
            }

            Disguise.ActiveCoroutines.Remove(player);
        }
    }
}
