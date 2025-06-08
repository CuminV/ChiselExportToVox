using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

public class ChiselExportVoxMod : ModSystem
{
    private BlockPos startPos;
    private BlockPos endPos;
    private ICoreClientAPI capi;

    public override void StartClientSide(ICoreClientAPI api)
    {
         
        this.capi = api;
        api.ChatCommands.Create("be")
            .WithDescription("Exporting a Selected Area to VOX")
            .RequiresPlayer()

            .BeginSubCommand("start")
                .HandleWith((args) =>
                {
                    startPos = api.World.Player.Entity.Pos.AsBlockPos;
                    api.ShowChatMessage($"The starting point has been set: {startPos}");
                    HiLightSection();
                    return TextCommandResult.Success();
                    
                })
            .EndSub()

            .BeginSubCommand("end")
                .HandleWith((args) =>
                {
                    endPos = api.World.Player.Entity.Pos.AsBlockPos;
                    api.ShowChatMessage($"The end point is set: {endPos}");
                    HiLightSection(); 
                    return TextCommandResult.Success();
                    
                })
            .EndSub()

            .BeginSubCommand("export")
                .WithArgs(api.ChatCommands.Parsers.Word("fileName"))
                .HandleWith((args) =>
                {
                    if (startPos == null || endPos == null)
                    {
                        return TextCommandResult.Error("Set start and end points before exporting!");
                    }

                    string fileName = (string)args[0];
                    ExportSelectedArea(api, api.World.Player, fileName);
                    return TextCommandResult.Success();
                })
            .EndSub();
    }
    private void HiLightSection()
    {
        if (startPos is null || endPos is null)
        {
            return;
        }

        List<BlockPos> blocks = new List<BlockPos>();

        for (int x = Math.Min(startPos.X, endPos.X); x < Math.Max(startPos.X, endPos.X); ++x)
        {
            for (int y = Math.Min(startPos.Y, endPos.Y); y < Math.Max(startPos.Y, endPos.Y); ++y)
            {
                for (int z = Math.Min(startPos.Z, endPos.Z); z < Math.Max(startPos.Z, endPos.Z); ++z)
                {
                    blocks.Add(new BlockPos(x, y, z));
                }
            }
        }

        List<int> list = new List<int>
            {
                ColorUtil.ToRgba(100, 0, 250, 0)
            };

        this.capi.World.HighlightBlocks(this.capi.World.Player, 501, blocks, list, 0, 0, 1f);
    }



    private void ExportSelectedArea(ICoreClientAPI api, IPlayer player, string fileName)
    {
        int processedBlocks = 0;
        int totalVoxels = 0;
        BlockPos minPos = new BlockPos(
            Math.Min(startPos.X, endPos.X),
            Math.Min(startPos.Y, endPos.Y),
            Math.Min(startPos.Z, endPos.Z)
        );

        BlockPos maxPos = new BlockPos(
            Math.Max(startPos.X, endPos.X),
            Math.Max(startPos.Y, endPos.Y),
            Math.Max(startPos.Z, endPos.Z)
        );

        List<byte[]> modelChunks = new List<byte[]>();
        List<byte[]> nTRNChunks = new List<byte[]>();
        List<byte[]> nSHPChunks = new List<byte[]>();

        int modelIdCounter = 0;
        int paletteColorIndex = 1;

        for (int x = minPos.X; x <= maxPos.X; x++)
        {
            for (int y = minPos.Y; y <= maxPos.Y; y++)
            {
                for (int z = minPos.Z; z <= maxPos.Z; z++)
                {
                    Block block = api.World.BlockAccessor.GetBlock(x, y, z);
                    if (block == null || block.Id == 0) continue;
                    // Вычисление локальной позиции относительно startPos
                    BlockPos blockPos = new BlockPos(x, y, z);
                    BlockPos localPos = new BlockPos(
                        (blockPos.Z - startPos.Z) * 16,
                        (blockPos.X - startPos.X) * 16,
                        (blockPos.Y - startPos.Y) * 16
                    );

                    // Теперь localPos - это локальная позиция внутри выделенной области
                    processedBlocks++;

                    byte[] voxels = new byte[16 * 16 * 16];
                    for (int i = 0; i < voxels.Length; i++) voxels[i] = 0;

                    if (block.Code.Path.Contains("chisel"))
                    {
                        var chiselledVoxels = GetChiselBlockVoxels(api, x, y, z);
                        for (int i = 0; i < chiselledVoxels.Length; i++)
                        {
                            voxels[i] = chiselledVoxels[i];
                        }
                    }
                    else
                    {
                        for (int vx = 0; vx < 16; vx++)
                            for (int vy = 0; vy < 16; vy++)
                                for (int vz = 0; vz < 16; vz++)
                                    voxels[vx + vy * 16 + vz * 256] = (byte)paletteColorIndex;
                    }

                    totalVoxels += voxels.Count(v => v != 0);

                    byte[] sizeChunk = CreateSIZEChunk(16, 16, 16);
                    byte[] xyziChunk = CreateXYZIChunk(voxels);

                    byte[] modelChunk = CombineChunks(sizeChunk, xyziChunk);
                    modelChunks.Add(modelChunk);

                    int trnNodeId = 2 + modelIdCounter * 2; // 2,4,6,...
                    int shpNodeId = 3 + modelIdCounter * 2; // 3,5,7...

                    nSHPChunks.Add(CreateSHPChunk(shpNodeId, modelIdCounter));
                    nTRNChunks.Add(CreateTRNChunk(trnNodeId, shpNodeId, localPos));

                    modelIdCounter++;
                    paletteColorIndex = 1;
                }
            }
        }

        byte[] nGRP = CreateNGRPChunk(1, nTRNChunks.Count);

        List<byte> allChunks = new List<byte>();
        allChunks.AddRange(modelChunks.SelectMany(b => b));
        allChunks.AddRange(CreateRGBAChunk()); // <--- Добавить RGBA с единым цветом
        allChunks.AddRange(CreateNTRNRootChunk());
        allChunks.AddRange(nGRP);
        for (int i = 0; i < nTRNChunks.Count; i++)
        {
            allChunks.AddRange(nTRNChunks[i]);
            allChunks.AddRange(nSHPChunks[i]);
        }




        byte[] mainContent = CombineChunks(allChunks.ToArray());
        byte[] fullFile = BuildVOXFile(mainContent);

        string exportFolder = Path.Combine(AppContext.BaseDirectory, "ExportToVox");
        if (!Directory.Exists(exportFolder))
            Directory.CreateDirectory(exportFolder);

        string savePath = Path.Combine(exportFolder, fileName + ".vox");

        File.WriteAllBytes(savePath, fullFile);
        api.ShowChatMessage($"Export complete: {savePath}");
    }

