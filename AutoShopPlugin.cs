using System;
using System.IO;
using System.Collections.Generic;
using TShockAPI;
using Terraria;
using TerrariaApi.Server;
using TShockAPI.Hooks;
using System.Linq;
using Terraria.ID;
using Terraria.GameContent;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Xna.Framework;

#pragma warning disable CS8600, CS8602, CS8603

namespace AutoShopPlugin
{
    /// <summary>
    /// Plugin tự động mở shop bán vật phẩm trong Terraria
    /// </summary>
    [ApiVersion(2, 1)]
    public class AutoShopPlugin : TerrariaPlugin
    {
        public override string Name => "AutoShop";
        public override Version Version => new Version(1, 0);
        public override string Author => "GILX_TERRARIAVUI-DEV";
        public override string Description => "Automatically opens a shop to sell potions and fishing baits";

        private List<ShopItem> regularItems = new List<ShopItem>();
        private const int ItemsPerPage = 10; // Number of items displayed per page
        private static readonly Random random = new Random();
        private DateTime lastShopOpen = DateTime.MinValue;
        private Config config = new Config();
        private DateTime lastMarketUpdate = DateTime.MinValue;
        private DateTime lastAutoSave = DateTime.MinValue;
        
        // Thêm biến theo dõi ngày game
        private bool wasNightTime = false;
        private bool isNewDay = false;
        
        // Thêm biến cho hệ thống kinh tế
        private float marketInflation = 1.0f; // Tỷ lệ lạm phát thị trường
        private DateTime lastInflationUpdate = DateTime.MinValue;
        private float serverEconomyIndex = 1.0f; // Chỉ số kinh tế server
        private int totalServerMoney = 0; // Tổng tiền trong server
        
        // Thêm biến lưu trữ cấu hình shop items
        private ShopItemsConfigFile shopItemsConfig = new ShopItemsConfigFile();

        // Thêm cache cho các tính toán phổ biến
        private readonly ConcurrentDictionary<int, int> priceCache = new();
        private readonly ConcurrentDictionary<string, ShopperData> shopperCache = new();

        // Thêm Dictionary để lưu trữ dữ liệu người mua
        private Dictionary<string, ShopperData> shopperData = new Dictionary<string, ShopperData>();

        // Thêm timer để cập nhật bảng xếp hạng định kỳ
        private DateTime lastRankingUpdate = DateTime.MinValue;

        // Tối ưu tìm kiếm item
        private readonly Dictionary<string, ShopItem> itemNameCache = new();
        private readonly Dictionary<int, ShopItem> itemIdCache = new();

        private readonly object shopLock = new object();
        private readonly object saveLock = new object();
        private Dictionary<ShopItem, DateTime> rareItemSchedule = new Dictionary<ShopItem, DateTime>();

        // Chỉ giữ lại các hằng số cho giá trị tiền tệ
        private const int COPPER_VALUE = 1;
        private const int SILVER_VALUE = 100;     // 1 Silver = 100 Copper
        private const int GOLD_VALUE = 10000;     // 1 Gold = 100 Silver = 10000 Copper
        private const int PLATINUM_VALUE = 1000000; // 1 Platinum = 100 Gold = 1000000 Copper

        public AutoShopPlugin(Main game) : base(game)
        {
        }

        public override void Initialize()
        {
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.ServerJoin.Register(this, OnPlayerJoin);
            ServerApi.Hooks.GamePostInitialize.Register(this, OnGamePostInitialize);
            
            Commands.ChatCommands.Add(new Command("", BuyItem, "buy"));
            Commands.ChatCommands.Add(new Command("", ShowShopList, "shoplist"));
            Commands.ChatCommands.Add(new Command("", AutoShopHelp, "autoshop"));
            Commands.ChatCommands.Add(new Command("autoshop.admin", GiveMoneyCommand, "givemoney"));
            Commands.ChatCommands.Add(new Command("", ShowRankingsCommand, "shoprank"));
            Commands.ChatCommands.Add(new Command("autoshop.admin", LoadShopAutoCommand, "loadshopauto"));
            Commands.ChatCommands.Add(new Command("", ShowEconomyStatus, "economy"));
            
            LoadConfig();
            Log("AutoShop plugin loaded successfully!");
            
            ServerApi.Hooks.GameUpdate.Register(this, OnAutoSave);
            
            // Khởi tạo chỉ số kinh tế ban đầu
            InitializeEconomyMetrics();
        }

        private void OnGamePostInitialize(EventArgs args)
        {
            try
            {
                LoadConfig();
                LoadShopperData();
                
                // Tạo hoặc load file config item
                LoadOrCreateShopItemsConfig();
                
                InitializeRegularItems(); // Chỉ cần gọi một lần này
                InitializeItemCaches(); // Cập nhật cache sau khi khởi tạo items
            }
            catch (Exception ex)
            {
                Log($"Error in OnGamePostInitialize: {ex.Message}", true);
            }
        }
        
