using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ExpressMapper.Extensions;
using LinqToDB;
using Microsoft.Extensions.Logging;
using Netsphere.Common.Configuration;
using Netsphere.Database;
using Netsphere.Database.Game;
using Netsphere.Database.Helpers;
using Netsphere.Network;
using Netsphere.Network.Data.Game;
using Netsphere.Network.Message.Game;
using Netsphere.Server.Game.Services;

namespace Netsphere.Server.Game
{
    public class Player : DatabaseObject, ISaveable
    {
        private static readonly uint[] s_licensesCompleted;

        private readonly GameOptions _gameOptions;
        private readonly IDatabaseProvider _databaseProvider;
        private readonly GameDataService _gameDataService;
        private IDisposable _scope;
        private byte _tutorialState;
        private uint _totalExperience;
        private uint _pen;
        private uint _ap;
        private uint _coins1;
        private uint _coins2;

        public ILogger Logger { get; }
        public Session Session { get; private set; }
        public Account Account { get; private set; }
        public CharacterManager CharacterManager { get; }
        public LicenseManager LicenseManager { get; }
        public PlayerInventory Inventory { get; }
        public byte TutorialState
        {
            get => _tutorialState;
            set => SetIfChanged(ref _tutorialState, value);
        }
        public uint TotalExperience
        {
            get => _totalExperience;
            set => SetIfChanged(ref _totalExperience, value);
        }
        public uint PEN
        {
            get => _pen;
            set => SetIfChanged(ref _pen, value);
        }
        public uint AP
        {
            get => _ap;
            set => SetIfChanged(ref _ap, value);
        }
        public uint Coins1
        {
            get => _coins1;
            set => SetIfChanged(ref _coins1, value);
        }
        public uint Coins2
        {
            get => _coins2;
            set => SetIfChanged(ref _coins2, value);
        }
        public Channel Channel { get; internal set; }
        public object Room { get; internal set; }

        public event EventHandler<PlayerEventArgs> Disconnected;

        internal void OnDisconnected()
        {
            Disconnected?.Invoke(this, new PlayerEventArgs(this));

            _scope.Dispose();
        }

        static Player()
        {
            s_licensesCompleted = new uint[100];
            for (uint i = 0; i < 100; ++i)
                s_licensesCompleted[i] = i;
        }

        internal Player(ILogger<Player> logger, GameOptions gameOptions, IDatabaseProvider databaseProvider,
            GameDataService gameDataService,
            CharacterManager characterManager, LicenseManager licenseManager, PlayerInventory inventory)
        {
            Logger = logger;
            _gameOptions = gameOptions;
            _databaseProvider = databaseProvider;
            _gameDataService = gameDataService;
            CharacterManager = characterManager;
            LicenseManager = licenseManager;
            Inventory = inventory;
        }

        internal void Initialize(Session session, Account account, PlayerEntity entity)
        {
            Session = session;
            Account = account;
            _scope = Logger.BeginScope("PlayerId={PlayerId} AccountId={AccountId} HostId={HostId} EndPoint={EndPoint}",
                entity.Id, account.Id, session.HostId, session.RemoteEndPoint);
            CharacterManager.Initialize(this, entity);
            LicenseManager.Initialize(this, entity.Licenses);
        }

