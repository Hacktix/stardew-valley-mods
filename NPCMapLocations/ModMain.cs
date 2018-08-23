/*
NPC Map Locations Mod by Bouhm.
Shows NPC locations on a modified map.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Quests;

namespace NPCMapLocations
{
	public class ModMain : Mod, IAssetLoader
	{
		private const int DRAW_DELAY = 3;

		// For debug info
		private const bool DEBUG_MODE = false;
		private static Dictionary<string, KeyValuePair<string, Vector2>> FarmBuildings;
		private static Vector2 _tileLower;
		private static Vector2 _tileUpper;
		private static string alertFlag;
		private Texture2D BuildingMarkers;
		private ModConfig Config;
		private ModCustomHandler CustomHandler;
		private Dictionary<string, string> CustomNames;

		// Multiplayer
		private Dictionary<long, MapMarker> FarmerMarkers;
		private bool hasOpenedMap;
		private bool isModMapOpen;

		private Dictionary<string, int>
			MarkerCropOffsets; // NPC head crops, top left corner (0, Y), width = 16, height = 15 

		private ModMinimap Minimap;
		private HashSet<MapMarker> NpcMarkers;
		private Dictionary<string, bool> SecondaryNpcs;

		// Replace game map with modified map
		public bool CanLoad<T>(IAssetInfo asset)
		{
			return asset.AssetNameEquals(@"LooseSprites\Map");
		}

		public T Load<T>(IAssetInfo asset)
		{
			T map;
			var mapName = CustomHandler.LoadMap();
			try
			{
				if (!mapName.Equals("default_map"))
					Monitor.Log($"Detected recolored map {CustomHandler.LoadMap()}.", LogLevel.Info);

				map = Helper.Content.Load<T>($@"assets\{mapName}.png"); // Replace map page
			}
			catch
			{
				Monitor.Log($"Unable to find {mapName}; loaded default map instead.", LogLevel.Info);
				map = Helper.Content.Load<T>($@"assets\default_map.png");
			}

			return map;
		}

		public override void Entry(IModHelper helper)
		{
			Config = Helper.ReadConfig<ModConfig>();
			MarkerCropOffsets = ModConstants.MarkerCropOffsets;
			CustomHandler = new ModCustomHandler(helper, Config, Monitor);
			BuildingMarkers =
				Helper.Content.Load<Texture2D>(@"assets/buildings.png"); // Load farm buildings

			SaveEvents.AfterLoad += SaveEvents_AfterLoad;
			TimeEvents.AfterDayStarted += TimeEvents_AfterDayStarted;
			LocationEvents.BuildingsChanged += LocationEvents_BuildingsChanged;
			InputEvents.ButtonPressed += InputEvents_ButtonPressed;
			InputEvents.ButtonReleased += InputEvents_ButtonReleased;
			GameEvents.HalfSecondTick += GameEvents_HalfSecondTick;
			GameEvents.UpdateTick += GameEvents_UpdateTick;
			GraphicsEvents.OnPreRenderHudEvent += GraphicsEvents_OnPreRenderHudEvent;
			GraphicsEvents.OnPostRenderEvent += GraphicsEvents_OnPostRenderEvent;
			GraphicsEvents.Resize += GraphicsEvents_Resize;
      MenuEvents.MenuClosed += MenuEvents_MenuClosed;
		}

    private HashSet<NPC> GetVillagers()
		{
			var villagers = new HashSet<NPC>();

			foreach (var location in Game1.locations)
			{
				foreach (var npc in location.characters)
				{
					if (npc == null) continue;
					if (!ModConstants.ExcludedVillagers.Contains(npc.Name)
					    && npc.isVillager())
						villagers.Add(npc);
				}
			}

			return villagers;
		}

		// For drawing farm buildings on the map 
		// and getting positions relative to the farm 
		private static void UpdateFarmBuildingLocs()
		{
			FarmBuildings = new Dictionary<string, KeyValuePair<string, Vector2>>();

			foreach (var building in Game1.getFarm().buildings)
			{
				if (building == null) continue;
				if (building.nameOfIndoorsWithoutUnique == null
				    || building.nameOfIndoors == null
				    || building.nameOfIndoors.Equals("null")) // Some actually have value of "null"
					continue;

				var locVector = LocationToMap(
					"Farm", // Get building position in farm
					building.tileX.Value,
					building.tileY.Value
				);
				// Using buildingType instead of nameOfIndoorsWithoutUnique because it is a better subset of currentLocation.Name 
				// since nameOfIndoorsWithoutUnique for Barn/Coop does not use Big/Deluxe but rather the upgrade level
				var commonName = building.buildingType.Value ?? building.nameOfIndoorsWithoutUnique;

				if (commonName.Contains("Barn")) locVector.Y += 3;

				// Format: { uniqueName: { commonName: positionOnFarm } }
				// buildingType will match currentLocation.Name for commonName
				FarmBuildings[building.nameOfIndoors] =
					new KeyValuePair<string, Vector2>(building.buildingType.Value, locVector);
			}

			// Greenhouse unlocked after pantry bundles completed
			if (((CommunityCenter) Game1.getLocationFromName("CommunityCenter")).areasComplete[CommunityCenter.AREA_Pantry])
			{
				var locVector = LocationToMap("Greenhouse");
				locVector.X -= 5 / 2 * 3;
				locVector.Y -= 7 / 2 * 3;
				FarmBuildings["Greenhouse"] = new KeyValuePair<string, Vector2>("Greenhouse", locVector);
			}
		}

		private void LocationEvents_BuildingsChanged(object sender, EventArgsLocationBuildingsChanged e)
		{
			if (e.Location.Name.Equals("Farm"))
				UpdateFarmBuildingLocs();
		}

		// Load config and other one-off data
		private void SaveEvents_AfterLoad(object sender, EventArgs e)
		{
			SecondaryNpcs = new Dictionary<string, bool>
			{
				{"Kent", false},
				{"Marlon", false},
				{"Merchant", false},
				{"Sandy", false},
				{"Wizard", false}
			};
			CustomHandler.UpdateCustomNpcs();
			CustomNames = CustomHandler.GetNpcNames();
			MarkerCropOffsets = CustomHandler.GetMarkerCropOffsets();
			UpdateFarmBuildingLocs();
		}

		// Handle opening mod menu and changing tooltip options
		private void InputEvents_ButtonPressed(object sender, EventArgsInput e)
		{
			if (!Context.IsWorldReady) return;

			if (Config.ShowMinimap && Minimap != null && (e.Button == SButton.MouseLeft || e.Button == SButton.ControllerA) &&
			    Game1.activeClickableMenu == null)
			{
				Minimap.HandleMouseDown();
				if (Minimap.isBeingDragged)
					e.SuppressButton();
			}

			if (Game1.activeClickableMenu is GameMenu)
				HandleInput((GameMenu) Game1.activeClickableMenu, e.Button);
		}

		private void InputEvents_ButtonReleased(object sender, EventArgsInput e)
		{
			if (Context.IsWorldReady && e.Button == SButton.MouseLeft)
				Minimap?.HandleMouseRelease();
		}

		// Handle keyboard/controller inputs
		private void HandleInput(GameMenu menu, SButton input)
		{
			if (menu.currentTab != GameMenu.mapTab) return;

			if (Context.IsMainPlayer)
				if (input.ToString().Equals(Config.MenuKey) || input is SButton.ControllerY)
					Game1.activeClickableMenu = new ModMenu(
						SecondaryNpcs,
						CustomNames,
						MarkerCropOffsets,
						Helper,
						Config
					);

			if (input.ToString().Equals(Config.TooltipKey) || input is SButton.RightShoulder)
				ChangeTooltipConfig();
			else if (input.ToString().Equals(Config.TooltipKey) || input is SButton.LeftShoulder) ChangeTooltipConfig(false);
		}

		private void ChangeTooltipConfig(bool incre = true)
		{
			if (incre)
			{
				if (++Config.NameTooltipMode > 3) Config.NameTooltipMode = 1;

				Helper.WriteConfig(Config);
			}
			else
			{
				if (--Config.NameTooltipMode < 1) Config.NameTooltipMode = 3;

				Helper.WriteConfig(Config);
			}
		}

		// Handle any checks that need to be made per day
		private void TimeEvents_AfterDayStarted(object sender, EventArgs e)
		{
			var npcEntries = new Dictionary<string, bool>(SecondaryNpcs);
			foreach (var npc in npcEntries)
			{
				var name = npc.Key;
				switch (name)
				{
					case "Kent":
						SecondaryNpcs[name] = Game1.year >= 2;
						break;
					case "Marlon":
						SecondaryNpcs[name] = Game1.player.eventsSeen.Contains(100162);
						break;
					case "Merchant":
						SecondaryNpcs[name] = ((Forest) Game1.getLocationFromName("Forest")).travelingMerchantDay;
						break;
					case "Sandy":
						SecondaryNpcs[name] = Game1.player.mailReceived.Contains("ccVault");
						break;
					case "Wizard":
						SecondaryNpcs[name] = Game1.player.eventsSeen.Contains(112);
						break;
					default: break;
				}
			}

			ResetMarkers();
			if (Config.ShowMinimap)
				Minimap = new ModMinimap(
					NpcMarkers,
					SecondaryNpcs,
					FarmerMarkers,
					MarkerCropOffsets,
					FarmBuildings,
					BuildingMarkers,
					Helper,
					Config
				);
		}

		private void ResetMarkers()
		{
			NpcMarkers = new HashSet<MapMarker>();
			foreach (var npc in GetVillagers())
			{
				// Handle case where Kent appears even though he shouldn't
				if (npc.Name.Equals("Kent") && !SecondaryNpcs["Kent"]) continue;

				var npcMarker = new MapMarker
				{
					Npc = npc,
					IsBirthday = npc.isBirthday(Game1.currentSeason, Game1.dayOfMonth)
				};
				NpcMarkers.Add(npcMarker);
			}

			if (Context.IsMultiplayer)
				FarmerMarkers = new Dictionary<long, MapMarker>();
		}

		// To initialize ModMap quicker for smoother rendering when opening map
		private void GameEvents_UpdateTick(object sender, EventArgs e)
		{
			if (Game1.activeClickableMenu == null || !(Game1.activeClickableMenu is GameMenu gameMenu))
			{
				isModMapOpen = false;
				return;
			}

			hasOpenedMap =
				gameMenu.currentTab == GameMenu.mapTab; // When map accessed by switching GameMenu tab or pressing M
			isModMapOpen = hasOpenedMap ? isModMapOpen : hasOpenedMap; // When vanilla MapPage is replaced by ModMap
			if (hasOpenedMap && !isModMapOpen) // Only run once on map open
				OpenModMap(gameMenu);
		}

		// Map page updates
		private void GameEvents_HalfSecondTick(object sender, EventArgs e)
		{
			if (!Context.IsWorldReady) return;
			if (Config.ShowMinimap)
				Minimap?.Update();
			UpdateMarkers();
		}

		private void OpenModMap(GameMenu gameMenu)
		{
			isModMapOpen = true;
			UpdateNpcs(true);
			var pages = Helper.Reflection
				.GetField<List<IClickableMenu>>(gameMenu, "pages").GetValue();

			// Changing the page in GameMenu instead of changing Game1.activeClickableMenu
			// allows for better compatibility with other mods that use MapPage
			pages[GameMenu.mapTab] = new ModMapPage(
				NpcMarkers,
				CustomNames,
				SecondaryNpcs,
				FarmerMarkers,
				MarkerCropOffsets,
				FarmBuildings,
				BuildingMarkers,
				Helper,
				Config
			);
		}

		private void UpdateMarkers(bool forceUpdate = false)
		{
			if (isModMapOpen || forceUpdate || true)
			{
				UpdateNpcs(forceUpdate);
				if (Context.IsMultiplayer)
					UpdateFarmers();
			}
		}

		// Update NPC marker data and names on hover
		private void UpdateNpcs(bool forceUpdate = false)
		{
			if (NpcMarkers == null) return;
			foreach (var npcMarker in NpcMarkers)
			{
				var npc = npcMarker.Npc;
				string locationName;
				var npcLocation = npc.currentLocation;

				// Handle null locations at beginning of new day
				if (npcLocation == null)
				{
					locationName = npc.DefaultMap;
					npcLocation = Game1.getLocationFromName(locationName);
				}
				else
				{
					locationName = npc.currentLocation.Name;
				}

				if (locationName == null // Couldn't resolve location name
				    || !ModConstants.MapVectors.TryGetValue(locationName, out var npcPos) // Location not mapped
				)
					continue;

				// For layering indoor/outdoor NPCs and indoor indicator
				npcMarker.IsOutdoors = npcLocation.IsOutdoors;

				// For show Npcs in player's location option
				var isSameLocation = false;

				if (Config.OnlySameLocation)
				{
					isSameLocation = locationName.Equals(Game1.player.currentLocation.Name);
					// Check inside buildings and rooms
					foreach (var door in Game1.player.currentLocation.doors.Pairs)
						// Check buildings
						if (door.Value.Equals(locationName))
						{
							isSameLocation = true;
							break;
						}
						// Check rooms
						else
						{
							foreach (var roomDoor in npcLocation.doors.Pairs)
								if (door.Value.Equals(roomDoor.Value))
								{
									isSameLocation = true;
									break;
								}
						}
				}

				// NPCs that won't be shown on the map unless 'Show Hidden NPCs' is checked
				npcMarker.IsHidden = Config.ImmersionOption == 2 && !Game1.player.hasTalkedToFriendToday(npc.Name)
				                     || Config.ImmersionOption == 3 && Game1.player.hasTalkedToFriendToday(npc.Name)
				                     || Config.OnlySameLocation && !isSameLocation
				                     || Config.ByHeartLevel
				                     && !(Game1.player.getFriendshipHeartLevelForNPC(npc.Name)
				                          >= Config.HeartLevelMin && Game1.player.getFriendshipHeartLevelForNPC(npc.Name)
				                          <= Config.HeartLevelMax);

				// NPCs that will be drawn onto the map
				if (!Config.NpcBlacklist.Contains(npc.Name) && (Config.ShowHiddenVillagers || !npcMarker.IsHidden))
				{
					// Check if gifted for birthday
					if (npcMarker.IsBirthday)
						npcMarker.IsBirthday = Game1.player.friendshipData.ContainsKey(npc.Name) &&
						                       Game1.player.friendshipData[npc.Name].GiftsToday == 0;

					// Check for daily quests
					foreach (var quest in Game1.player.questLog)
						if (quest.accepted.Value && quest.dailyQuest.Value && !quest.completed.Value)
							switch (quest.questType.Value)
							{
								case 3:
									npcMarker.HasQuest = ((ItemDeliveryQuest) quest).target.Value == npc.Name;
									break;
								case 4:
									npcMarker.HasQuest = ((SlayMonsterQuest) quest).target.Value == npc.Name;
									break;
								case 7:
									npcMarker.HasQuest = ((FishingQuest) quest).target.Value == npc.Name;
									break;
								case 10:
									npcMarker.HasQuest = ((ResourceCollectionQuest) quest).target.Value == npc.Name;
									break;
								default:
									break;
							}
						else
							npcMarker.HasQuest = false;

					npcMarker.Marker = npc.Sprite.Texture;

					// Establish draw order, higher number infront
					// Layers 4 - 7: Outdoor NPCs in order of hidden, hidden w/ quest/birthday, standard, standard w/ quest/birthday
					// Layers 0 - 3: Indoor NPCs in order of hidden, hidden w/ quest/birthday, standard, standard w/ quest/birthday
					npcMarker.Layer = npcMarker.IsOutdoors ? 6 : 2;
					if (npcMarker.IsHidden) npcMarker.Layer -= 2;

					if (npcMarker.HasQuest || npcMarker.IsBirthday) npcMarker.Layer++;

					/*
					// Only do calculations if NPCs are moving
					if (!forceUpdate 
					    && (npcMarker.Location != Rectangle.Empty
					    && (!npcLocation.IsOutdoors // Indoors
					    || !npcMarker.Npc.isMoving()))) // Not moving
					{
					    continue;
					}
					*/

					// Get center of NPC marker 
					var x = (int) GetMapPosition(npcLocation, npc.getTileX(), npc.getTileY()).X - 16;
					var y = (int) GetMapPosition(npcLocation, npc.getTileX(), npc.getTileY()).Y - 15;

					npcMarker.Location = new Vector2(x, y);
				}
				else
				{
					// Set no location so they don't get drawn
					npcMarker.Location = Vector2.Zero;
				}
			}
		}

		private void UpdateFarmers()
		{
			foreach (var farmer in Game1.getOnlineFarmers())
			{
				if (farmer?.currentLocation == null) continue;

				var farmerId = farmer.UniqueMultiplayerID;
				var farmerLoc = GetMapPosition(farmer.currentLocation, farmer.getTileX(), farmer.getTileY());

				if (FarmerMarkers.TryGetValue(farmer.UniqueMultiplayerID, out var farMarker))
				{
					var deltaX = farmerLoc.X - farMarker.PrevLocation.X;
					var deltaY = farmerLoc.Y - farMarker.PrevLocation.Y;

					// Location changes before tile position, causing farmhands to blink
					// to the wrong position upon entering new location. Handle this in draw.
					if (farmer.currentLocation.Name == farMarker.PrevLocationName && MathHelper.Distance(deltaX, deltaY) > 15)
						FarmerMarkers[farmerId].DrawDelay = DRAW_DELAY;
					else if (farMarker.DrawDelay > 0)
						FarmerMarkers[farmerId].DrawDelay--;
				}
				else
				{
					var newMarker = new MapMarker
					{
						Name = farmer.Name,
						DrawDelay = 0
					};

					FarmerMarkers.Add(farmerId, newMarker);
				}

				FarmerMarkers[farmerId].Location = farmerLoc;
				FarmerMarkers[farmerId].PrevLocation = farmerLoc;
				FarmerMarkers[farmerId].PrevLocationName = farmer.currentLocation.Name;
				FarmerMarkers[farmerId].IsOutdoors = farmer.currentLocation.IsOutdoors;
			}
		}

		// Helper method for LocationToMap
		public static Vector2 GetMapPosition(GameLocation location, int tileX, int tileY)
		{
			if (location == null || tileX < 0 || tileY < 0) return Vector2.Zero;

			// Handle farm buildings
			// Match currentLocation.Name with buildingType 
			// and use uniqueName to get location of buildings with the same currentLocation.Name
			if (location.IsFarm && !location.Name.Equals("FarmHouse"))
				if (location.uniqueName.Value != null
				    && (FarmBuildings[location.uniqueName.Value].Key.Equals(location.Name)
				        || FarmBuildings[location.uniqueName.Value].Key.Contains("Cabin")))
					return FarmBuildings[location.uniqueName.Value].Value;

			return LocationToMap(location.Name, tileX, tileY);
		}

		// MAIN METHOD FOR PINPOINTING CHARACTERS ON THE MAP
		// Calculated from mapping of game tile positions to pixel coordinates of the map in MapModConstants. 
		// Requires MapModConstants and modified map page in ./assets
		public static Vector2 LocationToMap(string location, int tileX = -1, int tileY = -1, IMonitor monitor = null)
		{
			var mapPagePos =
				Utility.getTopLeftPositionForCenteringOnScreen(300 * Game1.pixelZoom, 180 * Game1.pixelZoom, 0, 0);
			if (!ModConstants.MapVectors.TryGetValue(location, out var locVectors))
			{
				if (monitor != null && alertFlag != "UnknownLocation:" + location)
				{
					monitor.Log("Unknown location: " + location + ".", LogLevel.Trace);
					alertFlag = "UnknownLocation:" + location;
				}

				return Vector2.Zero;
			}

			int x;
			int y;

			// Precise (static) regions and indoor locations
			if (locVectors.Count() == 1 || tileX == -1 || tileY == -1)
			{
				x = locVectors.FirstOrDefault().X;
				y = locVectors.FirstOrDefault().Y;
			}
			else
			{
				// Sort map vectors by distance to point
				var vectors = locVectors.OrderBy(vector =>
					Math.Sqrt(Math.Pow(vector.TileX - tileX, 2) + Math.Pow(vector.TileY - tileY, 2)));

				MapVector lower = null;
				MapVector upper = null;
				var hasEqualTile = false;

				// Create rectangle bound from two pre-defined points (lower & upper bound) and calculate map scale for that area
				foreach (var vector in vectors)
				{
					if (lower != null && upper != null)
					{
						if (lower.TileX == upper.TileX || lower.TileY == upper.TileY)
							hasEqualTile = true;
						else
							break;
					}

					if ((lower == null || hasEqualTile) && tileX >= vector.TileX && tileY >= vector.TileY)
					{
						lower = vector;
						continue;
					}

					if ((upper == null || hasEqualTile) && tileX <= vector.TileX && tileY <= vector.TileY) upper = vector;
				}

				// Handle null cases - not enough vectors to calculate using lower/upper bound strategy
				// Uses fallback strategy - get closest points such that lower != upper
				var tilePos = "(" + tileX + ", " + tileY + ")";
				if (lower == null)
				{
					if (monitor != null && alertFlag != "NullBound:" + tilePos)
					{
						monitor.Log("Null lower bound: No vector less than " + tilePos + " in " + location, LogLevel.Trace);
						alertFlag = "NullBound:" + tilePos;
					}

					lower = upper == vectors.First() ? vectors.Skip(1).First() : vectors.First();
				}

				if (upper == null)
				{
					if (monitor != null && alertFlag != "NullBound:" + tilePos)
					{
						monitor.Log("Null upper bound: No vector greater than " + tilePos + " in " + location, LogLevel.Trace);
						alertFlag = "NullBound:" + tilePos;
					}

					upper = lower == vectors.First() ? vectors.Skip(1).First() : vectors.First();
				}

				x = (int) (lower.X + (tileX - lower.TileX) / (double) (upper.TileX - lower.TileX) * (upper.X - lower.X));
				y = (int) (lower.Y + (tileY - lower.TileY) / (double) (upper.TileY - lower.TileY) * (upper.Y - lower.Y));

				if (DEBUG_MODE)
				{
					_tileUpper = new Vector2(upper.TileX, upper.TileY);
					_tileLower = new Vector2(lower.TileX, lower.TileY);
				}
			}

			return new Vector2(x, y);
		}

		private void GraphicsEvents_Resize(object sender, EventArgs e)
		{
			if (!Context.IsWorldReady) return;

			UpdateMarkers(true);
			UpdateFarmBuildingLocs();
		}

		private void MenuEvents_MenuClosed(object sender, EventArgsClickableMenuClosed e)
		{
			if (!Context.IsWorldReady) return;
			if (e.PriorMenu is ModMenu)
				Minimap?.Resize();
		}

		private void GraphicsEvents_OnPreRenderHudEvent(object sender, EventArgs e)
		{
			if (Context.IsWorldReady)
			{
				Minimap?.DrawMiniMap();
			}
		}

		// DEBUG 
		private void GraphicsEvents_OnPostRenderEvent(object sender, EventArgs e)
		{
			if (!Context.IsWorldReady || Game1.player == null) return;

			if (DEBUG_MODE)
				ShowDebugInfo();
		}

		// Show debug info in top left corner
		private static void ShowDebugInfo()
		{
			if (Game1.player.currentLocation == null) return;

			// Black backgronud for legible text
			Game1.spriteBatch.Draw(Game1.shadowTexture, new Rectangle(50, 50, 425, 160), new Rectangle(1, 80, 1, 1),
				Color.Black);

			// Show map location and tile positions
			DrawText(
				Game1.player.currentLocation.Name + " (" + Game1.player.Position.X / Game1.tileSize + ", " +
				Game1.player.Position.Y / Game1.tileSize + ")", new Vector2(Game1.tileSize / 4, Game1.tileSize / 4));

			var currMenu = Game1.activeClickableMenu is GameMenu ? (GameMenu) Game1.activeClickableMenu : null;

			// Show lower & upper bound tiles used for calculations 
			if (currMenu != null && currMenu.currentTab == GameMenu.mapTab)
			{
				DrawText("Lower bound: (" + _tileLower.X + ", " + _tileLower.Y + ")",
					new Vector2(Game1.tileSize / 4, Game1.tileSize * 3 / 4 + 8));
				DrawText("Upper bound: (" + _tileUpper.X + ", " + _tileUpper.Y + ")",
					new Vector2(Game1.tileSize / 4, Game1.tileSize * 5 / 4 + 8 * 2));
			}
			else
			{
				DrawText("Lower bound: (" + _tileLower.X + ", " + _tileLower.Y + ")",
					new Vector2(Game1.tileSize / 4, Game1.tileSize * 3 / 4 + 8), Color.DimGray);
				DrawText("Upper bound: (" + _tileUpper.X + ", " + _tileUpper.Y + ")",
					new Vector2(Game1.tileSize / 4, Game1.tileSize * 5 / 4 + 8 * 2), Color.DimGray);
			}
		}

		// Draw outlined text
		private static void DrawText(string text, Vector2 pos, Color? color = null)
		{
			Game1.spriteBatch.DrawString(Game1.dialogueFont, text, pos + new Vector2(1, 1), Color.Black);
			Game1.spriteBatch.DrawString(Game1.dialogueFont, text, pos + new Vector2(-1, 1), Color.Black);
			Game1.spriteBatch.DrawString(Game1.dialogueFont, text, pos + new Vector2(1, -1), Color.Black);
			Game1.spriteBatch.DrawString(Game1.dialogueFont, text, pos + new Vector2(-1, -1), Color.Black);
			Game1.spriteBatch.DrawString(Game1.dialogueFont, text, pos, color ?? Color.White);
		}
	}

	// Class for map markers
	public class MapMarker
	{
		public string Name { get; set; }
		public Texture2D Marker { get; set; }
		public NPC Npc { get; set; }
		public Vector2 Location { get; set; }
		public Vector2 PrevLocation { get; set; }
		public string PrevLocationName { get; set; }
		public bool IsBirthday { get; set; }
		public bool HasQuest { get; set; }
		public bool IsOutdoors { get; set; }
		public bool IsHidden { get; set; }
		public int Layer { get; set; }
		public int DrawDelay { get; set; }
	}

	// Class for Location Vectors
	public class MapVector
	{
		public int TileX;
		public int TileY;
		public int X;
		public int Y;

		public MapVector()
		{
			TileX = 0;
			TileY = 0;
			X = 0;
			Y = 0;
		}

		public MapVector(int x, int y)
		{
			TileX = 0;
			TileY = 0;
			X = x;
			Y = y;
		}

		public MapVector(int tileX, int tileY, int x, int y)
		{
			TileX = tileX;
			TileY = tileY;
			X = x;
			Y = y;
		}

		public int[] GetValues()
		{
			return new[] {TileX, TileY, X, Y};
		}
	}
}