﻿using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;

namespace Barotrauma
{
    partial class HumanAIController : AIController
    {
        public static bool DisableCrewAI;

        private AIObjectiveManager objectiveManager;
        
        private float sortTimer;
        private float crouchRaycastTimer;
        private float reportTimer;
        private float hullVisibilityTimer;
        private bool shouldCrouch;

        const float crouchRaycastInterval = 1;
        const float reportInterval = 1;
        const float sortObjectiveInterval = 1;
        const float hullVisibilityInterval = 1;

        public static float HULL_SAFETY_THRESHOLD = 50;

        public HashSet<Hull> UnsafeHulls { get; private set; } = new HashSet<Hull>();

        private SteeringManager outsideSteering, insideSteering;

        public IndoorsSteeringManager PathSteering => insideSteering as IndoorsSteeringManager;
        public HumanoidAnimController AnimController => Character.AnimController as HumanoidAnimController;

        public override AIObjectiveManager ObjectiveManager
        {
            get { return objectiveManager; }
        }

        public Order CurrentOrder
        {
            get;
            private set;
        }

        public string CurrentOrderOption
        {
            get;
            private set;
        }

        private IEnumerable<Hull> visibleHulls;
        public IEnumerable<Hull> VisibleHulls
        {
            get
            {
                if (visibleHulls == null)
                {
                    visibleHulls = Character.GetVisibleHulls();
                }
                return visibleHulls;
            }
            private set
            {
                visibleHulls = value;
            }
        }

        public HumanAIController(Character c) : base(c)
        {
            insideSteering = new IndoorsSteeringManager(this, true, false);
            outsideSteering = new SteeringManager(this);
            objectiveManager = new AIObjectiveManager(c);
            reportTimer = Rand.Range(0f, reportInterval);
            sortTimer = Rand.Range(0f, sortObjectiveInterval);
            hullVisibilityTimer = Rand.Range(0f, hullVisibilityTimer);
            InitProjSpecific();
        }
        partial void InitProjSpecific();

