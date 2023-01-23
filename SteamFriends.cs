using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Configuration;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Steam Friends", "8SecSleeper", "1.0.0")]
    [Description("Developer API used to check a player's Steam friends")]
    class SteamFriends : RustPlugin
    {
		#region Fields
		
		private static SteamFriends Instance { get; set; }
		private const string STEAM_API_URL = "https://api.steampowered.com/ISteamUser/GetFriendList/v0001/?key={0}&steamid={1}";
		private JsonSerializerSettings errorHandling = new JsonSerializerSettings { Error = (se, ev) => { ev.ErrorContext.Handled = true; } };
		static HashSet<FriendsData> loadedFriendsData = new HashSet<FriendsData>();
		private DataFileSystem dataFile;

		#endregion
		
		#region Configuration
		
        private static ConfigData config;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Steam API key (get one here https://steamcommunity.com/dev/apikey)")]
            public string SteamAPIKey;
			
			[JsonProperty(PropertyName = "Delay between Steam query during plugin load event, min = 1 max = 10, default is 1 second")]
			public int initDelay;
			
			[JsonProperty(PropertyName = "Minimum time before a user's friend list can be updated, min = 60 max = 86400, default is 3600 seconds")]
			public int refreshInterval;
        }

        private ConfigData GetDefaultConfig() 
        {
            return new ConfigData
            {
               SteamAPIKey =  "-1",
			   initDelay = 1,
			   refreshInterval = 3600
            };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<ConfigData>();
                if (config == null)
                {
                    LoadDefaultConfig();
                }
				
				if (config.initDelay < 1)
					config.initDelay = 1;
				if (config.initDelay > 10)
					config.initDelay = 10;
				if (config.refreshInterval < 60)
					config.refreshInterval = 60;
				if (config.refreshInterval > 86400)
					config.refreshInterval = 86400;
            }
            catch
            {
                Puts("Configuration file is corrupt! Check your config file at https://jsonlint.com/");
                LoadDefaultConfig();
                return;
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config);
        }

        #endregion
		
		#region Oxide Hooks
		private void Init()
        {
            Instance = this;
		}
		
		private void OnServerInitialized()
        {
			if (config.SteamAPIKey == "-1")
			{
				Puts("Invalid Steam Key, plugin disabled, get one here https://steamcommunity.com/dev/apikey");
				timer.Once(1f, () =>
				{
					Interface.Oxide.UnloadPlugin(Name);
				});
				return;				
			}		
			ServerMgr.Instance.StartCoroutine(initFriendsData());
        }
		
		private void OnPlayerConnected(BasePlayer player) 
		{
			FriendsData.TryFindByID(player.UserIDString);
		}
		
		#endregion
		
        #region Api Hooks 
		
        bool isSteamFriend(string steamID, string targetSteamID)
		{
			FriendsData friendsData = FriendsData.TryFindByID(steamID);
			if (friendsData != null)
			{
				if (friendsData.friends.Contains(targetSteamID))
					return true;
			}
			
			friendsData = FriendsData.TryFindByID(targetSteamID);
			if (friendsData != null)
			{
				if (friendsData.friends.Contains(steamID))
					return true;
			}
			
			return false;
		}
		
		List<BasePlayer> inGameSteamFriends(string steamID)
		{
			List<BasePlayer> friends = new List<BasePlayer>();
			foreach(BasePlayer player in BasePlayer.activePlayerList)
			{
				if (isSteamFriend(steamID, player.UserIDString))
					friends.Add(player);
			}
			
			return friends;
		}
		
        #endregion
		
		#region Functions
		
		private void getSteamFriends(string steamID)
		{
			webrequest.Enqueue(string.Format(STEAM_API_URL, config.SteamAPIKey, steamID), null, (code, response) =>
				processSteamQuery(code, response, steamID), this, RequestMethod.GET, null, 10f);
		}
		
		private IEnumerator initFriendsData()
		{
			foreach(BasePlayer player in BasePlayer.activePlayerList)
			{
				FriendsData.TryFindByID(player.UserIDString);
				yield return new WaitForSeconds(config.initDelay);
			}	
			yield break;
		}
		
		private void processSteamQuery(int code, string response, string steamID)
		{			
			if (response != null && code == 200)
			{
				try
				{
					FriendsObject rootObject = JsonConvert.DeserializeObject<FriendsObject>(response, errorHandling);

					FriendsData friendsData = FriendsData.FindByID(steamID);
					if (friendsData == null)
						return;
					
					friendsData.friends.Clear();		
					if (rootObject.friendslist.friends != null)
					{
						int count = rootObject.friendslist.friends.Count();
						if (count > 0)
						{	
							for (int i=0; i<count; i++)
							{
								string targetSteamID = rootObject.friendslist.friends[i].steamid;
								friendsData.friends.Add(targetSteamID); 
							}
						} 				
					}

					FriendsData.RemoveByID(steamID);			
					friendsData.Save(steamID);						
					loadedFriendsData.Add(friendsData); 		                       
				}
				catch { }
			} 
		}

		#endregion
		
		#region Data Management
		
		public class FriendsData
        {
			public string UserID = "-1";
			public float lastUpdated = 0; 
			public List<string> friends = new List<string>();
			
			internal static void RemoveByID(string userID)
            {			
				FriendsData friendsData = FindByID(userID);
                if (friendsData != null)
				{
					loadedFriendsData.Remove(friendsData);
				}
            } 
			
			internal static void requestSteamFriends(string userID)
			{
				FriendsData friendsData = new FriendsData
				{
					UserID = userID,
					lastUpdated = UnityEngine.Time.time					
				};
				friendsData.Save(userID);
				loadedFriendsData.Add(friendsData);
				
				Instance.getSteamFriends(userID); 
			}
			
			internal static FriendsData TryFindByID(string userID)
            {
				FriendsData friendsData = FindByID(userID);
                if (friendsData != null)
                    return friendsData;

				friendsData = Interface.Oxide.DataFileSystem.ReadObject<FriendsData>($"SteamFriends/friendInfo_{userID}");     
                if (friendsData != null && friendsData.UserID != null)
				{
					loadedFriendsData.Add(friendsData);
					if (UnityEngine.Time.time > friendsData.lastUpdated + config.refreshInterval)
					{					
						requestSteamFriends(userID);
						return null;
					}
					return friendsData;
				} else {						
					requestSteamFriends(userID); 				
				}
                return null; 
            } 
			
			internal static FriendsData FindByID(string userID)
            {
                FriendsData friendsData = loadedFriendsData.ToList().Find((p) => p.UserID == userID);
                return friendsData;
            }
			
			internal void Save(string userID)
			{
				Interface.Oxide.DataFileSystem.WriteObject($"SteamFriends/friendInfo_{userID}", this, true);
			}	
		}
	
		public class FriendsObject
        {
            public Friendslist friendslist { get; set; }

            public class Friendslist
            {
                public List<Friends> friends = new List<Friends>();

                public class Friends
                {
                    public string steamid { get; set; }
                    public string relationship { get; set; }
                    public int friend_since { get; set; }                 
                }
            }
        }
		
		#endregion
    }
}