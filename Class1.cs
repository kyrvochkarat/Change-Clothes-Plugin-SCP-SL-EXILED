using CommandSystem;
using Exiled.API.Enums;
using Exiled.API.Extensions;
using Exiled.API.Features;
using Exiled.API.Features.Items;
using Exiled.API.Features.Pickups;
using MEC;
using Mirror;
using PlayerRoles;
using PlayerRoles.Ragdolls;
using PlayerStatsSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChangeClothes
{
    public class Config : Exiled.API.Interfaces.IConfig
    {
        public bool IsEnabled { get; set; } = true;
        public bool Debug { get; set; } = false;
        public float MaxDistance { get; set; } = 3f;
        public float ChangeDuration { get; set; } = 30f;
        public float ViewAngle { get; set; } = 45f;
        public bool DisableAllEffectsAfterChange { get; set; } = true;
        public bool ChangeNickname { get; set; } = true;
        public bool TransferDescription { get; set; } = true;
    }

    public class Main : Plugin<Config>
    {
        public override string Name => "ChangeClothes";
        public override string Author => "vityanvsk";
        public override Version Version => new Version(1, 4, 0);
        public override Version RequiredExiledVersion => new Version(8, 9, 0);

        public static Main Instance { get; private set; }
        public static Dictionary<Player, CoroutineHandle> ActiveProcesses = new Dictionary<Player, CoroutineHandle>();
        public static Dictionary<Player, string> OriginalNicknames = new Dictionary<Player, string>();
        public static Dictionary<Player, string> OriginalDescriptions = new Dictionary<Player, string>();

        // Храним описания по UserId игрока, чьё описание нужно перенести
        public static Dictionary<string, string> SavedDescriptions = new Dictionary<string, string>();
        // Связываем никнейм трупа с UserId оригинального игрока
        public static Dictionary<string, string> RagdollToUserId = new Dictionary<string, string>();

        public override void OnEnabled()
        {
            Instance = this;
            Exiled.Events.Handlers.Player.Dying += OnPlayerDying;
            Exiled.Events.Handlers.Player.Left += OnPlayerLeft;
            Exiled.Events.Handlers.Server.RestartingRound += OnRestartingRound;
            Exiled.Events.Handlers.Player.Verified += OnPlayerVerified;
            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            Instance = null;
            Exiled.Events.Handlers.Player.Dying -= OnPlayerDying;
            Exiled.Events.Handlers.Player.Left -= OnPlayerLeft;
            Exiled.Events.Handlers.Server.RestartingRound -= OnRestartingRound;
            Exiled.Events.Handlers.Player.Verified -= OnPlayerVerified;
            base.OnDisabled();
        }

        private void OnPlayerVerified(Exiled.Events.EventArgs.Player.VerifiedEventArgs ev)
        {
            // Сохраняем оригинальный ник при входе
            if (!OriginalNicknames.ContainsKey(ev.Player))
            {
                OriginalNicknames[ev.Player] = ev.Player.Nickname;
            }
        }

        private void OnPlayerDying(Exiled.Events.EventArgs.Player.DyingEventArgs ev)
        {
            CancelProcess(ev.Player);

            // Сохраняем описание умирающего игрока
            if (Config.TransferDescription)
            {
                // Получаем описание из CharSet если оно есть
                var charSetInstance = Exiled.Loader.Loader.Plugins.FirstOrDefault(p => p.Name == "CharSet");
                if (charSetInstance != null)
                {
                    try
                    {
                        var mainType = charSetInstance.GetType().Assembly.GetType("CharSet.Main");
                        if (mainType != null)
                        {
                            var instanceProperty = mainType.GetProperty("Instance");
                            var instance = instanceProperty?.GetValue(null);
                            if (instance != null)
                            {
                                var descriptionsField = mainType.GetField("playerDescriptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (descriptionsField != null)
                                {
                                    var descriptions = descriptionsField.GetValue(instance) as Dictionary<string, string>;
                                    if (descriptions != null && descriptions.ContainsKey(ev.Player.UserId))
                                    {
                                        SavedDescriptions[ev.Player.UserId] = descriptions[ev.Player.UserId];
                                        RagdollToUserId[ev.Player.Nickname] = ev.Player.UserId;
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Если не удалось получить из CharSet, используем CustomInfo
                        if (!string.IsNullOrEmpty(ev.Player.CustomInfo))
                        {
                            SavedDescriptions[ev.Player.UserId] = ev.Player.CustomInfo;
                            RagdollToUserId[ev.Player.Nickname] = ev.Player.UserId;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(ev.Player.CustomInfo))
                {
                    // CharSet не найден, используем CustomInfo
                    SavedDescriptions[ev.Player.UserId] = ev.Player.CustomInfo;
                    RagdollToUserId[ev.Player.Nickname] = ev.Player.UserId;
                }
            }
        }

        private void OnPlayerLeft(Exiled.Events.EventArgs.Player.LeftEventArgs ev)
        {
            CancelProcess(ev.Player);
            OriginalNicknames.Remove(ev.Player);
            OriginalDescriptions.Remove(ev.Player);
        }

        private void OnRestartingRound()
        {
            foreach (var handle in ActiveProcesses.Values)
            {
                Timing.KillCoroutines(handle);
            }
            ActiveProcesses.Clear();
            OriginalNicknames.Clear();
            OriginalDescriptions.Clear();
            SavedDescriptions.Clear();
            RagdollToUserId.Clear();
        }

        public static void CancelProcess(Player player)
        {
            if (ActiveProcesses.ContainsKey(player))
            {
                Timing.KillCoroutines(ActiveProcesses[player]);
                ActiveProcesses.Remove(player);

                // Восстанавливаем оригинальный ник
                if (Main.Instance.Config.ChangeNickname && OriginalNicknames.ContainsKey(player))
                {
                    player.DisplayNickname = OriginalNicknames[player];
                }

                // Восстанавливаем оригинальное описание
                if (Main.Instance.Config.TransferDescription && OriginalDescriptions.ContainsKey(player))
                {
                    SetPlayerDescription(player, OriginalDescriptions[player]);
                }

                player.Broadcast(3, "<color=red>Процесс переодевания отменен!</color>");
            }
        }

        // Делаем метод публичным
        public static void SetPlayerDescription(Player player, string description)
        {
            // Пытаемся установить описание через CharSet
            var charSetInstance = Exiled.Loader.Loader.Plugins.FirstOrDefault(p => p.Name == "CharSet");
            if (charSetInstance != null)
            {
                try
                {
                    var mainType = charSetInstance.GetType().Assembly.GetType("CharSet.Main");
                    if (mainType != null)
                    {
                        var instanceProperty = mainType.GetProperty("Instance");
                        var instance = instanceProperty?.GetValue(null);
                        if (instance != null)
                        {
                            var setDescriptionMethod = mainType.GetMethod("SetDescription");
                            if (setDescriptionMethod != null)
                            {
                                setDescriptionMethod.Invoke(instance, new object[] { player, description });
                                return;
                            }
                        }
                    }
                }
                catch { }
            }

            // Если CharSet недоступен, используем CustomInfo напрямую
            player.CustomInfo = description;
        }
    }

    [CommandHandler(typeof(ClientCommandHandler))]
    public class ChangeClothesCommand : ICommand
    {
        public string Command => "changeclothes";
        public string[] Aliases => new[] { "cc" };
        public string Description => "Позволяет переодеться в класс трупа, на который вы смотрите";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            Player player = Player.Get(sender);

            if (player == null)
            {
                response = "Эту команду может использовать только игрок!";
                return false;
            }

            if (!player.IsAlive)
            {
                response = "Вы должны быть живы, чтобы использовать эту команду!";
                return false;
            }

            if (Main.ActiveProcesses.ContainsKey(player))
            {
                response = "Вы уже переодеваетесь!";
                return false;
            }

            // Находим ближайший труп
            Ragdoll nearestRagdoll = null;
            float nearestDistance = Main.Instance.Config.MaxDistance;

            foreach (var ragdoll in Ragdoll.List)
            {
                float distance = Vector3.Distance(player.Position, ragdoll.Position);

                if (distance < nearestDistance)
                {
                    Vector3 directionToRagdoll = (ragdoll.Position - player.CameraTransform.position).normalized;
                    float angle = Vector3.Angle(player.CameraTransform.forward, directionToRagdoll);

                    if (angle < Main.Instance.Config.ViewAngle)
                    {
                        nearestDistance = distance;
                        nearestRagdoll = ragdoll;
                    }
                }
            }

            if (nearestRagdoll == null)
            {
                response = "Рядом нет трупов или вы не смотрите на труп!";
                return false;
            }

            // Проверяем роль
            if (nearestRagdoll.Role == RoleTypeId.Scp079 ||
                nearestRagdoll.Role == RoleTypeId.Spectator ||
                nearestRagdoll.Role == RoleTypeId.Filmmaker ||
                nearestRagdoll.Role == RoleTypeId.Overwatch ||
                nearestRagdoll.Role == RoleTypeId.Scp049 ||
                nearestRagdoll.Role == RoleTypeId.Scp0492 ||
                nearestRagdoll.Role == RoleTypeId.Scp939 ||
                nearestRagdoll.Role == RoleTypeId.Scp096 ||
                nearestRagdoll.Role == RoleTypeId.Scp106 ||
                nearestRagdoll.Role == RoleTypeId.Scp173 ||
                nearestRagdoll.Role == RoleTypeId.Scp3114 ||
                nearestRagdoll.Role == RoleTypeId.None)
            {
                response = "Вы не можете переодеться в этот класс!";
                return false;
            }

            // Проверяем, не SCP ли пытается переодеться
            if (player.Role.Team == Team.SCPs)
            {
                response = "SCP не могут переодеваться!";
                return false;
            }

            var handle = Timing.RunCoroutine(ChangeClothesProcess(player, nearestRagdoll));
            Main.ActiveProcesses[player] = handle;

            response = "Вы начинаете переодеваться...";
            return true;
        }

        private IEnumerator<float> ChangeClothesProcess(Player player, Ragdoll ragdoll)
        {
            Vector3 startPosition = player.Position;
            Vector3 savedPosition = player.Position;
            Quaternion savedRotation = player.Rotation;
            RoleTypeId targetRole = ragdoll.Role;
            string ragdollNickname = ragdoll.Nickname; // Сохраняем ник трупа

            // Сохраняем оригинальные данные игрока
            if (Main.Instance.Config.ChangeNickname && !Main.OriginalNicknames.ContainsKey(player))
            {
                Main.OriginalNicknames[player] = player.Nickname;
            }

            // Сохраняем оригинальное описание
            if (Main.Instance.Config.TransferDescription && !Main.OriginalDescriptions.ContainsKey(player))
            {
                // Получаем текущее описание из CharSet
                var charSetInstance = Exiled.Loader.Loader.Plugins.FirstOrDefault(p => p.Name == "CharSet");
                if (charSetInstance != null)
                {
                    try
                    {
                        var mainType = charSetInstance.GetType().Assembly.GetType("CharSet.Main");
                        if (mainType != null)
                        {
                            var instanceProperty = mainType.GetProperty("Instance");
                            var instance = instanceProperty?.GetValue(null);
                            if (instance != null)
                            {
                                var descriptionsField = mainType.GetField("playerDescriptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (descriptionsField != null)
                                {
                                    var descriptions = descriptionsField.GetValue(instance) as Dictionary<string, string>;
                                    if (descriptions != null && descriptions.ContainsKey(player.UserId))
                                    {
                                        Main.OriginalDescriptions[player] = descriptions[player.UserId];
                                    }
                                    else
                                    {
                                        Main.OriginalDescriptions[player] = "";
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        Main.OriginalDescriptions[player] = player.CustomInfo ?? "";
                    }
                }
                else
                {
                    Main.OriginalDescriptions[player] = player.CustomInfo ?? "";
                }
            }

            float duration = Main.Instance.Config.ChangeDuration;
            float elapsed = 0f;

            // Сохраняем инвентарь
            List<ItemType> savedItems = new List<ItemType>();
            Dictionary<AmmoType, ushort> savedAmmo = new Dictionary<AmmoType, ushort>();

            foreach (var item in player.Items.ToList())
            {
                if (item != null)
                    savedItems.Add(item.Type);
            }

            // Сохраняем патроны
            foreach (AmmoType ammoType in Enum.GetValues(typeof(AmmoType)))
            {
                ushort count = player.GetAmmo(ammoType);
                if (count > 0)
                    savedAmmo[ammoType] = count;
            }

            RoleTypeId oldRole = player.Role.Type;
            float healthPercentage = player.Health / player.MaxHealth;

            // Процесс переодевания
            while (elapsed < duration)
            {
                if (!player.IsAlive)
                {
                    Main.ActiveProcesses.Remove(player);
                    yield break;
                }

                if (Vector3.Distance(player.Position, startPosition) > 2f)
                {
                    player.Broadcast(3, "<color=red>Вы отошли слишком далеко! Переодевание отменено.</color>");

                    // Восстанавливаем оригинальные данные при отмене
                    if (Main.Instance.Config.ChangeNickname && Main.OriginalNicknames.ContainsKey(player))
                    {
                        player.DisplayNickname = Main.OriginalNicknames[player];
                    }
                    if (Main.Instance.Config.TransferDescription && Main.OriginalDescriptions.ContainsKey(player))
                    {
                        Main.SetPlayerDescription(player, Main.OriginalDescriptions[player]);
                    }

                    Main.ActiveProcesses.Remove(player);
                    yield break;
                }

                if (!Ragdoll.List.Contains(ragdoll))
                {
                    player.Broadcast(3, "<color=red>Труп исчез! Переодевание отменено.</color>");

                    // Восстанавливаем оригинальные данные при отмене
                    if (Main.Instance.Config.ChangeNickname && Main.OriginalNicknames.ContainsKey(player))
                    {
                        player.DisplayNickname = Main.OriginalNicknames[player];
                    }
                    if (Main.Instance.Config.TransferDescription && Main.OriginalDescriptions.ContainsKey(player))
                    {
                        Main.SetPlayerDescription(player, Main.OriginalDescriptions[player]);
                    }

                    Main.ActiveProcesses.Remove(player);
                    yield break;
                }

                int percentage = Mathf.RoundToInt((elapsed / duration) * 100);
                int remainingSeconds = Mathf.CeilToInt(duration - elapsed);

                string progressBar = GenerateProgressBar(percentage);
                player.Broadcast(1, $"<color=yellow>Переодевание: {progressBar} {percentage}% ({remainingSeconds} сек)</color>", Broadcast.BroadcastFlags.Normal);

                elapsed += 1f;
                yield return Timing.WaitForSeconds(1f);
            }

            // Финальные проверки
            if (!player.IsAlive || !Ragdoll.List.Contains(ragdoll))
            {
                Main.ActiveProcesses.Remove(player);
                yield break;
            }

            player.Broadcast(2, "<color=green>Переодевание завершено!</color>");

            // ВАЖНО: Сначала удаляем старый труп
            ragdoll.Destroy();

            // Ждем немного для синхронизации
            yield return Timing.WaitForSeconds(0.1f);

            // Создаем новый труп со старой ролью и оригинальным ником игрока
            var fakeHub = ReferenceHub.GetHub(GameObject.Instantiate(NetworkManager.singleton.playerPrefab));
            fakeHub.nicknameSync.Network_myNickSync = Main.OriginalNicknames.ContainsKey(player) ? Main.OriginalNicknames[player] : player.Nickname;

            var handler = new UniversalDamageHandler(0f, DeathTranslations.Unknown);
            var ragdollData = new RagdollData(
                fakeHub,
                handler,
                oldRole,
                startPosition,
                savedRotation,
                Main.OriginalNicknames.ContainsKey(player) ? Main.OriginalNicknames[player] : player.Nickname,
                NetworkTime.time
            );

            Ragdoll newRagdoll = Ragdoll.CreateAndSpawn(ragdollData);

            // Удаляем фейкового игрока
            GameObject.Destroy(fakeHub.gameObject);

            // Ждем создания трупа
            yield return Timing.WaitForSeconds(0.1f);

            // Сохраняем текущие эффекты статуса (не визуальные)
            var activeStatusEffects = new Dictionary<EffectType, float>();
            foreach (var effect in player.ActiveEffects)
            {
                if (effect.GetEffectType() != EffectType.Blinded &&
                    effect.GetEffectType() != EffectType.Flashed &&
                    effect.GetEffectType() != EffectType.Blurred &&
                    effect.GetEffectType() != EffectType.SpawnProtected)
                {
                    activeStatusEffects[effect.GetEffectType()] = effect.Duration;
                }
            }

            // Теперь меняем роль игрока
            player.ReferenceHub.roleManager.ServerSetRole(targetRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);

            // Ждем применения роли
            yield return Timing.WaitForSeconds(0.2f);

            // Меняем ник на ник трупа
            if (Main.Instance.Config.ChangeNickname)
            {
                player.DisplayNickname = ragdollNickname;
            }

            // Переносим описание от трупа
            if (Main.Instance.Config.TransferDescription)
            {
                string newDescription = "";

                // Проверяем есть ли сохраненное описание для этого трупа
                if (Main.RagdollToUserId.ContainsKey(ragdollNickname))
                {
                    string originalUserId = Main.RagdollToUserId[ragdollNickname];
                    if (Main.SavedDescriptions.ContainsKey(originalUserId))
                    {
                        newDescription = Main.SavedDescriptions[originalUserId];
                    }
                }

                // Устанавливаем новое описание через CharSet
                Main.SetPlayerDescription(player, newDescription);
            }

            // Возвращаем позицию
            player.Position = savedPosition;
            player.Rotation = savedRotation;

            // Полностью очищаем все эффекты
            player.DisableAllEffects();

            // Восстанавливаем только статусные эффекты
            foreach (var effect in activeStatusEffects)
            {
                if (effect.Value > 0)
                {
                    player.EnableEffect(effect.Key, effect.Value);
                }
            }

            // Очищаем инвентарь
            player.ClearInventory();

            // Восстанавливаем предметы
            foreach (var itemType in savedItems)
            {
                if (CanRoleHoldItem(targetRole, itemType))
                {
                    player.AddItem(itemType);
                }
                else
                {
                    // Выбрасываем несовместимые предметы
                    Pickup.CreateAndSpawn(itemType, savedPosition, Quaternion.identity);
                }
            }

            // Восстанавливаем патроны
            foreach (var ammo in savedAmmo)
            {
                player.SetAmmo(ammo.Key, ammo.Value);
            }

            // Восстанавливаем здоровье
            float newHealth = Mathf.Clamp(player.MaxHealth * healthPercentage, 1f, player.MaxHealth);
            player.Health = newHealth;

            // Финальная телепортация для гарантии
            yield return Timing.WaitForSeconds(0.1f);
            player.Position = savedPosition;

            // Еще раз убираем визуальные эффекты
            player.DisableEffect(EffectType.Blinded);
            player.DisableEffect(EffectType.Flashed);
            player.DisableEffect(EffectType.Blurred);
            player.DisableEffect(EffectType.SpawnProtected);

            player.Broadcast(5, $"<color=green>Вы успешно переоделись в {GetRoleName(targetRole)}!\nТеперь вы: {ragdollNickname}</color>");

            Main.ActiveProcesses.Remove(player);
        }

        private bool CanRoleHoldItem(RoleTypeId role, ItemType item)
        {
            // SCP не могут держать предметы
            if (PlayerRoles.PlayerRolesUtils.GetTeam(role) == Team.SCPs)
                return false;

            // Зомби SCP-049-2 не может держать предметы
            if (role == RoleTypeId.Scp0492)
                return false;

            return true;
        }

        private string GenerateProgressBar(int percentage)
        {
            int filledBars = percentage / 10;
            string bar = "[";

            for (int i = 0; i < 10; i++)
            {
                if (i < filledBars)
                    bar += "█";
                else
                    bar += "░";
            }

            bar += "]";
            return bar;
        }

        private string GetRoleName(RoleTypeId role)
        {
            switch (role)
            {
                case RoleTypeId.ClassD:
                    return "Класс-D";
                case RoleTypeId.Scientist:
                    return "Ученый";
                case RoleTypeId.FacilityGuard:
                    return "Охранник";
                case RoleTypeId.NtfPrivate:
                    return "Кадет МОГ";
                case RoleTypeId.NtfSergeant:
                    return "Сержант МОГ";
                case RoleTypeId.NtfCaptain:
                    return "Капитан МОГ";
                case RoleTypeId.NtfSpecialist:
                    return "Специалист МОГ";
                case RoleTypeId.ChaosConscript:
                    return "Рядовой Хаоса";
                case RoleTypeId.ChaosRifleman:
                    return "Стрелок Хаоса";
                case RoleTypeId.ChaosRepressor:
                    return "Подавитель Хаоса";
                case RoleTypeId.ChaosMarauder:
                    return "Мародер Хаоса";
                case RoleTypeId.Tutorial:
                    return "Tutorial";
                case RoleTypeId.Scp0492:
                    return "SCP-049-2";
                case RoleTypeId.Scp049:
                    return "SCP-049";
                case RoleTypeId.Scp096:
                    return "SCP-096";
                case RoleTypeId.Scp173:
                    return "SCP-173";
                case RoleTypeId.Scp106:
                    return "SCP-106";
                case RoleTypeId.Scp939:
                    return "SCP-939";
                case RoleTypeId.Scp3114:
                    return "SCP-3114";
                default:
                    return role.ToString();
            }
        }
    }
}