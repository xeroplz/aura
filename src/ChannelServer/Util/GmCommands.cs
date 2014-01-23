﻿// Copyright (c) Aura development team - Licensed under GNU GPL
// For more information, see license file in the main folder

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Aura.Channel.Network;
using Aura.Channel.Network.Sending;
using Aura.Channel.Util.Configuration.Files;
using Aura.Channel.World;
using Aura.Channel.World.Entities;
using Aura.Data;
using Aura.Data.Database;
using Aura.Shared.Mabi.Const;
using Aura.Shared.Network;
using Aura.Shared.Util;
using Aura.Shared.Util.Commands;

namespace Aura.Channel.Util
{
	public class GmCommandManager : CommandManager<GmCommand, GmCommandFunc>
	{
		public GmCommandManager()
		{
			// Players
			Add(00, 50, "where", "", HandleWhere);
			Add(00, 50, "cp", "", HandleCp);

			// VIPs
			Add(01, 50, "go", "<location>", HandleGo);
			Add(01, 50, "iteminfo", "<name>", HandleItemInfo);
			Add(01, 50, "height", "<height>", HandleBody);
			Add(01, 50, "weight", "<weight>", HandleBody);
			Add(01, 50, "upper", "<upper>", HandleBody);
			Add(01, 50, "lower", "<lower>", HandleBody);
			Add(01, 50, "haircolor", "<hex color>", HandleHairColor);

			// GMs
			Add(50, 50, "warp", "<region> [x] [y]", HandleWarp);
			Add(50, 50, "jump", "[x] [y]", HandleWarp);
			Add(50, 50, "item", "<id|name> [amount|color1 [color2 [color 3]]]", HandleItem);
			Add(50, 50, "skill", "<id> [rank]", HandleSkill);

			// Admins
			Add(99, 99, "variant", "<xml_file>", HandleVariant);
			Add(99, 99, "test", "", HandleTest);

			// Aliases
			AddAlias("item", "drop");
			AddAlias("iteminfo", "ii");
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Adds new command.
		/// </summary>
		/// <param name="auth"></param>
		/// <param name="name"></param>
		/// <param name="usage"></param>
		/// <param name="func"></param>
		public void Add(int auth, string name, string usage, GmCommandFunc func)
		{
			this.Add(auth, 100, name, usage, func);
		}

		/// <summary>
		/// Adds new command.
		/// </summary>
		/// <param name="auth"></param>
		/// <param name="name"></param>
		/// <param name="usage"></param>
		/// <param name="func"></param>
		public void Add(int auth, int charAuth, string name, string usage, GmCommandFunc func)
		{
			this.Add(new GmCommand(auth, charAuth, name, usage, func));
		}

		/// <summary>
		/// Adds alias for command.
		/// </summary>
		/// <param name="commandName"></param>
		/// <param name="alias"></param>
		public void AddAlias(string commandName, string alias)
		{
			var command = this.GetCommand(commandName);
			if (command == null)
				throw new Exception("Aliasing: Command '" + commandName + "' not found");

			_commands[alias] = command;
		}

		/// <summary>
		/// Tries to run command, based on message.
		/// Returns false if the message was of no interest to the
		/// command handler.
		/// </summary>
		/// <param name="client"></param>
		/// <param name="creature"></param>
		/// <param name="message"></param>
		/// <returns></returns>
		public bool Process(ChannelClient client, Creature creature, string message)
		{
			if (message.Length < 2 || !message.StartsWith(ChannelServer.Instance.Conf.Commands.Prefix.ToString()))
				return false;

			// Parse arguments
			var args = this.ParseLine(message);
			args[0].TrimStart(ChannelServer.Instance.Conf.Commands.Prefix);

			// Handle char commands
			var sender = creature;
			var target = creature;
			var isCharCommand = message.StartsWith(ChannelServer.Instance.Conf.Commands.Prefix2);
			if (isCharCommand)
			{
				// Get target player
				if (args.Length < 2 || (target = ChannelServer.Instance.World.GetPlayer(args[1])) == null)
				{
					Send.ServerMessage(creature, "Target not found.");
					return true;
				}

				// Any better way to remove the target? =/
				var tmp = new List<string>(args);
				tmp.RemoveAt(1);
				args = tmp.ToArray();
			}

			// Get command
			var command = this.GetCommand(args[0]);
			if (command == null)
			{
				Send.ServerMessage(creature, "Unknown command '{0}'.", args[0]);
				return true;
			}

			var commandConf = ChannelServer.Instance.Conf.Commands.GetAuth(args[0], command.Auth, command.CharAuth);

			// Check auth
			if ((!isCharCommand && client.Account.Authority < commandConf.Auth) || (isCharCommand && client.Account.Authority < commandConf.CharAuth))
			{
				Send.ServerMessage(creature, "Unknown command '{0}'.", args[0]);
				return true;
			}

			// Run
			var result = command.Func(client, sender, target, message, args);

			// Handle result
			if (result == CommandResult.InvalidArgument)
			{
				Send.ServerMessage(creature, "Usage: {0} {1}", command.Name, command.Usage);
				if (command.CharAuth <= client.Account.Authority)
					Send.ServerMessage(creature, "Usage: {0} <target> {1}", command.Name, command.Usage);

				return true;
			}

			if (result == CommandResult.Fail)
			{
				Send.ServerMessage(creature, "Failed to process command.");
				return true;
			}

			return true;
		}

		// ------------------------------------------------------------------

		public CommandResult HandleTest(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			//for (int i = 0; i < args.Length; ++i)
			//{
			//    Send.ServerMessage(sender, "Arg{0}: {1}", i, args[i]);
			//}

			Send.ServerMessage(sender, "test, test");

			return CommandResult.Okay;
		}

		public CommandResult HandleWhere(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			var pos = target.GetPosition();
			var msg = Localization.Get(sender == target ? "gm.where_you" : "gm.where_target"); // (You're|{3} is) here: {0} @ {1}, {2}

			Send.ServerMessage(sender, msg, target.RegionId, pos.X, pos.Y, target.Name);

			return CommandResult.Okay;
		}

		public CommandResult HandleWarp(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			// Handles both warp and jump

			var warp = (args[0] == "warp");
			var offset = (warp ? 1 : 0);

			if (warp && args.Length < 2)
				return CommandResult.InvalidArgument;

			// Get region id
			int regionId = 0;
			if (warp)
			{
				if (!int.TryParse(args[1], out regionId))
				{
					Send.ServerMessage(sender, Localization.Get("gm.warp_invalid_id")); // Invalid region id.
					return CommandResult.InvalidArgument;
				}
			}
			else
				regionId = target.RegionId;

			// Check region
			if (warp && !ChannelServer.Instance.World.HasRegion(regionId))
			{
				Send.ServerMessage(sender, Localization.Get("gm.warp_unknown")); // Region doesn't exist.
				return CommandResult.Fail;
			}

			int x = -1, y = -1;

			// Parse X
			if (args.Length > 1 + offset && !int.TryParse(args[1 + offset], out x))
			{
				Send.ServerMessage(sender, Localization.Get("gm.warp_invalid_x")); // Invalid X coordinate.
				return CommandResult.InvalidArgument;
			}

			// Parse Y
			if (args.Length > 2 + offset && !int.TryParse(args[2 + offset], out y))
			{
				Send.ServerMessage(sender, Localization.Get("gm.warp_invalid_y")); // Invalid Y coordinate.
				return CommandResult.InvalidArgument;
			}

			// Random coordinates if none were specified
			if (x == -1 || y == -1)
			{
				var rndc = AuraData.RegionInfoDb.RandomCoord(regionId);
				if (x < 0) x = rndc.X;
				if (y < 0) y = rndc.Y;
			}

			target.Warp(regionId, x, y);

			if (sender != target)
				Send.ServerMessage(target, Localization.Get("gm.warp_target"), sender.Name); // You've been warped by '{0}'.

			return CommandResult.Okay;
		}

		public CommandResult HandleGo(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			if (args.Length < 2)
			{
				Send.ServerMessage(sender,
					Localization.Get("gm.go_dest") + // Destinations:
					" Tir Chonaill, Dugald Isle, Dunbarton, Gairech, Bangor, Emain Macha, Taillteann, Nekojima, GM Island"
				);
				return CommandResult.InvalidArgument;
			}

			int regionId = -1, x = -1, y = -1;
			var destination = message.Substring(args[0].Length + 1).Trim();

			if (destination.StartsWith("tir")) { regionId = 1; x = 12801; y = 38397; }
			else if (destination.StartsWith("dugald")) { regionId = 16; x = 23017; y = 61244; }
			else if (destination.StartsWith("dun")) { regionId = 14; x = 38001; y = 38802; }
			else if (destination.StartsWith("gairech")) { regionId = 30; x = 39295; y = 53279; }
			else if (destination.StartsWith("bangor")) { regionId = 31; x = 12904; y = 12200; }
			else if (destination.StartsWith("emain")) { regionId = 52; x = 39818; y = 41621; }
			else if (destination.StartsWith("tail")) { regionId = 300; x = 212749; y = 192720; }
			else if (destination.StartsWith("neko")) { regionId = 600; x = 114430; y = 79085; }
			else if (destination.StartsWith("gm")) { regionId = 22; x = 2500; y = 2500; }
			else
			{
				Send.ServerMessage(sender, Localization.Get("gm.go_unk"), args[1]);
				return CommandResult.InvalidArgument;
			}

			if (regionId == -1 || x == -1 || y == -1)
			{
				Send.ServerMessage(sender, "Error while choosing destination.");
				Log.Error("HandleGo: Incomplete destination '{0}'.", args[1]);
				return CommandResult.Fail;
			}

			target.Warp(regionId, x, y);

			if (sender != target)
				Send.ServerMessage(target, Localization.Get("gm.warp_target"), sender.Name); // You've been warped by '{0}'.

			return CommandResult.Okay;
		}

		public CommandResult HandleItem(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			if (args.Length < 2)
				return CommandResult.InvalidArgument;

			var drop = (args[0] == "drop");
			int itemId = 0;
			ItemData itemData = null;

			// Get item data
			if (!int.TryParse(args[1], out itemId))
			{
				var all = AuraData.ItemDb.FindAll(args[1]);

				// One or multiple items found
				if (all.Count > 0)
				{
					// Find best result
					var score = 10000;
					foreach (var data in all)
					{
						var curScore = data.Name.LevenshteinDistance(args[1], false);
						if (curScore < score)
						{
							score = curScore;
							itemData = data;
						}

						if (score == 0)
							break;
					}

					if (all.Count > 1 && score != 0)
					{
						var perc = 100 - (100f / itemData.Name.Length * score);
						Send.ServerMessage(sender, Localization.Get("gm.item_mures"), args[1], itemData.Name, perc.ToString("0.0", CultureInfo.InvariantCulture)); // No exact match found for '{0}', using best result, '{1}' ({2}%).
					}
				}
			}
			else
			{
				itemData = AuraData.ItemDb.Find(itemId);
			}

			// Check item data
			if (itemData == null)
			{
				Send.ServerMessage(sender, Localization.Get("gm.item_nores"), args[1]); // Item '{0}' not found in database.
				return CommandResult.Fail;
			}

			var item = new Item(itemData.Id);

			// Check amount for stackable items
			if (itemData.StackType == StackType.Stackable && args.Length > 2)
			{
				int amount;

				// Get amount
				if (!int.TryParse(args[2], out amount) || amount <= 0)
				{
					Send.ServerMessage(sender, Localization.Get("gm.item_amount")); // Invalid amount.
					return CommandResult.Fail;
				}

				item.Amount = amount;
			}
			// Parse colors
			else if (itemData.StackType != StackType.Stackable && args.Length > 2)
			{
				for (int i = 0; i < 3; ++i)
				{
					if (args.Length < 3 + i)
						break;

					var sColor = args[2 + i];
					uint color = 0;

					// Hex color
					if (sColor.StartsWith("0x"))
					{
						if (!uint.TryParse(sColor.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out color))
						{
							Send.ServerMessage(sender, Localization.Get("gm.item_hex"), sColor); // Invalid hex color '{0}'.
							return CommandResult.Fail;
						}
					}
					else
					{
						switch (sColor)
						{
							case "black": color = 0x00000000; break;
							case "white": color = 0xFFFFFFFF; break;
							default:
								Send.ServerMessage(sender, Localization.Get("gm.item_color"), sColor); // Unknown color '{0}'.
								return CommandResult.Fail;
						}
					}

					switch (i)
					{
						case 0: item.Info.Color1 = color; break;
						case 1: item.Info.Color2 = color; break;
						case 2: item.Info.Color3 = color; break;
					}
				}
			}

			// Spawn item
			var success = true;
			if (!drop)
				success = target.Inventory.Add(item, Pocket.Temporary);
			else
				item.Drop(target.Region, target.GetPosition());

			if (success)
			{
				if (sender != target)
					Send.ServerMessage(target, Localization.Get("gm.item_target"), itemData.Name, sender.Name); // Item '{0}' has been spawned by '{1}'.
				Send.ServerMessage(sender, Localization.Get("gm.item_spawned"), itemData.Name); // Item '{0}' has been spawned.
				return CommandResult.Okay;
			}
			else
			{
				Send.ServerMessage(sender, Localization.Get("gm.item_fail")); // Failed to spawn item.
				return CommandResult.Fail;
			}
		}

		public CommandResult HandleVariant(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			if (args.Length < 2)
				return CommandResult.InvalidArgument;

			var actualId = 1;
			var dynamicId = 35001;
			var x = 12800;
			var y = 38100;

			ChannelServer.Instance.World.AddRegion(35001);
			sender.SetLocation(dynamicId, x, y);
			sender.Warping = true;

			var pp = new Packet(0x0000A97E, MabiId.Broadcast);
			pp.PutLong(sender.EntityId);
			pp.PutInt(actualId);
			pp.PutInt(dynamicId);
			pp.PutInt(x);
			pp.PutInt(y);
			pp.PutInt(0);
			pp.PutInt(1);
			pp.PutInt(dynamicId);
			pp.PutString("DynamicRegion" + dynamicId);
			pp.PutUInt(0x80000001);
			pp.PutInt(1);
			pp.PutString("uladh_main");
			pp.PutInt(200);
			pp.PutByte(0);
			pp.PutString("data/world/uladh_main/" + args[1] + ".xml"); // region_variation_797208

			client.Send(pp);

			return CommandResult.Okay;
		}

		public CommandResult HandleItemInfo(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			if (args.Length < 2)
				return CommandResult.InvalidArgument;

			var search = message.Substring(message.IndexOf(" ")).Trim();
			var items = AuraData.ItemDb.FindAll(search);
			if (items.Count == 0)
			{
				Send.ServerMessage(sender, Localization.Get("gm.ii_none"), search); // No items found for '{0}'.
				return CommandResult.Okay;
			}

			var eItems = items.OrderBy(a => a.Name.LevenshteinDistance(search)).GetEnumerator();
			var max = 20;
			for (int i = 0; eItems.MoveNext() && i < max; ++i)
			{
				var item = eItems.Current;
				Send.ServerMessage(sender, Localization.Get("gm.ii_for"), item.Id, item.Name, item.Type); // {0}: {1}, Type: {2}
			}

			Send.ServerMessage(target, Localization.Get("gm.ii_res"), items.Count, max); // Results: {0} (Max. {1} shown)

			return CommandResult.Okay;
		}

		public CommandResult HandleSkill(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			if (args.Length < 2)
				return CommandResult.InvalidArgument;

			int skillId;
			if (!int.TryParse(args[1], out skillId))
				return CommandResult.InvalidArgument;

			int rank = 0;
			if (args.Length > 2 && !int.TryParse(args[2], NumberStyles.HexNumber, null, out rank))
				return CommandResult.InvalidArgument;

			rank = Math2.MinMax(0, 18, 16 - rank);

			target.Skills.Give((SkillId)skillId, (SkillRank)rank);

			return CommandResult.Okay;
		}

		public CommandResult HandleBody(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			if (args.Length < 2)
				return CommandResult.InvalidArgument;

			float val;
			if (!float.TryParse(message.Substring(message.IndexOf(' ') + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out val))
				return CommandResult.InvalidArgument;

			switch (args[0])
			{
				case "height": target.Height = val; val = target.Height; break;
				case "weight": target.Weight = val; val = target.Weight; break;
				case "upper": target.Upper = val; val = target.Upper; break;
				case "lower": target.Lower = val; val = target.Lower; break;
			}

			Send.CreatureBodyUpdate(target);

			Send.ServerMessage(sender, Localization.Get("gm.body_success"), val.ToString("0.0", CultureInfo.InvariantCulture)); // Change successful, new value: {0}
			if (sender != target)
				Send.ServerMessage(target, Localization.Get("gm.body_target"), sender.Name); // Your appearance has been changed by {0}.

			return CommandResult.Okay;
		}

		public CommandResult HandleCp(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			if (sender == target)
				Send.ServerMessage(sender, Localization.Get("gm.cp_own"), target.CombatPower.ToString("0.0", CultureInfo.InvariantCulture)); // Your combat power: {0}
			else
				Send.ServerMessage(sender, Localization.Get("gm.cp_target"), target.Name, target.CombatPower); // {0}'s combat power: {1}

			return CommandResult.Okay;
		}

		public CommandResult HandleHairColor(ChannelClient client, Creature sender, Creature target, string message, string[] args)
		{
			if (args.Length < 2)
				return CommandResult.InvalidArgument;

			uint color;
			// Hex color
			if (args[1].StartsWith("0x"))
			{
				if (!uint.TryParse(args[1].Replace("0x", ""), NumberStyles.HexNumber, null, out color))
					return CommandResult.InvalidArgument;
			}
			// Mabi color
			else
			{
				if (!uint.TryParse(args[1], out color))
					return CommandResult.InvalidArgument;
				color += 0x10000000;
			}

			var hair = target.Inventory.GetItemAt(Pocket.Hair, 0, 0);
			if (hair == null)
				return CommandResult.Fail;

			hair.Info.Color1 = color;
			Send.EquipmentChanged(target, hair);

			Send.ServerMessage(sender, Localization.Get("gm.body_success"), "0x" + color.ToString("X8")); // Change successful, new value: {0}
			if (sender != target)
				Send.ServerMessage(target, Localization.Get("gm.body_target"), sender.Name); // Your appearance has been changed by {0}.

			return CommandResult.Okay;
		}
	}

	public class GmCommand : Command<GmCommandFunc>
	{
		public int Auth { get; private set; }
		public int CharAuth { get; private set; }

		public GmCommand(int auth, int charAuth, string name, string usage, GmCommandFunc func)
			: base(name, usage, "", func)
		{
			this.Auth = auth;
			this.CharAuth = charAuth;
		}
	}

	public delegate CommandResult GmCommandFunc(ChannelClient client, Creature sender, Creature target, string message, string[] args);
}
