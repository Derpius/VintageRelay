using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Threading.Tasks;

using Vintagestory.API.Server;
using Vintagestory.API.Config;

namespace SourceDedicatedServer
{
	public enum RequestType : byte
	{
		A2S_INFO = 0x54,
		A2S_PLAYER = 0x55,
		A2S_RULES = 0x56
	}

	public enum ResponseType : byte
	{
		CHALLENGE = 0x41,
		A2S_INFO = 0x49,
		A2S_PLAYER = 0x44,
		A2S_RULES = 0x45
	}

	public class StateObject
	{
		public const int bufferSize = 1400;

		public byte[] buffer = new byte[bufferSize];

		public Socket workSocket = null;
	}
	
	public static class ServerEmulator
	{
		private static ICoreServerAPI API;

		private static UTF8Encoding encoding = new UTF8Encoding();

		private static short MAX_PAYLOAD_SIZE = 1248;

		// 32 bit challenge to use and expect (no point implementing a real challenge system)
		// (V)intage (S)tory (D)edicated (S)erver
		private static byte[] CHALLENGE = { (byte)'V', (byte)'S', (byte)'D', (byte)'S' };
		private static int CHALLENGE_INT = BitConverter.ToInt32(CHALLENGE, 0);

		private static uint CHALLENGE_PACKET_LENGTH = 9;

		private static string INFO_PAYLOAD = "Source Engine Query\0";
		private static uint INFO_REQUEST_PACKET_LENGTH = 5 + (uint)INFO_PAYLOAD.Length;

		private static int packetId = 0;

		public static void StartListening(ICoreServerAPI api)
		{
			API = api;

			Task.Run(async() => 
			{
				using(var udpClient = new UdpClient(API.Server.Config.Port)) {
					while (true) {
						var request = await udpClient.ReceiveAsync();
						HandleRequest(udpClient, request);
					}
				}
			});
		}

		private static void HandleRequest(UdpClient client, UdpReceiveResult request)
		{
			int bytesRead = request.Buffer.Length;

			if (bytesRead < 9) return; // Smallest possible (valid) request packet is 9 bytes
			if (BitConverter.ToInt32(request.Buffer, 0) != -1) return; // Request should have a valid non-split header

			switch (request.Buffer[4])
			{
				case (byte)RequestType.A2S_INFO:
				{
					if (bytesRead != INFO_REQUEST_PACKET_LENGTH) break;

					string payload = encoding.GetString(request.Buffer, 5, INFO_PAYLOAD.Length);
					if (payload != INFO_PAYLOAD) break;

					SendInfoResponse(client, request.RemoteEndPoint);
					break;
				}
				case (byte)RequestType.A2S_PLAYER:
				{
					if (bytesRead != CHALLENGE_PACKET_LENGTH) break;

					int challenge = BitConverter.ToInt32(request.Buffer, 5);
					if (challenge == -1) {
						SendChallenge(client, request.RemoteEndPoint);
						break;
					} else if (challenge != CHALLENGE_INT) break;

					// Empty players response
					SendPlayersResponse(client, request.RemoteEndPoint);
					break;
				}
				case (byte)RequestType.A2S_RULES:
				{
					if (bytesRead != CHALLENGE_PACKET_LENGTH) break;

					int challenge = BitConverter.ToInt32(request.Buffer, 5);
					if (challenge == -1) {
						SendChallenge(client, request.RemoteEndPoint);
						break;
					} else if (challenge != CHALLENGE_INT) break;

					// Empty rules response
					Send(client, request.RemoteEndPoint, new byte[]{
						(byte)ResponseType.A2S_RULES,
						0
					});
					break;
				}
				default: break;
			}
		}

		private static void Send(UdpClient client, IPEndPoint endpoint, byte[] data)
		{
			int length = data.Length;
			if (length <= MAX_PAYLOAD_SIZE) {
				var buffer = new byte[4 + length];
				for (int i = 0; i < 4; i++) buffer[i] = 0xff;
				data.CopyTo(buffer, 4);

				client.Send(buffer, 4 + length, endpoint);
				return;
			}

			byte numPackets = (byte)Math.Ceiling((double)length / (double)MAX_PAYLOAD_SIZE);
			for (byte i = 0; i < numPackets; i++) {
				using (var stream = new MemoryStream()) {
					stream.Write(new byte[]{ 0xff, 0xff, 0xff, 0xfe }, 0, 4);
					stream.Write(BitConverter.GetBytes(packetId), 0, 4);
					stream.WriteByte(numPackets);
					stream.WriteByte(i);
					stream.Write(BitConverter.GetBytes(MAX_PAYLOAD_SIZE), 0, 2);
					stream.Write(data, i * MAX_PAYLOAD_SIZE, Math.Min(MAX_PAYLOAD_SIZE, length - i * MAX_PAYLOAD_SIZE));

					client.Send(stream.ToArray(), (int)stream.Length, endpoint);
				}
			}

			packetId++;
		}