        // Thêm phương thức tạo hoặc đọc file config
        private void LoadOrCreateShopItemsConfig()
        {
            try
            {
                string filePath = config.ShopItemConfigPath;
                string directory = Path.GetDirectoryName(filePath);
                
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                if (File.Exists(filePath))
                {
                    // Đọc file config nếu đã tồn tại
                    string json = File.ReadAllText(filePath);
                    shopItemsConfig = JsonConvert.DeserializeObject<ShopItemsConfigFile>(json) ?? new ShopItemsConfigFile();
                    
                    // Kiểm tra và loại bỏ các vật phẩm trùng lặp
                    RemoveDuplicateItems();
                    
                    Log($"Loaded shop items config with {shopItemsConfig.ShopItems.Count} items");
                }
                else
                {
                    // Tạo file config mới nếu chưa tồn tại
                    GenerateShopItemsConfig();
                    SaveShopItemsConfig();
                    Log("Generated new shop items config file");
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading shop items config: {ex.Message}", true);
                shopItemsConfig = new ShopItemsConfigFile();
            }
        }
        
        // Thêm phương thức tạo file config mới
        private void GenerateShopItemsConfig()
        {
            shopItemsConfig = new ShopItemsConfigFile();
            
            // Duyệt qua tất cả item trong game
            foreach (var kvp in ContentSamples.ItemsByType)
            {
                Item item = kvp.Value;
                int itemId = item.type;
                
                if (item == null || itemId <= 0) continue;
                
                string category = GetItemCategory(item);
                bool enabled = false;
                
                // Xác định item enabled dựa trên category
                switch (category)
                {
                    case "Potion":
                    case "Bait":
                    case "BossSummon":
                    case "EventSummon":
                        enabled = true;
                        break;
                    default:
                        enabled = false;
                        break;
                }
                
                var itemConfig = new ShopItemConfig(itemId, item.Name, enabled, category);
                shopItemsConfig.ShopItems[itemId] = itemConfig;
            }
        }
        
        // Thêm phương thức phân loại item
        private string GetItemCategory(Item item)
        {
            if (item == null) return "Unknown";
            
            // Phân loại vật phẩm triệu hồi
            if (IsEventSummonItem(item.type))
            {
                return IsBossSummon(item.type) ? "BossSummon" : "EventSummon";
            }
            
            // Phân loại potions
            if ((item.healLife > 0 || item.healMana > 0 || item.buffType > 0) && item.consumable)
            {
                return "Potion";
            }
            
            // Phân loại mồi câu cá
            if (item.bait > 0)
            {
                return "Bait";
            }
            
            // Phân loại vũ khí
            if (item.damage > 0 || item.useAmmo > 0 || item.melee || item.ranged || item.magic || item.summon)
            {
                return "Weapon";
            }
            
            // Phân loại giáp và phụ kiện
            if (item.defense > 0 || item.accessory)
            {
                return "ArmorAccessory";
            }
            
            // Phân loại item trang trí
            if (item.vanity)
            {
                return "Vanity";
            }
            
            // Các loại khác
            return "Other";
        }
        
        // Lưu file config
        private void SaveShopItemsConfig()
        {
            try
            {
                string filePath = config.ShopItemConfigPath;
                string json = JsonConvert.SerializeObject(shopItemsConfig, Formatting.Indented);
                File.WriteAllText(filePath, json);
                Log("Shop items config saved successfully");
            }
            catch (Exception ex)
            {
                Log($"Error saving shop items config: {ex.Message}", true);
            }
        }
        
        // Thêm command để reload shop config
        private void LoadShopAutoCommand(CommandArgs args)
        {
            try
            {
                LoadOrCreateShopItemsConfig();
                InitializeRegularItems();
                InitializeItemCaches();
                args.Player.SendSuccessMessage("Shop items reloaded successfully from config");
                Log("Shop items reloaded via command");
            }
            catch (Exception ex)
            {
                args.Player.SendErrorMessage($"Error reloading shop items: {ex.Message}");
                Log($"Error in LoadShopAutoCommand: {ex.Message}", true);
            }
        }

        private void InitializeRegularItems()
        {
            try
            {
                Log("Starting to initialize regular items...");
                
                if (Main.item == null)
                {
                    Log("Main.item is null, waiting for game initialization...");
                    return;
                }

                // Xóa danh sách cũ
                regularItems.Clear();
                
                // HashSet để theo dõi ItemId đã được thêm vào danh sách cửa hàng
                HashSet<int> addedItemIds = new HashSet<int>();

                foreach (var kvp in ContentSamples.ItemsByType)
                {
                    Item item = kvp.Value;
                    int itemId = item.type;
                    
                    if (item == null || itemId <= 0) continue;
                    
                    // Bỏ qua nếu item đã được thêm vào danh sách
                    if (addedItemIds.Contains(itemId)) continue;
                    
                    // Kiểm tra xem item có trong config không và có được enable không
                    if (!shopItemsConfig.ShopItems.TryGetValue(itemId, out var itemConfig))
                    {
                        // Nếu item không có trong config, thêm nó với giá trị mặc định
                        string category = GetItemCategory(item);
                        bool enabled = (category == "Potion" || category == "Bait" || 
                                       category == "BossSummon" || category == "EventSummon");
                        
                        itemConfig = new ShopItemConfig(itemId, item.Name, enabled, category);
                        shopItemsConfig.ShopItems[itemId] = itemConfig;
                        
                        // Lưu lại config sau khi cập nhật
                        SaveShopItemsConfig();
                    }
                    
                    // Chỉ thêm vào shop những item được enable
                    if (!itemConfig.Enabled) continue;
                    
                    int basePrice;
                    bool isRare = false;
                    
                    // Xác định giá và độ hiếm dựa trên loại vật phẩm
                    switch (itemConfig.Category)
                    {
                        case "BossSummon":
                            basePrice = 20 * GOLD_VALUE; // 20 gold cho Boss summons
                            isRare = true;
                            regularItems.Add(new ShopItem(itemId, item.Name, basePrice, isRare, random.Next(25, 71)));
                            addedItemIds.Add(itemId); // Đánh dấu đã thêm
                            Log($"Added boss summon: {item.Name} for 20 gold");
                            break;
                            
                        case "EventSummon":
                            basePrice = 10 * GOLD_VALUE; // 10 gold cho Event summons
                            isRare = true;
                            regularItems.Add(new ShopItem(itemId, item.Name, basePrice, isRare, random.Next(25, 71)));
                            addedItemIds.Add(itemId); // Đánh dấu đã thêm
                            Log($"Added event summon: {item.Name} for 10 gold");
                            break;
                            
                        case "Potion":
                            basePrice = CalculatePrice(item);
                            regularItems.Add(new ShopItem(itemId, item.Name, basePrice, false, random.Next(25, 71)));
                            addedItemIds.Add(itemId); // Đánh dấu đã thêm
                            Log($"Added potion: {item.Name}");
                            break;
                            
                        case "Bait":
                            basePrice = CalculatePrice(item) * 2;
                            regularItems.Add(new ShopItem(itemId, item.Name, basePrice, false, random.Next(25, 71)));
                            addedItemIds.Add(itemId); // Đánh dấu đã thêm
                            Log($"Added bait: {item.Name} (Power: {item.bait})");
                            break;
                            
                        default:
                            // Những item khác có thể được enable trong config
                            basePrice = CalculatePrice(item);
                            regularItems.Add(new ShopItem(itemId, item.Name, basePrice, false, random.Next(25, 71)));
                            addedItemIds.Add(itemId); // Đánh dấu đã thêm
                            Log($"Added other item: {item.Name}");
                            break;
                    }
                }

                Log($"Initialized shop with {regularItems.Count} items");
                
                // Kiểm tra và loại bỏ các vật phẩm trùng lặp dựa trên tên
                RemoveDuplicateShopItems();
            }
            catch (Exception ex)
            {
                Log($"Error in InitializeRegularItems: {ex.Message}", true);
            }
        }

        // Thêm phương thức kiểm tra vật phẩm triệu hồi
        private bool IsEventSummonItem(int itemType)
        {
            switch (itemType)
            {
                // Boss summons
                case ItemID.SuspiciousLookingEye:        // Eye of Cthulhu
                case ItemID.WormFood:                     // Eater of Worlds
                case ItemID.BloodySpine:                  // Brain of Cthulhu
                case ItemID.SlimeCrown:                   // King Slime
                case ItemID.Abeemination:                 // Queen Bee
                case ItemID.DeerThing:                    // Deerclops
                case ItemID.MechanicalWorm:               // The Destroyer
                case ItemID.MechanicalEye:                // The Twins
                case ItemID.MechanicalSkull:              // Skeletron Prime
                case ItemID.LihzahrdPowerCell:           // Golem
                case ItemID.TruffleWorm:                 // Duke Fishron
                case ItemID.QueenSlimeCrystal:           // Queen Slime  
                case ItemID.EmpressButterfly:            // Empress of Light
                
                // Event summons
                case ItemID.BloodMoonStarter:            // Blood Moon
                case ItemID.GoblinBattleStandard:        // Goblin Army
                case ItemID.PirateMap:                    // Pirate Invasion
                case ItemID.SnowGlobe:                    // Frost Legion
                case ItemID.SolarTablet:                  // Solar Eclipse
                case ItemID.PumpkinMoonMedallion:        // Pumpkin Moon
                case ItemID.NaughtyPresent:               // Frost Moon
                    return true;
            
                default:
                    return false;
            }
        }

        // Thêm phương thức để phân biệt Boss summons và Event summons
        private bool IsBossSummon(int itemType)
        {
            switch (itemType)
            {
                case ItemID.SuspiciousLookingEye:    // Eye of Cthulhu
                case ItemID.WormFood:                 // Eater of Worlds
                case ItemID.BloodySpine:              // Brain of Cthulhu
                case ItemID.SlimeCrown:               // King Slime
                case ItemID.Abeemination:             // Queen Bee
                case ItemID.DeerThing:                // Deerclops
                case ItemID.MechanicalWorm:           // The Destroyer
                case ItemID.MechanicalEye:            // The Twins
                case ItemID.MechanicalSkull:          // Skeletron Prime
                case ItemID.LihzahrdPowerCell:        // Golem
                case ItemID.TruffleWorm:              // Duke Fishron
                case ItemID.QueenSlimeCrystal:        // Queen Slime
                case ItemID.EmpressButterfly:         // Empress of Light
                    return true;
                default:
                    return false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.ServerJoin.Deregister(this, OnPlayerJoin);
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnGamePostInitialize);
                SaveShopperData();
                ServerApi.Hooks.GameUpdate.Deregister(this, OnAutoSave);
            }
            base.Dispose(disposing);
        }

        private void OnGameUpdate(EventArgs args)
        {
            // Kiểm tra chuyển ngày đêm để reset shop
            CheckDayNightCycle();
            
            // Cập nhật chỉ số lạm phát
            if ((DateTime.Now - lastInflationUpdate).TotalHours >= 1)
            {
                UpdateInflationRate();
            }
            
            // Chỉ cập nhật thời gian mà không gửi thông báo
            if ((DateTime.Now - lastShopOpen).TotalMinutes < 5)
                return;

            lastShopOpen = DateTime.Now;
            
            // Check daytime (shop opens during the day)
            if (!Main.dayTime)
                return;

            // Không còn gửi thông báo tự động nữa
            // Chỉ cập nhật trạng thái shop
            foreach (TSPlayer player in TShock.Players)
            {
                if (player?.Active == true)
                {
                    // Thay thế OpenShop bằng cách chỉ cập nhật danh sách items
                    UpdateShopItems(player);
                }
            }
        }
        
        // Thêm phương thức kiểm tra chuyển đổi ngày đêm
        private void CheckDayNightCycle()
        {
            // Kiểm tra chuyển đổi từ đêm sang ngày (một ngày game mới)
            if (wasNightTime && Main.dayTime)
            {
                isNewDay = true;
                ReloadSoldOutItems();
                Log("A new day has begun - reloaded sold out items");
            }
            
            // Cập nhật trạng thái đêm
            wasNightTime = !Main.dayTime;
        }
        
        // Thêm phương thức nạp lại các vật phẩm đã bán hết
        private void ReloadSoldOutItems()
        {
            lock (shopLock)
            {
                foreach (var item in regularItems)
                {
                    // Chỉ nạp lại các item đã hết (Stock = 0)
                    if (item.Stock <= 0)
                    {
                        // Random số lượng mới từ 25-70
                        item.ResetStock(random.Next(25, 71));
                        Log($"Restocked {item.Name} with {item.Stock} units");
                    }
                }
            }
        }