    private byte[] GetChiselBlockVoxels(ICoreClientAPI api, int x, int y, int z)
    {
        var block = api.World.BlockAccessor.GetBlock(x, y, z);

        if (block != null && block.Code.Path.Contains("chisel"))
        {
            var blockEntity = api.World.BlockAccessor.GetBlockEntity(new BlockPos(x, y, z));

            if (blockEntity != null && blockEntity is BlockEntityChisel chiselBlockEntity)
            {
                BoolArray16x16x16 voxelPresence;
                byte[,,] voxelMaterials;
                chiselBlockEntity.ConvertToVoxels(out voxelPresence, out voxelMaterials);

                byte[] voxels = new byte[16 * 16 * 16];
                int index = 0;
                for (int i = 0; i < 16; i++)
                    for (int j = 0; j < 16; j++)
                        for (int k = 0; k < 16; k++)
                            voxels[index++] = voxelPresence[i, j, k] ? (byte)1 : (byte)0;

                return voxels;
            }
        }
        return new byte[16 * 16 * 16];
    }

    private byte[] CombineChunks(params byte[][] chunks)
    {
        List<byte> combined = new List<byte>();
        foreach (var chunk in chunks)
            combined.AddRange(chunk);
        return combined.ToArray();
    }

    private byte[] CreateSIZEChunk(int x, int y, int z)
    {
        byte[] data = new byte[12];
        BitConverter.GetBytes(x).CopyTo(data, 0);
        BitConverter.GetBytes(y).CopyTo(data, 4);
        BitConverter.GetBytes(z).CopyTo(data, 8);
        return CreateChunk("SIZE", data);
    }

    private byte[] CreateXYZIChunk(byte[] voxels)
    {
        List<byte> xyziData = new List<byte>();
        int count = voxels.Count(v => v != 0);
        xyziData.AddRange(BitConverter.GetBytes(count));

        for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    byte colorIndex = voxels[x + y * 16 + z * 256];
                    if (colorIndex == 0) continue;

                    xyziData.Add((byte)x);
                    xyziData.Add((byte)z);
                    xyziData.Add((byte)y);
                    xyziData.Add(colorIndex);
                }

