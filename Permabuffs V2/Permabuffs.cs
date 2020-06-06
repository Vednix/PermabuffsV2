using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Timers;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;

namespace Permabuffs_V2
{
	[ApiVersion(2, 1)]
	public class Permabuffs : TerrariaPlugin
	{
		public override string Name { get { return "Permabuffs"; } }
		public override string Author { get { return "Zaicon"; } }
		public override string Description { get { return "A plugin for permabuffs."; } }
		public override Version Version { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

		private static Timer update;
		private static List<int> globalbuffs = new List<int>();
		private static List<RegionBuff> regionbuffs = new List<RegionBuff>();
		private static Dictionary<int, List<string>> hasAnnounced = new Dictionary<int, List<string>>();

		public static string configPath = Path.Combine(TShock.SavePath, "PermabuffsConfig.json");
		public static Config config = Config.Read(configPath);// = new Config();

		public Permabuffs(Main game)
			: base(game)
		{
			base.Order = 1;
		}

		#region Initalize/Dispose
		public override void Initialize()
		{
			ServerApi.Hooks.GameInitialize.Register(this, OnInitialize);
			ServerApi.Hooks.NetGreetPlayer.Register(this, OnGreet);
			ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
			AccountHooks.AccountDelete += OnAccDelete;
			PlayerHooks.PlayerPostLogin += OnPostLogin;
			RegionHooks.RegionEntered += OnRegionEnter;
			GeneralHooks.ReloadEvent += PBReload;
		}

		protected override void Dispose(bool Disposing)
		{
			if (Disposing)
			{
				ServerApi.Hooks.GameInitialize.Deregister(this, OnInitialize);
				ServerApi.Hooks.NetGreetPlayer.Deregister(this, OnGreet);
				ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
				AccountHooks.AccountDelete -= OnAccDelete;
				RegionHooks.RegionEntered -= OnRegionEnter;
				PlayerHooks.PlayerPostLogin -= OnPostLogin;
				GeneralHooks.ReloadEvent -= PBReload;
			}
			base.Dispose(Disposing);
		}
		#endregion

		#region Hooks
		public void OnInitialize(EventArgs args)
		{
			DB.Connect();

			update = new Timer { Interval = 1000, AutoReset = true, Enabled = true };
			update.Elapsed += OnElapsed;

			Commands.ChatCommands.Add(new Command("pb.use", PBuffs, "permabuff") { AllowServer = false, HelpText = "Adiciona um buff ao seu personagem permanentemente." });
			Commands.ChatCommands.Add(new Command("pb.check", PBCheck, "buffcheck") { HelpText = "Lista os permabuffs ativos no jogador específico." });
			Commands.ChatCommands.Add(new Command("pb.give", PBGive, "gpermabuff") { HelpText = "Concede um permabuff ao jogador específico." });
			Commands.ChatCommands.Add(new Command("pb.region", PBRegion, "regionbuff"));
			Commands.ChatCommands.Add(new Command("pb.global", PBGlobal, "globalbuff"));
			Commands.ChatCommands.Add(new Command("pb.use", PBClear, "clearbuffs") { HelpText = "Remove todos os buffs." });
		}

		public void OnGreet(GreetPlayerEventArgs args)
		{
			if (TShock.Players[args.Who] == null)
				return;

			if (globalbuffs.Count > 0)
				TShock.Players[args.Who].SendInfoMessage("[PermaBuff] Este servidor está com os seguintes permabuffs ativos: {0}", string.Join(", ", globalbuffs.Select(p => TShock.Utils.GetBuffName(p))));

			if (!hasAnnounced.ContainsKey(args.Who))
				hasAnnounced.Add(args.Who, new List<string>());

			if (!TShock.Players[args.Who].IsLoggedIn)
				return;

			int id = TShock.Players[args.Who].User.ID;

			if (!DB.PlayerBuffs.ContainsKey(id))
			{
				if (DB.LoadUserBuffs(id))
				{
					if (DB.PlayerBuffs[id].bufflist.Count > 0)
						TShock.Players[args.Who].SendInfoMessage("[PermaBuff] Seus buffs da sessão anterior ({0}) ainda estão ativos!", string.Join(", ", DB.PlayerBuffs[id].bufflist.Select(p => TShock.Utils.GetBuffName(p))));
				}
				else
					DB.AddNewUser(TShock.Players[args.Who].User.ID);
			}
			else
			{
				//loadDBInfo(args.Who);
				if (DB.PlayerBuffs[id].bufflist.Count > 0)
					TShock.Players[args.Who].SendInfoMessage("[PermaBuff] Seus buffs da sessão anterior ({0}) ainda estão ativos!", string.Join(", ", DB.PlayerBuffs[id].bufflist.Select(p => TShock.Utils.GetBuffName(p))));
			}
		}

		public void OnPostLogin(PlayerPostLoginEventArgs args)
		{
			if (!DB.PlayerBuffs.ContainsKey(args.Player.User.ID))
			{
				if (DB.LoadUserBuffs(args.Player.User.ID))
				{
					if (DB.PlayerBuffs[args.Player.User.ID].bufflist.Count > 0)
						args.Player.SendInfoMessage("[PermaBuff] Seus buffs da sessão anterior ({0}) ainda estão ativos!", string.Join(", ", DB.PlayerBuffs[args.Player.User.ID].bufflist.Select(p => TShock.Utils.GetBuffName(p))));
				}
				else
					DB.AddNewUser(args.Player.User.ID);
			}
			else
			{
				DB.PlayerBuffs.Remove(args.Player.User.ID);
				DB.LoadUserBuffs(args.Player.User.ID);
				if (DB.PlayerBuffs[args.Player.User.ID].bufflist.Count > 0)
					args.Player.SendInfoMessage("[PermaBuff] Seus buffs da sessão anterior ({0}) ainda estão ativos!", string.Join(", ", DB.PlayerBuffs[args.Player.User.ID].bufflist.Select(p => TShock.Utils.GetBuffName(p))));
			}
		}

		public void OnAccDelete(AccountDeleteEventArgs args)
		{
			DB.ClearPlayerBuffs(args.User.ID);
		}

		public void OnRegionEnter(RegionHooks.RegionEnteredEventArgs args)
		{
			RegionBuff rb = config.regionbuffs.FirstOrDefault(p => p.regionName == args.Region.Name && p.buffs.Count > 0);

			if (rb == null)
				return;

			//Probably occurs when this is thrown before Greet (ie when spawning)
			if (!hasAnnounced.ContainsKey(args.Player.Index))
				return;

			if (hasAnnounced[args.Player.Index].Contains(args.Region.Name))
				return;

			args.Player.SendSuccessMessage("[PermaBuff] Os seguintes buffs estão ativos dentro desta região: {0}", string.Join(", ", rb.buffs.Keys.Select(p => TShock.Utils.GetBuffName(p))));
			hasAnnounced[args.Player.Index].Add(args.Region.Name);
		}

		public void OnLeave(LeaveEventArgs args)
		{
			var plr = TShock.Players[args.Who];
			if (plr == null)
				return;

			if (hasAnnounced.Keys.Contains(args.Who))
				hasAnnounced.Remove(args.Who);

			if (!plr.IsLoggedIn)
				return;

			if (DB.PlayerBuffs.ContainsKey(plr.User.ID))
				DB.PlayerBuffs.Remove(plr.User.ID);
		}

		private void OnElapsed(object sender, ElapsedEventArgs args)
		{
			for (int i = 0; i < TShock.Players.Length; i++)
			{
				if (TShock.Players[i] == null)
					continue;

				foreach (int buff in globalbuffs)
				{
					TShock.Players[i].SetBuff(buff, 18000);
				}

				if (TShock.Players[i].CurrentRegion != null)
				{
					RegionBuff rb = config.regionbuffs.FirstOrDefault(p => TShock.Players[i].CurrentRegion.Name == p.regionName && p.buffs.Count > 0);

					if (rb != null)
					{
						foreach (KeyValuePair<int, int> kvp in rb.buffs)
						{
							TShock.Players[i].SetBuff(kvp.Key, kvp.Value * 60);
						}
					}
				}

				if (!TShock.Players[i].IsLoggedIn)
					continue;

				if (DB.PlayerBuffs.ContainsKey(TShock.Players[i].User.ID))
					foreach (var buff in DB.PlayerBuffs[TShock.Players[i].User.ID].bufflist)
						TShock.Players[i].SetBuff(buff, 18000);
			}
		}
		#endregion

		#region Buff Commands
		private void PBuffs(CommandArgs args)
		{
			if (config.buffgroups.Length == 0)
			{
				args.Player.SendErrorMessage("[PermaBuff] O Administrador do servidor não definiu nenhum grupo de buffs. Sinta-se livre para sugestões :)");
				return;
			}

			List<BuffGroup> availableBuffGroups = config.buffgroups.Where(e => args.Player.HasPermission($"pb.{e.groupPerm}") || args.Player.HasPermission("pb.useall")).ToList();

			int bufftype = -1;

			if (args.Parameters.Count == 0)
			{
				args.Player.SendErrorMessage("[PermaBuff] Uso Correto: [c/ffd700:{0}permabuff <nome do buff ou ID>]", (args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier));
				return;
			}

			string buff = string.Join(" ", args.Parameters);

			// Get buff type by name
			if (!int.TryParse(args.Parameters[0], out bufftype))
			{
				List<int> bufftypelist = TShock.Utils.GetBuffByName(buff);

				if (bufftypelist.Count < 1)
				{
					args.Player.SendErrorMessage("[PermaBuff] Nenhum buff com esse nome foi encontrado.");
					return;
				}
				else if (bufftypelist.Count > 1)
				{
					TShock.Utils.SendMultipleMatchError(args.Player, bufftypelist.Select(p => TShock.Utils.GetBuffName(p)));
					return;
				}
				else
					bufftype = bufftypelist[0];
			}
			else if (bufftype > Main.maxBuffTypes || bufftype < 1) // Buff ID is not valid (less than 1 or higher than 206 (1.3.5.3)).
				args.Player.SendErrorMessage("[PermaBuff] ID inválida!");


			int playerid = args.Player.User.ID;

			availableBuffGroups.RemoveAll(e => !e.buffIDs.Contains(bufftype));

			if (availableBuffGroups.Count == 0)
			{
				args.Player.SendErrorMessage("[PermaBuff] Você não tem acesso a esse buff!");
				return;
			}

			if (DB.PlayerBuffs[playerid].bufflist.Contains(bufftype))
			{
				DB.PlayerBuffs[playerid].bufflist.Remove(bufftype);
				DB.UpdatePlayerBuffs(playerid, DB.PlayerBuffs[playerid].bufflist);
				args.Player.SendInfoMessage("[PermaBuff] " + TShock.Utils.GetBuffName(bufftype) + " foi removido!");
				return;
			}

			if (bufftype.IsPermanent())
			{
				DB.PlayerBuffs[playerid].bufflist.Add(bufftype);
				DB.UpdatePlayerBuffs(playerid, DB.PlayerBuffs[playerid].bufflist);
				args.Player.SendSuccessMessage($"[PermaBuff] {TShock.Utils.GetBuffName(bufftype)} agora está ativo! Execute o comando novamente para desativa-lo.");
			}
			else
			{
				args.Player.SetBuff(bufftype);
				args.Player.SendSuccessMessage($"[PermaBuff] {TShock.Utils.GetBuffName(bufftype)} foi concedido a você.");
			}
		}

		private void PBCheck(CommandArgs args)
		{
			if (args.Parameters.Count == 0)
			{
				args.Player.SendErrorMessage("[PermaBuff] Uso Correto: [c/ffd700:{0}buffcheck <jogador>]", (args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier));
				return;
			}

			string playername = string.Join(" ", args.Parameters);

			List<TSPlayer> players = TShock.Utils.FindPlayer(playername);

			if (players.Count < 1)
				args.Player.SendErrorMessage("[PermaBuff] Nenhum jogador encontrado.");
			else if (players.Count > 1)
				TShock.Utils.SendMultipleMatchError(args.Player, players.Select(p => p.Name));
			else if (!players[0].IsLoggedIn)
				args.Player.SendErrorMessage("[PermaBuff] {0} não possúi nenhum permabuff ativo.", players[0].Name);
			else
			{
				if (DB.PlayerBuffs[players[0].User.ID].bufflist.Count == 0)
					args.Player.SendInfoMessage("[PermaBuff] {0} não possúi nenhum permabuff ativo.", players[0].Name);
				else
					args.Player.SendInfoMessage("[PermaBuff] {0} está com os seguintes permabuffs ativos: {1}", players[0].Name, string.Join(", ", DB.PlayerBuffs[players[0].User.ID].bufflist.Select(p => TShock.Utils.GetBuffName(p))));
			}
		}

		private void PBGive(CommandArgs args)
		{
			if (config.buffgroups.Length == 0)
			{
				args.Player.SendErrorMessage("[PermaBuff] O Administrador do servidor não definiu nenhum grupo de buffs. Sinta-se livre para sugestões :)");
				return;
			}

			List<BuffGroup> availableBuffGroups = config.buffgroups.Where(e => args.Player.HasPermission($"pb.{e.groupPerm}") || args.Player.HasPermission("pb.useall")).ToList();

			if (args.Parameters.Count == 2)
			{
				// /gpermabuffs -g list
				if (args.Parameters[0].Equals("-g", StringComparison.CurrentCultureIgnoreCase) && args.Parameters[1].Equals("list", StringComparison.CurrentCultureIgnoreCase))
				{
					args.Player.SendInfoMessage($"[PermaBuff] Grupos Disponíveis: {string.Join(", ", availableBuffGroups.Select(e => e.groupName))}");
					return;
				}

				// Get player id from args.Parameters[1]
				string playername = args.Parameters[1];
				List<TSPlayer> players = TShock.Utils.FindPlayer(playername);
				if (players.Count < 1)
				{
					args.Player.SendErrorMessage("[PermaBuff] Nenhum jogador encontrado.");
					return;
				}
				else if (players.Count > 1)
				{
					TShock.Utils.SendMultipleMatchError(args.Player, players.Select(p => p.Name));
					return;
				}
				else if (!players[0].IsLoggedIn)
				{
					args.Player.SendErrorMessage("[PermaBuff] Esse jogador não pode receber permabuffs!");
					return;
				}
				int playerid = players[0].User.ID;

				//Get buff name/id from args.Parameters[0]
				string buff = args.Parameters[0];
				if (!int.TryParse(args.Parameters[0], out int bufftype))
				{
					List<int> bufftypelist = new List<int>();
					bufftypelist = TShock.Utils.GetBuffByName(buff);

					if (bufftypelist.Count < 1)
					{
						args.Player.SendErrorMessage("[PermaBuff] Nenhum buff com esse nome foi encontrado.");
						return;
					}
					else if (bufftypelist.Count > 1)
					{
						TShock.Utils.SendMultipleMatchError(args.Player, bufftypelist.Select(p => TShock.Utils.GetBuffName(p)));
						return;
					}
					else
						bufftype = bufftypelist[0];
				}
				else if (bufftype > Main.maxBuffTypes || bufftype < 1) // Buff ID is not valid (less than 1 or higher than 192 (1.3.1)).
					args.Player.SendErrorMessage("[PermaBuff] ID inválida!");

				//Removes all groups where the buff isn't included, leaving only a list of groups where player has access AND contains the buff
				availableBuffGroups.RemoveAll(e => !e.buffIDs.Contains(bufftype));

				if (availableBuffGroups.Count == 0)
				{
					args.Player.SendErrorMessage("[PermaBuff] Você não tem acesso!");
					return;
				}

				if (DB.PlayerBuffs[playerid].bufflist.Contains(bufftype))
				{
					DB.PlayerBuffs[playerid].bufflist.Remove(bufftype);
					DB.UpdatePlayerBuffs(playerid, DB.PlayerBuffs[playerid].bufflist);
					args.Player.SendInfoMessage($"[PermaBuff] Você retirou o buff [c/ff4500:{TShock.Utils.GetBuffName(bufftype)}] do jogador [c/2859b8:{players[0].Name}].");
					if (!args.Silent)
						players[0].SendInfoMessage($"[c/2859b8:{args.Player.Name}] retirou seu permabuff [c/ff4500:{TShock.Utils.GetBuffName(bufftype)}].");
				}
				else if (bufftype.IsPermanent())
				{
					DB.PlayerBuffs[playerid].bufflist.Add(bufftype);
					DB.UpdatePlayerBuffs(playerid, DB.PlayerBuffs[playerid].bufflist);
					args.Player.SendSuccessMessage($"Você concedeu ao jogador [c/2859b8:{players[0].Name}] o permabuff [c/ff4500:{TShock.Utils.GetBuffName(bufftype)}]!");
					if (!args.Silent)
						players[0].SendInfoMessage($"[c/2859b8:{args.Player.Name}] concedeu-lhe o permabuff [c/ff4500:{TShock.Utils.GetBuffName(bufftype)}]!");
				}
				else
				{
					args.Player.SetBuff(bufftype);
					args.Player.SendSuccessMessage($"Você concedeu ao jogador [c/2859b8:{players[0].Name}] o permabuff [c/ff4500:{TShock.Utils.GetBuffName(bufftype)}]!");
					if (!args.Silent)
						players[0].SendInfoMessage($"[c/2859b8:{args.Player.Name}] concedeu-lhe o permabuff [c/ff4500:{TShock.Utils.GetBuffName(bufftype)}]!");
				}
			}
			//gpermabuff -g <group> <player>
			else if (args.Parameters.Count == 3)
			{
				if (args.Parameters[0] != "-g")
				{
					args.Player.SendErrorMessage("Uso Correto:");
					args.Player.SendErrorMessage("[c/ffd700:{0}gpermabuff <nome do buff ou ID> <jogador>]", TShock.Config.CommandSpecifier);
					args.Player.SendErrorMessage("[c/ffd700:{0}gpermabuff -g <grupo> <jogador>]", TShock.Config.CommandSpecifier);
				}

				var matchedPlayers = TShock.Utils.FindPlayer(args.Parameters[2]);

				if (matchedPlayers.Count == 0)
				{
					args.Player.SendErrorMessage($"[PermaBuff] Nenhum jogador encontrado com o nome: [c/2859b8:{args.Parameters[2]}]");
					return;
				}
				else if (matchedPlayers.Count > 1)
				{
					TShock.Utils.SendMultipleMatchError(args.Player, matchedPlayers.Select(p => p.Name));
					return;
				}
				else if (!matchedPlayers[0].IsLoggedIn)
				{
					args.Player.SendErrorMessage("[PermaBuff] Esse jogador não pode receber buffs!");
					return;
				}
				else if (!availableBuffGroups.Any(e => e.groupName.Equals(args.Parameters[1], StringComparison.CurrentCultureIgnoreCase)))
				{
					args.Player.SendErrorMessage("[PermaBuff] Nenhum grupo de buffs encontrado com esse nome!");
				}

				TSPlayer player = matchedPlayers[0];
				int id = matchedPlayers[0].User.ID;

				foreach (var buff in availableBuffGroups.First(e => e.groupName.Equals(args.Parameters[1], StringComparison.CurrentCultureIgnoreCase)).buffIDs)
				{
					if (!DB.PlayerBuffs[id].bufflist.Contains(buff) && buff.IsPermanent())
						DB.PlayerBuffs[id].bufflist.Add(buff);
				}
				DB.UpdatePlayerBuffs(id, DB.PlayerBuffs[id].bufflist);

				args.Player.SendSuccessMessage($"[PermaBuff] O jogador [c/2859b8:{player.Name}] agora possúi todos os buffs ativos do grupo [c/ff4500:{args.Parameters[1]}]!");

				if (!args.Silent)
					args.Player.SendInfoMessage($"[PermaBuff] [c/2859b8:{args.Player.Name}] concedeu-lhe todos os buffs ativos do grupo [c/ff4500:{args.Parameters[1]}]!");
			}
			else
			{
				args.Player.SendErrorMessage("Uso Correto:");
				args.Player.SendErrorMessage("[c/ffd700:{0}gpermabuff <nome do buff ou ID> <jogador>]", TShock.Config.CommandSpecifier);
				args.Player.SendErrorMessage("[c/ffd700:{0}gpermabuff -g <grupo> <jogador>]", TShock.Config.CommandSpecifier);
				return;
			}
		}

		private void PBReload(ReloadEventArgs args)
		{
			config = Config.Read(configPath);
		}

		private void PBRegion(CommandArgs args)
		{
			//regionbuff <add/del> <region> <buff>

			if (args.Parameters.Count < 3 || args.Parameters.Count > 4)
			{
				args.Player.SendErrorMessage("Invalid Syntax: {0}regionbuff <add/del> <region name> <buff name/ID> [duration]", (args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier));
				return;
			}

			if (args.Parameters[0].Equals("add", StringComparison.CurrentCultureIgnoreCase))
			{
				string regionname = args.Parameters[1];
				Region region = TShock.Regions.GetRegionByName(regionname);
				string buffinput = args.Parameters[2];
				if (args.Parameters.Count != 4)
				{
					args.Player.SendErrorMessage("Invalid Syntax: {0}regionbuff <add/del> <region name> <buff name/ID> [duration]", (args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier));
					return;
				}
				string durationinput = args.Parameters[3];
				int bufftype = -1;

				if (region == null)
				{
					args.Player.SendErrorMessage("Invalid region: {0}", regionname);
					return;
				}

				if (!int.TryParse(buffinput, out bufftype))
				{
					List<int> bufflist = TShock.Utils.GetBuffByName(buffinput);

					if (bufflist.Count == 0)
					{
						args.Player.SendErrorMessage("No buffs found by the name {0}.", buffinput);
						return;
					}

					if (bufflist.Count > 1)
					{
						TShock.Utils.SendMultipleMatchError(args.Player, bufflist.Select(p => TShock.Utils.GetBuffName(p)));
						return;
					}

					bufftype = bufflist[0];
				}

				if (bufftype < 0 || bufftype > Main.maxBuffTypes)
				{
					args.Player.SendErrorMessage("[PermaBuff] ID inválida: {0}", bufftype.ToString());
					return;
				}

				int duration = -1;

				if (!int.TryParse(durationinput, out duration) || (duration < 1 || duration > 540))
				{
					args.Player.SendErrorMessage("Invalid duration!");
					return;
				}

				bool found = false;

				for (int i = 0; i < config.regionbuffs.Length; i++)
				{
					if (config.regionbuffs[i].regionName == region.Name)
					{
						found = true;
						if (config.regionbuffs[i].buffs.Keys.Contains(bufftype))
						{
							args.Player.SendErrorMessage("Region {0} already contains buff {1}!", region.Name, TShock.Utils.GetBuffName(bufftype));
							return;
						}
						else
						{
							config.regionbuffs[i].buffs.Add(bufftype, duration);
							args.Player.SendSuccessMessage("Added buff {0} to region {1} with a duration of {2} seconds!", TShock.Utils.GetBuffName(bufftype), region.Name, duration.ToString());
							config.Write(configPath);
							return;
						}
					}
				}

				if (!found)
				{
					List<RegionBuff> temp = config.regionbuffs.ToList();
					temp.Add(new RegionBuff() { buffs = new Dictionary<int, int>() { { bufftype, duration } }, regionName = region.Name });
					config.regionbuffs = temp.ToArray();
					args.Player.SendSuccessMessage("Added buff {0} to region {1} with a duration of {2} seconds!", TShock.Utils.GetBuffName(bufftype), region.Name, duration.ToString());
					config.Write(configPath);
					return;
				}
			}

			if (args.Parameters[0].Equals("del", StringComparison.CurrentCultureIgnoreCase) || args.Parameters[0].Equals("delete", StringComparison.CurrentCultureIgnoreCase))
			{
				string regionname = args.Parameters[1];
				Region region = TShock.Regions.GetRegionByName(regionname);
				string buffinput = args.Parameters[2];
				int bufftype = -1;

				if (region == null)
				{
					args.Player.SendErrorMessage("Invalid region: {0}", regionname);
					return;
				}

				if (!int.TryParse(buffinput, out bufftype))
				{
					List<int> bufflist = TShock.Utils.GetBuffByName(buffinput);

					if (bufflist.Count == 0)
					{
						args.Player.SendErrorMessage("No buffs found by the name {0}.", buffinput);
						return;
					}

					if (bufflist.Count > 1)
					{
						TShock.Utils.SendMultipleMatchError(args.Player, bufflist.Select(p => TShock.Utils.GetBuffName(p)));
						return;
					}

					bufftype = bufflist[0];
				}

				if (bufftype < 0 || bufftype > Main.maxBuffTypes)
				{
					args.Player.SendErrorMessage("[PermaBuff] ID inválida: {0}", bufftype.ToString());
					return;
				}

				bool found = false;

				for (int i = 0; i < config.regionbuffs.Length; i++)
				{
					if (config.regionbuffs[i].regionName == region.Name)
					{
						if (config.regionbuffs[i].buffs.ContainsKey(bufftype))
						{
							config.regionbuffs[i].buffs.Remove(bufftype);
							args.Player.SendSuccessMessage("Removed buff {0} from region {1}!", TShock.Utils.GetBuffName(bufftype), region.Name);
							config.Write(configPath);
							found = true;
							return;
						}
					}
				}

				if (!found)
				{
					args.Player.SendSuccessMessage("Buff {0} is not a region buff in region {1}!", TShock.Utils.GetBuffName(bufftype), region.Name);
					return;
				}
			}

			args.Player.SendErrorMessage("Invalid syntax: {0}regionbuff <add/del> <region name> <buff name/ID>", (args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier));
		}

		private void PBGlobal(CommandArgs args)
		{
			if (args.Parameters.Count == 0)
			{
				args.Player.SendErrorMessage("Invalid Syntax: {0}globalbuff <buff name>", (args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier));
				return;
			}

			string buff = string.Join(" ", args.Parameters);

			if (!int.TryParse(args.Parameters[0], out int bufftype))
			{
				List<int> bufftypelist = TShock.Utils.GetBuffByName(buff);

				if (bufftypelist.Count < 1)
				{
					args.Player.SendErrorMessage("No buffs by that name were found.");
					return;
				}
				else if (bufftypelist.Count > 1)
				{
					TShock.Utils.SendMultipleMatchError(args.Player, bufftypelist.Select(p => TShock.Utils.GetBuffName(p)));
					return;
				}
				else
					bufftype = bufftypelist[0];
			}

			if (bufftype > Main.maxBuffTypes || bufftype < 1) // Buff ID is not valid (less than 1 or higher than 190).
				args.Player.SendErrorMessage("[PermaBuff] ID inválida!");

			if (!bufftype.IsPermanent() || !config.buffgroups.Any(e => e.buffIDs.Contains(bufftype)))
				args.Player.SendErrorMessage("This buff is not available as a global buff!");
			else if (globalbuffs.Contains(bufftype))
			{
				globalbuffs.Remove(bufftype);
				args.Player.SendSuccessMessage("{0} has been removed from the global permabuffs.", TShock.Utils.GetBuffName(bufftype));
			}
			else
			{
				globalbuffs.Add(bufftype);
				args.Player.SendSuccessMessage("{0} has been activated as a global permabuff!", TShock.Utils.GetBuffName(bufftype));
			}
		}

		private void PBClear(CommandArgs args)
		{
			if (args.Parameters.Count == 1 && (args.Parameters[0] == "*" || args.Parameters[0].Equals("all", StringComparison.CurrentCultureIgnoreCase)))
			{
				if (!args.Player.HasPermission("pb.clear"))
				{
					args.Player.SendErrorMessage("You do not have permission to clear all permabuffs.");
					return;
				}
				foreach (KeyValuePair<int, DBInfo> kvp in DB.PlayerBuffs)
				{
					kvp.Value.bufflist.Clear();
					DB.ClearDB();
				}
				args.Player.SendSuccessMessage("All permabuffs have been deactivated for all players.");
				if (!args.Silent)
				{
					TSPlayer.All.SendInfoMessage("{0} has deactivated all permabuffs!", args.Player.User.Name);
				}
			}
			else
			{
				if (!args.Player.RealPlayer)
				{
					args.Player.SendErrorMessage("You must be in-game to use this command.");
					return;
				}
				DB.PlayerBuffs[args.Player.User.ID].bufflist.Clear();

				DB.ClearPlayerBuffs(args.Player.User.ID);
				args.Player.SendSuccessMessage("All of your permabuffs have been deactivated.");
			}
		}
		#endregion
	}
}