        public async Task SendAccountInformation()
        {
            var licenses = _gameOptions.EnableLicenseRequirement
                ? LicenseManager.Select(x => (uint)x.ItemLicense).ToArray()
                : s_licensesCompleted;

            await Session.SendAsync(new SMyLicenseInfoAckMessage(licenses));
            await Session.SendAsync(new SInventoryInfoAckMessage
            {
                Items = Inventory.Select(x => x.Map<PlayerItem, ItemDto>()).ToArray()
            });

            await Session.SendAsync(new SCharacterSlotInfoAckMessage
            {
                ActiveCharacter = CharacterManager.CurrentSlot,
                CharacterCount = (byte)CharacterManager.Count,
                MaxSlots = 3
            });

            foreach (var character in CharacterManager)
            {
                await Session.SendAsync(new SOpenCharacterInfoAckMessage
                {
                    Slot = character.Slot,
                    Style = new CharacterStyle(character.Gender, character.Hair.Variation, character.Face.Variation,
                        character.Shirt.Variation,
                        character.Pants.Variation, character.Slot)
                });

                var message = new SCharacterEquipInfoAckMessage
                {
                    Slot = character.Slot,
                    Weapons = character.Weapons.GetItems().Select(x => x?.Id ?? 0).ToArray(),
                    Skills = new[] { character.Skills.GetItem(0).Item1?.Id ?? 0 },
                    Clothes = character.Costumes.GetItems().Select(x => x?.Id ?? 0).ToArray()
                };

                await Session.SendAsync(message);
            }

            await Session.SendAsync(new SRefreshCashInfoAckMessage(PEN, AP));
            await Session.SendAsync(new SSetCoinAckMessage(Coins1, Coins2));
            await Session.SendAsync(new SServerResultInfoAckMessage(ServerResult.WelcomeToS4World));
            await Session.SendAsync(new SBeginAccountInfoAckMessage
            {
                Level = (byte)_gameDataService.GetLevelFromExperience(TotalExperience).Level,
                TotalExp = TotalExperience,
                AP = AP,
                PEN = PEN,
                TutorialState = (uint)(_gameOptions.EnableTutorial ? TutorialState : 2),
                Nickname = Account.Nickname
            });

            await Session.SendAsync(new SServerResultInfoAckMessage(ServerResult.WelcomeToS4World2));

            if (Inventory.Count == 0)
            {
                IEnumerable<StartItemEntity> startItems;
                using (var db = _databaseProvider.Open<GameContext>())
                {
                    var securityLevel = (byte)Account.SecurityLevel;
                    startItems = await db.StartItems.Where(x => x.RequiredSecurityLevel <= securityLevel).ToArrayAsync();
                }

                foreach (var startItem in startItems)
                {
                    var item = _gameDataService.ShopItems.Values.First(group =>
                        group.GetItemInfo(startItem.ShopItemInfoId) != null);
                    var itemInfo = item.GetItemInfo(startItem.ShopItemInfoId);
                    var effect = itemInfo.EffectGroup.GetEffect(startItem.ShopEffectId);

                    if (itemInfo == null)
                    {
                        Logger.LogWarning("Cant find ShopItemInfo for Start item {startItemId} - Forgot to reload the cache?",
                            startItem.Id);
                        continue;
                    }

                    var price = itemInfo.PriceGroup.GetPrice(startItem.ShopPriceId);
                    if (price == null)
                    {
                        Logger.LogWarning("Cant find ShopPrice for Start item {startItemId} - Forgot to reload the cache?",
                            startItem.Id);
                        continue;
                    }

                    var color = startItem.Color;
                    if (color > item.ColorGroup)
                    {
                        Logger.LogWarning("Start item {startItemId} has an invalid color {color}", startItem.Id, color);
                        color = 0;
                    }

                    var count = startItem.Count;
                    if (count > 0 && item.ItemNumber.Category <= ItemCategory.Skill)
                    {
                        Logger.LogWarning("Start item {startItemId} cant have stacks(quantity={count})", startItem.Id, count);
                        count = 0;
                    }

                    if (count < 0)
                        count = 0;

                    Inventory.Create(itemInfo, price, color, effect.Effect, (uint)count);
                }
            }
        }

        public async Task Save(GameContext db)
        {
            if (IsDirty)
            {
                await db.UpdateAsync(new PlayerEntity
                {
                    Id = (int)Account.Id,
                    TutorialState = TutorialState,
                    TotalExperience = (int)TotalExperience,
                    PEN = (int)PEN,
                    AP = (int)AP,
                    Coins1 = (int)Coins1,
                    Coins2 = (int)Coins2,
                    CurrentCharacterSlot = CharacterManager.CurrentSlot
                });

                SetDirtyState(false);
            }

            await CharacterManager.Save(db);
            await LicenseManager.Save(db);
        }
    }
}