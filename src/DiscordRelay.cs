using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;

using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json;

[assembly: ModInfo(
	"DiscordRelay",
	Description = "Bridges Vintage Story and a Discord channel",
	Website     = "https://github.com/Derpius/VintageRelay",
	Authors     = new []{ "Derpius" }
)]

namespace DiscordRelay
{
	public class BasePayload {
		public string type;

		public BasePayload(string _type)
		{
			type = _type;
		}
	}

	public class CustomPayload : BasePayload
	{
		public string body;

		public CustomPayload(string _body) : base("custom")
		{
			body = _body;
		}
	}

	public class JoinPayload : BasePayload
	{
		public string name;
		
		public JoinPayload(IServerPlayer plr) : base("join")
		{
			name = plr.PlayerName;
		}
	}

	public class LeavePayload : BasePayload
	{
		public string name;
		
		public LeavePayload(IServerPlayer plr) : base("leave")
		{
			name = plr.PlayerName;
		}
	}

	public class MessagePayload : BasePayload
	{
		public string name;
		public string message;

		public string teamName;
		public string teamColour;

		public string steamID;

		private static int BASE_MESSAGE_LENGTH = ("<strong></strong> ").Length;
		public MessagePayload(IServerPlayer plr, string _message) : base("message")
		{
			name = plr.PlayerName;
			message = _message.Substring(BASE_MESSAGE_LENGTH + plr.PlayerName.Length);
			teamName = plr.Groups.Length > 0 ? plr.Groups[0].GroupName : "No Group";
			teamColour = "150, 150, 150";
			steamID = plr.PlayerUID;
		}
	}

	public class DeathPayload : BasePayload
	{
		public string victim;
		public string inflictor = "";
		public string attacker = "unknown";

		public string suicide = "0";
		public string noweapon = "1";

		public DeathPayload(IServerPlayer plr, IWorldAccessor world, DamageSource damageSource) : base("death")
		{
			victim = plr.PlayerName;
			switch (damageSource.Source)
			{
				case EnumDamageSource.Block:
					attacker = damageSource.SourceBlock.GetPlacedBlockName(world, damageSource.SourcePos.AsBlockPos);
					break;
				case EnumDamageSource.Player:
				case EnumDamageSource.Entity:
					attacker = damageSource.SourceEntity.GetName();
					break;
				case EnumDamageSource.Fall:
					attacker = "Gravity";
					break;
				case EnumDamageSource.Drown:
					attacker = "Water";
					break;
				case EnumDamageSource.Revive: // Don't think this should ever be the case with a death event
					attacker = "Respawning";
					break;
				case EnumDamageSource.Void:
					attacker = "The Void";
					break;
				case EnumDamageSource.Suicide:
					suicide = "1";
					attacker = victim;
					break;
				case EnumDamageSource.Internal:
					attacker = "Magic";
					break;
				case EnumDamageSource.Explosion:
					attacker = "An Explosion";
					break;
				case EnumDamageSource.Machine:
					attacker = "A Machine";
					break;
				case EnumDamageSource.Weather:
					attacker = "The Weather";
					break;
				case EnumDamageSource.Unknown:
				default:
					break;
			}
		}
	}

	public class MessagesContainer
	{
		public List<List<string>> chat;
		public List<string> rcon;
	}

	public class ResponsePayload
	{
		[JsonProperty(PropertyName="info-payload-dirty")]
		public bool infoPayloadDirty;
		public MessagesContainer messages;
	}

	public class DiscordRelayMod : ModSystem
	{
		static private ICoreServerAPI API;

		static private readonly HttpClient http = new HttpClient();
		static private string relayConnection = "http://localhost:8080";

