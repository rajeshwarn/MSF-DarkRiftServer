﻿using DarkRift;

namespace SpawnerHandler.Packets
{
    public class SpawnRequestPacket : IDarkRiftSerializable
    {
        public int SpawnerId;
        public int SpawnId;
        public string SpawnCode = "";
        public string OverrideExePath = "";

        public void Deserialize(DeserializeEvent e)
        {
            SpawnerId = e.Reader.ReadInt32();
            SpawnId = e.Reader.ReadInt32();
            SpawnCode = e.Reader.ReadString();
            OverrideExePath = e.Reader.ReadString();
        }

        public void Serialize(SerializeEvent e)
        {
            e.Writer.Write(SpawnerId);
            e.Writer.Write(SpawnId);
            e.Writer.Write(SpawnCode);
            e.Writer.Write(OverrideExePath);
        }
    }
}