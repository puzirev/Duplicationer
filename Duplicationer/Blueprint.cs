﻿using C3;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TinyJSON;
using Unfoundry;
using UnityEngine;

namespace Duplicationer
{
    public class Blueprint
    {
        public const uint FileMagicNumber = 0x42649921u;
        public const uint LatestBlueprintVersion = 3;

        public string Name => name;
        public Vector3Int Size => data.blocks.Size;
        public int SizeX => data.blocks.sizeX;
        public int SizeY => data.blocks.sizeY;
        public int SizeZ => data.blocks.sizeZ;
        public bool HasMinecartDepots => minecartDepotIndices.IsNullOrEmpty();

        public ItemTemplate[] IconItemTemplates => iconItemTemplates;
        private ItemTemplate[] iconItemTemplates;

        public Dictionary<ulong, ShoppingListData> ShoppingList { get; private set; }

        private string name;
        private BlueprintData data;
        private int[] minecartDepotIndices;

        private static List<BuildableObjectGO> bogoQueryResult = new List<BuildableObjectGO>(0);
        private static List<ConstructionTaskGroup.ConstructionTask> dependenciesTemp = new List<ConstructionTaskGroup.ConstructionTask>();
        private static Dictionary<ConstructionTaskGroup.ConstructionTask, List<ulong>> genericDependencies = new Dictionary<ConstructionTaskGroup.ConstructionTask, List<ulong>>();

        public delegate void PostBuildAction(ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task);
        private static List<PostBuildAction> postBuildActions = new List<PostBuildAction>();

        private static ItemTemplate powerlineItemTemplate;

        private static ItemTemplate PowerlineItemTemplate
        {
            get => (powerlineItemTemplate == null) ? powerlineItemTemplate = ItemTemplateManager.getItemTemplate("_base_power_line_i") : powerlineItemTemplate;
        }

        private struct PowerlineConnectionPair
        {
            public ulong fromEntityId;
            public ulong toEntityId;

            public PowerlineConnectionPair(ulong fromEntityId, ulong toEntityId)
            {
                this.fromEntityId = fromEntityId;
                this.toEntityId = toEntityId;
            }

            public override bool Equals(object obj)
            {
                if (!(obj is PowerlineConnectionPair)) return false;
                var other = (PowerlineConnectionPair)obj;
                return fromEntityId == other.fromEntityId && toEntityId == other.toEntityId;
            }

            public override int GetHashCode()
            {
                return fromEntityId.GetHashCode() ^ toEntityId.GetHashCode();
            }
        }
        private static HashSet<PowerlineConnectionPair> _powerlineConnectionPairs = new HashSet<PowerlineConnectionPair>();

        public Blueprint(string name, BlueprintData data, int[] minecartDepotIndices, Dictionary<ulong, ShoppingListData> shoppingList, ItemTemplate[] iconItemTemplates)
        {
            this.name = name;
            this.data = data;
            this.minecartDepotIndices = minecartDepotIndices;
            this.ShoppingList = shoppingList;
            this.iconItemTemplates = iconItemTemplates;
        }