		static private Dictionary<string, string> toPost = new Dictionary<string, string>();
		static private void CachePost<PayloadType>(PayloadType payload) where PayloadType : BasePayload
		{
			char nonce = (char)1;
			StringBuilder key = new StringBuilder(Environment.TickCount.ToString() + nonce);

			while (toPost.ContainsKey(key.ToString())) {
				nonce++;
				key[key.Length - 1] = nonce;
				if (
					nonce == 255 ||
					(nonce > 5 && payload.GetType() != typeof(MessagePayload) && payload.GetType() != typeof(CustomPayload))
				) {
					API.Logger.Warning("Preventing caching messages to avoid Discord rate limiting due to spam");
					return;
				}
			}

			toPost.Add(key.ToString(), JsonConvert.SerializeObject(payload, Formatting.None));
		}
		
		public override void StartServerSide(ICoreServerAPI api)
		{
			API = api;
			SourceDedicatedServer.ServerEmulator.StartListening(api);

			api.Event.RegisterGameTickListener(OnTick, (int)Math.Ceiling(api.Server.Config.TickTime * 16));
			api.Event.PlayerChat += OnPlayerChat;
			api.Event.PlayerJoin += OnPlayerJoin;
			api.Event.PlayerDisconnect += OnPlayerLeave;
			api.Event.PlayerDeath += OnPlayerDeath;
		}

		static void OnTick(float deltaTime)
		{
			// POST messages
			StringBuilder body = new StringBuilder("{");
			foreach(KeyValuePair<string, string> entry in toPost) {
				body.Append(String.Format("{0}:{1},", JsonConvert.SerializeObject(entry.Key, Formatting.None), entry.Value));
			}

			HttpRequestMessage request;
			if (body.Length > 1) {
				body[body.Length - 1] = '}';

				request = new HttpRequestMessage(HttpMethod.Post, relayConnection);
				request.Content = new StringContent(body.ToString(), Encoding.Default, "application/json");
				request.Content.Headers.ContentType.CharSet = "";
				request.Headers.Add("Source-Port", API.Server.Config.Port.ToString());

				Dictionary<string, string> oldPostData = toPost;
				toPost = new Dictionary<string, string>();
				http.SendAsync(request).ContinueWith(responseTask => {
					if (responseTask.Status == TaskStatus.Faulted || responseTask.Result.StatusCode != System.Net.HttpStatusCode.OK) {
						foreach(var payload in oldPostData)
							toPost.Add(payload.Key, payload.Value);
					}
				});
			}

			// GET messages
			request = new HttpRequestMessage(HttpMethod.Get, relayConnection);
			request.Headers.Add("Source-Port", API.Server.Config.Port.ToString());

			http.SendAsync(request).ContinueWith(responseTask => {
				if (responseTask.Status == TaskStatus.Faulted || responseTask.Result.StatusCode != System.Net.HttpStatusCode.OK) return;
				responseTask.Result.Content.ReadAsStringAsync().ContinueWith(readTask => {
					ResponsePayload response = JsonConvert.DeserializeObject<ResponsePayload>(readTask.Result);

					foreach (List<string> message in response.messages.chat) {
						API.BroadcastMessageToAllGroups(
							String.Format(
								"<strong><font color=\"#a69ded\">[Discord | {0}]</font> <font color=\"#{1}\">{2}</font>:</strong> {3}",
								message[3], message[2], message[0], message[4]
							),
							EnumChatType.OthersMessage
						);
					}

					foreach (string command in response.messages.rcon) {
						API.InjectConsole(command);
					}
				});
			});
		}

		static void OnPlayerChat(IServerPlayer plr, int channelId, ref string message, ref string data, BoolRef consumed)
		{
			CachePost<MessagePayload>(new MessagePayload(plr, message));
		}

		static void OnPlayerJoin(IServerPlayer plr)
		{
			plr.SetModdata("DiscordRelay.JoinTime", BitConverter.GetBytes(Environment.TickCount));
			CachePost<JoinPayload>(new JoinPayload(plr));
		}

		static void OnPlayerLeave(IServerPlayer plr)
		{
			plr.RemoveModdata("DiscordRelay.JoinTime");
			CachePost<LeavePayload>(new LeavePayload(plr));
		}

		static void OnPlayerDeath(IServerPlayer plr, DamageSource damageSource)
		{
			CachePost<DeathPayload>(new DeathPayload(plr, API.World, damageSource));
		}
	}
}