        public override void Update(float deltaTime)
        {
            if (DisableCrewAI || Character.IsUnconscious) return;
            
            if (Character.Submarine != null || SelectedAiTarget?.Entity?.Submarine != null)
            {
                if (steeringManager != insideSteering) insideSteering.Reset();
                steeringManager = insideSteering;
            }
            else
            {
                if (steeringManager != outsideSteering) outsideSteering.Reset();
                steeringManager = outsideSteering;
            }

            AnimController.Crouching = shouldCrouch;
            CheckCrouching(deltaTime);
            Character.ClearInputs();
            
            if (hullVisibilityTimer > 0)
            {
                hullVisibilityTimer--;
            }
            else
            {
                hullVisibilityTimer = hullVisibilityInterval;
                VisibleHulls = Character.GetVisibleHulls();
            }

            objectiveManager.UpdateObjectives(deltaTime);
            if (sortTimer > 0.0f)
            {
                sortTimer -= deltaTime;
            }
            else
            {
                objectiveManager.SortObjectives();
                sortTimer = sortObjectiveInterval;
            }

            if (reportTimer > 0.0f)
            {
                reportTimer -= deltaTime;
            }
            else
            {
                if (Character.SpeechImpediment < 100.0f)
                {
                    ReportProblems();
                    UpdateSpeaking();
                }
                reportTimer = reportInterval;
            }

            if (objectiveManager.CurrentObjective == null) { return; }

            objectiveManager.DoCurrentObjective(deltaTime);
            bool run = objectiveManager.CurrentObjective.ForceRun || objectiveManager.GetCurrentPriority() > AIObjectiveManager.RunPriority;
            if (ObjectiveManager.CurrentObjective is AIObjectiveGoTo goTo && goTo.Target != null)
            {
                run = Vector2.DistanceSquared(Character.SimPosition, goTo.Target.SimPosition) > 3 * 3;
            }
            if (run)
            {
                run = !AnimController.Crouching && !AnimController.IsMovingBackwards;
            }
            float currentSpeed = Character.AnimController.GetCurrentSpeed(run);
            steeringManager.Update(currentSpeed);

            bool ignorePlatforms = Character.AnimController.TargetMovement.Y < -0.5f &&
                (-Character.AnimController.TargetMovement.Y > Math.Abs(Character.AnimController.TargetMovement.X));

            if (steeringManager == insideSteering)
            {
                var currPath = PathSteering.CurrentPath;
                if (currPath != null && currPath.CurrentNode != null)
                {
                    if (currPath.CurrentNode.SimPosition.Y < Character.AnimController.GetColliderBottom().Y)
                    {
                        // Don't allow to jump from too high. The formula might require tweaking.
                        float allowedJumpHeight = Character.AnimController.ImpactTolerance / 2;
                        float height = Math.Abs(currPath.CurrentNode.SimPosition.Y - Character.SimPosition.Y);
                        ignorePlatforms = height < allowedJumpHeight;
                    }
                }

                if (Character.IsClimbing && PathSteering.IsNextLadderSameAsCurrent)
                {
                    Character.AnimController.TargetMovement = new Vector2(0.0f, Math.Sign(Character.AnimController.TargetMovement.Y));
                }
            }

            Character.AnimController.IgnorePlatforms = ignorePlatforms;

            Vector2 targetMovement = AnimController.TargetMovement;

            if (!Character.AnimController.InWater)
            {
                targetMovement = new Vector2(Character.AnimController.TargetMovement.X, MathHelper.Clamp(Character.AnimController.TargetMovement.Y, -1.0f, 1.0f));
            }

            float maxSpeed = Character.ApplyTemporarySpeedLimits(currentSpeed);
            targetMovement.X = MathHelper.Clamp(targetMovement.X, -maxSpeed, maxSpeed);
            targetMovement.Y = MathHelper.Clamp(targetMovement.Y, -maxSpeed, maxSpeed);

            //apply speed multiplier if 
            //  a. it's boosting the movement speed and the character is trying to move fast (= running)
            //  b. it's a debuff that decreases movement speed
            float speedMultiplier = Character.SpeedMultiplier;
            if (run || speedMultiplier <= 0.0f) targetMovement *= speedMultiplier;
            Character.ResetSpeedMultiplier();   // Reset, items will set the value before the next update
            Character.AnimController.TargetMovement = targetMovement;

            if (!NeedsDivingGear(Character.CurrentHull))
            {
                bool oxygenLow = Character.OxygenAvailable < CharacterHealth.LowOxygenThreshold;
                bool highPressure = Character.CurrentHull == null || Character.CurrentHull.LethalPressure > 0 && Character.PressureProtection <= 0;
                bool shouldKeepTheGearOn = !ObjectiveManager.IsCurrentObjective<AIObjectiveIdle>();

                // Don't allow to drop the diving suit in water or while climbing or if the current path has stairs
                bool removeDivingSuit = 
                    (oxygenLow && !highPressure) || 
                        (!shouldKeepTheGearOn && 
                        Character.CurrentHull.WaterPercentage < 1 && 
                        !Character.IsClimbing && 
                        steeringManager == insideSteering && 
                        !PathSteering.InStairs);

                if (removeDivingSuit)
                {
                    var divingSuit = Character.Inventory.FindItemByIdentifier("divingsuit") ?? Character.Inventory.FindItemByTag("divingsuit");
                    if (divingSuit != null)
                    {
                        // TODO: take the item where it was taken from?
                        divingSuit.Drop(Character);
                    }
                }
                bool takeMaskOff = oxygenLow || (!shouldKeepTheGearOn && Character.CurrentHull.WaterPercentage < 20);
                if (takeMaskOff)
                {
                    var mask = Character.Inventory.FindItemByIdentifier("divingmask");
                    if (mask != null && Character.Inventory.IsInLimbSlot(mask, InvSlotType.Head))
                    {
                        // Try to put the mask in an Any slot, and drop it if that fails
                        if (!mask.AllowedSlots.Contains(InvSlotType.Any) || !Character.Inventory.TryPutItem(mask, Character, new List<InvSlotType>() { InvSlotType.Any }))
                        {
                            mask.Drop(Character);
                        }
                    }
                }
            }
            if (!ObjectiveManager.IsCurrentObjective<AIObjectiveExtinguishFires>() && !ObjectiveManager.IsCurrentObjective<AIObjectiveExtinguishFire>())
            {
                var extinguisherItem = Character.Inventory.FindItemByIdentifier("extinguisher") ?? Character.Inventory.FindItemByTag("extinguisher");
                if (extinguisherItem != null && Character.HasEquippedItem(extinguisherItem))
                {
                    // TODO: take the item where it was taken from?
                    extinguisherItem.Drop(Character);
                }
            }
            foreach (var item in Character.Inventory.Items)
            {
                if (item == null) { continue; }
                if (ObjectiveManager.CurrentObjective is AIObjectiveIdle)
                {
                    if (item.AllowedSlots.Contains(InvSlotType.RightHand | InvSlotType.LeftHand) && Character.HasEquippedItem(item))
                    {
                        // Try to put the weapon in an Any slot, and drop it if that fails
                        if (!item.AllowedSlots.Contains(InvSlotType.Any) || !Character.Inventory.TryPutItem(item, Character, new List<InvSlotType>() { InvSlotType.Any }))
                        {
                            item.Drop(Character);
                        }
                    }
                }
            }

            if (Character.IsKeyDown(InputType.Aim))
            {
                var cursorDiffX = Character.CursorPosition.X - Character.Position.X;
                if (cursorDiffX > 10.0f)
                {
                    Character.AnimController.TargetDir = Direction.Right;
                }
                else if (cursorDiffX < -10.0f)
                {
                    Character.AnimController.TargetDir = Direction.Left;
                }

                if (Character.SelectedConstruction != null) Character.SelectedConstruction.SecondaryUse(deltaTime, Character);

            }
            else if (Math.Abs(Character.AnimController.TargetMovement.X) > 0.1f && !Character.AnimController.InWater)
            {
                Character.AnimController.TargetDir = Character.AnimController.TargetMovement.X > 0.0f ? Direction.Right : Direction.Left;
            }

            if (Character.CurrentHull != null)
            {
                Character.GetVisibleHulls().ForEach(h => PropagateHullSafety(Character.Controlled, h));
            }
        }

