// ==UndertaleModTool==
// @name        Realign Room Internal Order
// @name:zh-Hans 重新对齐房间内部顺序
// @name:zh-CN  重新对齐房间内部顺序
// ==/UndertaleModTool==
EnsureDataLoaded();

Data.GeneralInfo.RoomOrder.Clear();
for (int i = 0; i < Data.Rooms.Count; i++)
{
    Data.GeneralInfo.RoomOrder.Add(new UndertaleResourceById<UndertaleRoom, UndertaleChunkROOM>() { Resource = Data.Rooms[i] });
}