        // Thêm phương thức mới để cập nhật items mà không gửi thông báo
        private void UpdateShopItems(TSPlayer player)
        {
            if (player == null || !player.Active) return;

            try
            {
                lock (shopLock)
                {
                    var shopItems = new List<ShopItem>(regularItems);

                    // Update rare items availability
                    foreach (var item in shopItems.Where(i => i.IsRare))
                    {
                        if (rareItemSchedule.TryGetValue(item, out DateTime saleTime))
                        {
                            item.SetAvailable(DateTime.Now >= saleTime);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error updating shop items for {player.Name}: {ex.Message}", true);
            }
        }

        private void OpenShop(TSPlayer player)
        {
            // Create list of items for sale
            var shopItems = new List<ShopItem>(regularItems);

            // Display shop to player
            if (player == null || !player.Active)
            {
                return;
            }
            player.SendInfoMessage("Shop is now open!");
            foreach (var item in shopItems)
            {
                player.SendInfoMessage($"{item.Name} - {item.Price} coins");
            }
        }

        private int CalculatePrice(Item item)
        {
            if (item == null) return 0;
            
            try
            {
                // Giá cơ bản
                int basePrice = Math.Max(item.value, 1);

                // Nếu là potion (có heal, mana hoặc buff)
                if (item.healLife > 0 || item.healMana > 0 || item.buffType > 0)
                {
                    // Tính độ khó của công thức (số nguyên liệu cần)
                    Recipe recipe = Main.recipe.FirstOrDefault(r => r.createItem.type == item.type);
                    if (recipe != null)
                    {
                        int ingredientCount = recipe.requiredItem.Count(i => i.type > 0);
                        // Mỗi nguyên liệu tăng giá 20%
                        basePrice = (int)(basePrice * (1 + (ingredientCount * 0.2f)));
                    }

                    // Điều chỉnh theo hiệu quả
                    if (item.healLife > 0)
                    {
                        // 1 điểm hồi máu = 10 coin
                        basePrice += item.healLife * 10;
                    }
                    if (item.healMana > 0)
                    {
                        // 1 điểm hồi mana = 5 coin
                        basePrice += item.healMana * 5;
                    }
                    if (item.buffTime > 0)
                    {
                        // Mỗi giây buff = 2 coin
                        basePrice += (item.buffTime / 60) * 2;
                    }
                }
                // Nếu là mồi câu
                else if (item.bait > 0)
                {
                    // Giá tăng theo bait power
                    // Ví dụ: bait power 50% = tăng giá 50%
                    float baitMultiplier = 1 + (item.bait / 100f);
                    basePrice = (int)(basePrice * baitMultiplier);
                }

                return Math.Max(basePrice, 1); // Đảm bảo giá luôn > 0
            }
            catch (Exception ex)
            {
                Log($"Error calculating price for item {item.Name}: {ex.Message}", true);
                return 1;
            }
        }

        private bool IsQuestItem(Item item) => false;
        private bool HasQuestForItem(TSPlayer player, int itemId) => false;

        private void ShowShopList(CommandArgs args)
        {
            try
            {
                // Parse page number
                int page = 1;
                if (args.Parameters.Count > 0 && !int.TryParse(args.Parameters[0], out page))
                {
                    args.Player.SendErrorMessage("Invalid page number!");
                    return;
                }

                var items = regularItems
                    .OrderBy(i => i.ItemId)
                    .Skip((page - 1) * ItemsPerPage)
                    .Take(ItemsPerPage)
                    .ToList();

                if (!items.Any())
                {
                    args.Player.SendErrorMessage($"No items on page {page}");
                    return;
                }

                // Kiểm tra nếu người chơi sử dụng mobile
                bool isMobile = IsMobileUser(args.Player);
                int displayLimit = isMobile ? 5 : ItemsPerPage; // Giảm số item hiển thị trên mobile

                args.Player.SendInfoMessage($"=== Shop Items (Page {page}) ===");
                args.Player.SendInfoMessage("Use /buy <number> [quantity] to purchase");

                var shopperData = GetShopperData(args.Player);
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    int itemIndex = (page - 1) * ItemsPerPage + i + 1;
                    
                    int price = item.GetDiscountedPrice(shopperData);
                    string priceText = FormatCoins(price);
                    string itemIcon = GetItemIcon(item.ItemId);
                    
                    string stockInfo = item.Stock > 0 ? $"[c/00FF00:(Stock: {item.Stock})]" : "[c/FF0000:(Out of Stock)]";
                    string itemInfo = $"#{itemIndex} {itemIcon} {item.Name} - {priceText} {stockInfo}";
                    
                    if (item.bait > 0)
                        itemInfo += $" [c/87CEEB:(Bait Power: {item.bait}%)]";
                    else
                    {
                        Item tempItem = new Item();
                        tempItem.SetDefaults(item.ItemId);
                        if (tempItem.healLife > 0)
                            itemInfo += $" [c/FF69B4:(Heal: {tempItem.healLife})]";
                        if (tempItem.healMana > 0)
                            itemInfo += $" [c/4169E1:(Mana: {tempItem.healMana})]";
                        if (tempItem.buffTime > 0)
                            itemInfo += $" [c/98FB98:(Buff: {tempItem.buffTime/60}s)]";
                    }

                    // Điều chỉnh hiển thị cho mobile
                    if (isMobile)
                    {
                        // Rút gọn thông tin cho mobile
                        itemInfo = $"#{itemIndex} {itemIcon} {item.Name} - {priceText} {stockInfo}";
                    }

                    args.Player.SendInfoMessage(itemInfo);
                }

                int maxPages = (int)Math.Ceiling(regularItems.Count / (double)ItemsPerPage);
                args.Player.SendInfoMessage($"Page {page}/{maxPages}. Type /shoplist [page] to view other pages");
            }
            catch (Exception ex)
            {
                args.Player.SendErrorMessage("Error displaying shop!");
                Log($"Error in ShowShopList: {ex.Message}", true);
            }
        }

        // Thêm phương thức để lấy icon của item
        private string GetItemIcon(int itemId)
        {
            return $"[i:{itemId}]"; // Tag [i:itemId] sẽ hiển thị icon thực tế của item trong game
        }

        private void BuyItem(CommandArgs args)
        {
            try
            {
                if (args.Parameters.Count < 1)
                {
                    args.Player.SendErrorMessage("Usage: /buy <number> [quantity]");
                    return;
                }

                // Parse item index
                if (!int.TryParse(args.Parameters[0], out int itemIndex) || itemIndex < 1)
                {
                    args.Player.SendErrorMessage("Invalid item number!");
                    return;
                }

                // Get item from index
                int actualIndex = itemIndex - 1; // Convert to 0-based index
                var items = regularItems.OrderBy(i => i.ItemId).ToList();
                if (actualIndex >= items.Count)
                {
                    args.Player.SendErrorMessage("Item does not exist!");
                    return;
                }

                var item = items[actualIndex];

                // Parse quantity
                int quantity = 1;
                if (args.Parameters.Count > 1 && !int.TryParse(args.Parameters[1], out quantity))
                {
                    args.Player.SendErrorMessage("Invalid quantity!");
                    return;
                }

                // Process purchase
                ProcessPurchase(args.Player, item, quantity);
            }
            catch (Exception ex)
            {
                args.Player.SendErrorMessage("Error occurred while purchasing!");
                Log($"Error in BuyItem: {ex.Message}", true);
            }
        }

        // Thêm class để xử lý lỗi tùy chỉnh
        public class ShopException : Exception 
        {
            public ShopException(string message) : base(message) { }
        }

        // Thêm validation helper
        private static class Validator
        {
            public static void ValidateQuantity(int quantity, int maxQuantity)
            {
                if (quantity <= 0 || quantity > maxQuantity)
                    throw new ShopException($"Invalid quantity (1-{maxQuantity})");
            }

            public static void ValidatePrice(int price)
            {
                if (price < 0)
                    throw new ShopException("Giá không thể âm");
            }

            public static void ValidateInventorySpace(TSPlayer player, int slots)
            {
                int emptySlots = player.TPlayer.inventory.Count(i => i.IsAir);
                if (emptySlots < slots)
                    throw new ShopException($"Cần {slots} slot trống trong túi đồ");
            }
        }

        /// <summary>
        /// Xử lý giao dịch mua vật phẩm
        /// </summary>
        /// <param name="player">Người chơi mua</param>
        /// <param name="item">Vật phẩm muốn mua</param>
        /// <param name="quantity">Số lượng</param>
        /// <exception cref="ShopException">Lỗi validation</exception>
        private void ProcessPurchase(TSPlayer player, ShopItem item, int quantity)
        {
            try
            {
                if (quantity <= 0 || quantity > config.MaxPurchaseQuantity)
                    throw new Exception($"Invalid quantity (1-{config.MaxPurchaseQuantity})");

                if (!item.IsAvailable)
                    throw new Exception("Item is not available");

                if (quantity > item.Stock)
                    throw new Exception($"Only {item.Stock} items left in stock");

                // Tính giá và kiểm tra tiền
                var shopper = GetShopperData(player);
                int pricePerItem = item.GetDiscountedPrice(shopper);
                
                // Áp dụng biến động giá theo lạm phát
                pricePerItem = (int)(pricePerItem * marketInflation);
                
                int totalCost = pricePerItem * quantity;
                
                // Thêm thuế giao dịch
                int taxAmount = (int)(totalCost * config.TransactionFee);
                int totalWithTax = totalCost + taxAmount;

                // Kiểm tra và trừ tiền
                if (!TryTakeMoney(player, totalWithTax))
                    throw new Exception($"Not enough money. You need {FormatCoins(totalWithTax)} (including {FormatCoins(taxAmount)} tax)");

                using (var transaction = new ShopTransaction(this))
                {
                    if (!item.ReduceStock(quantity))
                        throw new Exception("Error updating stock");

                    if (!TryGiveItem(player, item.ItemId, quantity))
                    {
                        item.ReduceStock(-quantity); // Hoàn lại stock
                        throw new Exception("Cannot give item");
                    }

                    // Cập nhật dữ liệu người mua
                    shopper.TotalSpent += totalCost; // Chỉ tính tiền gốc, không tính thuế
                    shopper.TransactionCount++;
                    shopper.LastPurchase = DateTime.Now;
                    shopper.AddDailySpent(totalCost); // Thêm cập nhật chi tiêu trong ngày

                    transaction.Commit();
                }

                // Log và thông báo
                LogTransaction(player.Name, "bought", item.Name, quantity, totalWithTax);
                player.SendSuccessMessage($"Bought {quantity}x {item.Name} for {FormatCoins(totalCost)} + {FormatCoins(taxAmount)} tax");
                
                // Cập nhật giá dựa trên nhu cầu
                item.UpdatePriceBasedOnDemand(quantity);
            }
            catch (Exception ex)
            {
                player.SendErrorMessage(ex.Message);
                Log($"Purchase failed: {ex.Message}", true);
            }
        }

        /// <summary>
        /// Kiểm tra và trừ tiền của người chơi
        /// </summary>
        /// <returns>true nếu trừ tiền thành công</returns>
        private bool TryTakeMoney(TSPlayer player, int amount)
        {
            try
            {
                var inventory = player.TPlayer.inventory;
                int totalCopper = CalculateTotalCopper(inventory);

                if (totalCopper < amount)
                {
                    player.SendErrorMessage($"Not enough money. You need {FormatCoins(amount)}");
                    return false;
                }

                // Trừ tiền và tính tiền thối
                int remainingCopper = totalCopper - amount;

                // Xóa toàn bộ tiền cũ
                for (int i = 0; i < inventory.Length; i++)
                {
                    var item = inventory[i];
                    if (item == null) continue;

                    if (item.type == ItemID.CopperCoin || 
                        item.type == ItemID.SilverCoin ||
                        item.type == ItemID.GoldCoin ||
                        item.type == ItemID.PlatinumCoin)
                    {
                        inventory[i].SetDefaults(0);
                        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, i);
                    }
                }

                // Trả tiền thừa vào các slot cố định
                if (remainingCopper > 0)
                {
                    // Trả Platinum vào slot 50
                    int platinum = remainingCopper / PLATINUM_VALUE;
                    if (platinum > 0)
                    {
                        inventory[50].SetDefaults(ItemID.PlatinumCoin);
                        inventory[50].stack = platinum;
                        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, 50);
                        remainingCopper %= PLATINUM_VALUE;
                    }

                    // Trả Gold vào slot 51
                    int gold = remainingCopper / GOLD_VALUE;
                    if (gold > 0)
                    {
                        inventory[51].SetDefaults(ItemID.GoldCoin);
                        inventory[51].stack = gold;
                        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, 51);
                        remainingCopper %= GOLD_VALUE;
                    }

                    // Trả Silver vào slot 52
                    int silver = remainingCopper / SILVER_VALUE;
                    if (silver > 0)
                    {
                        inventory[52].SetDefaults(ItemID.SilverCoin);
                        inventory[52].stack = silver;
                        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, 52);
                        remainingCopper %= SILVER_VALUE;
                    }

                    // Trả Copper vào slot 53
                    if (remainingCopper > 0)
                    {
                        inventory[53].SetDefaults(ItemID.CopperCoin);
                        inventory[53].stack = remainingCopper;
                        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, 53);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Error taking money from {player.Name}: {ex.Message}", true);
                return false;
            }
        }