        protected void ReportProblems()
        {
            Order newOrder = null;
            if (Character.CurrentHull != null)
            {
                if (AIObjectiveExtinguishFires.IsValidTarget(Character.CurrentHull, Character))
                {
                    if (AddTargets<AIObjectiveExtinguishFires, Hull>(Character, Character.CurrentHull))
                    {
                        var orderPrefab = Order.PrefabList.Find(o => o.AITag == "reportfire");
                        newOrder = new Order(orderPrefab, Character.CurrentHull, null);
                    }
                }

                foreach (var gap in Character.CurrentHull.ConnectedGaps)
                {
                    if (AIObjectiveFixLeaks.IsValidTarget(gap, Character))
                    {
                        if (AddTargets<AIObjectiveFixLeaks, Gap>(Character, gap))
                        {
                            var orderPrefab = Order.PrefabList.Find(o => o.AITag == "reportbreach");
                            newOrder = new Order(orderPrefab, Character.CurrentHull, null);
                        }
                    }
                }

                foreach (Item item in Item.ItemList)
                {
                    if (item.CurrentHull != Character.CurrentHull) { continue; }
                    if (AIObjectiveRepairItems.IsValidTarget(item, Character))
                    {
                        if (item.Repairables.All(r => item.Condition > r.ShowRepairUIThreshold)) { continue; }
                        if (AddTargets<AIObjectiveRepairItems, Item>(Character, item))
                        {
                            var orderPrefab = Order.PrefabList.Find(o => o.AITag == "reportbrokendevices");
                            newOrder = new Order(orderPrefab, Character.CurrentHull, item.Repairables?.FirstOrDefault());
                        }
                    }
                }

                foreach (Character c in Character.CharacterList)
                {
                    if (c.CurrentHull != Character.CurrentHull) { continue; }
                    if (AIObjectiveFightIntruders.IsValidTarget(c, Character))
                    {
                        if (AddTargets<AIObjectiveFightIntruders, Character>(Character, c))
                        {
                            var orderPrefab = Order.PrefabList.Find(o => o.AITag == "reportintruders");
                            newOrder = new Order(orderPrefab, Character.CurrentHull, null);
                        }
                    }
                }

                if (Character.Bleeding > 1.0f || Character.Vitality < Character.MaxVitality * 0.1f)
                {

                    if (AddTargets<AIObjectiveRescueAll, Character>(Character, Character))
                    {
                        var orderPrefab = Order.PrefabList.Find(o => o.AITag == "requestfirstaid");
                        newOrder = new Order(orderPrefab, Character.CurrentHull, null);
                    }
                }
            }

            if (newOrder != null)
            {
                if (GameMain.GameSession?.CrewManager != null && GameMain.GameSession.CrewManager.AddOrder(newOrder, newOrder.FadeOutTime))
                {
                    Character.Speak(newOrder.GetChatMessage("", Character.CurrentHull?.DisplayName, givingOrderToSelf: false), ChatMessageType.Order);
                }
            }
        }