		private static void SendChallenge(UdpClient client, IPEndPoint endpoint)
		{
			byte[] data = {
				(byte)ResponseType.CHALLENGE,
				CHALLENGE[0], CHALLENGE[1], CHALLENGE[2], CHALLENGE[3]
			};
			Send(client, endpoint, data);
		}

		private static void SendInfoResponse(UdpClient client, IPEndPoint endpoint)
		{
			using (var stream = new MemoryStream()) {
				stream.Write(new byte[]{ (byte)ResponseType.A2S_INFO, 0 }, 0, 2);

				// Server name
				stream.Write(encoding.GetBytes(API.Server.Config.ServerName + '\0'), 0, API.Server.Config.ServerName.Length + 1);

				// Map
				string worldName = API.WorldManager.CurrentWorldName;
				int lastSlash = worldName.LastIndexOfAny(new char[]{'\\', '/'});
				int lastFullStop = worldName.LastIndexOf('.');
				worldName = worldName.Substring(lastSlash + 1, lastFullStop - lastSlash - 1);
				stream.Write(encoding.GetBytes(worldName + "\0"), 0, worldName.Length + 1);

				// Folder
				stream.Write(encoding.GetBytes("vintagestory\0"), 0, ("vintagestory\0").Length);

				// Game
				stream.Write(encoding.GetBytes("Vintage Story\0"), 0, ("Vintage Story\0").Length);

				// App ID (using core srcds's)
				stream.Write(BitConverter.GetBytes((short)204), 0, 2);

				// Player count
				stream.WriteByte((byte)API.World.AllOnlinePlayers.Length);

				// Max players
				stream.WriteByte((byte)API.Server.Config.MaxClients);

				// Bot count
				stream.WriteByte(0);

				// Server type
				if (API.Server.IsDedicated)
					stream.WriteByte((byte)'d');
				else
					stream.WriteByte((byte)'l');

				// Get operating system
				switch (Environment.OSVersion.Platform) {
					case PlatformID.MacOSX:
						stream.WriteByte((byte)'o');
						break;
					case PlatformID.Unix:
						stream.WriteByte((byte)'l');
						break;
					default:
						stream.WriteByte((byte)'w');
						break;
				}

				// Password protected
				stream.WriteByte((byte)(API.Server.Config.Password == null ? 0 : 1));

				// VAC protected
				stream.WriteByte((byte)0);

				// Game version
				stream.Write(encoding.GetBytes(GameVersion.OverallVersion + "\0"), 0, GameVersion.OverallVersion.Length + 1);

				Send(client, endpoint, stream.ToArray());
			}
		}

		private static void SendPlayersResponse(UdpClient client, IPEndPoint endpoint)
		{
			using (var stream = new MemoryStream()) {
				int numPlrs = API.World.AllOnlinePlayers.Length;
				stream.Write(new byte[]{ (byte)ResponseType.A2S_PLAYER, (byte)numPlrs }, 0, 2);

				for (int i = 0; i < numPlrs; i++) {
					stream.WriteByte((byte)i);
					stream.Write(encoding.GetBytes(API.World.AllOnlinePlayers[i].PlayerName + "\0"), 0, API.World.AllOnlinePlayers[i].PlayerName.Length + 1);

					 // Score (no obvious way to calculate this as of now)
					stream.Write(BitConverter.GetBytes(0), 0, 4);

					// Duration (using join events and os time)
					int joinTime = BitConverter.ToInt32(((IServerPlayer)API.World.AllOnlinePlayers[i]).GetModdata("DiscordRelay.JoinTime"), 0);
					stream.Write(BitConverter.GetBytes((float)(Environment.TickCount - joinTime) / 1000f), 0, 4);
				}

				Send(client, endpoint, stream.ToArray());
			}
		}
	}
}
