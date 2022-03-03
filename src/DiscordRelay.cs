using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;

using System;
using System.Net.Http;

[assembly: ModInfo(
	"DiscordRelay",
	Description = "Bridges Vintage Story and a Discord channel",
	Website     = "https://github.com/Derpius/VintageRelay",
	Authors     = new []{ "Derpius" }
)]

namespace DiscordRelay
{
	public class DiscordRelayMod : ModSystem
	{
		private ICoreServerAPI Server;
		private ICoreClientAPI Client;
		private ICoreAPI Shared;

		public override void Start(ICoreAPI api)
		{
			Shared = api;
		}
		
		public override void StartClientSide(ICoreClientAPI api)
		{
			Client = api;
		}
		
		public override void StartServerSide(ICoreServerAPI api)
		{
			Server = api;
			SourceDedicatedServer.ServerEmulator.StartListening(api);

			api.Event.RegisterGameTickListener(OnTick, (int)Math.Ceiling(api.Server.Config.TickTime * 16));
			api.Event.PlayerChat += OnPlayerChat;
			api.Event.PlayerJoin += OnPlayerJoin;
			api.Event.PlayerDisconnect += OnPlayerLeave;
			api.Event.PlayerDeath += OnPlayerDeath;
		}

		static void OnTick(float deltaTime)
		{

		}

		static void OnPlayerChat(IServerPlayer plr, int channelId, ref string message, ref string data, BoolRef consumed)
		{

		}

		static void OnPlayerJoin(IServerPlayer plr)
		{
			plr.SetModdata("DiscordRelay.JoinTime", BitConverter.GetBytes(Environment.TickCount));
		}

		static void OnPlayerLeave(IServerPlayer plr)
		{
			plr.RemoveModdata("DiscordRelay.JoinTime");
		}

		static void OnPlayerDeath(IServerPlayer plr, DamageSource damageSource)
		{

		}
	}
}