        private void UpdateSpeaking()
        {
            if (Character.Oxygen < 20.0f)
            {
                Character.Speak(TextManager.Get("DialogLowOxygen"), null, 0, "lowoxygen", 30.0f);
            }

            if (Character.Bleeding > 2.0f)
            {
                Character.Speak(TextManager.Get("DialogBleeding"), null, 0, "bleeding", 30.0f);
            }

            if (Character.PressureTimer > 50.0f && Character.CurrentHull != null)
            {
                Character.Speak(TextManager.Get("DialogPressure").Replace("[roomname]", Character.CurrentHull.DisplayName), null, 0, "pressure", 30.0f);
            }
        }

        public override void OnAttacked(Character attacker, AttackResult attackResult)
        {
            float damage = attackResult.Damage;
            if (damage <= 0) { return; }
            if (ObjectiveManager.CurrentObjective is AIObjectiveFightIntruders) { return; }
            if (attacker == null || attacker.IsDead || attacker.Removed)
            {
                // Ignore damage from falling etc that we shouldn't react to.
                if (Character.LastDamageSource == null) { return; }
                AddCombatObjective(AIObjectiveCombat.CombatMode.Retreat, Rand.Range(0.5f, 1f, Rand.RandSync.Unsynced));
            }
            else if (IsFriendly(attacker))
            {
                if (attacker.AnimController.Anim == Barotrauma.AnimController.Animation.CPR && attacker.SelectedCharacter == Character)
                {
                    // Don't attack characters that damage you while doing cpr, because let's assume that they are helping you.
                    // Should not cancel any existing ai objectives (so that if the character attacked you and then helped, we still would want to retaliate).
                    return;
                }
                if (!attacker.IsRemotePlayer && Character.Controlled != attacker && attacker.AIController != null && attacker.AIController.Enabled)
                {
                    // Don't retaliate on damage done by friendly ai, because we know that it's accidental
                    AddCombatObjective(AIObjectiveCombat.CombatMode.Retreat, Rand.Range(0.5f, 1f, Rand.RandSync.Unsynced));
                }
                else
                {
                    float currentVitality = Character.CharacterHealth.Vitality;
                    float dmgPercentage = damage / currentVitality * 100;
                    if (dmgPercentage < currentVitality / 10)
                    {
                        // Don't retaliate on minor (accidental) dmg done by friendly characters
                        AddCombatObjective(AIObjectiveCombat.CombatMode.Retreat, Rand.Range(0.5f, 1f, Rand.RandSync.Unsynced));
                    }
                    else
                    {
                        AddCombatObjective(AIObjectiveCombat.CombatMode.Defensive, Rand.Range(0.5f, 1f, Rand.RandSync.Unsynced));
                    }
                }
            }
            else
            {
                AddCombatObjective(AIObjectiveCombat.CombatMode.Defensive);
            }

            void AddCombatObjective(AIObjectiveCombat.CombatMode mode, float delay = 0)
            {
                if (ObjectiveManager.CurrentObjective is AIObjectiveCombat combatObjective)
                {
                    if (combatObjective.Enemy != attacker || (combatObjective.Enemy == null && attacker == null))
                    {
                        // Replace the old objective with the new.
                        ObjectiveManager.Objectives.Remove(combatObjective);
                        objectiveManager.AddObjective(new AIObjectiveCombat(Character, attacker, mode, objectiveManager));
                    }
                }
                else
                {
                    if (delay > 0)
                    {
                        objectiveManager.AddObjective(new AIObjectiveCombat(Character, attacker, mode, objectiveManager), delay);
                    }
                    else
                    {
                        objectiveManager.AddObjective(new AIObjectiveCombat(Character, attacker, mode, objectiveManager));
                    }
                }
            }
        }

