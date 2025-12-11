#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Archipelago.Traits
{
	[TraitLocation(SystemActors.Player)]
	public class APMissionObjectivesInfo : TraitInfo
	{
		[Desc("Set this to true if multiple cooperative players have a distinct set of " +
			"objectives that each of them has to complete to win the game. This is mainly " +
			"useful for multiplayer coop missions. Do not use this for skirmish team games.")]
		public readonly bool Cooperative = false;

		[Desc("If set to true, this setting causes the game to end immediately once the first " +
			"player (or team of cooperative players) fails or completes his objectives.  If " +
			"set to false, players that fail their objectives will stick around and become observers.")]
		public readonly bool EarlyGameOver = false;

		[Desc("Delay between the game over condition being met, and the game actually ending, in milliseconds.")]
		public readonly int GameOverDelay = 1500;

		[NotificationReference("Speech")]
		public readonly string WinNotification = null;

		[FluentReference(optional: true)]
		public readonly string WinTextNotification = null;

		[NotificationReference("Speech")]
		public readonly string LoseNotification = null;

		[FluentReference(optional: true)]
		public readonly string LoseTextNotification = null;

		[NotificationReference("Speech")]
		public readonly string LeaveNotification = null;

		[FluentReference(optional: true)]
		public readonly string LeaveTextNotification = null;

		public override object Create(ActorInitializer init) { return new APMissionObjectives(init.Self.Owner, this); }
	}

	public class APMissionObjectives : INotifyWinStateChanged, ISync, IResolveOrder, IWorldLoaded
	{
		public readonly APMissionObjectivesInfo Info;
		readonly List<MissionObjective> objectives = new();
		readonly Player player;
		public IReadOnlyList<MissionObjective> Objectives => objectives;

		Player[] enemies;
		Player[] allies;

		[Sync]
		public int ObjectivesHash
		{
			get
			{
				var hash = 0;
				foreach (var objective in objectives)
					hash ^= Sync.HashUsingHashCode(objective.State);
				return hash;
			}
		}

		// This property is used as a flag in 'Cooperative' games to mark that the player has completed all his objectives.
		// The player's WinState is only updated when his allies have all completed their objective as well.
		public WinState WinStateCooperative { get; private set; }

		public APMissionObjectives(Player player, APMissionObjectivesInfo info)
		{
			Info = info;
			this.player = player;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			// Players and NonCombatants are fixed once the game starts, but the result of IsAlliedWith
			// may change once players are marked as spectators, so cache these
			allies = player.World.Players.Where(p => !p.NonCombatant && player.IsAlliedWith(p)).ToArray();
			enemies = player.World.Players.Where(p => !p.NonCombatant && player.RelationshipWith(p) == PlayerRelationship.Enemy).ToArray();
		}

		public int Add(Player player, string description, string type, bool required = true, bool inhibitAnnouncement = false)
		{
			var newID = objectives.Count;
			objectives.Insert(newID, new MissionObjective(description, type, required));

			ObjectiveAdded(player, inhibitAnnouncement);
			foreach (var inou in player.PlayerActor.TraitsImplementing<INotifyObjectivesUpdated>())
				inou.OnObjectiveAdded(player, newID);

			return newID;
		}

		public void MarkCompleted(Player player, int objectiveID)
		{
			if (objectiveID >= objectives.Count || objectives[objectiveID].State != ObjectiveState.Incomplete)
				return;

			objectives[objectiveID].State = ObjectiveState.Completed;
			foreach (var inou in player.PlayerActor.TraitsImplementing<INotifyObjectivesUpdated>())
				inou.OnObjectiveCompleted(player, objectiveID);

			if (objectives[objectiveID].Required
				&& objectives.Where(o => o.Required).All(o => o.State == ObjectiveState.Completed))
			{
				foreach (var inwc in player.PlayerActor.TraitsImplementing<INotifyWinStateChanged>())
					inwc.OnPlayerWon(player);

				CheckIfGameIsOver(player);
			}
		}

		public void MarkFailed(Player player, int objectiveID)
		{
			if (objectiveID >= objectives.Count || objectives[objectiveID].State == ObjectiveState.Failed)
				return;

			objectives[objectiveID].State = ObjectiveState.Failed;
			foreach (var inou in player.PlayerActor.TraitsImplementing<INotifyObjectivesUpdated>())
				inou.OnObjectiveFailed(player, objectiveID);

			if (objectives[objectiveID].Required)
			{
				foreach (var inwc in player.PlayerActor.TraitsImplementing<INotifyWinStateChanged>())
					inwc.OnPlayerLost(player);

				CheckIfGameIsOver(player);
			}
		}

		void CheckIfGameIsOver(Player player)
		{
			var gameOver = player.World.Players.All(p => p.NonCombatant || p.WinState != WinState.Undefined || !p.HasObjectives);
			if (gameOver)
			{
				Game.RunAfterDelay(Info.GameOverDelay, () =>
				{
					if (Game.IsCurrentWorld(player.World))
						player.World.EndGame();
				});
			}
		}

		void INotifyWinStateChanged.OnPlayerWon(Player player)
		{
			if (Info.Cooperative)
			{
				WinStateCooperative = WinState.Won;

				if (allies.All(p => p.PlayerActor.Trait<APMissionObjectives>().WinStateCooperative == WinState.Won))
				{
					foreach (var p in allies)
					{
						p.WinState = WinState.Won;
						p.World.OnPlayerWinStateChanged(p);
					}

					if (Info.EarlyGameOver)
						foreach (var p in enemies)
							p.PlayerActor.Trait<APMissionObjectives>().ForceDefeat(p);
				}
			}
			else
			{
				player.WinState = WinState.Won;
				player.World.OnPlayerWinStateChanged(player);

				if (Info.EarlyGameOver)
					foreach (var p in enemies)
						p.PlayerActor.Trait<APMissionObjectives>().ForceDefeat(p);
			}

			CheckIfGameIsOver(player);
		}

		void INotifyWinStateChanged.OnPlayerLost(Player player)
		{
			if (Info.Cooperative)
			{
				WinStateCooperative = WinState.Lost;

				if (allies.Any(p => p.PlayerActor.Trait<APMissionObjectives>().WinStateCooperative == WinState.Lost))
				{
					foreach (var p in allies)
					{
						p.WinState = WinState.Lost;
						p.World.OnPlayerWinStateChanged(p);
					}

					if (Info.EarlyGameOver)
					{
						foreach (var p in enemies)
						{
							p.WinState = WinState.Won;
							p.World.OnPlayerWinStateChanged(p);
						}
					}
				}
			}
			else
			{
				player.WinState = WinState.Lost;
				player.World.OnPlayerWinStateChanged(player);

				if (Info.EarlyGameOver)
				{
					foreach (var p in enemies)
					{
						p.WinState = WinState.Won;
						p.World.OnPlayerWinStateChanged(p);
					}
				}
			}

			CheckIfGameIsOver(player);
		}

		public void ForceDefeat(Player player)
		{
			for (var id = 0; id < Objectives.Count; id++)
				if (Objectives[id].State == ObjectiveState.Incomplete)
					MarkFailed(player, id);
		}

		public event Action<Player, bool> ObjectiveAdded = (player, inhibitAnnouncement) => player.HasObjectives = true;

		public void ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "Surrender")
				ForceDefeat(self.Owner);
		}
	}
}