        public static Blueprint Create(Vector3Int from, Vector3Int size)
        {
            var to = from + size;

            var shoppingList = new Dictionary<ulong, ShoppingListData>();
            var minecartDepotIndices = new List<int>();
            var blocks = new byte[size.x * size.y * size.z];
            var blocksIndex = 0;
            for (int wz = from.z; wz < to.z; ++wz)
            {
                for (int wy = from.y; wy < to.y; ++wy)
                {
                    for (int wx = from.x; wx < to.x; ++wx)
                    {
                        ChunkManager.getChunkIdxAndTerrainArrayIdxFromWorldCoords(wx, wy, wz, out ulong chunkIndex, out uint blockIndex);

                        var blockId = ChunkManager.chunks_getTerrainData(chunkIndex, blockIndex);
                        blocks[blocksIndex++] = blockId;

                        if (blockId >= GameRoot.BUILDING_PART_ARRAY_IDX_START)
                        {
                            var partTemplate = ItemTemplateManager.getBuildingPartTemplate(GameRoot.BuildingPartIdxLookupTable.table[blockId]);
                            if (partTemplate.parentItemTemplate != null)
                            {
                                AddToShoppingList(shoppingList, partTemplate.parentItemTemplate);
                            }
                        }
                        else if (blockId > 0)
                        {
                            var blockTemplate = ItemTemplateManager.getTerrainBlockTemplateByByteIdx(blockId);
                            if (blockTemplate != null && blockTemplate.parentBOT != null && blockTemplate.parentBOT.parentItemTemplate != null)
                            {
                                AddToShoppingList(shoppingList, blockTemplate.parentBOT.parentItemTemplate);
                            }
                        }
                    }
                }
            }

            var buildings = new HashSet<BuildableObjectGO>(new BuildableObjectGOComparer());
            AABB3D aabb = ObjectPoolManager.aabb3ds.getObject();
            aabb.reinitialize(from.x, from.y, from.z, to.x - from.x, to.y - from.y, to.z - from.z);
            QuadtreeArray<BuildableObjectGO> quadTree = StreamingSystem.getBuildableObjectGOQuadtreeArray();
            bogoQueryResult.Clear();
            quadTree.queryAABB3D(aabb, bogoQueryResult, true);
            foreach (var bogo in bogoQueryResult)
            {
                if (aabb.hasXYZIntersection(bogo._aabb))
                {
                    switch (bogo.template.type)
                    {
                        case BuildableObjectTemplate.BuildableObjectType.BuildingPart:
                        case BuildableObjectTemplate.BuildableObjectType.WorldDecorMineAble:
                        case BuildableObjectTemplate.BuildableObjectType.ModularEntityModule:
                            break;

                        default:
                            buildings.Add(bogo);
                            break;
                    }
                }
            }
            ObjectPoolManager.aabb3ds.returnObject(aabb); aabb = null;

            var buildingDataArray = new BlueprintData.BuildableObjectData[buildings.Count];
            var customData = new List<BlueprintData.BuildableObjectData.CustomData>();
            var powerGridBuildings = new HashSet<BuildableObjectGO>(new BuildableObjectGOComparer());
            int buildingIndex = 0;
            foreach (var bogo in buildings)
            {
                BuildableEntity.BuildableEntityGeneralData generalData = default;
                var hasGeneralData = BuildingManager.buildingManager_getBuildableEntityGeneralData(bogo.Id, ref generalData);
                Debug.Assert(hasGeneralData == IOBool.iotrue, $"{bogo.Id} {bogo.template?.identifier}");

                if (bogo.template.type == BuildableObjectTemplate.BuildableObjectType.MinecartDepot)
                {
                    minecartDepotIndices.Add(buildingIndex);
                }

                buildingDataArray[buildingIndex].originalEntityId = bogo.relatedEntityId;
                buildingDataArray[buildingIndex].templateName = bogo.template.name;
                buildingDataArray[buildingIndex].templateId = generalData.buildableObjectTemplateId;
                buildingDataArray[buildingIndex].worldX = generalData.pos.x - from.x;
                buildingDataArray[buildingIndex].worldY = generalData.pos.y - from.y;
                buildingDataArray[buildingIndex].worldZ = generalData.pos.z - from.z;
                buildingDataArray[buildingIndex].orientationY = bogo.template.canBeRotatedAroundXAxis ? (byte)0 : generalData.orientationY;
                buildingDataArray[buildingIndex].itemMode = generalData.itemMode;

                if (bogo.template.canBeRotatedAroundXAxis)
                {
                    buildingDataArray[buildingIndex].orientationUnlockedX = generalData.orientationUnlocked.x;
                    buildingDataArray[buildingIndex].orientationUnlockedY = generalData.orientationUnlocked.y;
                    buildingDataArray[buildingIndex].orientationUnlockedZ = generalData.orientationUnlocked.z;
                    buildingDataArray[buildingIndex].orientationUnlockedW = generalData.orientationUnlocked.w;
                }
                else
                {
                    buildingDataArray[buildingIndex].orientationUnlockedX = 0.0f;
                    buildingDataArray[buildingIndex].orientationUnlockedY = 0.0f;
                    buildingDataArray[buildingIndex].orientationUnlockedZ = 0.0f;
                    buildingDataArray[buildingIndex].orientationUnlockedW = 1.0f;
                }

                var bogoType = bogo.GetType();

                customData.Clear();
                if (typeof(ProducerGO).IsAssignableFrom(bogoType))
                {
                    var assembler = (ProducerGO)bogo;
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("craftingRecipeId", assembler.getLastPolledRecipeId()));
                }
                if (typeof(LoaderGO).IsAssignableFrom(bogoType))
                {
                    var loader = (LoaderGO)bogo;
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("isInputLoader", loader.isInputLoader() ? "true" : "false"));
                    if (bogo.template.loader_isFilter)
                    {
                        customData.Add(new BlueprintData.BuildableObjectData.CustomData("loaderFilterTemplateId", Traverse.Create(loader).Field("_cache_lastSetFilterTemplateId").GetValue<ulong>()));
                    }
                }
                if (typeof(PipeLoaderGO).IsAssignableFrom(bogoType))
                {
                    var loader = (PipeLoaderGO)bogo;
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("isInputLoader", loader.isInputLoader() ? "true" : "false"));
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("pipeLoaderFilterTemplateId", Traverse.Create(loader).Field("_cache_lastSetFilterTemplateId").GetValue<ulong>()));
                }
                if (typeof(ConveyorBalancerGO).IsAssignableFrom(bogoType))
                {
                    var balancer = (ConveyorBalancerGO)bogo;
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("balancerInputPriority", balancer.getInputPriority()));
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("balancerOutputPriority", balancer.getOutputPriority()));
                }
                if (typeof(SignGO).IsAssignableFrom(bogoType))
                {
                    var signTextLength = SignGO.signEntity_getSignTextLength(bogo.relatedEntityId);
                    var signText = new byte[signTextLength];
                    byte useAutoTextSize = 0;
                    float textMinSize = 0;
                    float textMaxSize = 0;
                    SignGO.signEntity_getSignText(bogo.relatedEntityId, signText, signTextLength, ref useAutoTextSize, ref textMinSize, ref textMaxSize);

                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("signText", System.Text.Encoding.Default.GetString(signText)));
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("signUseAutoTextSize", useAutoTextSize));
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("signTextMinSize", textMinSize));
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("signTextMaxSize", textMaxSize));
                }
                if (typeof(BlastFurnaceBaseGO).IsAssignableFrom(bogoType))
                {
                    BlastFurnacePollingUpdateData data = default;
                    if (BlastFurnaceBaseGO.blastFurnaceEntity_queryPollingData(bogo.relatedEntityId, ref data) == IOBool.iotrue)
                    {
                        customData.Add(new BlueprintData.BuildableObjectData.CustomData("blastFurnaceModeTemplateId", data.modeTemplateId));
                    }
                }
                if (typeof(DroneTransportGO).IsAssignableFrom(bogoType))
                {
                    DroneTransportPollingUpdateData data = default;
                    if (DroneTransportGO.droneTransportEntity_queryPollingData(bogo.relatedEntityId, ref data, null, 0U) == IOBool.iotrue)
                    {
                        customData.Add(new BlueprintData.BuildableObjectData.CustomData("loadConditionFlags", data.loadConditionFlags));
                        customData.Add(new BlueprintData.BuildableObjectData.CustomData("loadCondition_comparisonType", data.loadCondition_comparisonType));
                        customData.Add(new BlueprintData.BuildableObjectData.CustomData("loadCondition_fillRatePercentage", data.loadCondition_fillRatePercentage));
                        customData.Add(new BlueprintData.BuildableObjectData.CustomData("loadCondition_seconds", data.loadCondition_seconds));
                    }

                    byte[] stationName = new byte[128];
                    uint stationNameLength = 0;
                    byte stationType = (byte)(bogo.template.droneTransport_isStartStation ? 1 : 0);
                    DroneTransportGO.droneTransportEntity_getStationName(bogo.relatedEntityId, stationType, stationName, (uint)stationName.Length, ref stationNameLength);
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("stationName", Encoding.UTF8.GetString(stationName, 0, (int)stationNameLength)));
                    customData.Add(new BlueprintData.BuildableObjectData.CustomData("stationType", stationType));
                }
                if (typeof(ChestGO).IsAssignableFrom(bogoType))
                {
                    ulong inventoryId = 0UL;
                    if (BuildingManager.buildingManager_getInventoryAccessors(bogo.relatedEntityId, 0U, ref inventoryId) == IOBool.iotrue)
                    {
                        uint slotCount = 0;
                        uint categoryLock = 0;
                        uint firstSoftLockedSlotIdx = 0;
                        InventoryManager.inventoryManager_getAuxiliaryDataById(inventoryId, ref slotCount, ref categoryLock, ref firstSoftLockedSlotIdx, IOBool.iofalse);

                        customData.Add(new BlueprintData.BuildableObjectData.CustomData("firstSoftLockedSlotIdx", firstSoftLockedSlotIdx));
                    }
                }
                if (typeof(IHasModularEntityBaseManager).IsAssignableFrom(bogoType))
                {
                    ModularBuildingData rootNode = null;
                    uint totalModuleCount = ModularBuildingManagerFrame.modularEntityBase_getTotalModuleCount(bogo.relatedEntityId, 0U);
                    for (uint id = 1; id <= totalModuleCount; ++id)
                    {
                        ulong botId = 0;
                        uint parentId = 0;
                        uint parentAttachmentPointIdx = 0;
                        ModularBuildingManagerFrame.modularEntityBase_getModuleDataForModuleId(bogo.relatedEntityId, id, ref botId, ref parentId, ref parentAttachmentPointIdx, 0U);
                        if (id == 1U)
                        {
                            rootNode = new ModularBuildingData(bogo.template, id);
                        }
                        else
                        {
                            var nodeById = FindModularBuildingNodeById(rootNode, parentId);
                            if (nodeById == null)
                            {
                                DuplicationerPlugin.log.LogError("parent node not found!");
                                break;
                            }
                            if (nodeById.attachments[(int)parentAttachmentPointIdx] != null)
                            {
                                DuplicationerPlugin.log.LogError("parent node attachment point is occupied!");
                                break;
                            }
                            var node = new ModularBuildingData(ItemTemplateManager.getBuildableObjectTemplate(botId), id);
                            nodeById.attachments[(int)parentAttachmentPointIdx] = node;
                        }
                    }
                    if (rootNode != null)
                    {
                        var rootNodeJSON = JSON.Dump(rootNode, EncodeOptions.NoTypeHints);
                        customData.Add(new BlueprintData.BuildableObjectData.CustomData("modularBuildingData", rootNodeJSON));
                    }
                }
                if (bogo.template.hasPoleGridConnection)
                {
                    if (!powerGridBuildings.Contains(bogo))
                    {
                        foreach (var powerGridBuilding in powerGridBuildings)
                        {
                            if (PowerLineHH.buildingManager_powerlineHandheld_checkIfAlreadyConnected(powerGridBuilding.relatedEntityId, bogo.relatedEntityId) == IOBool.iotrue)
                            {
                                customData.Add(new BlueprintData.BuildableObjectData.CustomData("powerline", powerGridBuilding.relatedEntityId));
                                AddToShoppingList(shoppingList, PowerlineItemTemplate);
                            }
                        }
                        powerGridBuildings.Add(bogo);
                    }
                }

                buildingDataArray[buildingIndex].customData = customData.ToArray();

                if (bogo.template.parentItemTemplate != null)
                {
                    AddToShoppingList(shoppingList, bogo.template.parentItemTemplate);
                }

                buildingIndex++;
            }

            BlueprintData blueprintData = new BlueprintData();
            blueprintData.buildableObjects = buildingDataArray;
            blueprintData.blocks.sizeX = size.x;
            blueprintData.blocks.sizeY = size.y;
            blueprintData.blocks.sizeZ = size.z;
            blueprintData.blocks.ids = blocks;

            return new Blueprint("new blueprint", blueprintData, minecartDepotIndices.ToArray(), shoppingList, new ItemTemplate[0]);
        }

        public int GetShoppingListEntry(ulong itemTemplateId, out string name)
        {
            if (!ShoppingList.TryGetValue(itemTemplateId, out ShoppingListData shoppingListEntry))
            {
                name = "";
                return 0;
            }

            name = shoppingListEntry.name;
            return shoppingListEntry.count;
        }

        private static void AddToShoppingList(Dictionary<ulong, ShoppingListData> shoppingList, ItemTemplate template, int count = 1)
        {
            if (template == null) throw new System.ArgumentNullException(nameof(template));

            if (!shoppingList.TryGetValue(template.id, out ShoppingListData shoppingListEntry))
            {
                shoppingListEntry = new ShoppingListData(template.id, template.name, 0);
            }
            shoppingListEntry.count += count;
            shoppingList[template.id] = shoppingListEntry;
        }

        public static bool TryLoadFileHeader(string path, out FileHeader header, out string name)
        {
            header = new FileHeader();
            name = "";

            var headerSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(FileHeader));
            var allBytes = File.ReadAllBytes(path);
            if (allBytes.Length < headerSize) return false;

            var reader = new BinaryReader(new MemoryStream(allBytes, false));

            header.magic = reader.ReadUInt32();
            if (header.magic != FileMagicNumber) return false;

            header.version = reader.ReadUInt32();

            header.icon1 = reader.ReadUInt64();
            header.icon2 = reader.ReadUInt64();
            header.icon3 = reader.ReadUInt64();
            header.icon4 = reader.ReadUInt64();

            name = reader.ReadString();

            reader.Close();
            reader.Dispose();

            return true;
        }

        public static Blueprint LoadFromFile(string path)
        {
            if (!File.Exists(path)) return null;

            var shoppingList = new Dictionary<ulong, ShoppingListData>();
            var minecartDepotIndices = new List<int>();

            var headerSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(FileHeader));
            var allBytes = File.ReadAllBytes(path);
            if (allBytes.Length < headerSize) throw new FileLoadException(path);

            var reader = new BinaryReader(new MemoryStream(allBytes, false));

            var magic = reader.ReadUInt32();
            if (magic != FileMagicNumber) throw new FileLoadException(path);

            var version = reader.ReadUInt32();

            var iconItemTemplates = new List<ItemTemplate>();
            for (int i = 0; i < 4; ++i)
            {
                var iconItemTemplateId = reader.ReadUInt64();
                if (iconItemTemplateId != 0)
                {
                    var template = ItemTemplateManager.getItemTemplate(iconItemTemplateId);
                    if (template != null) iconItemTemplates.Add(template);
                }
            }

            var name = reader.ReadString();

            ulong dataSize;
            var rawData = SaveManager.decompressByteArray(reader.ReadBytes(allBytes.Length - headerSize), out dataSize);
            var blueprintData = LoadDataFromString(Encoding.UTF8.GetString(rawData.Take((int)dataSize).ToArray()), shoppingList, minecartDepotIndices);

            reader.Close();
            reader.Dispose();

            return new Blueprint(name, blueprintData, minecartDepotIndices.ToArray(), shoppingList, iconItemTemplates.ToArray());
        }

        private static BlueprintData LoadDataFromString(string blueprint, Dictionary<ulong, ShoppingListData> shoppingList, List<int> minecartDepotIndices)
        {
            var blueprintData = JSON.Load(blueprint).Make<BlueprintData>();

            var powerlineEntityIds = new List<ulong>();
            int buildingIndex = 0;
            foreach (var buildingData in blueprintData.buildableObjects)
            {
                var buildingTemplate = ItemTemplateManager.getBuildableObjectTemplate(buildingData.templateId);
                if (buildingTemplate != null && buildingTemplate.parentItemTemplate != null)
                {
                    if (buildingTemplate.type == BuildableObjectTemplate.BuildableObjectType.MinecartDepot)
                    {
                        minecartDepotIndices.Add(buildingIndex);
                    }

                    AddToShoppingList(shoppingList, buildingTemplate.parentItemTemplate);
                }

                powerlineEntityIds.Clear();
                GetCustomDataList(ref blueprintData, buildingIndex, "powerline", powerlineEntityIds);
                if (powerlineEntityIds.Count > 0) AddToShoppingList(shoppingList, PowerlineItemTemplate, powerlineEntityIds.Count);

                ++buildingIndex;
            }

            int blockIndex = 0;
            for (int z = 0; z < blueprintData.blocks.sizeZ; ++z)
            {
                for (int y = 0; y < blueprintData.blocks.sizeY; ++y)
                {
                    for (int x = 0; x < blueprintData.blocks.sizeX; ++x)
                    {
                        var blockId = blueprintData.blocks.ids[blockIndex++];
                        if (blockId >= GameRoot.BUILDING_PART_ARRAY_IDX_START)
                        {
                            var partTemplate = ItemTemplateManager.getBuildingPartTemplate(GameRoot.BuildingPartIdxLookupTable.table[blockId]);
                            if (partTemplate != null)
                            {
                                var itemTemplate = partTemplate.parentItemTemplate;
                                if (itemTemplate != null)
                                {
                                    AddToShoppingList(shoppingList, itemTemplate);
                                }
                            }
                        }
                        else if (blockId > 0)
                        {
                            var blockTemplate = ItemTemplateManager.getTerrainBlockTemplateByByteIdx(blockId);
                            if (blockTemplate != null && blockTemplate.parentBOT != null)
                            {
                                var itemTemplate = blockTemplate.parentBOT.parentItemTemplate;
                                if (itemTemplate != null)
                                {
                                    AddToShoppingList(shoppingList, itemTemplate);
                                }
                            }
                        }
                    }
                }
            }

            return blueprintData;
        }

        public void Save(string path, string name, ItemTemplate[] iconItemTemplates)
        {
            this.name = name;
            this.iconItemTemplates = iconItemTemplates;

            var json = JSON.Dump(data, EncodeOptions.PrettyPrint | EncodeOptions.NoTypeHints);

            var compressed = SaveManager.compressByteArray(Encoding.UTF8.GetBytes(json), out ulong dataSize);

            var writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write));

            writer.Write(FileMagicNumber);
            writer.Write(LatestBlueprintVersion);

            for (int i = 0; i < iconItemTemplates.Length; i++)
            {
                var template = iconItemTemplates[i];
                writer.Write(template.id);
            }
            for (int i = iconItemTemplates.Length; i < 4; i++)
            {
                writer.Write(0ul);
            }

            writer.Write(name);

            writer.Write(compressed.Take((int)dataSize).ToArray());

            writer.Close();
            writer.Dispose();
        }

        public void Place(Vector3Int anchorPosition, ConstructionTaskGroup constructionTaskGroup) => Place(GameRoot.getClientUsernameHash(), anchorPosition, constructionTaskGroup);
        public void Place(Character character, Vector3Int anchorPosition, ConstructionTaskGroup constructionTaskGroup) => Place(character.usernameHash, anchorPosition, constructionTaskGroup);
        public void Place(ulong usernameHash, Vector3Int anchorPosition, ConstructionTaskGroup constructionTaskGroup)
        {
            _powerlineConnectionPairs.Clear();
            var entityIdMap = new Dictionary<ulong, ulong>();

            AABB3D aabb = ObjectPoolManager.aabb3ds.getObject();

            if (data.blocks.ids == null) throw new System.ArgumentNullException(nameof(data.blocks.ids));

            var quadTreeArray = StreamingSystem.getBuildableObjectGOQuadtreeArray();
            int blockIndex = 0;
            for (int z = 0; z < data.blocks.sizeZ; z++)
            {
                for (int y = 0; y < data.blocks.sizeY; y++)
                {
                    for (int x = 0; x < data.blocks.sizeX; x++)
                    {
                        var blockId = data.blocks.ids[blockIndex++];
                        if (blockId > 0)
                        {
                            var worldPos = new Vector3Int(x, y, z) + anchorPosition;
                            ChunkManager.getChunkIdxAndTerrainArrayIdxFromWorldCoords(worldPos.x, worldPos.y, worldPos.z, out ulong worldChunkIndex, out uint worldBlockIndex);
                            var terrainData = ChunkManager.chunks_getTerrainData(worldChunkIndex, worldBlockIndex);

                            if (terrainData == 0 && quadTreeArray.queryPointXYZ(worldPos) == null)
                            {
                                if (blockId >= GameRoot.BUILDING_PART_ARRAY_IDX_START)
                                {
                                    var partTemplate = ItemTemplateManager.getBuildingPartTemplate(GameRoot.BuildingPartIdxLookupTable.table[blockId]);
                                    if (partTemplate != null && partTemplate.parentItemTemplate != null)
                                    {
                                        ActionManager.AddQueuedEvent(() =>
                                        {
                                            int mode = 0;
                                            if (partTemplate.parentItemTemplate.toggleableModes != null && partTemplate.parentItemTemplate.toggleableModes.Length != 0 && partTemplate.parentItemTemplate.toggleableModeType == ItemTemplate.ItemTemplateToggleableModeTypes.MultipleBuildings)
                                            {
                                                for (int index = 0; index < partTemplate.parentItemTemplate.toggleableModes.Length; ++index)
                                                {
                                                    if (partTemplate.parentItemTemplate.toggleableModes[index].buildableObjectTemplate == partTemplate)
                                                    {
                                                        mode = index;
                                                        break;
                                                    }
                                                }
                                            }
                                            GameRoot.addLockstepEvent(new BuildEntityEvent(usernameHash, partTemplate.parentItemTemplate.id, mode, worldPos, 0, Quaternion.identity, DuplicationerPlugin.IsCheatModeEnabled ? 0 : 1, 0, false));
                                        });
                                    }
                                }
                                else
                                {
                                    var blockTemplate = ItemTemplateManager.getTerrainBlockTemplateByByteIdx(blockId);
                                    if (blockTemplate != null && blockTemplate.yieldItemOnDig_template != null && blockTemplate.yieldItemOnDig_template.buildableObjectTemplate != null)
                                    {
                                        ActionManager.AddQueuedEvent(() =>
                                        {
                                            int mode = 0;
                                            if (blockTemplate.yieldItemOnDig_template.toggleableModes != null && blockTemplate.yieldItemOnDig_template.toggleableModes.Length != 0 && blockTemplate.yieldItemOnDig_template.toggleableModeType == ItemTemplate.ItemTemplateToggleableModeTypes.MultipleBuildings)
                                            {
                                                for (int index = 0; index < blockTemplate.yieldItemOnDig_template.toggleableModes.Length; ++index)
                                                {
                                                    if (blockTemplate.yieldItemOnDig_template.toggleableModes[index].buildableObjectTemplate == blockTemplate.parentBOT)
                                                    {
                                                        mode = index;
                                                        break;
                                                    }
                                                }
                                            }
                                            GameRoot.addLockstepEvent(new BuildEntityEvent(usernameHash, blockTemplate.yieldItemOnDig_template.id, mode, worldPos, 0, Quaternion.identity, DuplicationerPlugin.IsCheatModeEnabled ? 0 : 1, 0, false));
                                        });
                                    }
                                    else if (blockTemplate != null && blockTemplate.parentBOT != null)
                                    {
                                        var itemTemplate = blockTemplate.parentBOT.parentItemTemplate;
                                        if (itemTemplate != null)
                                        {
                                            ActionManager.AddQueuedEvent(() =>
                                            {
                                                int mode = 0;
                                                if (itemTemplate.toggleableModes != null && itemTemplate.toggleableModes.Length != 0 && itemTemplate.toggleableModeType == ItemTemplate.ItemTemplateToggleableModeTypes.MultipleBuildings)
                                                {
                                                    for (int index = 0; index < itemTemplate.toggleableModes.Length; ++index)
                                                    {
                                                        if (itemTemplate.toggleableModes[index].buildableObjectTemplate == blockTemplate.parentBOT)
                                                        {
                                                            mode = index;
                                                            break;
                                                        }
                                                    }
                                                }
                                                GameRoot.addLockstepEvent(new BuildEntityEvent(usernameHash, itemTemplate.id, mode, worldPos, 0, Quaternion.identity, DuplicationerPlugin.IsCheatModeEnabled ? 0 : 1, 0, false));
                                            });
                                        }
                                        else
                                        {
                                            DuplicationerPlugin.log.LogWarning((string)$"No item template for terrain index {blockId}");
                                        }
                                    }
                                    else
                                    {
                                        DuplicationerPlugin.log.LogWarning((string)$"No block template for terrain index {blockId}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            int buildingIndex = 0;
            foreach (var buildableObjectData in data.buildableObjects)
            {
                postBuildActions.Clear();

                var template = ItemTemplateManager.getBuildableObjectTemplate(buildableObjectData.templateId);
                Debug.Assert(template != null);

                var worldPos = new Vector3Int(buildableObjectData.worldX, buildableObjectData.worldY, buildableObjectData.worldZ) + anchorPosition;

                int wx, wy, wz;
                if (template.canBeRotatedAroundXAxis)
                    BuildingManager.getWidthFromUnlockedOrientation(template, buildableObjectData.orientationUnlocked, out wx, out wy, out wz);
                else
                    BuildingManager.getWidthFromOrientation(template, (BuildingManager.BuildOrientation)buildableObjectData.orientationY, out wx, out wy, out wz);

                ulong additionalData_ulong_01 = 0ul;
                ulong additionalData_ulong_02 = 0ul;

                bool usePasteConfigSettings = false;
                ulong pasteConfigSettings_01 = 0ul;
                ulong pasteConfigSettings_02 = 0ul;

                var craftingRecipeId = GetCustomData<ulong>(buildingIndex, "craftingRecipeId");
                if (craftingRecipeId != 0)
                {
                    var recipe = ItemTemplateManager.getCraftingRecipeById(craftingRecipeId);
                    if (recipe != null && (DuplicationerPlugin.configAllowUnresearchedRecipes.Get() || recipe.isResearched()))
                    {
                        usePasteConfigSettings = true;
                        pasteConfigSettings_01 = craftingRecipeId;
                    }
                }

                if (HasCustomData(buildingIndex, "isInputLoader"))
                {
                    usePasteConfigSettings = true;
                    bool isInputLoader = GetCustomData<bool>(buildingIndex, "isInputLoader");
                    pasteConfigSettings_01 = isInputLoader ? 1u : 0u;

                    if (template.loader_isFilter)
                    {
                        if (HasCustomData(buildingIndex, "loaderFilterTemplateId"))
                        {
                            var loaderFilterTemplateId = GetCustomData<ulong>(buildingIndex, "loaderFilterTemplateId");
                            if (loaderFilterTemplateId > 0)
                            {
                                usePasteConfigSettings = true;
                                pasteConfigSettings_02 = loaderFilterTemplateId;
                            }
                        }
                    }

                    if (template.type == BuildableObjectTemplate.BuildableObjectType.PipeLoader)
                    {
                        if (HasCustomData(buildingIndex, "pipeLoaderFilterTemplateId"))
                        {
                            var pipeLoaderFilterTemplateId = GetCustomData<ulong>(buildingIndex, "pipeLoaderFilterTemplateId");
                            if (pipeLoaderFilterTemplateId > 0)
                            {
                                postBuildActions.Add((ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) =>
                                {
                                    if (task.entityId > 0)
                                    {
                                        GameRoot.addLockstepEvent(new SetPipeLoaderConfig(usernameHash, task.entityId, pipeLoaderFilterTemplateId, isInputLoader));
                                    }
                                });
                            }
                        }
                    }
                }

                if (HasCustomData(buildingIndex, "modularNodeIndex"))
                {
                    additionalData_ulong_01 = GetCustomData<ulong>(buildingIndex, "modularNodeIndex");
                }

                if (template.type == BuildableObjectTemplate.BuildableObjectType.ConveyorBalancer)
                {
                    var balancerInputPriority = GetCustomData<int>(buildingIndex, "balancerInputPriority");
                    var balancerOutputPriority = GetCustomData<int>(buildingIndex, "balancerOutputPriority");
                    postBuildActions.Add((ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) =>
                    {
                        if (task.entityId > 0)
                        {
                            GameRoot.addLockstepEvent(new SetConveyorBalancerConfig(usernameHash, task.entityId, balancerInputPriority, balancerOutputPriority));
                        }
                    });
                }

                if (template.type == BuildableObjectTemplate.BuildableObjectType.Sign)
                {
                    var signText = GetCustomData<string>(buildingIndex, "signText");
                    var signUseAutoTextSize = GetCustomData<byte>(buildingIndex, "signUseAutoTextSize");
                    var signTextMinSize = GetCustomData<float>(buildingIndex, "signTextMinSize");
                    var signTextMaxSize = GetCustomData<float>(buildingIndex, "signTextMaxSize");
                    postBuildActions.Add((ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) =>
                    {
                        if (task.entityId > 0)
                        {
                            GameRoot.addLockstepEvent(new SignSetTextEvent(usernameHash, task.entityId, signText, signUseAutoTextSize != 0, signTextMinSize, signTextMaxSize));
                        }
                    });
                }

                if (HasCustomData(buildingIndex, "blastFurnaceModeTemplateId"))
                {
                    var modeTemplateId = GetCustomData<ulong>(buildingIndex, "blastFurnaceModeTemplateId");
                    if (modeTemplateId > 0)
                    {
                        postBuildActions.Add((ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) =>
                        {
                            if (task.entityId > 0)
                            {
                                GameRoot.addLockstepEvent(new BlastFurnaceSetModeEvent(usernameHash, task.entityId, modeTemplateId));
                            }
                        });
                    }
                }

                if (template.type == BuildableObjectTemplate.BuildableObjectType.Storage && HasCustomData(buildingIndex, "firstSoftLockedSlotIdx"))
                {
                    var firstSoftLockedSlotIdx = GetCustomData<uint>(buildingIndex, "firstSoftLockedSlotIdx");
                    postBuildActions.Add((ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) =>
                    {
                        if (task.entityId > 0)
                        {
                            ulong inventoryId = 0UL;
                            if (BuildingManager.buildingManager_getInventoryAccessors(task.entityId, 0U, ref inventoryId) == IOBool.iotrue)
                            {
                                GameRoot.addLockstepEvent(new SetSoftLockForInventory(usernameHash, inventoryId, firstSoftLockedSlotIdx));
                            }
                            else
                            {
                                DuplicationerPlugin.log.LogWarning("Failed to get inventory accessor for storage");
                            }
                        }
                        else
                        {
                            DuplicationerPlugin.log.LogWarning("Failed to get entity id for storage");
                        }
                    });
                }

                if (template.type == BuildableObjectTemplate.BuildableObjectType.DroneTransport && HasCustomData(buildingIndex, "loadConditionFlags"))
                {
                    var loadConditionFlags = GetCustomData<byte>(buildingIndex, "loadConditionFlags");
                    var loadCondition_comparisonType = GetCustomData<byte>(buildingIndex, "loadCondition_comparisonType");
                    var loadCondition_fillRatePercentage = GetCustomData<byte>(buildingIndex, "loadCondition_fillRatePercentage");
                    var loadCondition_seconds = GetCustomData<uint>(buildingIndex, "loadCondition_seconds");
                    postBuildActions.Add((ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) =>
                    {
                        if (task.entityId > 0)
                        {
                            GameRoot.addLockstepEvent(new DroneTransportLoadConditionEvent(usernameHash, task.entityId, loadConditionFlags, loadCondition_fillRatePercentage, loadCondition_seconds, loadCondition_comparisonType));
                        }
                        else
                        {
                            DuplicationerPlugin.log.LogWarning("Failed to get entity id for drone transport");
                        }
                    });
                }

                if (template.type == BuildableObjectTemplate.BuildableObjectType.DroneTransport && HasCustomData(buildingIndex, "stationName"))
                {
                    var stationName = GetCustomData<string>(buildingIndex, "stationName");
                    var stationType = GetCustomData<byte>(buildingIndex, "stationType");
                    postBuildActions.Add((ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) =>
                    {
                        if (task.entityId > 0)
                        {
                            GameRoot.addLockstepEvent(new DroneTransportSetNameEvent(usernameHash, stationName, task.entityId, stationType));
                        }
                        else
                        {
                            DuplicationerPlugin.log.LogWarning("Failed to get entity id for drone transport");
                        }
                    });
                }

                if (HasCustomData(buildingIndex, "modularBuildingData"))
                {
                    var modularBuildingDataJSON = GetCustomData<string>(buildingIndex, "modularBuildingData");
                    var modularBuildingData = JSON.Load(modularBuildingDataJSON).Make<ModularBuildingData>();
                    postBuildActions.Add((ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) =>
                    {
                        if (task.entityId > 0)
                        {
                            byte out_mbState = 0;
                            byte out_isEnabled = 0;
                            byte out_canBeEnabled = 0;
                            byte out_constructionIsDismantle = 0;
                            uint out_assignedConstructionDronePorts = 0;
                            ModularBuildingManagerFrame.modularEntityBase_getGenericData(task.entityId, ref out_mbState, ref out_isEnabled, ref out_canBeEnabled, ref out_constructionIsDismantle, ref out_assignedConstructionDronePorts);
                            if (out_mbState == (byte)ModularBuildingManagerFrame.MBState.ConstructionSiteInactive)
                            {
                                var moduleCount = ModularBuildingManagerFrame.modularEntityBase_getTotalModuleCount(task.entityId, 0);
                                if (moduleCount <= 1)
                                {
                                    var mbmfData = modularBuildingData.BuildMBMFData();
                                    GameRoot.addLockstepEvent(new SetModularEntityConstructionStateDataEvent(usernameHash, 0U, task.entityId, mbmfData));
                                }
                            }
                        }
                    });
                }

                var powerlineEntityIds = new List<ulong>();
                GetCustomDataList(buildingIndex, "powerline", powerlineEntityIds);
                foreach (var powerlineEntityId in powerlineEntityIds)
                {
                    var powerlineIndex = FindEntityIndex(powerlineEntityId);
                    if (powerlineIndex >= 0)
                    {
                        var fromPos = worldPos;
                        var toBuildableObjectData = data.buildableObjects[powerlineIndex];
                        postBuildActions.Add((ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) =>
                        {
                            if (entityIdMap.TryGetValue(toBuildableObjectData.originalEntityId, out var toEntityId))
                            {
                                if (PowerLineHH.buildingManager_powerlineHandheld_checkIfAlreadyConnected(task.entityId, toEntityId) == IOBool.iofalse)
                                {
                                    GameRoot.addLockstepEvent(new PoleConnectionEvent(usernameHash, PowerlineItemTemplate.id, task.entityId, toEntityId));
                                }
                            }
                        });
                    }
                }

                aabb.reinitialize(worldPos.x, worldPos.y, worldPos.z, wx, wy, wz);
                var existingEntityId = CheckIfBuildingExists(aabb, worldPos, buildableObjectData);
                if (existingEntityId > 0)
                {
                    entityIdMap[buildableObjectData.originalEntityId] = existingEntityId;
                    var postBuildActionsArray = postBuildActions.ToArray();
                    constructionTaskGroup.AddTask(buildableObjectData.originalEntityId, (ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) => {
                        ActionManager.AddQueuedEvent(() =>
                        {
                            task.entityId = existingEntityId;
                            foreach (var action in postBuildActionsArray) action.Invoke(taskGroup, task);
                            while (taskGroup.InvokeNextTaskIfReady()) ;
                        });
                    });
                }
                else
                {
                    var postBuildActionsArray = postBuildActions.ToArray();
                    var originalEntityId = buildableObjectData.originalEntityId;
                    constructionTaskGroup.AddTask(originalEntityId, (ConstructionTaskGroup taskGroup, ConstructionTaskGroup.ConstructionTask task) => {
                        var buildEntityEvent = new BuildEntityEvent(
                            usernameHash,
                            template.parentItemTemplate.id,
                            buildableObjectData.itemMode,
                            worldPos,
                            buildableObjectData.orientationY,
                            buildableObjectData.orientationUnlocked,
                            DuplicationerPlugin.IsCheatModeEnabled
                                ? 0
                                : (template.modularBuildingModule_amountItemCost > 1 ? (int)template.modularBuildingModule_amountItemCost : 1),
                            0,
                            additionalData_ulong_01: additionalData_ulong_01,
                            additionalData_ulong_02: additionalData_ulong_02,
                            playSound: true,
                            usePasteConfigSettings: usePasteConfigSettings,
                            pasteConfigSettings_01: pasteConfigSettings_01,
                            pasteConfigSettings_02: pasteConfigSettings_02
                        );

                        ActionManager.AddQueuedEvent(() =>
                        {
                            ActionManager.AddBuildEvent(buildEntityEvent, (ulong entityId) =>
                            {
                                entityIdMap[originalEntityId] = entityId;
                                ActionManager.AddQueuedEvent(() =>
                                {
                                    task.entityId = entityId;
                                    foreach (var action in postBuildActionsArray) action.Invoke(taskGroup, task);
                                    while (taskGroup.InvokeNextTaskIfReady()) ;
                                });
                            });
                            GameRoot.addLockstepEvent(buildEntityEvent);
                        });
                    });
                }

                ++buildingIndex;
            }
            ObjectPoolManager.aabb3ds.returnObject(aabb); aabb = null;


            buildingIndex = 0;
            foreach (var buildableObjectData in data.buildableObjects)
            {
                dependenciesTemp.Clear();
                if (HasCustomData(buildingIndex, "modularParentId"))
                {
                    ulong parentId = GetCustomData<ulong>(buildingIndex, "modularParentId");

                    var dependency = constructionTaskGroup.GetTask(parentId);
                    if (dependency != null) dependenciesTemp.Add(dependency);
                    else DuplicationerPlugin.log.LogWarning($"Entity id {parentId} not found in blueprint");
                }

                var powerlineEntityIds = new List<ulong>();
                GetCustomDataList(buildingIndex, "powerline", powerlineEntityIds);
                foreach (var powerlineEntityId in powerlineEntityIds)
                {
                    var dependency = constructionTaskGroup.GetTask(powerlineEntityId);
                    if (dependency != null) dependenciesTemp.Add(dependency);
                    else DuplicationerPlugin.log.LogWarning($"Entity id {powerlineEntityId} not found in blueprint");
                }

                if (dependenciesTemp.Count > 0)
                {
                    var task = constructionTaskGroup.GetTask(buildableObjectData.originalEntityId);
                    if (task != null) task.dependencies = dependenciesTemp.ToArray();
                }

                buildingIndex++;
            }
        }

        private int FindEntityIndex(ulong entityId)
        {
            for (int i = 0; i < data.buildableObjects.Length; ++i)
            {
                if (data.buildableObjects[i].originalEntityId == entityId) return i;
            }
            return -1;
        }

        private int CountModularParents(ulong parentId)
        {
            var parentIndex = FindEntityIndex(parentId);
            if (parentIndex < 0) return 0;

            if (HasCustomData(parentIndex, "modularParentId"))
            {
                var grandparentId = GetCustomData<ulong>(parentIndex, "modularParentId");
                return CountModularParents(grandparentId) + 1;
            }

            return 1;
        }

        public bool HasCustomData(int index, string identifier) => HasCustomData(ref data, index, identifier);
        public static bool HasCustomData(ref BlueprintData data, int index, string identifier)
        {
            foreach (var customDataEntry in data.buildableObjects[index].customData) if (customDataEntry.identifier == identifier) return true;
            return false;
        }

        public T GetCustomData<T>(int index, string identifier) => GetCustomData<T>(ref data, index, identifier);
        public static T GetCustomData<T>(ref BlueprintData data, int index, string identifier)
        {
            foreach (var customDataEntry in data.buildableObjects[index].customData) if (customDataEntry.identifier == identifier) return (T)System.Convert.ChangeType(customDataEntry.value, typeof(T));
            return default;
        }

        public void GetCustomDataList<T>(int index, string identifier, List<T> list) => GetCustomDataList<T>(ref data, index, identifier, list);
        public static void GetCustomDataList<T>(ref BlueprintData data, int index, string identifier, List<T> list)
        {
            foreach (var customDataEntry in data.buildableObjects[index].customData) if (customDataEntry.identifier == identifier) list.Add((T)System.Convert.ChangeType(customDataEntry.value, typeof(T)));
        }

        internal BlueprintData.BuildableObjectData GetBuildableObjectData(int index) => GetBuildableObjectData(ref data, index);
        internal static BlueprintData.BuildableObjectData GetBuildableObjectData(ref BlueprintData data, int index)
        {
            if (index < 0 || index >= data.buildableObjects.Length) throw new System.IndexOutOfRangeException(nameof(index));

            return data.buildableObjects[index];
        }

        internal byte GetBlockId(int x, int y, int z) => GetBlockId(ref data, x, y, z);
        internal static byte GetBlockId(ref BlueprintData data, int x, int y, int z)
        {
            if (x < 0 || x >= data.blocks.sizeX) throw new System.IndexOutOfRangeException(nameof(x));
            if (y < 0 || y >= data.blocks.sizeY) throw new System.IndexOutOfRangeException(nameof(y));
            if (z < 0 || z >= data.blocks.sizeZ) throw new System.IndexOutOfRangeException(nameof(z));

            return data.blocks.ids[x + (y + z * data.blocks.sizeY) * data.blocks.sizeX];
        }

        internal byte GetBlockId(int index) => GetBlockId(ref data, index);
        internal static byte GetBlockId(ref BlueprintData data, int index)
        {
            if (index < 0 || index >= data.blocks.ids.Length) throw new System.IndexOutOfRangeException(nameof(index));

            return data.blocks.ids[index];
        }

        internal static ulong CheckIfBuildingExists(AABB3D aabb, Vector3Int worldPos, BlueprintData.BuildableObjectData buildableObjectData)
        {
            bogoQueryResult.Clear();
            StreamingSystem.getBuildableObjectGOQuadtreeArray().queryAABB3D(aabb, bogoQueryResult, false);
            if (bogoQueryResult.Count > 0)
            {
                var template = ItemTemplateManager.getBuildableObjectTemplate(buildableObjectData.templateId);
                foreach (var wbogo in bogoQueryResult)
                {
                    if (Traverse.Create(wbogo).Field("renderMode").GetValue<int>() != 1)
                    {
                        bool match = true;

                        BuildableEntity.BuildableEntityGeneralData generalData = default;
                        if (wbogo.template != template)
                        {
                            match = false;
                        }
                        else if (BuildingManager.buildingManager_getBuildableEntityGeneralData(wbogo.relatedEntityId, ref generalData) == IOBool.iotrue)
                        {
                            if (generalData.pos != worldPos) match = false;

                            if (template.canBeRotatedAroundXAxis)
                            {
                                if (generalData.orientationUnlocked != buildableObjectData.orientationUnlocked) match = false;
                            }
                            else
                            {
                                if (generalData.orientationY != buildableObjectData.orientationY) match = false;
                            }
                        }
                        else
                        {
                            DuplicationerPlugin.log.LogWarning("data not found");
                            match = false;
                        }

                        if (match) return wbogo.relatedEntityId;
                    }
                }
            }

            return 0ul;
        }

        internal void Rotate()
        {
            var oldSize = Size;
            var newSize = new Vector3Int(oldSize.z, oldSize.y, oldSize.x);
            var oldCenter = ((Vector3)oldSize) / 2.0f;
            var newCenter = ((Vector3)newSize) / 2.0f;

            BlueprintData rotatedData = new BlueprintData(data.buildableObjects.Length, data.blocks.Size);
            rotatedData.buildableObjects = new BlueprintData.BuildableObjectData[data.buildableObjects.Length];
            rotatedData.blocks.ids = new byte[data.blocks.ids.Length];
            for (int i = 0; i < data.buildableObjects.Length; ++i)
            {
                var buildableObjectData = data.buildableObjects[i];
                var offsetX = buildableObjectData.worldZ - oldCenter.z;
                var offsetZ = oldCenter.x - buildableObjectData.worldX;
                var newX = Mathf.RoundToInt(newCenter.x + offsetX);
                var newZ = Mathf.RoundToInt(newCenter.z + offsetZ);

                var template = ItemTemplateManager.getBuildableObjectTemplate(buildableObjectData.templateId);
                if (template != null)
                {
                    if (template.canBeRotatedAroundXAxis)
                    {
                        var oldOrientation = buildableObjectData.orientationUnlocked;
                        var newOrientation = Quaternion.Euler(0.0f, 90.0f, 0.0f) * oldOrientation;
                        BuildingManager.getWidthFromUnlockedOrientation(template, newOrientation, out _, out _, out int wz);
                        newZ -= wz;
                        buildableObjectData.orientationUnlocked = newOrientation;
                    }
                    else
                    {
                        var oldOrientation = buildableObjectData.orientationY;
                        var newOrientation = (byte)((oldOrientation + 1) & 0x3);
                        BuildingManager.getWidthFromOrientation(template, (BuildingManager.BuildOrientation)newOrientation, out _, out _, out int wz);
                        newZ -= wz;
                        buildableObjectData.orientationY = newOrientation;
                    }
                }

                buildableObjectData.worldX = newX;
                buildableObjectData.worldZ = newZ;
                rotatedData.buildableObjects[i] = buildableObjectData;
            }

            var newBlockIds = new byte[data.blocks.ids.Length];
            int fromIndex = 0;
            for (int x = 0; x < newSize.x; x++)
            {
                for (int y = 0; y < newSize.y; y++)
                {
                    for (int z = newSize.z - 1; z >= 0; z--)
                    {
                        newBlockIds[x + (y + z * newSize.y) * newSize.x] = data.blocks.ids[fromIndex++];
                    }
                }
            }

            rotatedData.blocks = new BlueprintData.BlockData(newSize, newBlockIds);

            data = rotatedData;
        }

        public void Show(Vector3Int anchorPosition, Vector3Int repeatFrom, Vector3Int repeatTo, Vector3Int repeatStepSize, BatchRenderingGroup placeholderRenderGroup, List<BlueprintPlaceholder> buildingPlaceholders, List<BlueprintPlaceholder> terrainPlaceholders)
        {
            for (int ry = repeatFrom.y; ry <= repeatTo.y; ++ry)
            {
                for (int rz = repeatFrom.z; rz <= repeatTo.z; ++rz)
                {
                    for (int rx = repeatFrom.x; rx <= repeatTo.x; ++rx)
                    {
                        var repeatIndex = new Vector3Int(rx, ry, rz);
                        var repeatAnchorPosition = anchorPosition + new Vector3Int(rx * repeatStepSize.x, ry * repeatStepSize.y, rz * repeatStepSize.z);

                        for (int buildingIndex = 0; buildingIndex < data.buildableObjects.Length; buildingIndex++)
                        {
                            var buildableObjectData = data.buildableObjects[buildingIndex];
                            var template = ItemTemplateManager.getBuildableObjectTemplate(buildableObjectData.templateId);

                            int wx, wy, wz;
                            if (template.canBeRotatedAroundXAxis)
                                BuildingManager.getWidthFromUnlockedOrientation(template, buildableObjectData.orientationUnlocked, out wx, out wy, out wz);
                            else
                                BuildingManager.getWidthFromOrientation(template, (BuildingManager.BuildOrientation)buildableObjectData.orientationY, out wx, out wy, out wz);

                            var position = new Vector3(buildableObjectData.worldX + wx * 0.5f, buildableObjectData.worldY + (template.canBeRotatedAroundXAxis ? wy * 0.5f : 0.0f), buildableObjectData.worldZ + wz * 0.5f) + repeatAnchorPosition;
                            var rotation = template.canBeRotatedAroundXAxis ? buildableObjectData.orientationUnlocked : Quaternion.Euler(0, buildableObjectData.orientationY * 90.0f, 0.0f);
                            var orientation = (BuildingManager.BuildOrientation)buildableObjectData.orientationY;

                            var baseTransform = Matrix4x4.TRS(position, rotation, Vector3.one);

                            if (buildableObjectData.TryGetCustomData("modularBuildingData", out var modularBuildingDataJSON))
                            {
                                var pattern = PlaceholderPattern.Instance(template.placeholderPrefab, template);
                                var handles = new List<BatchRenderingHandle>(pattern.Entries.Length);
                                for (int i = 0; i < pattern.Entries.Length; i++)
                                {
                                    var entry = pattern.Entries[i];
                                    var transform = baseTransform * entry.relativeTransform;
                                    handles.Add(placeholderRenderGroup.AddSimplePlaceholderTransform(entry.mesh, transform, BlueprintPlaceholder.stateColours[1]));
                                }

                                var modularBuildingData = JSON.Load(modularBuildingDataJSON).Make<ModularBuildingData>();

                                var centerOffset = new Vector3(wx, 0.0f, wz) * -0.5f;

                                var extraBoundingBoxes = new List<BoundsInt>();

                                AABB3D aabb = ObjectPoolManager.aabb3ds.getObject();
                                aabb.reinitialize(0, 0, 0, wx, wy, wz);
                                BuildModularBuildPlaceholders(anchorPosition, handles, placeholderRenderGroup, buildingPlaceholders, repeatIndex, buildingIndex, template, baseTransform, position + centerOffset, orientation, aabb, modularBuildingData, extraBoundingBoxes);
                                ObjectPoolManager.aabb3ds.returnObject(aabb);

                                buildingPlaceholders.Add(new BlueprintPlaceholder(buildingIndex, repeatIndex, template, position, rotation, orientation, handles.ToArray(), extraBoundingBoxes.ToArray()));
                            }
                            else
                            {
                                var pattern = PlaceholderPattern.Instance(template.placeholderPrefab, template);
                                var handles = new BatchRenderingHandle[pattern.Entries.Length];
                                for (int i = 0; i < pattern.Entries.Length; i++)
                                {
                                    var entry = pattern.Entries[i];
                                    var transform = baseTransform * entry.relativeTransform;
                                    handles[i] = placeholderRenderGroup.AddSimplePlaceholderTransform(entry.mesh, transform, BlueprintPlaceholder.stateColours[1]);
                                }

                                buildingPlaceholders.Add(new BlueprintPlaceholder(buildingIndex, repeatIndex, template, position, rotation, orientation, handles));
                            }
                        }

                        int blockIndex = 0;
                        for (int z = 0; z < data.blocks.sizeZ; ++z)
                        {
                            for (int y = 0; y < data.blocks.sizeY; ++y)
                            {
                                for (int x = 0; x < data.blocks.sizeX; ++x)
                                {
                                    var id = data.blocks.ids[blockIndex];
                                    if (id > 0)
                                    {
                                        var worldPos = new Vector3(x + repeatAnchorPosition.x + 0.5f, y + repeatAnchorPosition.y, z + repeatAnchorPosition.z + 0.5f);

                                        BuildableObjectTemplate template = null;
                                        if (id < GameRoot.MAX_TERRAIN_COUNT)
                                        {
                                            var tbt = ItemTemplateManager.getTerrainBlockTemplateByByteIdx(id);
                                            if (tbt != null) template = tbt.parentBOT;
                                        }
                                        else
                                        {
                                            template = ItemTemplateManager.getBuildingPartTemplate(GameRoot.BuildingPartIdxLookupTable.table[id]);
                                            if (template == null) DuplicationerPlugin.log.LogWarning((string)$"Template not found for terrain index {id}-{GameRoot.BUILDING_PART_ARRAY_IDX_START} with id {GameRoot.BuildingPartIdxLookupTable.table[id]} at ({worldPos.x}, {worldPos.y}, {worldPos.z})");
                                        }

                                        if (template != null)
                                        {
                                            var baseTransform = Matrix4x4.Translate(worldPos);

                                            var pattern = PlaceholderPattern.Instance(template.placeholderPrefab, template);
                                            var handles = new BatchRenderingHandle[pattern.Entries.Length];
                                            for (int i = 0; i < pattern.Entries.Length; i++)
                                            {
                                                var entry = pattern.Entries[i];
                                                var transform = baseTransform * entry.relativeTransform;
                                                handles[i] = placeholderRenderGroup.AddSimplePlaceholderTransform(entry.mesh, transform, BlueprintPlaceholder.stateColours[1]);
                                            }

                                            terrainPlaceholders.Add(new BlueprintPlaceholder(blockIndex, repeatIndex, template, worldPos, Quaternion.identity, BuildingManager.BuildOrientation.xPos, handles));
                                        }
                                    }
                                    blockIndex++;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static Vector3 getWorldPositionByLocalOffsetOrientationY(AABB3D aabb, int orientation, Vector3 localOffset)
        {
            Vector3 result = new Vector3(aabb.x0, aabb.y0 + localOffset.y, aabb.z0);
            switch (orientation)
            {
                case 0:
                    result.x += localOffset.x;
                    result.z += localOffset.z;
                    break;
                case 1:
                    result.z += aabb.wz;
                    result.x += localOffset.z;
                    result.z -= localOffset.x;
                    break;
                case 2:
                    result.x += aabb.wx;
                    result.z += aabb.wz;
                    result.x -= localOffset.x;
                    result.z -= localOffset.z;
                    break;
                case 3:
                    result.x += aabb.wx;
                    result.x -= localOffset.z;
                    result.z += localOffset.x;
                    break;
            }

            return result;
        }


        private static void BuildModularBuildPlaceholders(Vector3Int anchorPosition, List<BatchRenderingHandle> handles, BatchRenderingGroup placeholderRenderGroup, List<BlueprintPlaceholder> buildingPlaceholders, Vector3Int repeatIndex, int buildingIndex, BuildableObjectTemplate template, Matrix4x4 baseTransform, Vector3 position, BuildingManager.BuildOrientation orientation, AABB3D aabb, ModularBuildingData modularBuildingData, List<BoundsInt> extraBoundingBoxes)
        {
            for (int attachmentIndex = 0; attachmentIndex < modularBuildingData.attachments.Length; attachmentIndex++)
            {
                var attachment = modularBuildingData.attachments[attachmentIndex];
                if (attachment == null) continue;

                foreach (var node in template.modularBuildingConnectionNodes[attachmentIndex].nodeData)
                {
                    if (node.botId == attachment.templateId)
                    {
                        var attachmentTemplate = ItemTemplateManager.getBuildableObjectTemplate(attachment.templateId);

                        BuildingManager.getWidthFromOrientation(attachmentTemplate, node.positionData.orientation, out var wx, out var wy, out var wz);

                        var offsetPosition = getWorldPositionByLocalOffsetOrientationY(aabb, (int)orientation, node.positionData.offset + new Vector3(wx, 0.0f, wz) * 0.5f);
                        var attachmentOrientation = (BuildingManager.BuildOrientation)(((int)node.positionData.orientation + (int)orientation) % 4);

                        var attachmentPosition = position + offsetPosition;
                        var attachmentRotation = Quaternion.Euler(0.0f, (int)attachmentOrientation * 90.0f, 0.0f);
                        var attachmentTransform = Matrix4x4.TRS(attachmentPosition, attachmentRotation, Vector3.one);

                        var attachmentPattern = PlaceholderPattern.Instance(attachmentTemplate.placeholderPrefab, attachmentTemplate);
                        foreach (var entry in attachmentPattern.Entries)
                        {
                            var transform = attachmentTransform * entry.relativeTransform;
                            handles.Add(placeholderRenderGroup.AddSimplePlaceholderTransform(entry.mesh, transform, BlueprintPlaceholder.stateColours[1]));
                        }

                        BuildingManager.getWidthFromOrientation(attachmentTemplate, attachmentOrientation, out wx, out wy, out wz);
                        var centerOffset = new Vector3(wx, 0.0f, wz) * -0.5f;

                        AABB3D attachmentAabb = ObjectPoolManager.aabb3ds.getObject();
                        attachmentAabb.reinitialize(0, 0, 0, wx, wy, wz);
                        BuildModularBuildPlaceholders(anchorPosition, handles, placeholderRenderGroup, buildingPlaceholders, repeatIndex, buildingIndex, attachmentTemplate, baseTransform, attachmentPosition + centerOffset, attachmentOrientation, attachmentAabb, attachment, extraBoundingBoxes);
                        ObjectPoolManager.aabb3ds.returnObject(attachmentAabb);

                        extraBoundingBoxes.Add(new BoundsInt(Vector3Int.RoundToInt(attachmentPosition + centerOffset) - anchorPosition, new Vector3Int(wx, wy, wz)));

                        break;
                    }
                }
            }
        }

        internal void GetExistingMinecartDepots(Vector3Int targetPosition, List<MinecartDepotGO> existingMinecartDepots)
        {
            AABB3D aabb = ObjectPoolManager.aabb3ds.getObject();
            foreach (var index in minecartDepotIndices)
            {
                if (index >= data.buildableObjects.Length) continue;

                var buildableObjectData = data.buildableObjects[index];
                var template = ItemTemplateManager.getBuildableObjectTemplate(buildableObjectData.templateId);
                if (template != null)
                {
                    var worldPos = new Vector3Int(buildableObjectData.worldX + targetPosition.x, buildableObjectData.worldY + targetPosition.y, buildableObjectData.worldZ + targetPosition.z);
                    int wx, wy, wz;
                    if (template.canBeRotatedAroundXAxis)
                        BuildingManager.getWidthFromUnlockedOrientation(template, buildableObjectData.orientationUnlocked, out wx, out wy, out wz);
                    else
                        BuildingManager.getWidthFromOrientation(template, (BuildingManager.BuildOrientation)buildableObjectData.orientationY, out wx, out wy, out wz);
                    aabb.reinitialize(worldPos.x, worldPos.y, worldPos.z, wx, wy, wz);

                    var depotEntityId = CheckIfBuildingExists(aabb, worldPos, buildableObjectData);
                    if (depotEntityId > 0)
                    {
                        var bogo = StreamingSystem.getBuildableObjectGOByEntityId(depotEntityId);
                        if (bogo != null)
                        {
                            var depot = (MinecartDepotGO)bogo;
                            if (depot != null) existingMinecartDepots.Add(depot);
                        }
                    }
                }
            }
            ObjectPoolManager.aabb3ds.returnObject(aabb); aabb = null;
        }

        internal bool HasExistingMinecartDepots(Vector3Int targetPosition)
        {
            AABB3D aabb = ObjectPoolManager.aabb3ds.getObject();
            foreach (var index in minecartDepotIndices)
            {
                if (index >= data.buildableObjects.Length) continue;

                var buildableObjectData = data.buildableObjects[index];
                var template = ItemTemplateManager.getBuildableObjectTemplate(buildableObjectData.templateId);
                if (template != null)
                {
                    var worldPos = new Vector3Int(buildableObjectData.worldX + targetPosition.x, buildableObjectData.worldY + targetPosition.y, buildableObjectData.worldZ + targetPosition.z);
                    int wx, wy, wz;
                    if (template.canBeRotatedAroundXAxis)
                        BuildingManager.getWidthFromUnlockedOrientation(template, buildableObjectData.orientationUnlocked, out wx, out wy, out wz);
                    else
                        BuildingManager.getWidthFromOrientation(template, (BuildingManager.BuildOrientation)buildableObjectData.orientationY, out wx, out wy, out wz);
                    aabb.reinitialize(worldPos.x, worldPos.y, worldPos.z, wx, wy, wz);

                    var depotEntityId = CheckIfBuildingExists(aabb, worldPos, buildableObjectData);
                    if (depotEntityId > 0)
                    {
                        var bogo = StreamingSystem.getBuildableObjectGOByEntityId(depotEntityId);
                        if (bogo != null)
                        {
                            var depot = (MinecartDepotGO)bogo;
                            if (depot != null) return true;
                        }
                    }
                }
            }
            ObjectPoolManager.aabb3ds.returnObject(aabb); aabb = null;

            return false;
        }

        [System.Serializable]
        public class ModularBuildingData
        {
            public uint id;
            public ulong templateId;
            public ModularBuildingData[] attachments;

            public ModularBuildingData()
            {
                id = 0;
                templateId = 0;
                attachments = new ModularBuildingData[0];
            }

            public ModularBuildingData(BuildableObjectTemplate template, uint id)
            {
                this.id = id;
                templateId = template.id;
                attachments = new ModularBuildingData[template.modularBuildingConnectionNodes.Length];
            }

            public MBMFData_BuildingNode BuildMBMFData()
            {
                var attachmentPoints = new MBMFData_BuildingNode[attachments.Length];
                for (int i = 0; i < attachments.Length; i++)
                {
                    var attachment = attachments[i];
                    if (attachment != null) attachmentPoints[i] = attachment.BuildMBMFData();
                }
                return new MBMFData_BuildingNode(templateId, attachmentPoints, id);
            }
        }

        private static ModularBuildingData FindModularBuildingNodeById(ModularBuildingData node, uint id)
        {
            if (node.id == id) return node;
            for (uint index = 0; index < node.attachments.Length; ++index)
            {
                if (node.attachments[(int)index] != null)
                {
                    var nodeById = FindModularBuildingNodeById(node.attachments[(int)index], id);
                    if (nodeById != null) return nodeById;
                }
            }
            return null;
        }

        public struct ShoppingListData
        {
            public ulong itemTemplateId;
            public string name;
            public int count;

            public ShoppingListData(ulong itemTemplateId, string name, int count)
            {
                this.itemTemplateId = itemTemplateId;
                this.name = name;
                this.count = count;
            }
        }

        public struct FileHeader
        {
            public uint magic;
            public uint version;
            public ulong icon1;
            public ulong icon2;
            public ulong icon3;
            public ulong icon4;
        }
    }
}