        public void SetOrder(Order order, string option, Character orderGiver, bool speak = true)
        {
            CurrentOrderOption = option;
            CurrentOrder = order;
            objectiveManager.SetOrder(order, option, orderGiver);
            if (ObjectiveManager.CurrentOrder != null && speak && Character.SpeechImpediment < 100.0f)
            {
                if (ObjectiveManager.CurrentOrder is AIObjectiveRepairItems repairItems && repairItems.Targets.None())
                {
                    Character.Speak(TextManager.Get("DialogNoRepairTargets"), null, 3.0f, "norepairtargets");
                }
                else if (ObjectiveManager.CurrentOrder is AIObjectiveChargeBatteries chargeBatteries && chargeBatteries.Targets.None())
                {
                    Character.Speak(TextManager.Get("DialogNoBatteries"), null, 3.0f, "nobatteries");
                }
                else if (ObjectiveManager.CurrentOrder is AIObjectiveExtinguishFires extinguishFires && extinguishFires.Targets.None())
                {
                    Character.Speak(TextManager.Get("DialogNoFire"), null, 3.0f, "nofire");
                }
                else if (ObjectiveManager.CurrentOrder is AIObjectiveFixLeaks fixLeaks && fixLeaks.Targets.None())
                {
                    Character.Speak(TextManager.Get("DialogNoLeaks"), null, 3.0f, "noleaks");
                }
                else if (ObjectiveManager.CurrentOrder is AIObjectiveFightIntruders fightIntruders && fightIntruders.Targets.None())
                {
                    Character.Speak(TextManager.Get("DialogNoEnemies"), null, 3.0f, "noenemies");
                }
                else if (ObjectiveManager.CurrentOrder is AIObjectiveRescueAll rescueAll && rescueAll.Targets.None())
                {
                    Character.Speak(TextManager.Get("DialogNoRescueTargets"), null, 3.0f, "norescuetargets");
                }
                else if (ObjectiveManager.CurrentOrder is AIObjectivePumpWater pumpWater && pumpWater.Targets.None())
                {
                    Character.Speak(TextManager.Get("DialogNoPumps"), null, 3.0f, "nopumps");
                }
                else
                {
                    Character.Speak(TextManager.Get("DialogAffirmative"), null, 1.0f);
                }
            }
            SetOrderProjSpecific(order);
        }

        partial void SetOrderProjSpecific(Order order);

        public override void SelectTarget(AITarget target)
        {
            SelectedAiTarget = target;
        }