        return CreateChunk("XYZI", xyziData.ToArray());
    }

    private byte[] CreateChunk(string chunkType, byte[] chunkData)
    {
        List<byte> chunk = new List<byte>();
        chunk.AddRange(Encoding.ASCII.GetBytes(chunkType));
        chunk.AddRange(BitConverter.GetBytes(chunkData.Length));
        chunk.AddRange(BitConverter.GetBytes(0));
        chunk.AddRange(chunkData);
        return chunk.ToArray();
    }
    private byte[] CreateRGBAChunk()
    {
        byte[] data = new byte[256 * 4];

        // Цвет под индексом 1 — например, чисто белый (R=255, G=255, B=255, A=255)
        data[0] = 255; // R
        data[1] = 255; // G
        data[2] = 255; // B
        data[3] = 255; // A

        // Остальные можно оставить прозрачными
        for (int i = 1; i < 256; i++)
        {
            data[i * 4 + 0] = 0;
            data[i * 4 + 1] = 0;
            data[i * 4 + 2] = 0;
            data[i * 4 + 3] = 0;
        }

        return CreateChunk("RGBA", data);
    }

    private byte[] CreateNTRNRootChunk()
    {
        return new byte[]
        {
        0x6E, 0x54, 0x52, 0x4E, // "nTRN"
        0x1C, 0x00, 0x00, 0x00, // content size = 28
        0x00, 0x00, 0x00, 0x00, // children size = 0
        0x00, 0x00, 0x00, 0x00, // node id
        0x00, 0x00, 0x00, 0x00, // child id
        0x01, 0x00, 0x00, 0x00, // reserved
        0xFF, 0xFF, 0xFF, 0xFF, // reserved
        0xFF, 0xFF, 0xFF, 0xFF, // reserved
        0x01, 0x00, 0x00, 0x00, // frame attr dict
        0x00, 0x00, 0x00, 0x00  // reserved
        };
    }


    private byte[] CreateNGRPChunk(int groupId, int childCount)
    {
        List<byte> data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(groupId)); // group id
        data.AddRange(BitConverter.GetBytes(0)); // attr dict
        data.AddRange(BitConverter.GetBytes(childCount));

        for (int i = 0; i < childCount; i++)
        {
            int childId = 2 + i * 2; // id nTRN (2,4,6,...)
            data.AddRange(BitConverter.GetBytes(childId));
        }

        return CreateChunk("nGRP", data.ToArray());
    }
    private byte[] CreateTRNChunk(int nodeId, int childNodeId, BlockPos localPos)
    {
        List<byte> data = new List<byte>();

        // Стандартная часть структуры nTRN
        data.AddRange(BitConverter.GetBytes(nodeId)); // node id
        data.AddRange(BitConverter.GetBytes(0)); // attr dict
        data.AddRange(BitConverter.GetBytes(childNodeId)); // child node id
        data.AddRange(BitConverter.GetBytes(0)); // reserved
        data.AddRange(BitConverter.GetBytes(-1)); // layer id (-1)

        // Динамическая информация
        byte[] keyBytes = Encoding.ASCII.GetBytes("_t"); // ключ "_t"
        int keyLength = keyBytes.Length;

        // Строка с координатами блока
        string coordinates = $"{localPos.X} {localPos.Y} {localPos.Z}";
        byte[] coordinatesBytes = Encoding.ASCII.GetBytes(coordinates);
        int coordinatesLength = coordinatesBytes.Length;

        // Добавление данных для ключа и координат
        data.AddRange(BitConverter.GetBytes(1)); // num frames
        data.AddRange(BitConverter.GetBytes(1)); // ключ-значение (например, 1)
        data.AddRange(BitConverter.GetBytes(keyLength)); // длина ключа
        data.AddRange(keyBytes); // сам ключ "_t"
        data.AddRange(BitConverter.GetBytes(coordinatesLength)); // длина значения
        data.AddRange(coordinatesBytes); // координаты блока

        // Возвращаем готовую структуру
        return CreateChunk("nTRN", data.ToArray());
    }


    private byte[] CreateSHPChunk(int nodeId, int modelId)
    {
        List<byte> data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(nodeId)); // node id
        data.AddRange(BitConverter.GetBytes(0)); // attr dict
        data.AddRange(BitConverter.GetBytes(1)); // model count
        data.AddRange(BitConverter.GetBytes(modelId)); // model id
        data.AddRange(BitConverter.GetBytes(0)); // model attr dict
        return CreateChunk("nSHP", data.ToArray());
    }

   

    private byte[] BuildVOXFile(byte[] mainChunkContent)
    {
        List<byte> fileData = new List<byte>();
        fileData.AddRange(Encoding.ASCII.GetBytes("VOX "));
        fileData.AddRange(BitConverter.GetBytes(150));
        fileData.AddRange(Encoding.ASCII.GetBytes("MAIN"));
        fileData.AddRange(BitConverter.GetBytes(0));
        fileData.AddRange(BitConverter.GetBytes(mainChunkContent.Length));
        fileData.AddRange(mainChunkContent);
        return fileData.ToArray();
    }
}