        private int CalculateTotalCopper(Item[] inventory)
        {
            int total = 0;
            // Kiểm tra toàn bộ inventory
            for (int i = 0; i < inventory.Length; i++)
            {
                var item = inventory[i];
                if (item == null || item.IsAir) continue;

                switch (item.type)
                {
                    case ItemID.CopperCoin:
                        total += item.stack;
                        break;
                    case ItemID.SilverCoin:
                        total += item.stack * SILVER_VALUE;
                        break;
                    case ItemID.GoldCoin:
                        total += item.stack * GOLD_VALUE;
                        break;
                    case ItemID.PlatinumCoin:
                        total += item.stack * PLATINUM_VALUE;
                        break;
                }
            }
            return total;
        }

        private bool TryGiveItem(TSPlayer player, int itemId, int stack)
        {
            try
            {
                if (player?.Active != true) return false;

                // Tìm slot trống hoặc slot có cùng loại item
                int slot = FindEmptySlot(player.TPlayer.inventory);

                if (slot == -1)
                {
                    player.SendErrorMessage("Your inventory is full!");
                    return false;
                }

                // Thêm item vào slot
                if (player.TPlayer.inventory[slot].type == itemId)
                {
                    // Stack với item hiện có
                    player.TPlayer.inventory[slot].stack += stack;
                }
                else
                {
                    // Tạo item mới
                    var item = new Item();
                    item.SetDefaults(itemId);
                    item.stack = stack;
                    player.TPlayer.inventory[slot] = item;
                }

                // Cập nhật client
                NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, slot);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error giving item to {player.Name}: {ex.Message}", true);
                return false;
            }
        }

        private void GiveMoney(TSPlayer player, int amount)
        {
            try
            {
                // Thêm Platinum
                if (amount >= PLATINUM_VALUE)
                {
                    int platinum = amount / PLATINUM_VALUE;
                    player.GiveItem(ItemID.PlatinumCoin, platinum);
                    amount %= PLATINUM_VALUE;
                }
                
                // Thêm Gold
                if (amount >= GOLD_VALUE)
                {
                    int gold = amount / GOLD_VALUE;
                    player.GiveItem(ItemID.GoldCoin, gold);
                    amount %= GOLD_VALUE;
                }
                
                // Thêm Silver
                if (amount >= SILVER_VALUE)
                {
                    int silver = amount / SILVER_VALUE;
                    player.GiveItem(ItemID.SilverCoin, silver);
                    amount %= SILVER_VALUE;
                }
                
                // Thêm Copper
                if (amount > 0)
                {
                    player.GiveItem(ItemID.CopperCoin, amount);
                }
            }
            catch (Exception ex)
            {
                Log($"Error giving money to {player.Name}: {ex.Message}", true);
            }
        }

        private void AutoShopHelp(CommandArgs args)
        {
            args.Player.SendInfoMessage("=== TERRARIA_VUI-SHOP ===");
            args.Player.SendInfoMessage("/shoplist [page] - View items in shop");
            args.Player.SendInfoMessage("/buy <number> [quantity] - Buy items from shop");
            args.Player.SendInfoMessage("/shoprank - View shopper rankings");
            args.Player.SendInfoMessage("/economy - View server economy status");
            
            // Thêm hướng dẫn về lệnh mới cho admin
            if (args.Player.HasPermission("autoshop.admin"))
            {
                args.Player.SendInfoMessage("Admin Commands:");
                args.Player.SendInfoMessage("/givemoney <player> <amount> - Give money to a player");
                args.Player.SendInfoMessage("/loadshopauto - Reload shop items from config");
                args.Player.SendInfoMessage("Config file located at: " + config.ShopItemConfigPath);
            }
        }

        private void LoadConfig()
        {
            config = new Config(); // Tạo mới config thay vì load từ file
        }

        public class ShopItem
        {
            public int ItemId { get; }
            public string Name { get; }
            private int basePrice; // Giá gốc tính bằng copper
            public int Stock { get; private set; }
            public int MaxStock { get; private set; }
            public bool IsAvailable => Stock > 0;
            public int TimesSold { get; private set; }
            public DateTime LastRestock { get; private set; }
            public bool IsRare { get; private set; }
            public int bait { get; private set; }
            
            // Thêm thuộc tính cho biến động giá
            private float demandMultiplier = 1.0f;
            private float stockMultiplier = 1.0f;

            public ShopItem(int itemId, string name, int basePrice, bool isRare = false, int initialStock = -1)
            {
                ItemId = itemId;
                Name = name ?? string.Empty;
                this.basePrice = basePrice;
                IsRare = isRare;
                
                Item tempItem = new Item();
                tempItem.SetDefaults(itemId);
                bait = tempItem.bait;

                // Set số lượng vật phẩm trong kho từ 25-70
                MaxStock = initialStock > 0 ? initialStock : random.Next(25, 71);
                Stock = MaxStock;
                LastRestock = DateTime.Now;
                TimesSold = 0;
                
                // Khởi tạo biến động giá
                demandMultiplier = 1.0f;
                stockMultiplier = 1.0f;
            }

            public int GetDiscountedPrice(ShopperData shopper)
            {
                float discount = shopper?.GetDiscount() ?? 0f;
                return (int)(basePrice * (1 - discount) * demandMultiplier * stockMultiplier);
            }

            public bool ReduceStock(int amount)
            {
                if (amount < 0) // Cho phép số âm để hoàn stock trong trường hợp rollback
                {
                    Stock = Math.Min(Stock - amount, MaxStock); // Không vượt quá MaxStock
                    return true;
                }
                
                if (amount <= 0 || amount > Stock)
                    return false;
                    
                Stock -= amount;
                TimesSold += amount;
                
                // Cập nhật stockMultiplier dựa trên Stock còn lại
                UpdateStockMultiplier();
                
                return true;
            }

            public void ResetStock(int newStock)
            {
                Stock = newStock;
                MaxStock = newStock;
                LastRestock = DateTime.Now;
                
                // Reset các biến số biến động giá
                UpdateStockMultiplier();
            }

            public void SetAvailable(bool available)
            {
                if (!available)
                {
                    Stock = 0;
                }
                else
                {
                    Stock = MaxStock;
                    TimesSold = 0;
                    UpdatePrice();
                }
            }

            public void UpdatePrice()
            {
                UpdateDemandMultiplier();
                UpdateStockMultiplier();
            }
            
            // Thêm phương thức cập nhật giá dựa trên nhu cầu
            public void UpdatePriceBasedOnDemand(int quantitySold)
            {
                // Tăng biến số nhu cầu dựa trên số lượng bán
                float demandIncrease = 0.01f * quantitySold; // Tăng 1% cho mỗi vật phẩm
                demandMultiplier += demandIncrease;
                
                // Giới hạn biến số nhu cầu từ 0.8 đến 2.0
                demandMultiplier = Math.Max(0.8f, Math.Min(2.0f, demandMultiplier));
                
                // Cập nhật biến số kho hàng
                UpdateStockMultiplier();
            }
            
            // Thêm phương thức cập nhật biến số kho hàng
            private void UpdateStockMultiplier()
            {
                if (MaxStock <= 0) return;
                
                // Tính tỷ lệ còn hàng
                float stockRatio = (float)Stock / MaxStock;
                
                // Biến số kho hàng tăng khi hàng ít (khan hiếm)
                stockMultiplier = 1.0f + (1.0f - stockRatio) * 0.5f; // Tối đa tăng 50% khi hết hàng
                
                // Giới hạn biến số kho hàng từ 1.0 đến 1.5
                stockMultiplier = Math.Max(1.0f, Math.Min(1.5f, stockMultiplier));
            }
            
            // Thêm phương thức cập nhật biến số nhu cầu
            private void UpdateDemandMultiplier()
            {
                // Giảm dần biến số nhu cầu về 1.0 theo thời gian
                demandMultiplier = (demandMultiplier + 1.0f) / 2.0f;
            }
            
            // Thêm phương thức để thay đổi giá trực tiếp (dùng cho sự kiện kinh tế)
            public void InflatePrice(float factor)
            {
                basePrice = (int)(basePrice * factor);
            }

            // Thêm property Price
            public int Price => (int)(basePrice * demandMultiplier * stockMultiplier);
        }

        public class Config
        {
            public int UpdateInterval { get; set; } = 5; // minutes
            public bool EnableDayTimeOnly { get; set; } = true;
            public bool EnableRareItems { get; set; } = true;
            public float TransactionFee { get; set; } = 0.05f; // 5%
            public float SellPriceMultiplier { get; set; } = 0.4f; // 40% of buy price
            public float TransactionFeeSell { get; set; } = 0.05f; // 5% fee when selling
            public int MaxRankingDisplayCount { get; set; } = 10;
            public int RankingUpdateInterval { get; set; } = 5;
            public int MaxPurchaseQuantity { get; set; } = 999;
            public float MaxDiscountPercent { get; set; } = 0.20f;
            public string SavePath { get; set; } = "tshock/AutoShop/shopdata.json";
            public string BackupPath { get; set; } = "tshock/AutoShop/shopdata.backup.json";
            public List<int> BlacklistedItems { get; set; } = new List<int>();
            public List<int> WhitelistedItems { get; set; } = new List<int>();
            public bool OnlyAllowWhitelisted { get; set; } = false;
            public string ShopItemConfigPath { get; set; } = "tshock/AutoShop/shopitems.json";
            
            // Thêm các cài đặt liên quan đến kinh tế
            public bool EnableDynamicEconomy { get; set; } = true;
            public float MaxInflationRate { get; set; } = 3.0f;
            public float MinInflationRate { get; set; } = 0.5f;
            public float DailyPriceFluctuation { get; set; } = 0.05f; // 5% biến động giá hàng ngày
            public int MaxDailyPurchasesPerPlayer { get; set; } = 100; // Giới hạn giao dịch mỗi ngày
        }

        // Thêm class mới cho cấu hình vật phẩm
        public class ShopItemConfig
        {
            [JsonProperty("ItemId")]
            public int ItemId { get; set; }
            
            [JsonProperty("Name")]
            public string Name { get; set; }
            
            [JsonProperty("Enabled")]
            public bool Enabled { get; set; }
            
            [JsonProperty("Category")]
            public string Category { get; set; }
            
            public ShopItemConfig(int itemId, string name, bool enabled, string category)
            {
                ItemId = itemId;
                Name = name;
                Enabled = enabled;
                Category = category;
            }
        }

        // Class chứa toàn bộ cấu hình vật phẩm
        public class ShopItemsConfigFile
        {
            [JsonProperty("ShopItems")]
            public Dictionary<int, ShopItemConfig> ShopItems { get; set; } = new Dictionary<int, ShopItemConfig>();
        }

        public void Log(string message, bool error = false)
        {
            string logMessage = $"[AutoShop] {message}";
            if (error)
                TShock.Log.Error(logMessage);
            else
                TShock.Log.Info(logMessage);
        }

        private bool HasInventorySpace(TSPlayer player, int itemId, int quantity)
        {
            return player.TPlayer.inventory.Any(slot => slot.IsAir || (slot.type == itemId && slot.stack + quantity <= slot.maxStack));
        }

        private void OnPlayerJoin(JoinEventArgs args)
        {
            try
            {
                TSPlayer player = TShock.Players[args.Who];
                if (player != null)
                {
                    // Check if player has received starter money
                    if (!HasReceivedStarterMoney(player))
                    {
                        GiveStarterMoney(player);
                        SetReceivedStarterMoney(player);
                    }

                    // Send welcome messages and instructions
                    player.SendInfoMessage("Welcome to AutoShop!");
                    player.SendInfoMessage("Use /autoshop for usage guide.");
                    player.SendInfoMessage("Use /shoplist to view available items.");
                    player.SendInfoMessage("You can earn more money by fishing and selling items to the shop!");
                }
            }
            catch (Exception ex)
            {
                Log($"Error in OnPlayerJoin: {ex.Message}");
            }
        }

        private bool HasReceivedStarterMoney(TSPlayer player)
        {
            // Kiểm tra trong DB hoặc file config
            string key = $"starter_money_{player.Account.ID}";
            return player.GetData<bool>(key);
        }

        private void SetReceivedStarterMoney(TSPlayer player)
        {
            string key = $"starter_money_{player.Account.ID}";
            player.SetData(key, true);
        }

        private void GiveStarterMoney(TSPlayer player)
        {
            // Give 1 platinum coin (1,000,000 copper)
            player.GiveItem(ItemID.PlatinumCoin, 1);
            player.SendSuccessMessage("You have received 1 Platinum Coin to start trading!");
        }

        // Thêm command để admin có thể tặng tiền cho người chơi
        private void GiveMoneyCommand(CommandArgs args)
        {
            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Usage: /givemoney <player> <amount>");
                return;
            }

            string playerName = args.Parameters[0];
            var targetPlayer = FindPlayer(playerName);
            if (targetPlayer == null)
            {
                args.Player.SendErrorMessage($"Player {playerName} not found!");
                return;
            }

            if (!int.TryParse(args.Parameters[1], out int amount))
            {
                args.Player.SendErrorMessage("Invalid amount!");
                return;
            }

            GiveMoney(targetPlayer, amount);
            args.Player.SendSuccessMessage($"Given {amount} coins to {playerName}!");
            targetPlayer.SendSuccessMessage($"You have received {amount} coins from {args.Player.Name}!");
        }

        private void UpdateShopperData(TSPlayer player, int amount)
        {
            if (!shopperData.ContainsKey(player.Name))
            {
                shopperData[player.Name] = new ShopperData
                {
                    PlayerName = player.Name,
                    TotalSpent = 0,
                    TransactionCount = 0,
                    LastPurchase = DateTime.Now
                };
            }

            var data = shopperData[player.Name];
            data.TotalSpent += amount;
            data.TransactionCount++;
            data.LastPurchase = DateTime.Now;
            
            // Cập nhật chi tiêu trong ngày
            data.AddDailySpent(amount);

            // Thông báo cho người chơi về cấp độ của họ
            string rank = data.GetRank();
            float discount = data.GetDiscount();
            
            string message = $"[c/{ChatColors.Green}:Shopping Status]\n" +
                            $"Rank: {rank}\n" +
                            $"Total Spent: {FormatCoins(data.TotalSpent)}\n" +
                            $"Today Spent: {FormatCoins(data.TodaySpent)}\n" +
                            $"Transactions: {data.TransactionCount}\n" +
                            $"Current Discount: {discount * 100}%";
            
            SendColoredMessage(player, message, ChatColors.Yellow);
        }

        // Thêm phương thức để hiển thị bảng xếp hạng
        private void ShowShopRankings()
        {
            var currentTime = DateTime.Now;
            if ((currentTime - lastRankingUpdate).TotalMinutes < 5) return; // Cập nhật mỗi 5 phút

            // Lọc và xếp hạng người mua theo chi tiêu trong ngày
            var allShoppers = shopperData.Values
                .Where(x => x.TodaySpent > 0) // Chỉ lấy những người đã chi tiêu trong ngày
                .OrderByDescending(x => x.TodaySpent)
                .ToList();
            
            // Lấy top 3 người mua
            var top3Shoppers = allShoppers.Take(3).ToList();

            if (allShoppers.Count == 0)
            {
                // Không có ai chi tiêu hôm nay
                SendColoredMessageToAll("[c/FFD700:=== SHOP RANKINGS ===]\nNo transactions have been made today!", ChatColors.Yellow);
                lastRankingUpdate = currentTime;
                return;
            }

            string rankingMessage = "[c/FFD700:=== TOP SHOPPERS TODAY ===]\n";
            
            for (int i = 0; i < top3Shoppers.Count; i++)
            {
                var shopper = top3Shoppers[i];
                string medal;
                string medalName;
                string color;
                
                // Gán huy chương và màu sắc theo thứ hạng
                switch (i)
                {
                    case 0: // Top 1
                        medal = "🥇";
                        medalName = "Gold";
                        color = ChatColors.Gold;
                        break;
                    case 1: // Top 2
                        medal = "🥈";
                        medalName = "Silver";
                        color = ChatColors.Silver;
                        break;
                    case 2: // Top 3
                        medal = "🥉";
                        medalName = "Bronze";
                        color = ChatColors.Bronze;
                        break;
                    default:
                        medal = $"{i + 1}.";
                        medalName = "";
                        color = ChatColors.White;
                        break;
                }
                
                // Hiển thị theo format mới: levanA: Rank 1 => 🥇 (Gold)
                rankingMessage += $"{shopper.PlayerName}: Rank {i + 1} => [c/{color}:{medal} ({medalName})]\n";
            }

            // Gửi thông báo cho tất cả người chơi
            SendColoredMessageToAll(rankingMessage, ChatColors.Yellow);
            lastRankingUpdate = currentTime;
        }

        // Thêm command để xem bảng xếp hạng
        private void ShowRankingsCommand(CommandArgs args)
        {
            try
            {
                // Lọc và xếp hạng người mua theo chi tiêu trong ngày
                var allShoppers = shopperData.Values
                    .Where(x => x.TodaySpent > 0) // Chỉ lấy những người đã chi tiêu trong ngày
                    .OrderByDescending(x => x.TodaySpent)
                    .ToList();
                
                // Lấy top 3 người mua
                var top3Shoppers = allShoppers.Take(3).ToList();

                if (allShoppers.Count == 0)
                {
                    // Không có ai chi tiêu hôm nay
                    args.Player.SendInfoMessage("No one has shopped in the store today!");
                    return;
                }

                args.Player.SendInfoMessage("[c/FFD700:=== TOP SHOPPERS TODAY ===]");
                
                // Hiển thị top 3
                for (int i = 0; i < top3Shoppers.Count; i++)
                {
                    var shopper = top3Shoppers[i];
                    string medal;
                    string medalName;
                    string color;
                    
                    // Gán huy chương và màu sắc theo thứ hạng
                    switch (i)
                    {
                        case 0: // Top 1
                            medal = "🥇";
                            medalName = "Gold";
                            color = ChatColors.Gold;
                            break;
                        case 1: // Top 2
                            medal = "🥈";
                            medalName = "Silver";
                            color = ChatColors.Silver;
                            break;
                        case 2: // Top 3
                            medal = "🥉";
                            medalName = "Bronze";
                            color = ChatColors.Bronze;
                            break;
                        default:
                            medal = $"{i + 1}.";
                            medalName = "";
                            color = ChatColors.White;
                            break;
                    }
                    
                    // Hiển thị theo format mới: levanA: Rank 1 => 🥇 (Gold)
                    args.Player.SendInfoMessage($"{shopper.PlayerName}: Rank {i + 1} => [c/{color}:{medal} ({medalName})]");
                }
                
                // Tìm vị trí xếp hạng của người chơi hiện tại
                int playerRank = -1;
                for (int i = 0; i < allShoppers.Count; i++)
                {
                    if (allShoppers[i].PlayerName == args.Player.Name)
                    {
                        playerRank = i + 1; // +1 vì index bắt đầu từ 0
                        break;
                    }
                }
                
                // Hiển thị vị trí xếp hạng của người chơi nếu có
                if (playerRank > 0)
                {
                    // Nếu người chơi nằm trong top 3, không hiển thị dòng này để tránh lặp lại
                    if (playerRank > 3)
                    {
                        args.Player.SendInfoMessage($"You ({args.Player.Name}) are currently at rank: {playerRank}");
                    }
                }
                else
                {
                    // Người chơi chưa có trong danh sách chi tiêu hôm nay
                    args.Player.SendInfoMessage("You haven't made any purchases in the shop today!");
                }
            }
            catch (Exception ex)
            {
                args.Player.SendErrorMessage("Error displaying shop rankings!");
                Log($"Error in ShowRankingsCommand: {ex.Message}", true);
            }
        }

        private void SaveShopperData()
        {
            try
            {
                lock (saveLock)
                {
                    // Backup existing data
                    if (File.Exists(config.SavePath))
                    {
                        File.Copy(config.SavePath, config.BackupPath, true);
                    }

                    string directory = Path.GetDirectoryName(config.SavePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                        string json = JsonConvert.SerializeObject(shopperData, Formatting.Indented);
                        File.WriteAllText(config.SavePath, json);
                        Log("Shopper data saved successfully");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error saving shopper data: {ex.Message}", true);
            }
        }

        private void LoadShopperData()
        {
            try
            {
                if (File.Exists(config.SavePath))
                {
                    string json = File.ReadAllText(config.SavePath);
                    shopperData = JsonConvert.DeserializeObject<Dictionary<string, ShopperData>>(json) 
                                 ?? new Dictionary<string, ShopperData>();
                    Log($"Loaded {shopperData.Count} shopper records");
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading shopper data: {ex.Message}");
                shopperData = new Dictionary<string, ShopperData>();
            }
        }

        private ShopperData GetShopperData(TSPlayer player)
        {
            if (!shopperData.TryGetValue(player.Name, out var data))
            {
                data = new ShopperData
                {
                    PlayerName = player.Name,
                    TotalSpent = 0,
                    TransactionCount = 0,
                    LastPurchase = DateTime.Now
                };
                shopperData[player.Name] = data;
            }
            return data;
        }

        private void OnAutoSave(EventArgs args)
        {
            if ((DateTime.Now - lastAutoSave).TotalMinutes >= 5)
            {
                SaveShopperData();
                lastAutoSave = DateTime.Now;
            }
        }

        private ShopItem? FindShopItem(string input)
        {
            if (int.TryParse(input, out int id))
                return itemIdCache.GetValueOrDefault(id);

            return itemNameCache.GetValueOrDefault(input.ToLower());
        }

        private void HandlePlayer(TSPlayer? player)
        {
            if (player?.Active == true)
            {
                OpenShop(player);
            }
        }

        private TSPlayer? FindPlayer(string playerName)
        {
            var targetPlayer = TShock.Players.FirstOrDefault(p => p?.Name == playerName);
            if (targetPlayer is null)
            {
                Log($"Player {playerName} not found");
                return null;
            }
            return targetPlayer;
        }

        private void BackupShopData()
        {
            try
            {
                lock (saveLock)
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string? configPath = config.SavePath;
                    if (string.IsNullOrEmpty(configPath))
                    {
                        Log("SavePath is not configured", true);
                        return;
                    }

                    string? directory = Path.GetDirectoryName(configPath);
                    if (string.IsNullOrEmpty(directory))
                    {
                        Log("Invalid save path directory", true);
                        return;
                    }

                    string backupDir = Path.Combine(directory, "backups");
                    string backupPath = Path.Combine(backupDir, $"shopdata_{timestamp}.json");

                    // Tạo thư mục backup nếu chưa tồn tại
                    if (!Directory.Exists(backupDir))
                        Directory.CreateDirectory(backupDir);

                    // Giữ tối đa 10 file backup
                    var backupFiles = Directory.GetFiles(backupDir, "shopdata_*.json")
                                             .OrderByDescending(f => f)
                                             .Skip(9);
                    foreach (var oldBackup in backupFiles)
                    {
                        try { File.Delete(oldBackup); }
                        catch { /* ignore deletion errors */ }
                    }

                    // Tạo backup mới
                    if (File.Exists(configPath))
                    {
                        File.Copy(configPath, backupPath);
                        Log($"Created backup: {Path.GetFileName(backupPath)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error backing up shop data: {ex.Message}", true);
            }
        }

        private bool RestoreFromBackup()
        {
            try
            {
                string? savePath = config.SavePath;
                if (string.IsNullOrEmpty(savePath))
                {
                    Log("SavePath is not configured", true);
                    return false;
                }

                string? saveDir = Path.GetDirectoryName(savePath);
                if (string.IsNullOrEmpty(saveDir))
                {
                    Log("Invalid save directory", true);
                    return false;
                }

                string backupDir = Path.Combine(saveDir, "backups");
                if (!Directory.Exists(backupDir))
                    return false;

                // Tìm backup file mới nhất
                var latestBackup = Directory.GetFiles(backupDir, "shopdata_*.json")
                                          .OrderByDescending(f => f)
                                          .FirstOrDefault();

                if (latestBackup == null)
                    return false;

                // Khôi phục từ backup
                string json = File.ReadAllText(latestBackup);
                var restoredData = JsonConvert.DeserializeObject<Dictionary<string, ShopperData>>(json);
                if (restoredData != null)
                {
                    shopperData = restoredData;
                    Log($"Restored data from backup: {Path.GetFileName(latestBackup)}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Error restoring from backup: {ex.Message}", true);
            }
            return false;
        }

        private bool IsItemAllowed(int itemId)
        {
            if (config.OnlyAllowWhitelisted)
                return config.WhitelistedItems.Contains(itemId);
            
            return !config.BlacklistedItems.Contains(itemId);
        }

        private void SendColoredMessage(TSPlayer player, string message, string colorHex)
        {
            if (player == null) return;
            
            // Chuyển đổi hex sang RGB
            var color = HexToColor(colorHex);
            // Chuyển đổi từ Color tự tạo sang Microsoft.Xna.Framework.Color
            var xnaColor = new Microsoft.Xna.Framework.Color(color.R, color.G, color.B, color.A);
            player.SendMessage(message, xnaColor);
        }

        private Color HexToColor(string hex)
        {
            hex = hex.Replace("#", "");
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return new Color(r, g, b);
        }

        private void SendColoredMessageToAll(string message, string colorHex)
        {
            string coloredMessage = $"[c/{colorHex}:]{message}]";
            TSPlayer.All.SendInfoMessage(coloredMessage);
        }

        private void LogTransaction(string playerName, string action, string itemName, int quantity, int price)
        {
            string message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {playerName} {action} {quantity}x {itemName} for {FormatCoins(price)}";
            Log(message);
        }

        private void InitializeItemCaches()
        {
            itemNameCache.Clear();
            itemIdCache.Clear();
            
            foreach (var item in regularItems)
            {
                itemNameCache[item.Name.ToLower()] = item;
                itemIdCache[item.ItemId] = item;
            }
        }

        // Thêm phương thức FormatCoins
        private string FormatCoins(int copperAmount)
        {
            var parts = new List<string>();
            
            int platinum = copperAmount / PLATINUM_VALUE;
            copperAmount %= PLATINUM_VALUE;
            
            int gold = copperAmount / GOLD_VALUE;
            copperAmount %= GOLD_VALUE;
            
            int silver = copperAmount / SILVER_VALUE;
            int copper = copperAmount % SILVER_VALUE;

            if (platinum > 0) parts.Add($"{platinum} platinum");
            if (gold > 0) parts.Add($"{gold} gold");
            if (silver > 0) parts.Add($"{silver} silver");
            if (copper > 0) parts.Add($"{copper} copper");

            return parts.Count > 0 ? string.Join(", ", parts) : "0 copper";
        }

        private int FindEmptySlot(Item[] inventory)
        {
            for (int i = 0; i < inventory.Length; i++)
            {
                if (inventory[i].type == 0 || inventory[i].IsAir)
                    return i;
            }
            return -1;
        }

        // Hàm kiểm tra người dùng mobile
        private bool IsMobileUser(TSPlayer player)
        {
            // Kiểm tra kích thước màn hình hoặc client version
            // Đây là một cách đơn giản, bạn có thể cần điều chỉnh logic này dựa trên cách TShock xác định mobile
            return player.TPlayer.width < 1000; // Giả sử màn hình nhỏ là mobile
        }

        // Thêm phương thức kiểm tra và loại bỏ các vật phẩm trùng lặp trong cửa hàng
        private void RemoveDuplicateShopItems()
        {
            try
            {
                Dictionary<string, ShopItem> uniqueItems = new Dictionary<string, ShopItem>(StringComparer.OrdinalIgnoreCase);
                List<ShopItem> duplicates = new List<ShopItem>();
                
                foreach (var item in regularItems)
                {
                    string normalizedName = item.Name.ToLower().Trim();
                    
                    if (uniqueItems.ContainsKey(normalizedName))
                    {
                        duplicates.Add(item);
                        Log($"Found duplicate shop item: {item.Name} (ID: {item.ItemId})");
                    }
                    else
                    {
                        uniqueItems[normalizedName] = item;
                    }
                }
                
                // Xóa các vật phẩm trùng lặp
                if (duplicates.Count > 0)
                {
                    Log($"Removing {duplicates.Count} duplicate items from shop");
                    
                    foreach (var item in duplicates)
                    {
                        regularItems.Remove(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error removing duplicate shop items: {ex.Message}", true);
            }
        }

        // Thêm phương thức kiểm tra và loại bỏ các vật phẩm trùng lặp trong cấu hình
        private void RemoveDuplicateItems()
        {
            try
            {
                // Tìm kiếm vật phẩm trùng lặp theo tên
                Dictionary<string, List<int>> itemNameMap = new Dictionary<string, List<int>>();
                List<int> duplicateIds = new List<int>();
                
                // Gom nhóm các vật phẩm theo tên
                foreach (var item in shopItemsConfig.ShopItems.Values)
                {
                    string normalizedName = item.Name.ToLower().Trim();
                    
                    if (!itemNameMap.ContainsKey(normalizedName))
                    {
                        itemNameMap[normalizedName] = new List<int>();
                    }
                    
                    itemNameMap[normalizedName].Add(item.ItemId);
                }
                
                // Tìm những vật phẩm có tên trùng lặp
                foreach (var group in itemNameMap.Where(g => g.Value.Count > 1))
                {
                    Log($"Found duplicate items with name '{group.Key}': {string.Join(", ", group.Value)}");
                    
                    // Giữ lại item đầu tiên, đánh dấu các item còn lại là trùng lặp
                    bool keepFirst = true;
                    foreach (int itemId in group.Value)
                    {
                        if (keepFirst)
                        {
                            keepFirst = false;
                            continue;
                        }
                        
                        duplicateIds.Add(itemId);
                    }
                }
                
                // Xóa các vật phẩm trùng lặp
                if (duplicateIds.Count > 0)
                {
                    Log($"Removing {duplicateIds.Count} duplicate items from config");
                    
                    foreach (int itemId in duplicateIds)
                    {
                        shopItemsConfig.ShopItems.Remove(itemId);
                    }
                    
                    // Lưu lại config sau khi đã loại bỏ vật phẩm trùng
                    SaveShopItemsConfig();
                }
            }
            catch (Exception ex)
            {
                Log($"Error removing duplicate items: {ex.Message}", true);
            }
        }

        // Thêm phương thức khởi tạo chỉ số kinh tế
        private void InitializeEconomyMetrics()
        {
            try
            {
                // Khởi tạo giá trị mặc định
                marketInflation = 1.0f;
                serverEconomyIndex = 1.0f;
                totalServerMoney = 0;
                
                // Tính tổng tiền trong server
                CalculateTotalServerMoney();
                
                // Log thông tin kinh tế ban đầu
                Log($"Economy initialized: Inflation={marketInflation}, EconomyIndex={serverEconomyIndex}, TotalMoney={totalServerMoney}");
            }
            catch (Exception ex)
            {
                Log($"Error initializing economy metrics: {ex.Message}", true);
            }
        }
        
        // Thêm phương thức tính tổng tiền trong server
        private void CalculateTotalServerMoney()
        {
            totalServerMoney = 0;
            
            try
            {
                // Tính tổng tiền từ tất cả người chơi đang online
                foreach (TSPlayer player in TShock.Players.Where(p => p?.Active == true))
                {
                    if (player?.TPlayer?.inventory != null)
                    {
                        totalServerMoney += CalculateTotalCopper(player.TPlayer.inventory);
                    }
                }
                
                // Thêm tổng tiền từ dữ liệu người chơi đã lưu
                foreach (var shopper in shopperData.Values)
                {
                    // Giả định mỗi người chơi có khoảng 10% tổng số tiền đã chi tiêu làm dự trữ
                    totalServerMoney += (int)(shopper.TotalSpent * 0.1f);
                }
            }
            catch (Exception ex)
            {
                Log($"Error calculating total server money: {ex.Message}", true);
            }
        }
        
        // Thêm phương thức cập nhật chỉ số lạm phát
        private void UpdateInflationRate()
        {
            try
            {
                // Chỉ cập nhật mỗi giờ thực
                if ((DateTime.Now - lastInflationUpdate).TotalHours < 1)
                    return;
                
                lastInflationUpdate = DateTime.Now;
                
                // Tính lại tổng tiền
                int previousTotal = totalServerMoney;
                CalculateTotalServerMoney();
                
                // Tính tỷ lệ tăng tiền trong server
                float moneyGrowthRate = (previousTotal <= 0) ? 1.0f : (float)totalServerMoney / previousTotal;
                
                // Cập nhật tỷ lệ lạm phát dựa trên tăng trưởng tiền
                // Nếu tiền tăng nhanh, lạm phát tăng
                if (moneyGrowthRate > 1.1f) // Tăng hơn 10%
                {
                    marketInflation *= 1.05f; // Tăng lạm phát 5%
                }
                // Nếu tiền giảm, giảm lạm phát
                else if (moneyGrowthRate < 0.9f) // Giảm hơn 10%
                {
                    marketInflation *= 0.95f; // Giảm lạm phát 5%
                }
                
                // Giới hạn lạm phát từ 0.5 đến 3.0
                marketInflation = Math.Max(0.5f, Math.Min(3.0f, marketInflation));
                
                // Tính chỉ số kinh tế server (càng cao càng tốt)
                serverEconomyIndex = 1.0f / marketInflation;
                
                Log($"Economy updated: Inflation={marketInflation}, EconomyIndex={serverEconomyIndex}, TotalMoney={totalServerMoney}");
                
                // Thực hiện sự kiện kinh tế đặc biệt nếu lạm phát quá cao
                if (marketInflation >= 2.0f && random.Next(100) < 30) // 30% cơ hội nếu lạm phát cao
                {
                    TriggerEconomicEvent();
                }
            }
            catch (Exception ex)
            {
                Log($"Error updating inflation rate: {ex.Message}", true);
            }
        }
        
        // Thêm phương thức tạo sự kiện kinh tế đặc biệt
        private void TriggerEconomicEvent()
        {
            try
            {
                string eventMessage = "";
                bool isBad = marketInflation > 2.0f;
                
                if (isBad)
                {
                    // Sự kiện tiêu cực khi lạm phát cao
                    int eventType = random.Next(3);
                    switch (eventType)
                    {
                        case 0:
                            // Tăng giá tất cả vật phẩm
                            foreach (var item in regularItems)
                            {
                                item.InflatePrice(1.2f); // Tăng giá 20%
                            }
                            eventMessage = "Thị trường khủng hoảng! Giá tất cả vật phẩm tăng 20%!";
                            break;
                            
                        case 1:
                            // Giảm stock tất cả vật phẩm
                            foreach (var item in regularItems)
                            {
                                item.ReduceStock((int)(item.Stock * 0.3f)); // Giảm 30% stock
                            }
                            eventMessage = "Khan hiếm hàng hóa! Số lượng vật phẩm trong cửa hàng giảm mạnh!";
                            break;
                            
                        case 2:
                            // Tăng thuế giao dịch
                            config.TransactionFee *= 1.5f;
                            config.TransactionFee = Math.Min(0.25f, config.TransactionFee); // Tối đa 25%
                            eventMessage = $"Chính sách thuế mới! Phí giao dịch tăng lên {config.TransactionFee * 100}%!";
                            break;
                    }
                }
                else
                {
                    // Sự kiện tích cực khi lạm phát ổn định
                    int eventType = random.Next(3);
                    switch (eventType)
                    {
                        case 0:
                            // Giảm giá vật phẩm
                            foreach (var item in regularItems)
                            {
                                item.InflatePrice(0.8f); // Giảm giá 20%
                            }
                            eventMessage = "Khuyến mãi đặc biệt! Giá tất cả vật phẩm giảm 20%!";
                            break;
                            
                        case 1:
                            // Tăng stock
                            foreach (var item in regularItems)
                            {
                                item.ResetStock(random.Next(50, 100)); // Stock mới từ 50-100
                            }
                            eventMessage = "Hàng mới về! Cửa hàng vừa nhập thêm nhiều vật phẩm!";
                            break;
                            
                        case 2:
                            // Giảm thuế
                            config.TransactionFee *= 0.7f;
                            eventMessage = $"Ưu đãi thuế! Phí giao dịch giảm xuống {config.TransactionFee * 100}%!";
                            break;
                    }
                }
                
                // Thông báo sự kiện cho tất cả người chơi
                SendColoredMessageToAll(eventMessage, isBad ? ChatColors.Red : ChatColors.Green);
                Log($"Economic event triggered: {eventMessage}");
                
                // Đặt lại chỉ số lạm phát về mức trung bình
                marketInflation = (marketInflation + 1.0f) / 2;
            }
            catch (Exception ex)
            {
                Log($"Error triggering economic event: {ex.Message}", true);
            }
        }

        // Thêm phương thức hiển thị tình trạng kinh tế
        private void ShowEconomyStatus(CommandArgs args)
        {
            try
            {
                UpdateInflationRate();
                
                string stability;
                string color;
                
                if (marketInflation < 0.8f)
                {
                    stability = "Deflation";
                    color = ChatColors.Blue;
                }
                else if (marketInflation < 1.2f)
                {
                    stability = "Stable";
                    color = ChatColors.Green;
                }
                else if (marketInflation < 1.8f)
                {
                    stability = "Mild Inflation";
                    color = ChatColors.Yellow;
                }
                else
                {
                    stability = "High Inflation";
                    color = ChatColors.Red;
                }
                
                string message = $"=== Server Economy Status ===\n" +
                                $"Inflation Rate: {marketInflation:F2}\n" +
                                $"Economy Index: {serverEconomyIndex:F2}\n" +
                                $"Total Server Money: {FormatCoins(totalServerMoney)}\n" +
                                $"Status: [c/{color}:{stability}]\n" +
                                $"Current Transaction Tax: {config.TransactionFee * 100}%";
                
                SendColoredMessage(args.Player, message, ChatColors.Yellow);
            }
            catch (Exception ex)
            {
                args.Player.SendErrorMessage("Error displaying economy information!");
                Log($"Error in ShowEconomyStatus: {ex.Message}", true);
            }
        }
    }

    // Thêm class ChatColors để quản lý màu
    public static class ChatColors 
    {
        public const string White = "FFFFFF";
        public const string Yellow = "FFFF00";
        public const string Red = "FF0000";
        public const string Green = "00FF00";
        public const string Blue = "0000FF";
        public const string Gray = "808080";
        public const string Orange = "FFA500";
        public const string Pink = "FFC0CB";
        public const string Purple = "800080";
        public const string Gold = "FFD700";
        public const string Silver = "C0C0C0";
        public const string Bronze = "CD7F32";
    }

    public class Color
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; }

        public Color(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static Color White => new Color(255, 255, 255);
        public static Color Yellow => new Color(255, 255, 0);
        public static Color Red => new Color(255, 0, 0);
        // Thêm các màu khác nếu cần
    }

    public class ShopTransaction : IDisposable
    {
        private readonly AutoShopPlugin plugin;
        private bool committed;
        private readonly List<Action> rollbackActions = new();

        public ShopTransaction(AutoShopPlugin plugin)
        {
            this.plugin = plugin;
        }

        public void AddRollbackAction(Action action)
        {
            rollbackActions.Add(action);
        }

        public void Commit()
        {
            committed = true;
        }

        public void Dispose()
        {
            if (!committed)
            {
                foreach (var action in rollbackActions)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        plugin.Log($"Rollback failed: {ex.Message}", true);
                    }
                }
            }
        }
    }

    public class ShopperData
    {
        public string PlayerName { get; set; } = string.Empty;
        public int TotalSpent { get; set; }
        public int TransactionCount { get; set; }
        public DateTime LastPurchase { get; set; } = DateTime.Now;
        
        // Thêm thông tin mua hàng theo ngày
        public int TodaySpent { get; set; } = 0;
        public DateTime LastDaySpentReset { get; set; } = DateTime.Now.Date;
        
        public string GetRank()
        {
            if (TotalSpent >= 1000000) return "[c/FFD700:GOLD]";
            if (TotalSpent >= 500000) return "[c/C0C0C0:SILVER]";
            return "[c/CD7F32:BRONZE]";
        }

        public float GetDiscount()
        {
            if (TotalSpent >= 1000000) return 0.10f;  // 10% discount
            if (TotalSpent >= 500000) return 0.05f;   // 5% discount
            return 0f;
        }
        
        // Thêm phương thức cập nhật chi tiêu trong ngày
        public void AddDailySpent(int amount)
        {
            // Kiểm tra nếu sang ngày mới thì reset số liệu
            DateTime today = DateTime.Now.Date;
            if (today != LastDaySpentReset)
            {
                TodaySpent = 0;
                LastDaySpentReset = today;
            }
            
            // Cộng thêm số tiền chi tiêu trong ngày
            TodaySpent += amount;
        }
    }
} 