        private void CheckCrouching(float deltaTime)
        {
            crouchRaycastTimer -= deltaTime;
            if (crouchRaycastTimer > 0.0f) return;

            crouchRaycastTimer = crouchRaycastInterval;

            //start the raycast in front of the character in the direction it's heading to
            Vector2 startPos = Character.SimPosition;
            startPos.X += MathHelper.Clamp(Character.AnimController.TargetMovement.X, -1.0f, 1.0f);

            //do a raycast upwards to find any walls
            float minCeilingDist = Character.AnimController.Collider.height / 2 + Character.AnimController.Collider.radius + 0.1f;
            shouldCrouch = Submarine.PickBody(startPos, startPos + Vector2.UnitY * minCeilingDist, null, Physics.CollisionWall) != null;
        }

        public static bool NeedsDivingGear(Hull hull) => hull == null || hull.OxygenPercentage < 50 || hull.WaterPercentage > 50;

        /// <summary>
        /// Check whether the character has a diving suit in usable condition plus some oxygen.
        /// </summary>
        public static bool HasDivingSuit(Character character) => HasItem(character, "divingsuit", "oxygensource");

        /// <summary>
        /// Check whether the character has a diving mask in usable condition plus some oxygen.
        /// </summary>
        public static bool HasDivingMask(Character character) => HasItem(character, "diving", "oxygensource");

        public static bool HasItem(Character character, string tag, string containedTag, float conditionPercentage = 0)
        {
            var item = character.Inventory.FindItemByTag(tag);
            return item != null &&
                item.ConditionPercentage > conditionPercentage &&
                character.HasEquippedItem(item) &&
                (containedTag == null ||
                (item.ContainedItems != null &&
                item.ContainedItems.Any(i => i.HasTag(containedTag) && i.ConditionPercentage > conditionPercentage)));
        }

        public static void DoForEachCrewMember(Character character, Action<HumanAIController> action)
        {
            if (character == null) { return; }
            foreach (var c in Character.CharacterList)
            {
                if (c == null || c.IsDead || c.Removed) { continue; }
                if (c.AIController is HumanAIController humanAi && humanAi.IsFriendly(character))
                {
                    action(humanAi);
                }
            }
        }

        /// <summary>
        /// Updates the hull safety for all ai characters in the team.
        /// </summary>
        public static void PropagateHullSafety(Character character, Hull hull)
        {
            DoForEachCrewMember(character, (humanAi) => humanAi.RefreshHullSafety(hull));
        }

        private void RefreshHullSafety(Hull hull)
        {
            if (GetHullSafety(hull) > HULL_SAFETY_THRESHOLD)
            {
                UnsafeHulls.Remove(hull);
            }
            else
            {
                UnsafeHulls.Add(hull);
            }
        }

        public static void RefreshTargets(Character character, Order order, Hull hull)
        {
            switch (order.AITag)
            {
                case "reportfire":
                    AddTargets<AIObjectiveExtinguishFires, Hull>(character, hull);
                    break;
                case "reportbreach":
                    foreach (var gap in hull.ConnectedGaps)
                    {
                        if (AIObjectiveFixLeaks.IsValidTarget(gap, character))
                        {
                            AddTargets<AIObjectiveFixLeaks, Gap>(character, gap);
                        }
                    }
                    break;
                case "reportbrokendevices":
                    foreach (var item in Item.ItemList)
                    {
                        if (item.CurrentHull != hull) { continue; }
                        if (AIObjectiveRepairItems.IsValidTarget(item, character))
                        {
                            if (item.Repairables.All(r => item.Condition > r.ShowRepairUIThreshold)) { continue; }
                            AddTargets<AIObjectiveRepairItems, Item>(character, item);
                        }
                    }
                    break;
                case "reportintruders":
                    foreach (var enemy in Character.CharacterList)
                    {
                        if (enemy.CurrentHull != hull) { continue; }
                        if (AIObjectiveFightIntruders.IsValidTarget(enemy, character))
                        {
                            AddTargets<AIObjectiveFightIntruders, Character>(character, enemy);
                        }
                    }
                    break;
                case "requestfirstaid":
                    foreach (var c in Character.CharacterList)
                    {
                        if (c.CurrentHull != hull) { continue; }
                        if (AIObjectiveRescueAll.IsValidTarget(c, character))
                        {
                            AddTargets<AIObjectiveRescueAll, Character>(character, c);
                        }
                    }
                    break;
                default:
#if DEBUG
                    DebugConsole.ThrowError(order.AITag + " not implemented!");
#endif
                    break;
            }
        }

        private static bool AddTargets<T1, T2>(Character caller, T2 target) where T1 : AIObjectiveLoop<T2>
        {
            bool targetAdded = false;
            DoForEachCrewMember(caller, humanAI =>
            {
                var objective = humanAI.ObjectiveManager.GetObjective<T1>();
                if (objective != null)
                {
                    if (!targetAdded && objective.AddTarget(target))
                    {
                        targetAdded = true;
                    }
                }
            });
            return targetAdded;
        }

        public static void RemoveTargets<T1, T2>(Character caller, T2 target) where T1 : AIObjectiveLoop<T2>
        {
            DoForEachCrewMember(caller, humanAI =>
                humanAI.ObjectiveManager.GetObjective<T1>()?.ReportedTargets.Remove(target));
        }

        public float GetHullSafety(Hull hull)
        {
            if (hull == null) { return 0; }
            bool ignoreFire = ObjectiveManager.IsCurrentObjective<AIObjectiveExtinguishFires>() || ObjectiveManager.IsCurrentObjective<AIObjectiveExtinguishFire>();
            bool ignoreWater = HasDivingSuit(Character);
            bool ignoreOxygen = ignoreWater || HasDivingMask(Character);
            bool ignoreEnemies = ObjectiveManager.IsCurrentObjective<AIObjectiveFightIntruders>();
            return GetHullSafety(hull, Character, ignoreWater, ignoreOxygen, ignoreFire, ignoreEnemies);
        }

        public static float GetHullSafety(Hull hull, Character character, bool ignoreWater = false, bool ignoreOxygen = false, bool ignoreFire = false, bool ignoreEnemies = false)
        {
            if (hull == null) { return 0; }
            if (hull.LethalPressure > 0 && character.PressureProtection <= 0) { return 0; }
            float oxygenFactor = ignoreOxygen ? 1 : MathHelper.Lerp(0.25f, 1, hull.OxygenPercentage / 100);
            float waterFactor = ignoreWater ? 1 : MathHelper.Lerp(1, 0.25f, hull.WaterPercentage / 100);
            if (!character.NeedsAir)
            {
                oxygenFactor = 1;
                waterFactor = 1;
            }
            // Even the smallest fire reduces the safety by 50%
            float fire = hull.FireSources.Count * 0.5f + hull.FireSources.Sum(fs => fs.DamageRange) / hull.Size.X;
            float fireFactor = ignoreFire ? 1 : MathHelper.Lerp(1, 0, MathHelper.Clamp(fire, 0, 1));
            int enemyCount = Character.CharacterList.Count(e => 
                e.CurrentHull == hull && !e.IsDead && !e.IsUnconscious && 
                (e.AIController is EnemyAIController || (e.TeamID != character.TeamID && character.TeamID != Character.TeamType.FriendlyNPC && e.TeamID != Character.TeamType.FriendlyNPC)));
            // The hull safety decreases 90% per enemy up to 100% (TODO: test smaller percentages)
            float enemyFactor = ignoreEnemies ? 1 : MathHelper.Lerp(1, 0, MathHelper.Clamp(enemyCount * 0.9f, 0, 1));
            float safety = oxygenFactor * waterFactor * fireFactor * enemyFactor;
            return MathHelper.Clamp(safety * 100, 0, 100);
        }

        public bool IsFriendly(Character other) => IsFriendly(Character, other);
        // TODO: If the aliens are quaranteed to be in another team than the player, we wouldn't need to check the species.
        public static bool IsFriendly(Character me, Character other) => other.TeamID == me.TeamID && other.SpeciesName == me.SpeciesName;
    }
}
